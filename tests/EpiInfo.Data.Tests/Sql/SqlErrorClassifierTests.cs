using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using EpiInfo.Data.Sql;
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
        {
            Assert.Throws<ArgumentException>(() => new SqlConnection("this is not a connection string")),
            SqlFailureKind.Unexpected,
            null
        },
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
