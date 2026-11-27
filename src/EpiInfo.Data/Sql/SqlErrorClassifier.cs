using System.Collections.Frozen;
using System.Globalization;
using System.Net.Sockets;
using EpiInfo.Domain.Storage;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Sql;

/// <summary>
/// The class a failed store operation falls into, which decides how the API reports it.
/// </summary>
public enum SqlFailureKind
{
    /// <summary>
    /// The failure was caused by the client aborting the request: the caller's cancellation token was cancelled and
    /// the failure is the cancellation itself, an <see cref="OperationCanceledException"/> or SqlClient's
    /// acknowledgement that it cancelled the running command. This is not a database failure: the API writes no
    /// response for it. A failure that only coincides with a cancelled token keeps its own class.
    /// </summary>
    Cancelled,

    /// <summary>
    /// SQL Server could not be reached or used: a connectivity, login, timeout or deadlock failure, or connection
    /// pool exhaustion. Raised as <see cref="DataStoreUnavailableException"/> and reported as 503.
    /// </summary>
    Unavailable,

    /// <summary>
    /// Any other failure. Raised as <see cref="DataStoreWriteException"/> and reported as 500.
    /// </summary>
    Unexpected,
}

/// <summary>
/// The classification of one failure, as <see cref="SqlErrorClassifier.Classify"/> returns it.
/// </summary>
/// <param name="Kind">The class of the failure.</param>
/// <param name="SqlErrorNumber">
/// The SQL Server error number when the failure is a <see cref="SqlException"/>: for an unavailable failure, the
/// number that decided it; otherwise the exception's primary number. <see langword="null"/> when the
/// failure is not a <see cref="SqlException"/> or carries no error.
/// </param>
public readonly record struct SqlFailure(SqlFailureKind Kind, int? SqlErrorNumber);

/// <summary>
/// How far a write transaction had progressed when it failed, which bounds what is known about the stored data.
/// </summary>
public enum WriteStage
{
    /// <summary>
    /// No <c>INSERT</c> or <c>UPDATE</c> has been dispatched, including a failure while opening or locking, and a
    /// failed or unconfirmed <c>Commit</c> of a transaction that dispatched none, which held nothing to keep.
    /// </summary>
    BeforeDml,

    /// <summary>At least one <c>INSERT</c> or <c>UPDATE</c> was sent, and <c>Commit</c> has not been called.</summary>
    DmlSent,

    /// <summary>
    /// At least one <c>INSERT</c> or <c>UPDATE</c> was sent, and <c>Commit</c> was called and threw or did not
    /// return. The server may or may not have committed, unless the failure itself proves the transaction was not
    /// kept: a rollback that completed without error, or the server's deadlock-victim abort (1205). See
    /// <see cref="SqlErrorClassifier.ResolveWriteOutcome"/>.
    /// </summary>
    Committing,
}

/// <summary>
/// The single owner of failure classification for the SQL Server store: which failures make the store unavailable
/// (503), which are unexpected (500), which are a client cancellation, and what a failed write reports as its
/// <see cref="WriteOutcome"/>.
/// </summary>
/// <remarks>
/// <para>
/// The classifier only inspects: it retries nothing and logs nothing. It reads exception message text in three places
/// only, none of which depends on the current culture: it compares the message of an
/// <see cref="InvalidOperationException"/> raised while opening, by ordinal equality, with the pool-timeout texts
/// SqlClient 5.2.3 ships; it compares the message of each error of a <see cref="SqlException"/> raised under a
/// cancelled token, by ordinal equality, with the "operation cancelled" texts SqlClient 5.2.3 ships; and it reads the
/// integer SNI error code from the SQL Network Interfaces scaffold <c>(provider: ..., error: ... - ...)</c> that marks
/// an error SqlClient's network interface layer raised. The evidence that a failure is the client's cancellation is
/// the exception's type, or SqlClient's attention acknowledgement: errors all number 0 below severity class 20, one of
/// them carrying an "operation cancelled" text. It never copies, returns or logs message text. The exceptions it
/// builds carry fixed messages, the caller's fixed operation name and the SQL error number, never SQL text, a
/// parameter value, a connection string or a database message; the original failure is attached only as the inner
/// exception. The only other thing it writes is <see cref="Exception.Data"/> of the exception raised for a failure: a
/// cancellation's SQL error number and a failed rollback's type name and SQL error number.
/// </para>
/// <para>
/// A <see cref="DataStoreUnavailableException"/> or <see cref="DataStoreWriteException"/> that a store method has
/// already raised, such as the unavailable failure for an absent connection string, is rethrown unchanged by its
/// callers rather than classified again: classification applies to the underlying failure only.
/// </para>
/// </remarks>
public static class SqlErrorClassifier
{
    /// <summary>
    /// The <see cref="Exception.Data"/> key under which <c>SqlRecordRepository</c> stores the type name
    /// (<see cref="string"/>) of a rollback that failed, on the exception it raises for the original failure.
    /// </summary>
    /// <remarks>
    /// The rollback exception's message is never stored, because it may hold SQL text. With
    /// <see cref="RollbackSqlErrorNumberKey"/>, this lets <c>ApiExceptionHandler</c> log the rollback failure while
    /// the original failure keeps deciding the status and the classification.
    /// </remarks>
    public const string RollbackExceptionTypeKey = "EpiInfo.Data.RollbackExceptionType";

    /// <summary>
    /// The <see cref="Exception.Data"/> key under which <c>SqlRecordRepository</c> stores the SQL error number
    /// (<see cref="int"/>) of a rollback that failed with a <see cref="SqlException"/>; absent for any other rollback
    /// failure.
    /// </summary>
    /// <remarks>
    /// Stored beside <see cref="RollbackExceptionTypeKey"/> on the exception raised for the original failure, never
    /// in place of it, and never with the rollback exception's message.
    /// </remarks>
    public const string RollbackSqlErrorNumberKey = "EpiInfo.Data.RollbackSqlErrorNumber";

    /// <summary>
    /// The <see cref="Exception.Data"/> key under which the store keeps the SQL error number (<see cref="int"/>) of a
    /// failure it raises as an <see cref="OperationCanceledException"/>, such as 0 for SqlClient's acknowledgement of a
    /// cancelled command; absent when the cancellation carried no SQL error.
    /// </summary>
    /// <remarks>
    /// An <see cref="OperationCanceledException"/> has no SQL error number of its own, so this lets
    /// <c>ApiExceptionHandler</c> log the classified number of a client abort, as it logs a store exception's. The
    /// failure's message is never stored, because it may hold SQL text.
    /// </remarks>
    public const string SqlErrorNumberKey = "EpiInfo.Data.SqlErrorNumber";

    /// <summary>The fixed message of the cancellation exception <see cref="ToDataStoreException"/> builds.</summary>
    internal const string CancelledMessage = "The data store operation was cancelled.";

    // SqlClient 5.2.3 raises pool exhaustion while opening as an InvalidOperationException with no error number, whose
    // message is its pool-timeout text in the culture it formats the message in. These are that text exactly as the
    // pinned package ships it, in its neutral resources and in each culture it ships resources for. An asynchronous
    // open can format the message on a thread other than the one that classifies it, so the set holds every culture's
    // text and IsPoolTimeout never consults the current culture. The texts change only with the SqlClient version:
    // SqlClientFailureRecognitionTests raises the failure through SqlClient itself in each of these cultures.
    private static readonly FrozenSet<string> PoolTimeoutMessages = new[]
    {
        // The neutral resources (English), whose text a culture SqlClient ships no resources for also gets.
        "Timeout expired.  The timeout period elapsed prior to obtaining a connection from the pool.  This may have "
            + "occurred because all pooled connections were in use and max pool size was reached.",
        // de (German)
        "Timeout abgelaufen. Das Zeitlimit ist vor dem Anfordern einer Verbindung aus dem Pool abgelaufen. "
            + "Möglicherweise werden alle Verbindungen im Pool bereits verwendet, und die maximale Poolgröße wurde "
            + "erreicht.",
        // es (Spanish)
        "Valor de tiempo de espera caducado. El período de tiempo de espera expiró antes de obtener una conexión del "
            + "grupo. Esto puede suceder porque todas las conexiones de la agrupación estaban en uso y se alcanzó el "
            + "máximo tamaño del grupo.",
        // fr (French)
        "Expiration du délai d'attente. Le délai d'attente s'est écoulé avant obtention d'une connexion du pool. Ceci "
            + "est probablement dû au fait que toutes les connexions regroupées sont en cours d'utilisation et que la "
            + "taille maximale du pool a été atteinte.",
        // it (Italian)
        "Timeout scaduto. Il tempo disponibile è scaduto prima di aver ottenuto la connessione dal pool.  Ciò "
            + "potrebbe essere accaduto perché tutte le connessioni dal pool erano in uso ed era già stata raggiunta "
            + "la dimensione massima del pool.",
        // ja (Japanese)
        "タイムアウトに達しました。プールから接続を取得する前にタイムアウト期間が過ぎました。"
            + "プールされた接続がすべて使用中で、プール サイズの制限値に達した可能性があります。",
        // ko (Korean)
        "제한 시간이 만료되었습니다. 풀에서 연결을 만들기 전에 제한 시간이 경과되었습니다. 풀링된 연결이 사용 "
            + "중이었거나 최대 풀 크기에 도달했기 때문일 수 있습니다.",
        // pt-BR (Portuguese, Brazil)
        "Tempo limite expirado.  O período de tempo limite foi atingido antes que fosse obtida uma conexão do pool.  "
            + "Isso pode ter ocorrido porque todas as conexões do pool estavam em uso e o tamanho máximo do pool foi "
            + "atingido.",
        // ru (Russian)
        "Истекло время ожидания. Время ожидания истекло раньше, чем удалось получить подключение из пула. Возможно, "
            + "все подключения в пуле уже используются и достигнут максимальный размер пула.",
        // zh-Hans (Chinese, Simplified)
        "超时时间已到。超时时间已到，但是尚未从池中获取连接。出现这种情况可能是因为所有池连接均在使用，"
            + "并且达到了最大池大小。",
        // zh-Hant (Chinese, Traditional)
        "已超過連接逾時的設定。在取得集區連接之前超過逾時等待的時間，可能的原因為所有的共用連接已在使用中，"
            + "並已達共用集區大小的最大值。",
    }.ToFrozenSet(StringComparer.Ordinal);

    // When the server acknowledges the attention SqlClient 5.2.3 sends for a cancelled command, SqlClient appends an
    // "operation cancelled" error of its own, whose message is that text in the culture it formats the message in.
    // These are that text exactly as the pinned package ships it, in its neutral resources and in each culture it
    // ships resources for. The acknowledgement can be formatted on a thread other than the one that classifies it, so
    // the set holds every culture's text and IsAttentionAcknowledgement never consults the current culture. The texts
    // change only with the SqlClient version: SqlClientFailureRecognitionTests cancels a running command through
    // SqlClient itself in each of these cultures.
    private static readonly FrozenSet<string> OperationCancelledMessages = new[]
    {
        // The neutral resources (English), whose text a culture SqlClient ships no resources for also gets.
        "Operation cancelled by user.",
        // de (German)
        "Der Vorgang wurde vom Benutzer abgebrochen.",
        // es (Spanish)
        "Operación cancelada por el usuario.",
        // fr (French)
        "Opération annulée par l'utilisateur.",
        // it (Italian)
        "Operazione annullata dall'utente.",
        // ja (Japanese)
        "操作はユーザーによって取り消されました。",
        // ko (Korean)
        "사용자가 작업을 취소했습니다.",
        // pt-BR (Portuguese, Brazil)
        "Operação cancelada pelo usuário.",
        // ru (Russian)
        "Операция отменена пользователем.",
        // zh-Hans (Chinese, Simplified)
        "用户已取消操作。",
        // zh-Hant (Chinese, Traditional)
        "使用者已經取消作業。",
    }.ToFrozenSet(StringComparer.Ordinal);

    // The error number SqlClient gives each error of its attention acknowledgement; SQL Server never raises number 0.
    private const int AttentionAcknowledgementErrorNumber = 0;

    // The server rolls back a deadlock victim's transaction itself, so nothing it sent is kept.
    private const int DeadlockVictimErrorNumber = 1205;

    // The error number and minimum severity class SqlClient gives every error its network layer raises. SqlClient
    // also gives them to fatal errors of its own that are not transport failures, so the pair alone never decides:
    // IsClientTransportFailure also requires the network layer's text and transport evidence.
    private const int ClientTransportErrorNumber = 0;
    private const byte FatalErrorClass = 20;

    /// <summary>
    /// The SQL Server error numbers that make the store unavailable (503): 53, -1, 2, 40, 64, 121, 233, 10053, 10054,
    /// 10060, 10061 and 11001 (the connection cannot be made, or it broke); 18456 (login failed); 4060 (cannot open
    /// the database); -2 (timeout); 1205 (deadlock victim).
    /// </summary>
    public static IReadOnlySet<int> UnavailableErrorNumbers { get; } = new[]
    {
        53, -1, 2, 40, 64, 121, 233, 10053, 10054, 10060, 10061, 11001,
        18456,
        4060,
        -2,
        DeadlockVictimErrorNumber,
    }.ToFrozenSet();

    /// <summary>Classifies a failure raised by a store operation. Never throws.</summary>
    /// <param name="exception">The failure caught by the store.</param>
    /// <param name="cancellationToken">
    /// The token the operation ran under. Its cancellation decides <see cref="SqlFailureKind.Cancelled"/> only together
    /// with evidence that the failure is the cancellation itself; on its own it changes no classification.
    /// </param>
    /// <param name="duringOpen">
    /// <see langword="true"/> when the failure came from opening the connection, the only place SqlClient's
    /// pool-exhaustion <see cref="InvalidOperationException"/> means the store is unavailable.
    /// </param>
    /// <returns>
    /// <list type="number">
    /// <item><description>
    /// <see cref="SqlFailureKind.Cancelled"/> when <paramref name="cancellationToken"/> is cancelled and the failure
    /// is the cancellation itself: an <see cref="OperationCanceledException"/>, <see cref="TaskCanceledException"/>
    /// included, which SqlClient raises for a token cancelled before a command or while opening; or SqlClient's
    /// attention acknowledgement for a command it cancelled, a <see cref="SqlException"/> whose errors are all number 0
    /// with a severity class below 20, at least one of them carrying, by ordinal equality, the "operation cancelled"
    /// text SqlClient 5.2.3 ships in its neutral (English) resources or in one of the cultures it ships resources for
    /// (de, es, fr, it, ja, ko, pt-BR, ru, zh-Hans and zh-Hant), whatever the current culture. The number is the
    /// <see cref="SqlException"/>'s primary number, 0 for the acknowledgement, and absent for an
    /// <see cref="OperationCanceledException"/>. Any other failure under a cancelled token, such as a timeout (-2), a
    /// permission error, a server error raised alongside the acknowledgement, the lone severe error SqlClient raises
    /// for a statement the server marked as failed without sending an error of its own, a fatal number-0 error or a
    /// <see cref="SqlException"/> carrying no error, keeps the class the items below give it.
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unavailable"/> for a <see cref="SqlException"/> whose
    /// <see cref="SqlException.Number"/>, or failing that one of whose <see cref="SqlException.Errors"/>, is in
    /// <see cref="UnavailableErrorNumbers"/>, with that number. A <see cref="System.ComponentModel.Win32Exception"/>
    /// counts only inside such a <see cref="SqlException"/>, never on its own.
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unavailable"/>, with number 0, for a <see cref="SqlException"/> whose primary error is
    /// number 0 with severity class 20 or above, has a message ending in the "(provider: …, error: … - …)" text that
    /// SqlClient's network interface layer appends to every error it raises, and carries transport evidence: a
    /// socket, I/O or timeout cause (<see cref="SocketException"/>, <see cref="IOException"/> or
    /// <see cref="TimeoutException"/>) in its <see cref="Exception.InnerException"/> chain or, when it has no inner
    /// exception, a connection-failure SNI error code in that text, such as 40 "could not open a connection", 2
    /// "connection terminated" or 11 "connection timeout". This is the form in which the managed network layer, used
    /// on Linux and macOS, reports the connection failures that Windows reports under the listed numbers. A malformed
    /// connection string's code 25 and the other configuration codes, and a generic fatal number-0 error without the
    /// network layer's text, such as a server-side severe error, stay <see cref="SqlFailureKind.Unexpected"/> (500).
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unavailable"/>, with no number, for SqlClient's pool-timeout
    /// <see cref="InvalidOperationException"/> when <paramref name="duringOpen"/> is <see langword="true"/>: its
    /// message equals, by ordinal comparison, the pool-timeout text SqlClient 5.2.3 ships in its neutral (English)
    /// resources or in one of the cultures it ships resources for (de, es, fr, it, ja, ko, pt-BR, ru, zh-Hans and
    /// zh-Hant), whatever the current culture. A message holding only part of such a text, or more than it, does not
    /// qualify, and SqlClient's non-pooled open timeout is not pool exhaustion.
    /// </description></item>
    /// <item><description>
    /// <see cref="SqlFailureKind.Unexpected"/> for everything else, whether or not
    /// <paramref name="cancellationToken"/> is cancelled: any other <see cref="SqlException"/> (with its primary
    /// number), including SqlClient's attention acknowledgement under a token that was not cancelled; a malformed
    /// connection string's <see cref="ArgumentException"/>; any other <see cref="InvalidOperationException"/>, and the
    /// pool-timeout one outside opening; an <see cref="OperationCanceledException"/> under a token that was not
    /// cancelled; and any non-SQL exception.
    /// </description></item>
    /// </list>
    /// </returns>
    public static SqlFailure Classify(Exception exception, CancellationToken cancellationToken, bool duringOpen = false)
    {
        var sqlException = exception as SqlException;

        if (cancellationToken.IsCancellationRequested && IsCancellation(exception, sqlException))
        {
            return new SqlFailure(SqlFailureKind.Cancelled, sqlException is null ? null : PrimaryNumber(sqlException));
        }

        if (sqlException is not null)
        {
            return ClassifySqlException(sqlException);
        }

        if (duringOpen && IsPoolTimeout(exception))
        {
            return new SqlFailure(SqlFailureKind.Unavailable, null);
        }

        return new SqlFailure(SqlFailureKind.Unexpected, null);
    }

    // The evidence that a failure under a cancelled token is the cancellation itself rather than a failure that only
    // coincides with it. SqlClient raises an OperationCanceledException (TaskCanceledException included) for a token
    // cancelled before a command or while opening; that exception's own token is not the caller's, so its type is the
    // evidence. A command cancelled while running ends in SqlClient's attention acknowledgement instead.
    private static bool IsCancellation(Exception exception, SqlException? sqlException) =>
        exception is OperationCanceledException
        || (sqlException is not null && IsAttentionAcknowledgement(sqlException));

    // When the server acknowledges the attention SqlClient sends for a cancelled command (a cancelled token or
    // SqlCommand.Cancel), SqlClient 5.2.3 raises a SqlException of errors it builds itself, each number 0 with class
    // 11: a severe error, then its "operation cancelled" error. It appends the "operation cancelled" error only on the
    // attention path, so one of OperationCancelledMessages is the evidence, compared by ordinal equality. SqlClient
    // raises the same severe error alone for a statement the server marked as failed without sending an error of its
    // own, a failure of the command that may merely coincide with an abort, so a lone severe error stays its own
    // class. SQL Server never raises number 0, and a command timeout is the single error -2. Any error the server
    // raised itself (a nonzero number), a fatal class, or no error at all means the failure is not the abort alone,
    // so it keeps its own classification. Classify asks only under a cancelled token, so an acknowledgement under any
    // other token stays unexpected.
    private static bool IsAttentionAcknowledgement(SqlException exception)
    {
        if (exception.Errors.Count == 0)
        {
            return false;
        }

        bool cancelled = false;
        foreach (SqlError error in exception.Errors)
        {
            if (error.Number != AttentionAcknowledgementErrorNumber || error.Class >= FatalErrorClass)
            {
                return false;
            }

            cancelled |= error.Message is string message && OperationCancelledMessages.Contains(message);
        }

        return cancelled;
    }

    // SqlClient's pool-exhaustion InvalidOperationException: its message is, by ordinal equality, one of
    // PoolTimeoutMessages. A message holding only part of such a text, or more than it, is not; nothing else is read.
    private static bool IsPoolTimeout(Exception exception) =>
        exception is InvalidOperationException { Message: string message } && PoolTimeoutMessages.Contains(message);

    /// <summary>
    /// Classifies a single SQL Server error number: <see cref="SqlFailureKind.Unavailable"/> when it is in
    /// <see cref="UnavailableErrorNumbers"/>, otherwise <see cref="SqlFailureKind.Unexpected"/>, carrying the number
    /// either way.
    /// </summary>
    /// <param name="number">A SQL Server error number.</param>
    /// <returns>The classification of <paramref name="number"/>.</returns>
    internal static SqlFailure ClassifyNumber(int number) =>
        new(UnavailableErrorNumbers.Contains(number) ? SqlFailureKind.Unavailable : SqlFailureKind.Unexpected, number);

    /// <summary>
    /// Decides the <see cref="WriteOutcome"/> a failed write reports, from how far the transaction had progressed and
    /// whether the failure proves the transaction was not kept.
    /// </summary>
    /// <param name="stage">How far the write had progressed when it failed.</param>
    /// <param name="rollbackCompleted">
    /// <see langword="true"/> when <c>Rollback</c> was attempted after the failure and completed without error.
    /// </param>
    /// <param name="failure">The classification of the original failure.</param>
    /// <returns>
    /// <see cref="WriteOutcome.NotWritten"/> before any DML (<see cref="WriteStage.BeforeDml"/>). After DML, whether
    /// <c>Commit</c> was not yet called (<see cref="WriteStage.DmlSent"/>) or was called and threw or did not return
    /// (<see cref="WriteStage.Committing"/>): <see cref="WriteOutcome.NotWritten"/> when the rollback completed or the
    /// server aborted the transaction as a deadlock victim (1205); otherwise <see cref="WriteOutcome.Unknown"/>, as for
    /// a failed, timed-out or unacknowledged commit, or a failed rollback, without that evidence. An undefined
    /// <paramref name="stage"/> is <see cref="WriteOutcome.Unknown"/> whatever the evidence.
    /// <see cref="WriteOutcome.Written"/> is never returned: <c>RecordService</c> records it once the store reports
    /// a commit.
    /// </returns>
    public static WriteOutcome ResolveWriteOutcome(WriteStage stage, bool rollbackCompleted, SqlFailure failure) =>
        stage switch
        {
            WriteStage.BeforeDml => WriteOutcome.NotWritten,
            // After DML, before or during the commit call, only conclusive evidence that the transaction was not kept
            // proves nothing was written: a rollback that completed without error, or the server's deadlock-victim
            // abort, because the engine rolls the victim back before it returns 1205. A failed, timed-out or
            // unacknowledged commit without that evidence may have been applied, so it is never reported as resendable.
            WriteStage.DmlSent or WriteStage.Committing
                when rollbackCompleted || failure.SqlErrorNumber == DeadlockVictimErrorNumber =>
                WriteOutcome.NotWritten,
            WriteStage.DmlSent or WriteStage.Committing => WriteOutcome.Unknown,
            // An undefined stage proves nothing about the data, so it reports the one outcome that is never resent.
            _ => WriteOutcome.Unknown,
        };

    /// <summary>
    /// Builds the exception a store raises for a classified failure. The expected use is
    /// <c>throw SqlErrorClassifier.ToDataStoreException(...)</c>.
    /// </summary>
    /// <param name="failure">The classification of <paramref name="inner"/>.</param>
    /// <param name="operation">
    /// A fixed operation name chosen by the calling code, such as <c>InsertRecord</c>; never request input.
    /// </param>
    /// <param name="outcome">
    /// What is known about the stored data: <see cref="WriteOutcome.NotWritten"/> for reads, otherwise the result of
    /// <see cref="ResolveWriteOutcome"/>.
    /// </param>
    /// <param name="inner">The original failure, attached for diagnostics only.</param>
    /// <returns>
    /// A <see cref="DataStoreUnavailableException"/> for <see cref="SqlFailureKind.Unavailable"/>, a
    /// <see cref="DataStoreWriteException"/> for <see cref="SqlFailureKind.Unexpected"/>, and an
    /// <see cref="OperationCanceledException"/> with a fixed message for <see cref="SqlFailureKind.Cancelled"/>, which
    /// keeps the failure's SQL error number, when it has one, under <see cref="SqlErrorNumberKey"/>. For a
    /// cancellation the caller may instead use <see cref="ToCancellation"/>, which keeps an original
    /// <see cref="OperationCanceledException"/> and the caller's token.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="operation"/> is <see langword="null"/>, empty or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="outcome"/> is not a defined value.</exception>
    public static Exception ToDataStoreException(SqlFailure failure, string operation, WriteOutcome outcome, Exception inner) =>
        failure.Kind switch
        {
            SqlFailureKind.Unavailable =>
                new DataStoreUnavailableException(operation, outcome, failure.SqlErrorNumber, inner),
            SqlFailureKind.Cancelled =>
                WithSqlErrorNumber(new OperationCanceledException(CancelledMessage, inner), failure),
            _ => new DataStoreWriteException(operation, outcome, failure.SqlErrorNumber, inner),
        };

    /// <summary>
    /// The exception a store raises for a failure classified as <see cref="SqlFailureKind.Cancelled"/>: the original
    /// <see cref="OperationCanceledException"/> itself, or, for SqlClient's acknowledgement of a cancelled command, a
    /// new one with the fixed message, the caller's token and the original failure as its inner exception. The
    /// failure's SQL error number, when it has one, is kept under <see cref="SqlErrorNumberKey"/>.
    /// </summary>
    /// <param name="failure">
    /// The classification of <paramref name="exception"/>, of kind <see cref="SqlFailureKind.Cancelled"/>.
    /// </param>
    /// <param name="exception">The original failure.</param>
    /// <param name="cancellationToken">The caller's token, which a new exception carries.</param>
    /// <returns>
    /// <paramref name="exception"/> when it is an <see cref="OperationCanceledException"/>, which the caller rethrows
    /// with <c>throw;</c>; otherwise the new exception.
    /// </returns>
    internal static Exception ToCancellation(SqlFailure failure, Exception exception, CancellationToken cancellationToken) =>
        WithSqlErrorNumber(
            exception as OperationCanceledException
                ?? new OperationCanceledException(CancelledMessage, exception, cancellationToken),
            failure);

    /// <summary>
    /// Records a rollback that failed on <paramref name="target"/>, the exception raised for the original failure: the
    /// rollback exception's type name under <see cref="RollbackExceptionTypeKey"/> and, for a
    /// <see cref="SqlException"/> carrying an error, its number under <see cref="RollbackSqlErrorNumberKey"/>. The
    /// rollback exception's message is never stored, and <paramref name="target"/> keeps its own type, message and
    /// classification.
    /// </summary>
    /// <param name="target">The exception raised for the original failure.</param>
    /// <param name="rollbackError">What the rollback threw, or <see langword="null"/> when it did not fail.</param>
    internal static void AttachRollbackFailure(Exception target, Exception? rollbackError)
    {
        if (rollbackError is null)
        {
            return;
        }

        target.Data[RollbackExceptionTypeKey] = rollbackError.GetType().Name;
        if (rollbackError is SqlException sqlError && PrimaryNumber(sqlError) is int number)
        {
            target.Data[RollbackSqlErrorNumberKey] = number;
        }
    }

    // Keeps the classified SQL error number of a cancellation on the exception raised for it, which has no number of
    // its own.
    private static Exception WithSqlErrorNumber(Exception cancellation, SqlFailure failure)
    {
        if (failure.SqlErrorNumber is int number)
        {
            cancellation.Data[SqlErrorNumberKey] = number;
        }

        return cancellation;
    }

    private static SqlFailure ClassifySqlException(SqlException exception)
    {
        int? primary = PrimaryNumber(exception);
        if (primary is int number && ClassifyNumber(number) is { Kind: SqlFailureKind.Unavailable } byNumber)
        {
            return byNumber;
        }

        // SqlClient reports the first error as Number; a listed number further down the collection still decides.
        foreach (SqlError error in exception.Errors)
        {
            if (ClassifyNumber(error.Number) is { Kind: SqlFailureKind.Unavailable } byError)
            {
                return byError;
            }
        }

        if (IsClientTransportFailure(exception))
        {
            return new SqlFailure(SqlFailureKind.Unavailable, primary);
        }

        return new SqlFailure(SqlFailureKind.Unexpected, primary);
    }

    // SqlClient 5.2.3's SqlException.Number returns 0 when Errors is empty, which would be indistinguishable from
    // SqlClient's own error number 0; the guard reports an exception that carries no error as null instead.
    private static int? PrimaryNumber(SqlException exception) =>
        exception.Errors.Count > 0 ? exception.Number : null;

    // SqlClient's managed network layer, the one it uses on Linux and macOS, reports a connection that cannot be made,
    // that broke or that timed out (a refused connection, an unreachable server, an unresolvable host, a connection
    // reset or closed by the peer) as error number 0 with the fatal class 20, and carries the SNI or socket code that
    // Windows reports as Number (40, 53, 10061, 11001, ...) only in its message. In SqlClient 5.2.3,
    // TdsParser.ProcessSNIError builds every error of that layer with that number and class and the message
    // "{context} (provider: {provider}, error: {code} - {text})", where code is the layer's SNI error code. The layer
    // reports configuration problems the same way, such as a malformed connection string (code 25), and TdsParser
    // raises number 0 with class 20 for failures outside the layer: the DONE_SRVERROR severe error ("A severe error
    // occurred on the current command. ..."), UserInstanceFailure and the bare PreloginError message. So number and
    // class alone never decide; both of these must be present too:
    // - the primary error's message ends with the ProcessSNIError scaffold, the mark of an error the network layer
    //   raised. Its literal parts are the same in every culture SqlClient ships, and no SqlClient resource string
    //   contains "(provider: ";
    // - transport evidence. An InnerException decides alone, because the layer attaches the exception it caught when
    //   it has one: only a socket, I/O or timeout cause in its chain counts (HasTransportCause). Without one, the
    //   scaffold's SNI code must be a connection-failure code (TransportNetworkInterfaceCodes), such as 40 "could not
    //   open a connection", 2 "connection terminated" or 11 "connection timeout".
    // A malformed connection string's code 25 and the other configuration codes, and a generic fatal number-0 error
    // without the network layer's text, stay Unexpected (500). The primary error alone is tested: SQL Server itself
    // never raises number 0, and a session the server killed gives the errors [596 class 21, 0 class 20], whose
    // trailing number-0 error must not turn it into unavailability.
    private static bool IsClientTransportFailure(SqlException exception)
    {
        if (exception.Errors.Count == 0)
        {
            return false;
        }

        SqlError primary = exception.Errors[0];
        if (primary.Number != ClientTransportErrorNumber
            || primary.Class < FatalErrorClass
            || !TryReadNetworkInterfaceErrorCode(primary.Message, out int networkInterfaceCode))
        {
            return false;
        }

        return exception.InnerException is { } cause
            ? HasTransportCause(cause)
            : TransportNetworkInterfaceCodes.Contains(networkInterfaceCode);
    }

    // The SNI error codes with which SqlClient 5.2.3's managed network layer (constants in SNICommon) reports, without
    // an exception, a connection that could not be made, broke or timed out:
    // - 0: a read returned no data because the peer closed the connection (SNITcpHandle, SNINpHandle), reported with
    //   the provider's own text, such as "Success";
    // - 2, ConnTerminatedError: "Connection was terminated" (SNIPacket);
    // - 11, ConnTimeoutError: "Timeout error" (SNITcpHandle, SNIMarsHandle);
    // - 19, ConnNotUsableError: "Physical connection is not usable" (SNIMarsConnection);
    // - 40, ConnOpenFailedError: "Could not open a connection to SQL Server" (SNITcpHandle, SNINpHandle).
    // Every other code raised without an exception names a configuration problem, not an unavailable server: 5 and 8
    // (invalid parameter, protocol not supported), 25 InvalidConnStringError ("Connection string is not valid"), 26
    // ErrorLocatingServerInstance without the socket cause of an instance lookup (SNIProxy, for a LocalDB name the
    // managed layer cannot resolve), 47-49 (MultiSubnetFailover misuse) and 50-57 (LocalDB).
    private static readonly FrozenSet<int> TransportNetworkInterfaceCodes = new[] { 0, 2, 11, 19, 40 }.ToFrozenSet();

    // How many links of an InnerException chain HasTransportCause follows. SqlClient's transport causes sit at most
    // two links deep (an IOException wrapping a SocketException); the bound keeps any longer chain from being walked.
    private const int MaxCauseDepth = 8;

    // True when the chain holds a socket, I/O or timeout cause: a SocketException, an IOException or a
    // TimeoutException, the exceptions the managed network layer catches when a connection cannot be made, breaks or
    // times out (a named pipe it cannot open within the connect timeout raises a TimeoutException). Any other cause,
    // such as the AuthenticationException of a failed TLS handshake or an ObjectDisposedException, is not one.
    private static bool HasTransportCause(Exception cause)
    {
        Exception? current = cause;
        for (int depth = 0; current is not null && depth < MaxCauseDepth; depth++)
        {
            if (current is SocketException or IOException or TimeoutException)
            {
                return true;
            }

            current = current.InnerException;
        }

        return false;
    }

    // The literal parts of the scaffold "{0} (provider: {1}, error: {2} - {3})" that TdsParser.ProcessSNIError formats.
    private const string ProviderMarker = " (provider: ";
    private const string ErrorMarker = ", error: ";
    private const string CodeSeparator = " - ";

    // True when message ends with the scaffold: its last " (provider: ", then ", error: ", one or more ASCII digits
    // with an optional leading '-', then " - ", and a closing ")" as the final character. code is then the SNI error
    // code those digits give, and 0 when the method returns false. The test is ordinal and cannot throw, and no part
    // of the message leaves it except that integer.
    private static bool TryReadNetworkInterfaceErrorCode(string? message, out int code)
    {
        code = 0;
        if (string.IsNullOrEmpty(message) || message[^1] != ')')
        {
            return false;
        }

        int provider = message.LastIndexOf(ProviderMarker, StringComparison.Ordinal);
        if (provider < 0)
        {
            return false;
        }

        int error = message.IndexOf(ErrorMarker, provider + ProviderMarker.Length, StringComparison.Ordinal);
        if (error < 0)
        {
            return false;
        }

        int codeStart = error + ErrorMarker.Length;
        int position = codeStart;
        if (position < message.Length && message[position] == '-')
        {
            position++;
        }

        int digitsStart = position;
        while (position < message.Length && char.IsAsciiDigit(message[position]))
        {
            position++;
        }

        // TryParse rejects digits that overflow int, leaving code 0 and returning false.
        return position > digitsStart
            && message.AsSpan(position).StartsWith(CodeSeparator, StringComparison.Ordinal)
            && int.TryParse(
                message.AsSpan(codeStart, position - codeStart),
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out code);
    }
}
