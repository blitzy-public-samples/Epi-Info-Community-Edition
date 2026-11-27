using System.Collections;
using System.ComponentModel;
using System.Data;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using EpiInfo.Data.Records;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Storage;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Tests.Sql;

/// <summary>
/// Unit tests of <see cref="SqlErrorClassifier"/>. No test here opens a connection: every
/// <see cref="SqlException"/> is built by <see cref="SqlExceptionFactory"/> in the shape SqlClient 5.2.3 raises it.
/// </summary>
public sealed class SqlErrorClassifierTests
{
    [Fact]
    public void Factory_BuildsSqlExceptionWithRequestedNumber()
    {
        var inner = new InvalidOperationException("inner");

        SqlException plain = SqlExceptionFactory.Create(4060, "factory message");
        SqlException wrapped = SqlExceptionFactory.Create(1205, "factory message", inner);

        Assert.Equal(4060, plain.Number);
        Assert.Contains("factory message", plain.Message, StringComparison.Ordinal);
        Assert.Null(plain.InnerException);
        Assert.Equal(1205, wrapped.Number);
        Assert.Same(inner, wrapped.InnerException);
    }

    // Listed unavailable numbers: each one is unavailable, with its own number, while opening and during a command.

    // The listed numbers, written out here and never read from SqlErrorClassifier.UnavailableErrorNumbers: 53, -1, 2,
    // 40, 64, 121, 233, 10053, 10054, 10060, 10061 and 11001 (the connection cannot be made, or it broke); 18456
    // (login failed); 4060 (cannot open the database); -2 (timeout); 1205 (deadlock victim).
    private static readonly int[] ListedUnavailableNumbers =
    [
        53, -1, 2, 40, 64, 121, 233, 10053, 10054, 10060, 10061, 11001,
        18456,
        4060,
        -2,
        1205,
    ];

    /// <summary>
    /// Each listed number, raised while opening and during a command: error number and whether it was raised while
    /// opening.
    /// </summary>
    public static TheoryData<int, bool> ListedUnavailableNumberCases
    {
        get
        {
            var cases = new TheoryData<int, bool>();
            foreach (int number in ListedUnavailableNumbers)
            {
                cases.Add(number, true);
                cases.Add(number, false);
            }

            return cases;
        }
    }

    [Fact]
    public void UnavailableErrorNumbers_IsExactlyTheDocumentedSet()
    {
        Assert.Equal(ListedUnavailableNumbers.Order(), SqlErrorClassifier.UnavailableErrorNumbers.Order());
    }

    [Theory]
    [MemberData(nameof(ListedUnavailableNumberCases))]
    public void Classify_ListedNumber_IsUnavailableWithThatNumber(int number, bool duringOpen)
    {
        SqlException exception = SqlExceptionFactory.Create(number);

        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None, duringOpen);

        Assert.Equal(new SqlFailure(SqlFailureKind.Unavailable, number), failure);
    }

    // A Win32Exception counts only as the inner exception of a SqlException with a listed number: the number decides,
    // and the inner exception never promotes an unlisted one.
    [Theory]
    [InlineData(10054, SqlFailureKind.Unavailable)]
    [InlineData(53, SqlFailureKind.Unavailable)]
    [InlineData(547, SqlFailureKind.Unexpected)]
    public void Classify_SqlExceptionWithWin32Inner_IsDecidedByItsNumber(int number, SqlFailureKind expectedKind)
    {
        SqlException exception = SqlExceptionFactory.Create(number, inner: new Win32Exception(10054));

        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None, duringOpen: true);

        Assert.Equal(new SqlFailure(expectedKind, number), failure);
    }

    // Unexpected failures (500) under a request that was not aborted, while opening and during a command.

    /// <summary>
    /// Unlisted SQL Server errors with the severity class the server raises them with, each while opening and during
    /// a command: permission (229, 230), constraint (547) and conversion (245). Number, class and whether it was
    /// raised while opening.
    /// </summary>
    public static TheoryData<int, byte, bool> UnlistedNumberCases
    {
        get
        {
            (int Number, byte Class)[] errors = [(229, 14), (230, 14), (547, 16), (245, 16)];
            var cases = new TheoryData<int, byte, bool>();
            foreach ((int number, byte errorClass) in errors)
            {
                cases.Add(number, errorClass, true);
                cases.Add(number, errorClass, false);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(UnlistedNumberCases))]
    public void Classify_UnlistedNumber_IsUnexpectedWithThatNumber(int number, byte errorClass, bool duringOpen)
    {
        SqlException exception = SqlExceptionFactory.Create([SqlExceptionFactory.Error(number, errorClass, "test error")]);

        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None, duringOpen);

        Assert.Equal(new SqlFailure(SqlFailureKind.Unexpected, number), failure);
    }

    /// <summary>
    /// Failures that are not a <see cref="SqlException"/>, each while opening and during a command: a bare or wrapped
    /// <see cref="Win32Exception"/>, a malformed connection string's <see cref="ArgumentException"/> and other non-SQL
    /// exceptions. Exception and whether it was raised while opening.
    /// </summary>
    public static TheoryData<Exception, bool> NonSqlFailureCases
    {
        get
        {
            Exception[] failures =
            [
                new Win32Exception(10061),
                new IOException("x", new Win32Exception(10061)),
                new InvalidOperationException("x", new Win32Exception(10061)),
                MalformedConnectionStringException(),
                new NullReferenceException(),
                new FormatException(),
                new Exception("boom"),
            ];

            var cases = new TheoryData<Exception, bool>();
            foreach (Exception failure in failures)
            {
                cases.Add(failure, true);
                cases.Add(failure, false);
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(NonSqlFailureCases))]
    public void Classify_NonSqlFailure_IsUnexpectedWithNoNumber(Exception exception, bool duringOpen)
    {
        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None, duringOpen);

        Assert.Equal(new SqlFailure(SqlFailureKind.Unexpected, null), failure);
    }

    // SqlClient parses the connection string in the SqlConnection constructor; nothing is opened.
    private const string MalformedConnectionString = "this is not a connection string";

    private static ArgumentException MalformedConnectionStringException() =>
        Assert.Throws<ArgumentException>(() => new SqlConnection(MalformedConnectionString));

    // Cancellation: a cancelled request token decides only together with evidence that the failure is the abort.

    // The two errors, both number 0 with class 11 and state 0, of the attention acknowledgement SqlClient 5.2.3 raises
    // for a command cancelled while it runs, in the English (neutral) and German resources of SqlClient.
    private const string SevereErrorEnglish =
        "A severe error occurred on the current command.  The results, if any, should be discarded.";
    private const string OperationCancelledEnglish = "Operation cancelled by user.";
    private const string SevereErrorGerman =
        "Schwerer Fehler für den aktuellen Befehl. Die Ergebnisse (wenn vorhanden) sollten verworfen werden.";
    private const string OperationCancelledGerman = "Der Vorgang wurde vom Benutzer abgebrochen.";

    private static readonly CancellationToken CancelledToken = new(canceled: true);

    /// <summary>
    /// Cancellation evidence as SqlClient raises it, each under a cancelled and an uncancelled token: UI culture,
    /// exception, whether the token is cancelled, expected kind and expected number.
    /// </summary>
    public static TheoryData<string, Exception, bool, SqlFailureKind, int?> CancellationEvidence => new()
    {
        { "en-US", new OperationCanceledException(CancelledToken), true, SqlFailureKind.Cancelled, null },
        { "en-US", new TaskCanceledException("x", null, CancelledToken), true, SqlFailureKind.Cancelled, null },
        { "en-US", new TaskCanceledException(), true, SqlFailureKind.Cancelled, null },
        {
            "en-US", AttentionAcknowledgement(SevereErrorEnglish, OperationCancelledEnglish), true,
            SqlFailureKind.Cancelled, 0
        },
        {
            "de-DE", AttentionAcknowledgement(SevereErrorGerman, OperationCancelledGerman), true,
            SqlFailureKind.Cancelled, 0
        },
        { "en-US", new OperationCanceledException(), false, SqlFailureKind.Unexpected, null },
        {
            "en-US", AttentionAcknowledgement(SevereErrorEnglish, OperationCancelledEnglish), false,
            SqlFailureKind.Unexpected, 0
        },
        {
            "de-DE", AttentionAcknowledgement(SevereErrorGerman, OperationCancelledGerman), false,
            SqlFailureKind.Unexpected, 0
        },
    };

    [Theory]
    [MemberData(nameof(CancellationEvidence))]
    public void Classify_CancellationEvidence_IsCancelledOnlyUnderCancelledToken(
        string uiCulture, Exception exception, bool tokenCancelled, SqlFailureKind expectedKind, int? expectedNumber)
    {
        using var culture = new UiCultureScope(uiCulture);
        CancellationToken token = tokenCancelled ? CancelledToken : CancellationToken.None;

        SqlFailure failure = SqlErrorClassifier.Classify(exception, token);

        Assert.Equal(new SqlFailure(expectedKind, expectedNumber), failure);
    }

    /// <summary>
    /// Failures that only coincide with a cancelled token, each with the classification it keeps: exception, expected
    /// kind and expected number.
    /// </summary>
    public static TheoryData<Exception, SqlFailureKind, int?> FailuresCoincidingWithCancellation => new()
    {
        {
            SqlExceptionFactory.Create([SqlExceptionFactory.Error(
                -2,
                11,
                "Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the "
                    + "server is not responding.",
                0)]),
            SqlFailureKind.Unavailable,
            -2
        },
        {
            SqlExceptionFactory.Create([SqlExceptionFactory.Error(18456, 14, "Login failed for user 'sa'.")]),
            SqlFailureKind.Unavailable,
            18456
        },
        {
            SqlExceptionFactory.Create([SqlExceptionFactory.Error(
                229, 14, "The SELECT permission was denied on the object 'metaViews', database 'db', schema 'dbo'.")]),
            SqlFailureKind.Unexpected,
            229
        },
        {
            SqlExceptionFactory.Create([SqlExceptionFactory.Error(
                230,
                14,
                "The SELECT permission was denied on the column 'Name' of the object 'metaViews', database 'db', "
                    + "schema 'dbo'.")]),
            SqlFailureKind.Unexpected,
            230
        },
        { MalformedConnectionStringException(), SqlFailureKind.Unexpected, null },
        { new Exception("boom"), SqlFailureKind.Unexpected, null },
        {
            SqlExceptionFactory.Create([SqlExceptionFactory.Error(0, 11, SevereErrorEnglish, 0)]),
            SqlFailureKind.Unexpected,
            0
        },
        {
            SqlExceptionFactory.Create(
            [
                SqlExceptionFactory.Error(
                    547, 16, "The INSERT statement conflicted with the FOREIGN KEY constraint \"FK_Test\".", 0),
                SqlExceptionFactory.Error(0, 11, SevereErrorEnglish, 0),
                SqlExceptionFactory.Error(0, 11, OperationCancelledEnglish, 0),
            ]),
            SqlFailureKind.Unexpected,
            547
        },
    };

    [Theory]
    [MemberData(nameof(FailuresCoincidingWithCancellation))]
    public void Classify_FailureCoincidingWithCancelledToken_KeepsItsOwnClass(
        Exception exception, SqlFailureKind expectedKind, int? expectedNumber)
    {
        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancelledToken);

        Assert.Equal(new SqlFailure(expectedKind, expectedNumber), failure);
    }

    private static SqlException AttentionAcknowledgement(string severeError, string operationCancelled) =>
        SqlExceptionFactory.Create(
        [
            SqlExceptionFactory.Error(0, 11, severeError, 0),
            SqlExceptionFactory.Error(0, 11, operationCancelled, 0),
        ]);

    // Pool exhaustion while opening: recognised by SqlClient's own pool-timeout message in the effective UI culture.

    // SqlClient 5.2.3's ADP_PooledOpenTimeout text in its English (neutral), German and French resources, and its
    // ADP_NonPooledOpenTimeout text, which is not pool exhaustion.
    private const string PooledOpenTimeoutEnglish =
        "Timeout expired.  The timeout period elapsed prior to obtaining a connection from the pool.  This may have "
        + "occurred because all pooled connections were in use and max pool size was reached.";
    private const string PooledOpenTimeoutGerman =
        "Timeout abgelaufen. Das Zeitlimit ist vor dem Anfordern einer Verbindung aus dem Pool abgelaufen. "
        + "Möglicherweise werden alle Verbindungen im Pool bereits verwendet, und die maximale Poolgröße wurde "
        + "erreicht.";
    private const string PooledOpenTimeoutFrench =
        "Expiration du délai d'attente. Le délai d'attente s'est écoulé avant obtention d'une connexion du pool. "
        + "Ceci est probablement dû au fait que toutes les connexions regroupées sont en cours d'utilisation et que "
        + "la taille maximale du pool a été atteinte.";
    private const string NonPooledOpenTimeoutEnglish =
        "Timeout attempting to open the connection.  The time period elapsed prior to attempting to open the "
        + "connection has been exceeded.  This may have occurred because of too many simultaneous non-pooled "
        + "connection attempts.";

    [Theory]
    [InlineData("en-US", typeof(InvalidOperationException), PooledOpenTimeoutEnglish, true, SqlFailureKind.Unavailable)]
    [InlineData("en-US", typeof(InvalidOperationException), PooledOpenTimeoutEnglish, false, SqlFailureKind.Unexpected)]
    [InlineData("de-DE", typeof(InvalidOperationException), PooledOpenTimeoutGerman, true, SqlFailureKind.Unavailable)]
    [InlineData("de-DE", typeof(InvalidOperationException), PooledOpenTimeoutGerman, false, SqlFailureKind.Unexpected)]
    [InlineData("fr-FR", typeof(InvalidOperationException), PooledOpenTimeoutFrench, true, SqlFailureKind.Unavailable)]
    [InlineData("fr-FR", typeof(InvalidOperationException), PooledOpenTimeoutFrench, false, SqlFailureKind.Unexpected)]
    [InlineData("de-DE", typeof(InvalidOperationException), PooledOpenTimeoutEnglish, true, SqlFailureKind.Unavailable)]
    [InlineData("en-US", typeof(InvalidOperationException), "Invalid operation. The connection is closed.", true,
        SqlFailureKind.Unexpected)]
    [InlineData("en-US", typeof(InvalidOperationException), "Invalid operation. The connection is closed.", false,
        SqlFailureKind.Unexpected)]
    [InlineData("en-US", typeof(InvalidOperationException), "Timeout expired.", true, SqlFailureKind.Unexpected)]
    [InlineData("en-US", typeof(InvalidOperationException), "Timeout expired.", false, SqlFailureKind.Unexpected)]
    [InlineData("en-US", typeof(InvalidOperationException), NonPooledOpenTimeoutEnglish, true,
        SqlFailureKind.Unexpected)]
    [InlineData("en-US", typeof(InvalidOperationException), NonPooledOpenTimeoutEnglish, false,
        SqlFailureKind.Unexpected)]
    [InlineData("en-US", typeof(Exception), PooledOpenTimeoutEnglish, true, SqlFailureKind.Unexpected)]
    public void Classify_PoolTimeout_IsUnavailableOnlyForSqlClientPoolTimeoutWhileOpening(
        string uiCulture, Type exceptionType, string message, bool duringOpen, SqlFailureKind expectedKind)
    {
        using var culture = new UiCultureScope(uiCulture);
        var exception = (Exception)Activator.CreateInstance(exceptionType, message)!;

        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None, duringOpen);

        Assert.Equal(new SqlFailure(expectedKind, null), failure);
    }

    // Managed-network transport failures: primary error number 0 with a fatal class and affirmative transport evidence.

    // Messages in the form SqlClient 5.2.3's managed network layer raised them on Linux, each as a single error with
    // number 0, class 20 and state 0. The named-pipe open failure came with a TimeoutException as its inner
    // exception, and the unknown instance with a SocketException; the malformed connection strings and the LocalDB
    // name came with none.
    private const string RefusedOrUnreachableMessage =
        "A network-related or instance-specific error occurred while establishing a connection to SQL Server. The "
        + "server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is "
        + "configured to allow remote connections. (provider: TCP Provider, error: 40 - Could not open a connection to "
        + "SQL Server)";

    private const string GermanRefusedMessage =
        "Netzwerkbezogener oder instanzspezifischer Fehler beim Herstellen einer Verbindung mit SQL Server. Der Server "
        + "wurde nicht gefunden, oder auf ihn kann nicht zugegriffen werden. Überprüfen Sie, ob der Instanzname richtig "
        + "ist und ob SQL Server Remoteverbindungen zulässt. (provider: TCP-Anbieter, error: 40 - Es konnte keine "
        + "Verbindung zu SQL Server hergestellt werden)";

    private const string ResetMidCommandMessage =
        "A transport-level error has occurred when receiving results from the server. (provider: TCP Provider, "
        + "error: 2 - Connection was terminated)";

    private const string ClosedDuringPreloginMessage =
        "A connection was successfully established with the server, but then an error occurred during the pre-login "
        + "handshake. (provider: TCP Provider, error: 0 - Success)";

    private const string UnresolvableHostMessage =
        "A network-related or instance-specific error occurred while establishing a connection to SQL Server. The "
        + "server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is "
        + "configured to allow remote connections. (provider: TCP Provider, error: 35 - An internal exception was "
        + "caught)";

    private const string WriteOnResetConnectionMessage =
        "A transport-level error has occurred when sending the request to the server. (provider: TCP Provider, "
        + "error: 35 - An internal exception was caught)";

    private const string NamedPipeOpenFailedMessage =
        "A network-related or instance-specific error occurred while establishing a connection to SQL Server. The "
        + "server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is "
        + "configured to allow remote connections. (provider: Named Pipes Provider, error: 40 - Could not open a "
        + "connection to SQL Server)";

    private const string InstanceNotFoundMessage =
        "A network-related or instance-specific error occurred while establishing a connection to SQL Server. The "
        + "server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is "
        + "configured to allow remote connections. (provider: TCP Provider, error: 26 - Error Locating "
        + "Server/Instance Specified)";

    private const string MalformedTcpConnectionStringMessage =
        "A network-related or instance-specific error occurred while establishing a connection to SQL Server. The "
        + "server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is "
        + "configured to allow remote connections. (provider: TCP Provider, error: 25 - Connection string is not "
        + "valid)";

    private const string MalformedNamedPipeConnectionStringMessage =
        "A network-related or instance-specific error occurred while establishing a connection to SQL Server. The "
        + "server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is "
        + "configured to allow remote connections. (provider: Named Pipes Provider, error: 25 - Connection string is "
        + "not valid)";

    private const string LocalDbNameOnManagedNetworkMessage =
        "A network-related or instance-specific error occurred while establishing a connection to SQL Server. The "
        + "server was not found or was not accessible. Verify that the instance name is correct and that SQL Server is "
        + "configured to allow remote connections. (provider: SQL Network Interfaces, error: 26 - Error Locating "
        + "Server/Instance Specified)";

    // Connection-failure codes SqlClient 5.2.3's managed network layer raises without an exception on a broken
    // connection, composed as TdsParser.ProcessSNIError formats them from SqlClient's own resource texts: code 11
    // (ConnTimeoutError) and code 19 (ConnNotUsableError, from the MARS layer's SMux provider).
    private const string ReceiveTimeoutMessage =
        "A transport-level error has occurred when receiving results from the server. (provider: TCP Provider, "
        + "error: 11 - Timeout error)";

    private const string MarsConnectionNotUsableMessage =
        "A transport-level error has occurred when sending the request to the server. (provider: SMux Provider, "
        + "error: 19 - Physical connection is not usable)";

    // Fatal number-0 errors SqlClient 5.2.3's TdsParser raises without its network layer: DONE_SRVERROR,
    // UserInstanceFailure and PreloginError.
    private const string ServerSevereErrorMessage =
        "A severe error occurred on the current command.  The results, if any, should be discarded.";

    private const string UserInstanceFailureMessage =
        "A user instance was requested in the connection string but the server specified does not support this "
        + "option.";

    private const string PreloginErrorMessage =
        "A connection was successfully established with the server, but then an error occurred during the pre-login "
        + "handshake.";

    [Theory]
    [InlineData(RefusedOrUnreachableMessage)]
    [InlineData(ResetMidCommandMessage)]
    [InlineData(ClosedDuringPreloginMessage)]
    [InlineData(GermanRefusedMessage)]
    [InlineData(ReceiveTimeoutMessage)]
    [InlineData(MarsConnectionNotUsableMessage)]
    public void Classify_ManagedTransportErrorWithNetworkInterfaceText_IsUnavailableWithNumberZero(string message)
    {
        SqlException exception = SqlExceptionFactory.Create([SqlExceptionFactory.Error(0, 20, message, state: 0)]);

        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None);

        Assert.Equal(new SqlFailure(SqlFailureKind.Unavailable, 0), failure);
    }

    /// <summary>
    /// Socket, I/O and timeout causes the managed network layer attaches to a transport failure: message and inner
    /// exception.
    /// </summary>
    public static TheoryData<string, Exception> TransportCauses => new()
    {
        { UnresolvableHostMessage, new SocketException((int)SocketError.HostNotFound) },
        {
            WriteOnResetConnectionMessage,
            new IOException(
                "Unable to write data to the transport connection: Broken pipe.",
                new SocketException((int)SocketError.Shutdown))
        },
        { InstanceNotFoundMessage, new SocketException((int)SocketError.Success) },
        { NamedPipeOpenFailedMessage, new TimeoutException() },
    };

    [Theory]
    [MemberData(nameof(TransportCauses))]
    public void Classify_ManagedTransportErrorWithTransportCause_IsUnavailableWithNumberZero(
        string message, Exception cause)
    {
        SqlException exception = SqlExceptionFactory.Create([SqlExceptionFactory.Error(0, 20, message, state: 0)], cause);

        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None);

        Assert.Equal(new SqlFailure(SqlFailureKind.Unavailable, 0), failure);
    }

    [Theory]
    [InlineData(0, 20, ServerSevereErrorMessage)]
    [InlineData(0, 20, UserInstanceFailureMessage)]
    [InlineData(0, 20, PreloginErrorMessage)]
    [InlineData(0, 11, RefusedOrUnreachableMessage)]
    [InlineData(50000, 20, RefusedOrUnreachableMessage)]
    [InlineData(0, 20, MalformedTcpConnectionStringMessage)]
    [InlineData(0, 20, MalformedNamedPipeConnectionStringMessage)]
    [InlineData(0, 20, LocalDbNameOnManagedNetworkMessage)]
    public void Classify_NumberZeroOrFatalErrorWithoutTransportEvidence_IsUnexpectedWithPrimaryNumber(
        int number, int errorClass, string message)
    {
        SqlException exception = SqlExceptionFactory.Create(
            [SqlExceptionFactory.Error(number, (byte)errorClass, message, state: 0)]);

        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None);

        Assert.Equal(new SqlFailure(SqlFailureKind.Unexpected, number), failure);
    }

    /// <summary>
    /// Number-0 fatal errors whose inner exception is no transport evidence: a cause that is not a socket, I/O or
    /// timeout cause under the network layer's text, and a socket cause without that text. Message and inner exception.
    /// </summary>
    public static TheoryData<string, Exception> CausesWithoutTransportEvidence => new()
    {
        { RefusedOrUnreachableMessage, new AuthenticationException("x") },
        { WriteOnResetConnectionMessage, new ObjectDisposedException(typeof(Socket).FullName) },
        { ServerSevereErrorMessage, new SocketException((int)SocketError.ConnectionReset) },
    };

    [Theory]
    [MemberData(nameof(CausesWithoutTransportEvidence))]
    public void Classify_NumberZeroFatalErrorWithCauseButNoTransportEvidence_IsUnexpected(
        string message, Exception cause)
    {
        SqlException exception = SqlExceptionFactory.Create([SqlExceptionFactory.Error(0, 20, message, state: 0)], cause);

        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None);

        Assert.Equal(new SqlFailure(SqlFailureKind.Unexpected, 0), failure);
    }

    [Fact]
    public void Classify_KilledSessionWithTrailingSevereError_IsUnexpectedWithPrimaryNumber()
    {
        SqlException exception = SqlExceptionFactory.Create(
        [
            SqlExceptionFactory.Error(596, 21, "Cannot continue the execution because the session is in the kill state."),
            SqlExceptionFactory.Error(0, 20, ServerSevereErrorMessage, state: 0),
        ]);

        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None);

        Assert.Equal(new SqlFailure(SqlFailureKind.Unexpected, 596), failure);
    }

    [Fact]
    public void Classify_SqlExceptionWithoutErrors_IsUnexpectedWithNoNumber()
    {
        SqlException exception = SqlExceptionFactory.CreateWithoutErrors();

        SqlFailure failure = SqlErrorClassifier.Classify(exception, CancellationToken.None);

        // SqlClient reports Number 0 for an empty Errors collection; the classifier reports no number instead.
        Assert.Equal(0, exception.Number);
        Assert.Equal(new SqlFailure(SqlFailureKind.Unexpected, null), failure);
    }

    // Write outcome: after DML, before or during the commit call, only a completed rollback or the server's
    // deadlock-victim abort (1205) proves nothing was kept; a failed or unacknowledged commit without it is unknown.

    /// <summary>
    /// The write-outcome table, stage by stage, with and without evidence that the transaction was not kept: stage,
    /// whether the rollback completed, the original failure's classification and the expected outcome.
    /// </summary>
    public static TheoryData<WriteStage, bool, SqlFailure, WriteOutcome> WriteOutcomeCases => new()
    {
        // No DML was sent, whatever the failure and the rollback.
        { WriteStage.BeforeDml, false, new SqlFailure(SqlFailureKind.Unexpected, 547), WriteOutcome.NotWritten },
        { WriteStage.BeforeDml, true, new SqlFailure(SqlFailureKind.Unexpected, 547), WriteOutcome.NotWritten },
        { WriteStage.BeforeDml, false, new SqlFailure(SqlFailureKind.Unavailable, 53), WriteOutcome.NotWritten },
        { WriteStage.BeforeDml, true, new SqlFailure(SqlFailureKind.Unavailable, -2), WriteOutcome.NotWritten },

        // DML was sent and Commit was not called.
        { WriteStage.DmlSent, true, new SqlFailure(SqlFailureKind.Unavailable, -2), WriteOutcome.NotWritten },
        { WriteStage.DmlSent, true, new SqlFailure(SqlFailureKind.Unexpected, 547), WriteOutcome.NotWritten },
        { WriteStage.DmlSent, false, new SqlFailure(SqlFailureKind.Unavailable, 1205), WriteOutcome.NotWritten },
        { WriteStage.DmlSent, true, new SqlFailure(SqlFailureKind.Unavailable, 1205), WriteOutcome.NotWritten },
        { WriteStage.DmlSent, false, new SqlFailure(SqlFailureKind.Unavailable, -2), WriteOutcome.Unknown },
        { WriteStage.DmlSent, false, new SqlFailure(SqlFailureKind.Unexpected, 547), WriteOutcome.Unknown },

        // Commit was called and threw or did not return: a commit-time deadlock and a completed rollback prove nothing
        // was kept; a lost acknowledgement (broken connection, timeout, managed transport failure) or any other
        // failure without that evidence does not.
        { WriteStage.Committing, false, new SqlFailure(SqlFailureKind.Unavailable, 1205), WriteOutcome.NotWritten },
        { WriteStage.Committing, true, new SqlFailure(SqlFailureKind.Unavailable, 1205), WriteOutcome.NotWritten },
        { WriteStage.Committing, true, new SqlFailure(SqlFailureKind.Unavailable, -2), WriteOutcome.NotWritten },
        { WriteStage.Committing, true, new SqlFailure(SqlFailureKind.Unexpected, 547), WriteOutcome.NotWritten },
        { WriteStage.Committing, false, new SqlFailure(SqlFailureKind.Unavailable, 10054), WriteOutcome.Unknown },
        { WriteStage.Committing, false, new SqlFailure(SqlFailureKind.Unavailable, -2), WriteOutcome.Unknown },
        { WriteStage.Committing, false, new SqlFailure(SqlFailureKind.Unavailable, 0), WriteOutcome.Unknown },
        { WriteStage.Committing, false, new SqlFailure(SqlFailureKind.Unexpected, 547), WriteOutcome.Unknown },
        { WriteStage.Committing, false, new SqlFailure(SqlFailureKind.Unexpected, null), WriteOutcome.Unknown },

        // An undefined stage proves nothing, whatever the evidence.
        { (WriteStage)99, true, new SqlFailure(SqlFailureKind.Unavailable, -2), WriteOutcome.Unknown },
        { (WriteStage)99, false, new SqlFailure(SqlFailureKind.Unavailable, 1205), WriteOutcome.Unknown },
    };

    [Theory]
    [MemberData(nameof(WriteOutcomeCases))]
    public void ResolveWriteOutcome_StageAndEvidence_GiveTheTableOutcome(
        WriteStage stage, bool rollbackCompleted, SqlFailure failure, WriteOutcome expectedOutcome)
    {
        WriteOutcome outcome = SqlErrorClassifier.ResolveWriteOutcome(stage, rollbackCompleted, failure);

        Assert.Equal(expectedOutcome, outcome);
    }

    [Fact]
    public void ResolveWriteOutcome_CommitTimeDeadlock_IsNotWrittenAndKeepsUnavailableClassification()
    {
        SqlException deadlock = SqlExceptionFactory.Create(1205);

        SqlFailure failure = SqlErrorClassifier.Classify(deadlock, CancellationToken.None);
        WriteOutcome outcome =
            SqlErrorClassifier.ResolveWriteOutcome(WriteStage.Committing, rollbackCompleted: false, failure);
        Exception raised = SqlErrorClassifier.ToDataStoreException(failure, "InsertRecord", outcome, deadlock);

        Assert.Equal(new SqlFailure(SqlFailureKind.Unavailable, 1205), failure);
        Assert.Equal(WriteOutcome.NotWritten, outcome);
        DataStoreUnavailableException unavailable = Assert.IsType<DataStoreUnavailableException>(raised);
        Assert.Equal(WriteOutcome.NotWritten, unavailable.Outcome);
        Assert.Equal(1205, unavailable.SqlErrorNumber);
        Assert.Same(deadlock, unavailable.InnerException);
    }

    // Store exceptions: the class decides the type, and the message is fixed text, never the database's message. Unlike
    // legacy Epi Info's SqlException, which re-exposed the inner message and kept the SQL statement, nothing of the
    // original failure reaches Message; the original is attached only as the inner exception.

    private const string SqlTextSentinel = "SELECT secret FROM [CaseReport] WHERE LastName = 'T-sentinel'";

    [Fact]
    public void ToDataStoreException_Unavailable_IsUnavailableExceptionWithFixedMessage()
    {
        SqlException inner = SqlExceptionFactory.Create(-2, SqlTextSentinel);

        Exception raised = SqlErrorClassifier.ToDataStoreException(
            new SqlFailure(SqlFailureKind.Unavailable, -2), "Insert", WriteOutcome.Unknown, inner);

        DataStoreUnavailableException unavailable = Assert.IsType<DataStoreUnavailableException>(raised);
        Assert.Equal("Insert", unavailable.Operation);
        Assert.Equal(WriteOutcome.Unknown, unavailable.Outcome);
        Assert.Equal(-2, unavailable.SqlErrorNumber);
        Assert.Same(inner, unavailable.InnerException);
        AssertRedacted(DataStoreUnavailableException.FixedMessage, unavailable.Message, inner);
    }

    [Fact]
    public void ToDataStoreException_Unexpected_IsWriteExceptionWithFixedMessage()
    {
        SqlException inner = SqlExceptionFactory.Create(547, SqlTextSentinel);

        Exception raised = SqlErrorClassifier.ToDataStoreException(
            new SqlFailure(SqlFailureKind.Unexpected, 547), "Update", WriteOutcome.NotWritten, inner);

        DataStoreWriteException unexpected = Assert.IsType<DataStoreWriteException>(raised);
        Assert.Equal("Update", unexpected.Operation);
        Assert.Equal(WriteOutcome.NotWritten, unexpected.Outcome);
        Assert.Equal(547, unexpected.SqlErrorNumber);
        Assert.Same(inner, unexpected.InnerException);
        AssertRedacted(DataStoreWriteException.FixedMessage, unexpected.Message, inner);
    }

    [Fact]
    public void ToDataStoreException_Cancelled_IsOperationCanceledWithFixedMessage()
    {
        SqlException inner = SqlExceptionFactory.Create([SqlExceptionFactory.Error(0, 11, SqlTextSentinel, 0)]);

        Exception raised = SqlErrorClassifier.ToDataStoreException(
            new SqlFailure(SqlFailureKind.Cancelled, 0), "Read", WriteOutcome.NotWritten, inner);

        OperationCanceledException cancelled = Assert.IsType<OperationCanceledException>(raised);
        Assert.Same(inner, cancelled.InnerException);
        AssertRedacted(SqlErrorClassifier.CancelledMessage, cancelled.Message, inner);
    }

    [Theory]
    [InlineData(SqlFailureKind.Unavailable)]
    [InlineData(SqlFailureKind.Unexpected)]
    public void ToDataStoreException_BlankOperationOrUndefinedOutcome_IsRejected(SqlFailureKind kind)
    {
        var failure = new SqlFailure(kind, 547);
        SqlException inner = SqlExceptionFactory.Create(547);

        Assert.Throws<ArgumentException>(
            () => SqlErrorClassifier.ToDataStoreException(failure, " ", WriteOutcome.NotWritten, inner));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SqlErrorClassifier.ToDataStoreException(failure, "Update", (WriteOutcome)99, inner));
    }

    // Failure metadata: the exception a failed write raises keeps the original failure's classification, and carries
    // beside it in Data only the classified SQL error number of a cancellation and the type name and SQL error number
    // of a rollback that failed, never a message.

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void CancelledFailure_IsTheFixedCancellationKeepingItsSqlErrorNumber(int? number)
    {
        using var culture = new UiCultureScope("en-US");
        SqlException inner = AttentionAcknowledgement(SevereErrorEnglish, OperationCancelledEnglish);
        var failure = new SqlFailure(SqlFailureKind.Cancelled, number);

        Exception fromStoreException =
            SqlErrorClassifier.ToDataStoreException(failure, "Read", WriteOutcome.NotWritten, inner);
        Exception fromCancellation = SqlErrorClassifier.ToCancellation(failure, inner, CancelledToken);

        foreach (Exception raised in new[] { fromStoreException, fromCancellation })
        {
            OperationCanceledException cancelled = Assert.IsType<OperationCanceledException>(raised);
            Assert.Same(inner, cancelled.InnerException);
            Assert.Equal(SqlErrorClassifier.CancelledMessage, cancelled.Message);
            AssertFailureData(cancelled, number, expectedRollbackType: null, expectedRollbackNumber: null);
        }

        Assert.Equal(CancelledToken, Assert.IsType<OperationCanceledException>(fromCancellation).CancellationToken);
    }

    /// <summary>
    /// An OperationCanceledException a write raised under the cancelled token, with the rollback failure that
    /// followed it: the original, the rollback's exception, and the type name and SQL error number recorded for it.
    /// </summary>
    public static TheoryData<Exception, Exception, string, int?> OriginalCancellationsWithFailedRollback => new()
    {
        {
            new OperationCanceledException(CancelledToken),
            SqlExceptionFactory.Create(10054, SqlTextSentinel),
            nameof(SqlException),
            10054
        },
        {
            new TaskCanceledException("x", null, CancelledToken),
            SqlExceptionFactory.CreateWithoutErrors(SqlTextSentinel),
            nameof(SqlException),
            null
        },
        {
            new OperationCanceledException(CancelledToken),
            new InvalidOperationException(SqlTextSentinel),
            nameof(InvalidOperationException),
            null
        },
    };

    [Theory]
    [MemberData(nameof(OriginalCancellationsWithFailedRollback))]
    public void BuildFailure_OriginalCancellation_IsRaisedItselfCarryingTheRollbackFailure(
        Exception original,
        Exception rollbackError,
        string expectedRollbackType,
        int? expectedRollbackNumber)
    {
        Exception raised = SqlRecordRepository.BuildFailure(
            original,
            RecordOperations.Update,
            WriteStage.DmlSent,
            rollbackCompleted: false,
            rollbackError,
            CancelledToken);

        Assert.Same(original, raised);
        AssertFailureData(raised, expectedNumber: null, expectedRollbackType, expectedRollbackNumber);
    }

    /// <summary>
    /// SqlClient's acknowledgement of a cancelled command under the cancelled token, with no rollback failure and with
    /// one: the rollback's exception, or none, and the type name and SQL error number recorded for it.
    /// </summary>
    public static TheoryData<Exception?, string?, int?> AcknowledgedCancellationRollbacks => new()
    {
        { null, null, null },
        { SqlExceptionFactory.Create(10054, SqlTextSentinel), nameof(SqlException), 10054 },
        { new InvalidOperationException(SqlTextSentinel), nameof(InvalidOperationException), null },
    };

    [Theory]
    [MemberData(nameof(AcknowledgedCancellationRollbacks))]
    public void BuildFailure_CancellationAcknowledgement_IsANewCancellationKeepingItsNumberAndTheRollbackFailure(
        Exception? rollbackError,
        string? expectedRollbackType,
        int? expectedRollbackNumber)
    {
        using var culture = new UiCultureScope("en-US");
        SqlException acknowledgement = AttentionAcknowledgement(SevereErrorEnglish, OperationCancelledEnglish);

        Exception raised = SqlRecordRepository.BuildFailure(
            acknowledgement,
            RecordOperations.Update,
            WriteStage.DmlSent,
            rollbackCompleted: false,
            rollbackError,
            CancelledToken);

        OperationCanceledException cancelled = Assert.IsType<OperationCanceledException>(raised);
        Assert.Same(acknowledgement, cancelled.InnerException);
        Assert.Equal(SqlErrorClassifier.CancelledMessage, cancelled.Message);
        Assert.Equal(CancelledToken, cancelled.CancellationToken);
        AssertFailureData(cancelled, expectedNumber: 0, expectedRollbackType, expectedRollbackNumber);
    }

    /// <summary>
    /// Classified write failures with a rollback that failed: the original failure, the stage, the rollback's
    /// exception, then the expected store exception type, outcome and SQL error number, and the type name and SQL error
    /// number recorded for the rollback. The last case is the rollback of a write that sent no DML failing on its own,
    /// so the rollback's exception is the original failure.
    /// </summary>
    public static TheoryData<Exception, WriteStage, Exception, Type, WriteOutcome, int?, string, int?>
        ClassifiedFailuresWithFailedRollback
    {
        get
        {
            var noDmlRollbackFailure = new InvalidOperationException(SqlTextSentinel);
            return new()
            {
                {
                    SqlExceptionFactory.Create(547, SqlTextSentinel), WriteStage.DmlSent,
                    SqlExceptionFactory.Create(10054, SqlTextSentinel), typeof(DataStoreWriteException),
                    WriteOutcome.Unknown, 547, nameof(SqlException), 10054
                },
                {
                    SqlExceptionFactory.Create(547, SqlTextSentinel), WriteStage.DmlSent,
                    SqlExceptionFactory.CreateWithoutErrors(SqlTextSentinel), typeof(DataStoreWriteException),
                    WriteOutcome.Unknown, 547, nameof(SqlException), null
                },
                {
                    SqlExceptionFactory.Create(-2, SqlTextSentinel), WriteStage.BeforeDml,
                    new InvalidOperationException(SqlTextSentinel), typeof(DataStoreUnavailableException),
                    WriteOutcome.NotWritten, -2, nameof(InvalidOperationException), null
                },
                {
                    noDmlRollbackFailure, WriteStage.BeforeDml, noDmlRollbackFailure, typeof(DataStoreWriteException),
                    WriteOutcome.NotWritten, null, nameof(InvalidOperationException), null
                },
            };
        }
    }

    [Theory]
    [MemberData(nameof(ClassifiedFailuresWithFailedRollback))]
    public void BuildFailure_ClassifiedFailure_KeepsItsClassificationAndCarriesTheRollbackFailure(
        Exception original,
        WriteStage stage,
        Exception rollbackError,
        Type expectedType,
        WriteOutcome expectedOutcome,
        int? expectedNumber,
        string expectedRollbackType,
        int? expectedRollbackNumber)
    {
        Exception raised = SqlRecordRepository.BuildFailure(
            original,
            RecordOperations.Update,
            stage,
            rollbackCompleted: false,
            rollbackError,
            CancellationToken.None);

        Assert.IsType(expectedType, raised);
        Assert.Same(original, raised.InnerException);
        Assert.Equal((expectedOutcome, expectedNumber), StoreOutcome(raised));
        AssertFailureData(raised, expectedNumber: null, expectedRollbackType, expectedRollbackNumber);
    }

    // The outcome and SQL error number of a raised store exception.
    private static (WriteOutcome Outcome, int? SqlErrorNumber) StoreOutcome(Exception raised) => raised switch
    {
        DataStoreUnavailableException unavailable => (unavailable.Outcome, unavailable.SqlErrorNumber),
        DataStoreWriteException failed => (failed.Outcome, failed.SqlErrorNumber),
        _ => throw new InvalidOperationException("The raised failure is not a store exception."),
    };

    // The raised exception's Data holds exactly the expected entries: a cancellation's SQL error number, and a failed
    // rollback's type name and SQL error number, each only when known, and no value carrying a message.
    private static void AssertFailureData(
        Exception raised,
        int? expectedNumber,
        string? expectedRollbackType,
        int? expectedRollbackNumber)
    {
        var expected = new Dictionary<object, object?>();
        if (expectedNumber is int number)
        {
            expected.Add(SqlErrorClassifier.SqlErrorNumberKey, number);
        }

        if (expectedRollbackType is not null)
        {
            expected.Add(SqlErrorClassifier.RollbackExceptionTypeKey, expectedRollbackType);
        }

        if (expectedRollbackNumber is int rollbackNumber)
        {
            expected.Add(SqlErrorClassifier.RollbackSqlErrorNumberKey, rollbackNumber);
        }

        Dictionary<object, object?> actual = raised.Data
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        Assert.Equal(expected, actual);
        Assert.DoesNotContain(
            actual.Values,
            value => value is string text && text.Contains(SqlTextSentinel, StringComparison.Ordinal));
    }

    // Message is exactly the fixed text, while the inner exception still carries the sentinel the outer one must not.
    private static void AssertRedacted(string expectedMessage, string message, Exception inner)
    {
        Assert.Contains(SqlTextSentinel, inner.Message, StringComparison.Ordinal);
        Assert.Equal(expectedMessage, message);
        Assert.DoesNotContain(SqlTextSentinel, message, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", message, StringComparison.OrdinalIgnoreCase);
    }

    // Connection factory and store: an absent or blank connection string is unavailable before any connection object
    // exists, and a malformed one is a configuration fault (500). Neither reaches the network.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task OpenAsync_AbsentOrBlankConnectionString_IsUnavailableAndNotWritten(string? connectionString)
    {
        var factory = new SqlConnectionFactory(new DatabaseOptions(connectionString));

        DataStoreUnavailableException failure = await Assert.ThrowsAsync<DataStoreUnavailableException>(
            () => factory.OpenAsync("Ping", CancellationToken.None));

        Assert.Equal("Ping", failure.Operation);
        Assert.Equal(WriteOutcome.NotWritten, failure.Outcome);
        Assert.Null(failure.SqlErrorNumber);
        // No inner exception: the absence is detected before SqlClient is asked to parse or open anything.
        Assert.Null(failure.InnerException);
        Assert.Equal(DataStoreUnavailableException.FixedMessage, failure.Message);
    }

    [Fact]
    public async Task PingAsync_AbsentConnectionString_ThrowsUnavailableForThePing()
    {
        var store = new SqlServerDataStore(new DatabaseOptions(null), NullSqlCommandObserver.Instance);

        DataStoreUnavailableException failure = await Assert.ThrowsAsync<DataStoreUnavailableException>(
            () => store.PingAsync(CancellationToken.None));

        Assert.Equal(nameof(IDataStore.PingAsync), failure.Operation);
        Assert.Equal(WriteOutcome.NotWritten, failure.Outcome);
        Assert.Null(failure.SqlErrorNumber);
        Assert.Null(failure.InnerException);
        Assert.Equal(DataStoreUnavailableException.FixedMessage, failure.Message);
    }

    // Every way SqlClient rejects a connection string, each with the exception SqlConnection raises for it, the same
    // behind the pool default as alone: no key-value syntax; a value that does not convert; an integer beyond Int32; a
    // value outside the keyword's set, under either spelling of Pool Blocking Period; settings that contradict each
    // other; and a keyword this platform does not support. A string holding no setting at all is refused when it is
    // opened, before any server is contacted.
    [Theory]
    [InlineData(MalformedConnectionString, typeof(ArgumentException))]
    [InlineData("Server=127.0.0.1,1;Connect Timeout=abc", typeof(ArgumentException))]
    [InlineData("Server=127.0.0.1,1;Pooling=maybe", typeof(ArgumentException))]
    [InlineData("Server=127.0.0.1,1;Connect Timeout=99999999999", typeof(ArgumentException))]
    [InlineData("Server=127.0.0.1,1;Pool Blocking Period=Sometimes", typeof(ArgumentException))]
    [InlineData("Server=127.0.0.1,1;PoolBlockingPeriod=Bogus", typeof(ArgumentException))]
    [InlineData("Server=127.0.0.1,1;Type System Version=Unknown", typeof(ArgumentException))]
    [InlineData("Server=127.0.0.1,1;Min Pool Size=5;Max Pool Size=1", typeof(ArgumentException))]
    [InlineData("Server=127.0.0.1,1;Network Library=dbmssocn", typeof(NotSupportedException))]
    [InlineData(";", typeof(InvalidOperationException))]
    [InlineData(" ;\t; ", typeof(InvalidOperationException))]
    public async Task OpenAsync_MalformedConnectionString_IsUnexpectedWriteExceptionAndNotWritten(
        string connectionString,
        Type expectedInnerType)
    {
        var factory = new SqlConnectionFactory(new DatabaseOptions(connectionString));

        DataStoreWriteException failure = await Assert.ThrowsAsync<DataStoreWriteException>(
            () => factory.OpenAsync("Ping", CancellationToken.None));

        Assert.Equal("Ping", failure.Operation);
        Assert.Equal(WriteOutcome.NotWritten, failure.Outcome);
        Assert.Null(failure.SqlErrorNumber);
        Assert.IsType(expectedInnerType, failure.InnerException);
        Assert.Equal(DataStoreWriteException.FixedMessage, failure.Message);
    }

    // The password of the connection strings the effective-string cases parse; no login uses it, and none is opened.
    private const string PoolPasswordSentinel = "Sentinel-Pool-5d8e1b";

    // The effective connection string disables the pool blocking period unless the configured string sets one, under
    // either spelling and in any case, and keeps every other setting as given, the password included: the configured
    // string follows the default verbatim, so SqlClient reads its own setting last.
    [Theory]
    [InlineData(
        "Server=127.0.0.1,1;Database=Fixture;User Id=sa;Password=" + PoolPasswordSentinel +
        ";TrustServerCertificate=True;Connect Timeout=2;Application Name=Fixture",
        PoolBlockingPeriod.NeverBlock)]
    [InlineData(
        "Server=127.0.0.1,1;User Id=sa;Password=" + PoolPasswordSentinel + ";PoolBlockingPeriod=AlwaysBlock",
        PoolBlockingPeriod.AlwaysBlock)]
    [InlineData(
        "Server=127.0.0.1,1;User Id=sa;Password=" + PoolPasswordSentinel + ";Pool Blocking Period=Auto",
        PoolBlockingPeriod.Auto)]
    [InlineData(
        "pool blocking period=alwaysblock;Server=127.0.0.1,1;User Id=sa;Password=" + PoolPasswordSentinel,
        PoolBlockingPeriod.AlwaysBlock)]
    public void EffectiveConnectionString_BlocksOnlyWhenTheStringSaysSoAndKeepsEveryOtherSetting(
        string configured,
        PoolBlockingPeriod expected)
    {
        string effectiveString = SqlConnectionFactory.EffectiveConnectionString(configured);
        var effective = new SqlConnectionStringBuilder(effectiveString);
        var expectedSettings = new SqlConnectionStringBuilder(configured) { PoolBlockingPeriod = expected };

        Assert.EndsWith(configured, effectiveString, StringComparison.Ordinal);
        Assert.Equal(expected, effective.PoolBlockingPeriod);
        Assert.True(effective.ShouldSerialize("Pool Blocking Period"), "The effective string leaves the setting out.");
        Assert.True(
            expectedSettings.EquivalentTo(effective),
            "The effective string adds, drops or changes a setting other than Pool Blocking Period.");
    }

    // A failed open is classified before the unopened connection is released, so a release that throws neither
    // replaces the open failure nor leaves it unclassified: nothing was sent, and the outcome stays NotWritten.

    /// <summary>
    /// Open failures, each with whether the request token is cancelled, the type it is reported as, whether that is the
    /// original instance itself rather than an exception wrapping it, and the outcome and SQL error number the reported
    /// store exception carries (both null for a cancellation).
    /// </summary>
    public static TheoryData<Exception, bool, Type, bool, WriteOutcome?, int?> OpenFailureCases => new()
    {
        {
            SqlExceptionFactory.Create(53), false, typeof(DataStoreUnavailableException), false,
            WriteOutcome.NotWritten, 53
        },
        {
            SqlExceptionFactory.Create(18456), false, typeof(DataStoreUnavailableException), false,
            WriteOutcome.NotWritten, 18456
        },
        {
            new InvalidOperationException(PooledOpenTimeoutEnglish), false, typeof(DataStoreUnavailableException),
            false, WriteOutcome.NotWritten, null
        },
        {
            SqlExceptionFactory.Create([SqlExceptionFactory.Error(547, 16, "constraint")]), false,
            typeof(DataStoreWriteException), false, WriteOutcome.NotWritten, 547
        },
        {
            new OperationCanceledException(CancelledToken), true, typeof(OperationCanceledException), true, null, null
        },
        {
            AttentionAcknowledgement(SevereErrorEnglish, OperationCancelledEnglish), true,
            typeof(OperationCanceledException), false, null, null
        },
    };

    [Theory]
    [MemberData(nameof(OpenFailureCases))]
    public async Task OpenFailureAsync_ReleaseThrows_ReportsTheClassifiedOpenFailure(
        Exception openFailure,
        bool tokenCancelled,
        Type expectedType,
        bool expectedOriginal,
        WriteOutcome? expectedOutcome,
        int? expectedNumber)
    {
        var connection = new ThrowingReleaseConnection();
        CancellationToken token = tokenCancelled ? CancelledToken : CancellationToken.None;

        Exception raised = await SqlConnectionFactory.OpenFailureAsync(connection, "InsertAsync", openFailure, token);

        // The release was attempted, and its failure surfaced nowhere in what is reported.
        Assert.Equal(1, connection.ReleaseCalls);
        for (Exception? reported = raised; reported is not null; reported = reported.InnerException)
        {
            Assert.NotSame(connection.ReleaseFailure, reported);
        }

        Assert.IsType(expectedType, raised);
        Assert.Same(openFailure, expectedOriginal ? raised : raised.InnerException);
        (string? operation, WriteOutcome? outcome, int? number) = raised switch
        {
            DataStoreUnavailableException unavailable =>
                (unavailable.Operation, unavailable.Outcome, unavailable.SqlErrorNumber),
            DataStoreWriteException failed => (failed.Operation, failed.Outcome, failed.SqlErrorNumber),
            _ => ((string?)null, (WriteOutcome?)null, (int?)null),
        };
        Assert.Equal(expectedOutcome is null ? null : "InsertAsync", operation);
        Assert.Equal(expectedOutcome, outcome);
        Assert.Equal(expectedNumber, number);
    }

    /// <summary>A connection stand-in whose release is counted and always fails.</summary>
    private sealed class ThrowingReleaseConnection : IAsyncDisposable
    {
        public InvalidOperationException ReleaseFailure { get; } = new("release failed");

        public int ReleaseCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            ReleaseCalls++;
            throw ReleaseFailure;
        }
    }

    /// <summary>
    /// A statement a local guard of <see cref="SqlExecution"/> refuses is never sent, so its dispatch callback is not
    /// called and the observer hears nothing, for both forms a write sends: the state guard (a connection never
    /// opened) and an argument guard (a timeout below one second). The connection is only constructed, from a
    /// connection string naming no credential, and never opened, so nothing reaches a network.
    /// </summary>
    [Theory]
    [InlineData(false, 30, typeof(InvalidOperationException))]
    [InlineData(false, 0, typeof(ArgumentOutOfRangeException))]
    [InlineData(true, 30, typeof(InvalidOperationException))]
    [InlineData(true, 0, typeof(ArgumentOutOfRangeException))]
    public async Task Execute_GuardRefusesTheStatement_NeverCallsTheDispatchCallback(
        bool scalar,
        int commandTimeoutSeconds,
        Type expectedType)
    {
        await using var connection = new SqlConnection("Data Source=127.0.0.1,1;Initial Catalog=NeverOpened");
        var recorder = new SqlCommandRecorder();
        var statement = new SqlStatement(
            "UPDATE [GuardFixture] SET [Value] = @p0",
            [new SqlParameterValue("@p0", DbType.Int32, 1, Size: null)]);
        var dispatches = 0;
        void OnDispatch() => dispatches++;

        Task execute = scalar
            ? SqlExecution.ExecuteScalarAsync<int>(
                connection,
                transaction: null,
                RecordOperations.Update,
                statement,
                commandTimeoutSeconds,
                recorder,
                CancellationToken.None,
                OnDispatch)
            : SqlExecution.ExecuteAsync(
                connection,
                transaction: null,
                RecordOperations.Update,
                statement,
                commandTimeoutSeconds,
                recorder,
                CancellationToken.None,
                OnDispatch);
        Exception refused = await Assert.ThrowsAnyAsync<Exception>(() => execute);

        Assert.IsType(expectedType, refused);
        Assert.Equal(0, dispatches);
        Assert.Empty(recorder.Commands);
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    /// <summary>Sets <see cref="CultureInfo.CurrentUICulture"/> for one test and restores it on disposal.</summary>
    private sealed class UiCultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentUICulture;

        public UiCultureScope(string name) => CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentUICulture = _previous;
    }

    /// <summary>
    /// Builds <see cref="SqlError"/> and <see cref="SqlException"/> instances through the non-public members of
    /// Microsoft.Data.SqlClient 5.2.3, which has no public constructor for either. A missing member fails with the
    /// member name and the SqlClient assembly version.
    /// </summary>
    private static class SqlExceptionFactory
    {
        private const byte DefaultClass = 20;
        private const byte DefaultState = 1;
        private const string Server = "test-server";
        private const string ServerVersion = "16.00.0000";

        private static readonly Type[] ErrorParameterPrefix =
        [
            typeof(int), typeof(byte), typeof(byte), typeof(string), typeof(string), typeof(string), typeof(int),
        ];

        /// <summary>A single-error exception: <paramref name="number"/> with class 20 and state 1.</summary>
        public static SqlException Create(int number, string message = "test error", Exception? inner = null) =>
            Create([Error(number, DefaultClass, message, DefaultState)], inner);

        /// <summary>An exception carrying <paramref name="errors"/> in order; the first is the primary error.</summary>
        public static SqlException Create(IReadOnlyList<SqlError> errors, Exception? inner = null)
        {
            ArgumentNullException.ThrowIfNull(errors);

            var collection = Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true) as SqlErrorCollection
                ?? throw Missing("SqlErrorCollection()");
            MethodInfo add = typeof(SqlErrorCollection).GetMethod(
                    "Add", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(SqlError)])
                ?? throw Missing("SqlErrorCollection.Add(SqlError)");
            foreach (SqlError error in errors)
            {
                add.Invoke(collection, [error]);
            }

            MethodInfo[] factories = typeof(SqlException)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "CreateException")
                .Where(m => m.GetParameters() is [var first, var second, ..]
                    && first.ParameterType == typeof(SqlErrorCollection)
                    && second.ParameterType == typeof(string))
                .ToArray();

            MethodInfo factory = (inner is null
                    ? factories.FirstOrDefault(m => m.GetParameters().Length == 2)
                    : factories
                        .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(Exception))
                            && m.GetParameters().Any(p => p.ParameterType == typeof(Guid)))
                        .OrderBy(m => m.GetParameters().Length)
                        .FirstOrDefault())
                ?? throw Missing("SqlException.CreateException(SqlErrorCollection, string, ...)");

            object?[] arguments = factory.GetParameters()
                .Select((p, i) => i switch
                {
                    0 => collection,
                    1 => ServerVersion,
                    _ when p.ParameterType == typeof(Guid) => Guid.Empty,
                    _ when p.ParameterType == typeof(Exception) => inner,
                    _ => p.HasDefaultValue ? p.DefaultValue : null,
                })
                .ToArray();

            return factory.Invoke(null, arguments) as SqlException ?? throw Missing("SqlException.CreateException result");
        }

        /// <summary>One error as SqlClient builds it: server <c>test-server</c>, no procedure, line 0.</summary>
        public static SqlError Error(int number, byte errorClass, string message, byte state = DefaultState)
        {
            ArgumentNullException.ThrowIfNull(message);

            ConstructorInfo constructor = typeof(SqlError)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(c => c.GetParameters().Length >= ErrorParameterPrefix.Length
                    && c.GetParameters().Take(ErrorParameterPrefix.Length).Select(p => p.ParameterType)
                        .SequenceEqual(ErrorParameterPrefix))
                .OrderBy(c => c.GetParameters().Length)
                .FirstOrDefault()
                ?? throw Missing("SqlError(int, byte, byte, string, string, string, int, ...)");

            object?[] arguments = constructor.GetParameters()
                .Select((p, i) => i switch
                {
                    0 => number,
                    1 => state,
                    2 => errorClass,
                    3 => Server,
                    4 => message,
                    5 => string.Empty,
                    6 => 0,
                    _ when p.ParameterType == typeof(uint) => 0u,
                    _ when p.ParameterType == typeof(Exception) => null,
                    _ => p.HasDefaultValue ? p.DefaultValue : null,
                })
                .ToArray();

            return constructor.Invoke(arguments) as SqlError ?? throw Missing("SqlError constructor result");
        }

        /// <summary>
        /// An exception whose <see cref="SqlException.Errors"/> is empty, built through the non-public constructor
        /// (string, SqlErrorCollection, Exception, Guid) because every <c>CreateException</c> overload reads the first
        /// error.
        /// </summary>
        public static SqlException CreateWithoutErrors(string message = "test error")
        {
            ArgumentNullException.ThrowIfNull(message);

            var collection = Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true) as SqlErrorCollection
                ?? throw Missing("SqlErrorCollection()");
            ConstructorInfo constructor = typeof(SqlException).GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Instance,
                    [typeof(string), typeof(SqlErrorCollection), typeof(Exception), typeof(Guid)])
                ?? throw Missing("SqlException(string, SqlErrorCollection, Exception, Guid)");

            return constructor.Invoke([message, collection, null, Guid.Empty]) as SqlException
                ?? throw Missing("SqlException constructor result");
        }

        private static InvalidOperationException Missing(string member) =>
            new($"Microsoft.Data.SqlClient {typeof(SqlException).Assembly.GetName().Version} has no usable {member}.");
    }
}

/// <summary>
/// Integration tests of the shared test lock's failure attribution (<see cref="DatabaseWriteLock"/>): a code
/// <c>sp_getapplock</c> returned and a failure that only coincides with a cancelled token are each reported as
/// themselves, and only SqlClient's own report of a cancellation becomes an <see cref="OperationCanceledException"/>.
/// They need the rehearsal database, and change no data: they take application locks and run <c>SELECT 1/0</c>.
/// </summary>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class DatabaseWriteLockTests : IAsyncLifetime
{
    private const int DivideByZeroErrorNumber = 8134;

    public Task InitializeAsync() => TestDatabase.EnsureReachableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // -1 timed out, -2 cancelled, -3 deadlock victim, -999 call error: each is sp_getapplock's answer, so a token
    // cancelled after it returned never turns it into a cancellation.
    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-999)]
    public async Task AcquireAsync_ReturnedFailureCodeUnderLateCancellation_ReportsTheCodeAndClosesTheSession(int code)
    {
        using var cancellation = new CancellationTokenSource();
        SqlConnection? session = null;

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseWriteLock.AcquireAsync(
                DatabaseLockMode.Exclusive,
                (connection, _) =>
                {
                    session = connection;
                    cancellation.Cancel();
                    return Task.FromResult(code);
                },
                cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Contains(
            "sp_getapplock returned " + code.ToString(CultureInfo.InvariantCulture) + " (",
            failure.Message,
            StringComparison.Ordinal);
        Assert.Contains($"'{DatabaseWriteLock.ResourceName}'", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(DatabaseLockMode.Exclusive), failure.Message, StringComparison.Ordinal);
        Assert.NotNull(session);
        Assert.Equal(ConnectionState.Closed, session.State);
    }

    [Fact]
    public async Task AcquireAsync_ServerErrorUnderLateCancellation_PropagatesTheSqlExceptionAndClosesTheSession()
    {
        using var cancellation = new CancellationTokenSource();
        SqlConnection? session = null;

        SqlException failure = await Assert.ThrowsAsync<SqlException>(() =>
            DatabaseWriteLock.AcquireAsync(
                DatabaseLockMode.Exclusive,
                async (connection, _) =>
                {
                    session = connection;
                    cancellation.Cancel();
                    await using var command = new SqlCommand("SELECT 1/0;", connection);
                    await command.ExecuteNonQueryAsync(CancellationToken.None);
                    return 0;
                },
                cancellation.Token));

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(DivideByZeroErrorNumber, failure.Number);
        Assert.NotNull(session);
        Assert.Equal(ConnectionState.Closed, session.State);
    }

    [Fact]
    public async Task AcquireExclusiveAsync_CancelledWhileWaiting_ThrowsOperationCanceledException()
    {
        await using DatabaseWriteLock held = await DatabaseWriteLock.AcquireExclusiveAsync();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DatabaseWriteLock.AcquireExclusiveAsync(cancellation.Token));
    }
}

/// <summary>
/// Integration tests of the connection string <see cref="SqlConnectionFactory"/> opens with: the pool default it puts
/// ahead of the configured string changes neither which strings SqlClient accepts nor how SqlClient reads them. Each
/// configured string opens through the factory as it opens through SqlClient alone, with the same session settings.
/// They need the rehearsal database and change no data: their only statement reads the session's own properties.
/// </summary>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class SqlConnectionFactoryConnectionStringTests : IAsyncLifetime
{
    public Task InitializeAsync() => TestDatabase.EnsureReachableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    // The test database's connection string, surrounded by text SqlClient accepts: a Connect Timeout set twice, first
    // to an invalid value, which SqlConnectionStringBuilder rejects and SqlClient accepts, because it validates only
    // the value it keeps; and leading or trailing separators and white space. A null timeout is whatever SqlClient
    // reads from the configured string.
    [Theory]
    [InlineData("", ";Connect Timeout=-1;Connect Timeout=5", 5)]
    [InlineData(";", "", null)]
    [InlineData(" \t", "", null)]
    [InlineData("", ";", null)]
    [InlineData(" ; ;", ";; ", null)]
    public async Task OpenAsync_ConnectionStringSqlClientAccepts_OpensWithTheSettingsSqlClientReadsFromIt(
        string leading,
        string trailing,
        int? expectedConnectTimeout)
    {
        string configured = leading + TestDatabase.ConnectionString + trailing;
        var factory = new SqlConnectionFactory(new DatabaseOptions(configured));
        await using var reference = new SqlConnection(configured);
        try
        {
            await reference.OpenAsync();
            await using SqlConnection connection = await factory.OpenAsync(
                nameof(IDataStore.PingAsync),
                CancellationToken.None);
            try
            {
                (int One, string Database, string Application, string Login) session =
                    await DescribeSessionAsync(connection);

                Assert.Equal(1, session.One);
                Assert.Equal(await DescribeSessionAsync(reference), session);
                Assert.Equal(reference.ConnectionTimeout, connection.ConnectionTimeout);
                if (expectedConnectTimeout is int timeout)
                {
                    Assert.Equal(timeout, connection.ConnectionTimeout);
                }
            }
            finally
            {
                // Closes the pooled session the effective string keys, so it does not outlive the test.
                SqlConnection.ClearPool(connection);
            }
        }
        finally
        {
            // Closes the pooled session the configured string keys, so it does not outlive the test.
            SqlConnection.ClearPool(reference);
        }
    }

    // What the server reports for the session: the result of SELECT 1, the database, the application name and the
    // login, each of which follows from a connection string setting.
    private static async Task<(int One, string Database, string Application, string Login)> DescribeSessionAsync(
        SqlConnection connection)
    {
        await using var command = new SqlCommand("SELECT 1, DB_NAME(), APP_NAME(), SUSER_SNAME();", connection);
        await using SqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "The session query returned no row.");
        return (reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
    }
}

/// <summary>
/// Integration test of recovery without a restart (<see cref="SqlConnectionFactory"/>): once SQL Server answers again
/// after a failed open, the very next open reaches it, instead of receiving SqlClient's cached failure for the pool's
/// blocking period. The server is reached through <see cref="SwitchableForwarder"/>, so the outage is this test's own,
/// and its pool, keyed by the forwarder's port and a unique application name, is shared with no other test. It needs
/// the rehearsal database and changes no data: its only statement is <c>SELECT 1</c>.
/// </summary>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class SqlConnectionFactoryRecoveryTests : IAsyncLifetime
{
    // Short, so the failed open ends soon; long enough for a login relayed over loopback.
    private const int ConnectTimeoutSeconds = 3;

    private const int DefaultSqlServerPort = 1433;
    private const string TcpPrefix = "tcp:";
    private const string ApplicationNamePrefix = "EpiInfo.Web.Tests recovery ";

    public Task InitializeAsync() => TestDatabase.EnsureReachableAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task OpenAsync_ServerBackAfterAFailedOpen_NextOpenReachesItWithoutWaitingOutThePoolBlockingPeriod()
    {
        SqlConnectionStringBuilder target = TestDatabase.Builder();
        (string host, int port) = UpstreamEndpoint(target.DataSource);
        SwitchableForwarder forwarder = SwitchableForwarder.Start(host, port);
        string? effective = null;
        try
        {
            // The certificate is still checked against the server's own name, not the forwarder's address.
            if (!target.ShouldSerialize("Host Name In Certificate"))
            {
                target.HostNameInCertificate = host;
            }

            target.DataSource = string.Create(CultureInfo.InvariantCulture, $"{TcpPrefix}127.0.0.1,{forwarder.Port}");
            target.ConnectTimeout = ConnectTimeoutSeconds;
            target.ApplicationName = ApplicationNamePrefix + Guid.NewGuid().ToString("N");

            // The test is about the factory's own pool default, so the fixture string never decides it: a Pool Blocking
            // Period from the environment's connection string, under either spelling, would override that default, and
            // without pooling there would be no pool to block.
            target.Remove("Pool Blocking Period");
            target.Pooling = true;
            effective = SqlConnectionFactory.EffectiveConnectionString(target.ConnectionString);
            var factory = new SqlConnectionFactory(new DatabaseOptions(target.ConnectionString));

            DataStoreUnavailableException outage = await Assert.ThrowsAsync<DataStoreUnavailableException>(
                () => factory.OpenAsync(nameof(IDataStore.PingAsync), CancellationToken.None));
            Assert.Equal(WriteOutcome.NotWritten, outage.Outcome);

            // The server is back. A pool in its blocking period would now answer with the cached failure for at least
            // five seconds without contacting the server, so this open would fail at once.
            forwarder.Forwarding = true;
            await using SqlConnection connection = await factory.OpenAsync(
                nameof(IDataStore.PingAsync),
                CancellationToken.None);
            await using var command = new SqlCommand("SELECT 1;", connection);

            Assert.Equal(1, await command.ExecuteScalarAsync());
        }
        finally
        {
            // Only this test's pool is cleared: the one keyed by the effective string of its own connection string.
            if (effective is not null)
            {
                await using var poolKey = new SqlConnection(effective);
                SqlConnection.ClearPool(poolKey);
            }

            await forwarder.DisposeAsync();
        }
    }

    // The TCP endpoint of the test server's data source: "host" (port 1433) or "host,port", optionally prefixed "tcp:",
    // where an "\instance" is ignored when a port is given, as SqlClient ignores it. An instance without a port is found
    // through the SQL Server Browser, and a named pipe or LocalDB has no TCP endpoint, so the forwarder cannot stand in
    // for either; the test then fails naming the data source, which holds no secret.
    private static (string Host, int Port) UpstreamEndpoint(string dataSource)
    {
        string source = dataSource.Trim();
        if (source.StartsWith(TcpPrefix, StringComparison.OrdinalIgnoreCase))
        {
            source = source[TcpPrefix.Length..];
        }

        string[] parts = source.Split(',');
        string server = parts[0].Trim();
        int instanceStart = server.IndexOf('\\', StringComparison.Ordinal);
        string host = instanceStart < 0 ? server : server[..instanceStart];
        if (host is "." || string.Equals(host, "(local)", StringComparison.OrdinalIgnoreCase))
        {
            host = "localhost";
        }

        int port = DefaultSqlServerPort;
        bool usable = parts.Length <= 2
            && host.Length > 0
            && !host.Contains(':', StringComparison.Ordinal)
            && !host.StartsWith('(')
            && (parts.Length == 2
                ? int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
                    && port is > 0 and <= IPEndPoint.MaxPort
                : instanceStart < 0);
        if (!usable)
        {
            Assert.Fail(
                $"{nameof(SqlConnectionFactoryRecoveryTests)} relays to the test server over TCP, so its data source " +
                $"must be host or host,port, optionally prefixed {TcpPrefix}; '{dataSource}' is not. Set " +
                $"{TestDatabase.SqlHostVariable} or {TestDatabase.ConnectionStringVariable} to such a data source.");
        }

        return (host, port);
    }

    /// <summary>
    /// A loopback TCP listener standing in for the test server. Each connection it accepts is either reset at once
    /// (<see cref="Forwarding"/> false, the initial state: the server has gone away) or relayed byte for byte to the
    /// server (true). Switching keeps the same port, so the factory's connection string, and with it its pool, is the
    /// same during the outage and after it.
    /// </summary>
    private sealed class SwitchableForwarder : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stopping = new();
        private readonly List<Task> relays = [];
        private readonly string upstreamHost;
        private readonly int upstreamPort;
        private Task accepting = Task.CompletedTask;
        private volatile bool forwarding;

        private SwitchableForwarder(string upstreamHost, int upstreamPort)
        {
            this.upstreamHost = upstreamHost;
            this.upstreamPort = upstreamPort;
        }

        /// <summary>The port it listens on, on 127.0.0.1.</summary>
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        /// <summary>Whether a connection accepted from now on is relayed to the server rather than reset.</summary>
        public bool Forwarding
        {
            get => forwarding;
            set => forwarding = value;
        }

        /// <summary>Starts listening on a free loopback port, resetting every connection it accepts.</summary>
        /// <param name="upstreamHost">The test server's host.</param>
        /// <param name="upstreamPort">The test server's TCP port.</param>
        /// <returns>The running forwarder; the caller disposes it.</returns>
        public static SwitchableForwarder Start(string upstreamHost, int upstreamPort)
        {
            var forwarder = new SwitchableForwarder(upstreamHost, upstreamPort);
            forwarder.listener.Start();
            forwarder.accepting = forwarder.AcceptAsync(forwarder.stopping.Token);
            return forwarder;
        }

        /// <summary>Stops listening, ends every relay, and returns once each has closed both of its sockets.</summary>
        /// <returns>A task that completes when the forwarder has stopped.</returns>
        public async ValueTask DisposeAsync()
        {
            await stopping.CancelAsync();
            listener.Stop();
            await accepting;

            // The accept loop has ended, so no relay is added while these are awaited.
            await Task.WhenAll(relays);
            stopping.Dispose();
        }

        private async Task AcceptAsync(CancellationToken stop)
        {
            while (true)
            {
                Socket client;
                try
                {
                    client = await listener.AcceptSocketAsync(stop);
                }
                catch (Exception failure) when (stop.IsCancellationRequested && IsTransportEnd(failure))
                {
                    return;
                }

                if (forwarding)
                {
                    relays.Add(RelayAsync(client, stop));
                }
                else
                {
                    // A zero linger timeout makes the close send a TCP reset, as a server that has gone away does.
                    client.LingerState = new LingerOption(enable: true, seconds: 0);
                    client.Dispose();
                }
            }
        }

        // Relays one accepted connection until either side closes it or the forwarder stops. A transport error ends
        // the relay quietly: the test asserts what SqlClient reports, not how the relay ended.
        private async Task RelayAsync(Socket client, CancellationToken stop)
        {
            using var upstream = new TcpClient();
            try
            {
                try
                {
                    await upstream.ConnectAsync(upstreamHost, upstreamPort, stop);
                }
                catch (Exception failure) when (IsTransportEnd(failure))
                {
                    return;
                }

                await using var clientStream = new NetworkStream(client, ownsSocket: false);
                NetworkStream serverStream = upstream.GetStream();
                Task toServer = PumpAsync(clientStream, serverStream, stop);
                Task toClient = PumpAsync(serverStream, clientStream, stop);
                await Task.WhenAny(toServer, toClient);

                // One side has closed: closing both sockets ends the other direction as well.
                client.Close();
                upstream.Close();
                await Task.WhenAll(toServer, toClient);
            }
            finally
            {
                client.Dispose();
            }
        }

        private static async Task PumpAsync(Stream source, Stream destination, CancellationToken stop)
        {
            try
            {
                await source.CopyToAsync(destination, stop);
            }
            catch (Exception failure) when (IsTransportEnd(failure))
            {
                // The connection closed or the forwarder stopped; either way this direction is done.
            }
        }

        private static bool IsTransportEnd(Exception failure) =>
            failure is IOException or SocketException or OperationCanceledException or ObjectDisposedException;
    }
}
