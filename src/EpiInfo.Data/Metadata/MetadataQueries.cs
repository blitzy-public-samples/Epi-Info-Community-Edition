namespace EpiInfo.Data.Metadata;

/// <summary>
/// Fixed SQL read by <c>SqlMetadataReader</c> from the Epi Info <c>meta*</c> tables and the SQL Server catalogue.
/// Every statement is a <c>SELECT</c> whose only variable input is a parameter, and <see cref="Columns"/> picks its
/// <c>OPENJSON</c> path or its fallback path by the database's compatibility level; the column aliases are the
/// contract with the reader's Dapper row classes.
/// </summary>
/// <remarks>
/// <para>
/// The metadata tables are referenced unqualified, as legacy Epi Info does, so they resolve as any unqualified
/// name does: in the caller's default schema first, then in <c>dbo</c>. The parameter names below are spelled
/// literally inside the statements, because a constant built from them would need interpolation; keep both
/// spellings identical.
/// </para>
/// <para>
/// The six statements of a form read (<see cref="View"/>, <see cref="DataTableName"/>, <see cref="Pages"/>,
/// <see cref="Fields"/>, <see cref="Columns"/> and <see cref="TableClaims"/>) are composed from row sources that
/// <see cref="FormFingerprint"/> embeds unchanged, so the probe a write sends inside its transaction digests exactly
/// the rows and columns a form read returns.
/// </para>
/// </remarks>
internal static class MetadataQueries
{
    /// <summary>Name of the form id parameter, bound as <c>DbType.Int32</c>.</summary>
    public const string ViewIdParameter = "@viewId";

    /// <summary>Name of the table-name list parameter: a JSON array of strings, bound as <c>DbType.String</c> with size -1 (<c>nvarchar(max)</c>).</summary>
    public const string NamesParameter = "@names";

    /// <summary>Name of the base-table name parameter of <see cref="TableClaims"/>: the form's one non-blank <c>DataTableName</c>, exactly as the reader holds it, bound as <c>DbType.String</c> with size -1 (<c>nvarchar(max)</c>).</summary>
    public const string BaseNameParameter = "@baseName";

    /// <summary>Name of the session temporary table parameter: the whole session name, including its single leading <c>#</c>, bound as <c>DbType.String</c> and quoted as one identifier part by the statement.</summary>
    public const string TableNameParameter = "@tableName";

    /// <summary>Name of the <c>tempdb</c> object id parameter, the id <see cref="TempTableObjectId"/> resolved, bound as <c>DbType.Int32</c>.</summary>
    public const string ObjectIdParameter = "@objectId";

    /// <summary>Name of the check-code length parameter of <see cref="CheckCode"/>, the most UTF-16 code units the statement returns, bound as <c>DbType.Int32</c>.</summary>
    public const string MaxLengthParameter = "@maxLength";

    /// <summary>No parameters; returns <c>ViewId</c>, <c>Name</c>, <c>PageCount</c>, one row per <c>metaViews</c> row ordered by <c>ViewId</c>.</summary>
    /// <remarks>Related views are listed too: legacy does not filter on <c>IsRelatedView</c>.</remarks>
    internal const string ListForms = """
        SELECT v.[ViewId] AS ViewId,
               v.[Name] AS Name,
               (SELECT COUNT(*) FROM [metaPages] AS p WHERE p.[ViewId] = v.[ViewId]) AS PageCount
        FROM [metaViews] AS v
        ORDER BY v.[ViewId];
        """;

    /// <summary>Name of the form verification probe's result column, the digest <see cref="FormFingerprint"/> returns.</summary>
    public const string FingerprintColumn = "Fingerprint";

    // The row sources of a form read: each statement below is its source followed by its ORDER BY, and
    // FormFingerprint embeds each source unchanged as a derived table, so both select the same rows and columns.
    private const string ViewSource = """
        SELECT v.[ViewId] AS ViewId,
               v.[Name] AS Name
        FROM [metaViews] AS v
        WHERE v.[ViewId] = @viewId
        """;

    private const string DataTableNameSource = """
        SELECT DISTINCT [DataTableName] AS DataTableName
        FROM [metaFields]
        WHERE [ViewId] = @viewId
          AND [DataTableName] IS NOT NULL
        """;

    private const string PagesSource = """
        SELECT [PageId] AS PageId,
               [Name] AS Name,
               [Position] AS Position
        FROM [metaPages]
        WHERE [ViewId] = @viewId
        """;

    private const string FieldsSource = """
        SELECT f.[FieldId] AS FieldId,
               f.[Name] AS Name,
               f.[PromptText] AS Prompt,
               f.[TabIndex] AS TabIndex,
               f.[PageId] AS PageId,
               f.[IsRequired] AS IsRequired,
               f.[IsReadOnly] AS IsReadOnly,
               f.[IsEncrypted] AS IsEncrypted,
               f.[MaxLength] AS MaxLength,
               f.[Lower] AS Lower,
               f.[Upper] AS Upper,
               f.[Pattern] AS Pattern,
               f.[SourceTableName] AS SourceTableName,
               f.[CodeColumnName] AS CodeColumnName,
               f.[TextColumnName] AS TextColumnName,
               f.[Sort] AS Sort,
               f.[List] AS List,
               f.[FieldTypeId] AS FieldTypeId,
               ft.[Name] AS FieldTypeName
        FROM [metaFields] AS f
        INNER JOIN [metaFieldTypes] AS ft ON ft.[FieldTypeId] = f.[FieldTypeId]
        WHERE f.[ViewId] = @viewId
        """;

    // The catalogue columns of the tables a list of names reaches, up to the IN that each user completes with its own
    // list of names: Columns with @names, FormFingerprint with the form's own names. The user tables are the
    // sys.objects rows of type U, the rows of sys.tables, read without the joins sys.tables adds for columns this
    // source never selects.
    private const string ColumnsSource = """
        SELECT t.[name] AS TableName,
               c.[name] AS ColumnName,
               ty.[name] AS TypeName,
               c.[max_length] AS MaxLengthBytes,
               c.[is_nullable] AS IsNullable,
               c.[is_identity] AS IsIdentity,
               c.[column_id] AS ColumnId,
               t.[object_id] AS ObjectId,
               CONVERT(nvarchar(16), CAST(t.[modify_date] AS binary(8)), 2) AS ModifiedStamp
        FROM sys.objects AS t
        INNER JOIN sys.columns AS c ON c.[object_id] = t.[object_id]
        INNER JOIN sys.types AS ty ON ty.[user_type_id] = c.[user_type_id]
        WHERE t.[type] = 'U'
          AND t.[object_id] = OBJECT_ID(QUOTENAME(t.[name]))
          AND t.[name] COLLATE Latin1_General_100_CI_AS IN
        """;

    // The common table expressions of TableClaims, without their WITH: [Own], the form's own table names; [Split],
    // each own name cut before each of its last 1 to 11 UTF-16 code units; and [Named], each form's distinct
    // data-table names. FormFingerprint uses all three.
    private const string ClaimSources = """
        [Own] ([Name]) AS
        (
            SELECT @baseName
            UNION ALL
            SELECT @baseName + CAST(p.[PageId] AS nvarchar(11))
            FROM [metaPages] AS p
            WHERE p.[ViewId] = @viewId
        ),
        [Split] ([Prefix], [Suffix]) AS
        (
            -- The prefix length is guarded inside the expression, because SQL Server may compute it before the filter.
            SELECT SUBSTRING(o.[Name] COLLATE Latin1_General_100_BIN2, 1, CASE WHEN n.[Units] > s.[Length] THEN n.[Units] - s.[Length] ELSE 0 END),
                   SUBSTRING(o.[Name] COLLATE Latin1_General_100_BIN2, n.[Units] - s.[Length] + 1, s.[Length])
            FROM [Own] AS o
            CROSS APPLY (SELECT DATALENGTH(o.[Name]) / 2) AS n ([Units])
            CROSS JOIN (VALUES (1), (2), (3), (4), (5), (6), (7), (8), (9), (10), (11)) AS s ([Length])
            WHERE n.[Units] > s.[Length]
        ),
        [Named] ([ViewId], [DataTableName], [Length]) AS
        (
            -- One row per form and spelling, kept apart by its binary text and its length, before any comparison.
            SELECT f.[ViewId], f.[DataTableName] COLLATE Latin1_General_100_BIN2, DATALENGTH(f.[DataTableName])
            FROM [metaFields] AS f
            GROUP BY f.[ViewId], f.[DataTableName] COLLATE Latin1_General_100_BIN2, DATALENGTH(f.[DataTableName])
        )
        """;

    private const string ClaimsSource = """
        SELECT f.[ViewId] AS ViewId,
               f.[DataTableName] AS DataTableName,
               CAST(NULL AS int) AS PageId
        FROM [Named] AS f
        INNER JOIN [Own] AS o
            ON f.[DataTableName] COLLATE Latin1_General_100_CI_AS = o.[Name] COLLATE Latin1_General_100_CI_AS
        WHERE f.[ViewId] <> @viewId
        GROUP BY f.[ViewId], f.[DataTableName], f.[Length]
        UNION ALL
        SELECT p.[ViewId],
               f.[DataTableName],
               p.[PageId]
        FROM [Split] AS s
        INNER JOIN [metaPages] AS p
            ON CAST(p.[PageId] AS nvarchar(11)) COLLATE Latin1_General_100_BIN2 = s.[Suffix] COLLATE Latin1_General_100_BIN2
           AND p.[ViewId] <> @viewId
        INNER JOIN [Named] AS f
            ON f.[ViewId] = p.[ViewId]
           AND f.[DataTableName] COLLATE Latin1_General_100_CI_AS = s.[Prefix] COLLATE Latin1_General_100_CI_AS
        GROUP BY p.[ViewId], p.[PageId], f.[DataTableName], f.[Length]
        """;

    /// <summary>Takes <c>@viewId</c>; returns <c>ViewId</c>, <c>Name</c> of that <c>metaViews</c> row, or no row for an unknown form.</summary>
    internal const string View = $"""
        {ViewSource};
        """;

    /// <summary>Takes <c>@viewId</c>; returns the distinct non-null <c>DataTableName</c> values of the form's <c>metaFields</c> rows.</summary>
    /// <remarks>
    /// Same statement as legacy <c>MetadataDbProvider.GetDataTableName</c>. Legacy takes the first row; the reader
    /// instead requires exactly one non-blank row and treats anything else as broken metadata.
    /// </remarks>
    internal const string DataTableName = $"""
        {DataTableNameSource};
        """;

    /// <summary>Takes <c>@viewId</c>; returns <c>PageId</c>, <c>Name</c>, <c>Position</c> (<c>smallint</c>, 0-based in the seed) ordered by <c>Position</c>, then <c>PageId</c>.</summary>
    internal const string Pages = $"""
        {PagesSource}
        ORDER BY [Position], [PageId];
        """;

    /// <summary>Takes <c>@viewId</c>; returns every <c>metaFields</c> row of the form with its <c>metaFieldTypes</c> name as <c>FieldTypeName</c>, ordered by <c>FieldId</c>.</summary>
    /// <remarks>
    /// System rows with a null <c>PageId</c> (<c>UniqueKey</c>, <c>RecStatus</c>, <c>GlobalRecordId</c>) are included;
    /// the reader and the form-definition factory decide what is listed. <c>PromptText</c> is aliased <c>Prompt</c>.
    /// </remarks>
    internal const string Fields = $"""
        {FieldsSource}
        ORDER BY f.[FieldId];
        """;

    /// <summary>Takes <c>@viewId</c> and <c>@baseName</c>; returns <c>ViewId</c>, <c>DataTableName</c>, <c>PageId</c>: each table name another form's metadata claims that can name one of this form's tables.</summary>
    /// <remarks>
    /// <para>
    /// The form's own names (<c>[Own]</c>) are <c>@baseName</c> and <c>@baseName</c> followed by each of its
    /// <c>metaPages.PageId</c> values in decimal digits, the names the reader resolves. Another form claims each of
    /// its <c>DataTableName</c> values, and that name followed by each of its page ids. A base claim is a distinct
    /// (<c>ViewId</c>, <c>DataTableName</c>) of another form whose name equals an own name, with <c>PageId</c>
    /// <c>NULL</c>. A page claim is a distinct (<c>ViewId</c>, <c>DataTableName</c>, <c>PageId</c>) of another form
    /// whose name followed by the page id equals an own name: each own name is split before each of its last 1 to 11
    /// UTF-16 code units (<c>[Split]</c>), 11 being the longest decimal <c>int</c>, the suffix is compared with the
    /// page id's digits under a binary collation and the prefix with the data-table name. Rows of the form itself are
    /// not returned. A form with several data-table names, which the reader refuses for itself, still claims every
    /// one, so adding a name to a form never frees the tables of its others.
    /// </para>
    /// <para>
    /// Names are compared under the same explicit case-insensitive collation as <see cref="Columns"/>, so every
    /// claim that can resolve to one of the form's tables is returned: a claim resolves to one of them only when it
    /// equals, letter by letter ignoring case, a catalogue table that equals an own name. The reader decides each
    /// returned claim by resolving it, so a returned row that names no table of the form, such as one equal only
    /// under trailing-space padding, refuses nothing. Lengths come from <c>DATALENGTH</c>, which counts trailing
    /// spaces where <c>LEN</c> does not, and the text is cut under a binary collation, so positions count UTF-16
    /// code units under any database collation. Each claimed spelling is grouped by its binary text and its length,
    /// so case and trailing-space variants are each returned as stored; each form's spellings are grouped so before
    /// any comparison (<c>[Named]</c>), so the comparisons grow with the forms' distinct data-table names, not with
    /// their fields. The statement uses no <c>TRY_CAST</c>,
    /// <c>OPENJSON</c> or <c>STRING_SPLIT</c>, so it runs at every compatibility level SQL Server 2022 supports.
    /// </para>
    /// </remarks>
    internal const string TableClaims = $"""
        WITH {ClaimSources}
        {ClaimsSource};
        """;

    /// <summary>Takes <c>@names</c>; returns <c>TableName</c>, <c>ColumnName</c>, <c>TypeName</c>, <c>MaxLengthBytes</c>, <c>IsNullable</c>, <c>IsIdentity</c>, <c>ColumnId</c>, <c>ObjectId</c>, <c>ModifiedStamp</c> for each named table that its unqualified name reaches for the caller, ordered by table, then <c>ColumnId</c>.</summary>
    /// <remarks>
    /// <para>
    /// The names arrive as one JSON array, so the text stays constant and the list stays a single typed parameter;
    /// Dapper's list expansion is not used because the reader binds every parameter with an explicit
    /// <c>DbType</c>. The array is read with <c>OPENJSON</c> when it is available, at database compatibility level
    /// 130 or higher. Below 130 the same array is read by a fixed recursive decoder of the JSON string syntax, so
    /// discovery works at every compatibility level SQL Server 2022 supports. Deferred name resolution keeps the
    /// <c>OPENJSON</c> branch from being bound below 130. The seed scripts under <c>testdata/sql</c> pin no level,
    /// so the rehearsal database takes the server's <c>model</c> level.
    /// </para>
    /// <para>
    /// The decoder reads the text under a binary collation, so no database collation can ignore or fold a quote or
    /// a backslash, and every position counts UTF-16 code units, as the searches do. It begins after the first
    /// quote. Inside a string it moves to the next quote or backslash. A quote ends the string, which is emitted,
    /// and the next string begins after the following quote. A backslash decodes <c>\uXXXX</c> to that UTF-16 code
    /// unit, <c>\b</c>, <c>\f</c>, <c>\n</c>, <c>\r</c> and <c>\t</c> to their control characters, and any other
    /// escaped character to itself. Every step moves forward through the text and an unterminated string ends the
    /// scan, so the unlimited recursion always finishes.
    /// </para>
    /// <para>
    /// Names are compared under an explicit case-insensitive collation whatever the database collation is, because
    /// seeded metadata spells code tables differently from the catalogue (<c>codeState</c> against <c>codestate</c>).
    /// The reader prefers an exact ordinal match among the rows returned.
    /// </para>
    /// <para>
    /// A catalogue table is returned only when its own bracket-quoted one-part name,
    /// <c>OBJECT_ID(QUOTENAME(t.[name]))</c>, reaches that very table for the caller: <c>OBJECT_ID</c> resolves a
    /// one-part name in the caller's default schema first, then in <c>dbo</c>, as SQL Server resolves every
    /// unqualified reference. No schema is returned. The reader keeps only that one-part catalogue name, and the
    /// one-part identifiers it builds from it in later SQL, like the unqualified <c>meta*</c> reads of this class,
    /// resolve the same way, so descriptors, catalogue rows and record or code-table SQL always reach one object.
    /// A table, view or synonym of the same name in the default schema shadows a <c>dbo</c> table: a shadowing
    /// table is the one returned, and a shadowing view or synonym, which is not a user table (a <c>sys.objects</c>
    /// row of type <c>U</c>, as every <c>sys.tables</c> row is), leaves the name with no row.
    /// </para>
    /// <para>
    /// <c>sys.types</c> is joined on <c>user_type_id</c>, so a column of an alias type reports the alias name, which
    /// the field-type mapper does not admit, and the field ends up unsupported.
    /// </para>
    /// <para>
    /// <c>ObjectId</c> and <c>ModifiedStamp</c> identify the table each row belongs to: its <c>object_id</c>, and its
    /// <c>modify_date</c> as the 16 hexadecimal digits of the <c>datetime</c>'s eight bytes, which change when the
    /// table is dropped and created again or altered. The reader builds no schema from them; they make the rows a
    /// form read returns, and so the digest <see cref="FormFingerprint"/> compares, name the very tables the read
    /// bound.
    /// </para>
    /// </remarks>
    internal const string Columns = $"""
        IF (SELECT d.[compatibility_level] FROM sys.databases AS d WHERE d.[database_id] = DB_ID()) >= 130
            {ColumnsSource}
                  (SELECT n.[value] COLLATE Latin1_General_100_CI_AS FROM OPENJSON(@names) AS n)
            ORDER BY t.[name], c.[column_id];
        ELSE
            WITH [Scan] ([Position], [Text], [Name]) AS
            (
                -- The first string's content starts after the first quote.
                SELECT CAST(CHARINDEX(N'"', @names COLLATE Latin1_General_100_BIN2) + 1 AS bigint),
                       CAST(N'' AS nvarchar(max)) COLLATE Latin1_General_100_BIN2,
                       CAST(NULL AS nvarchar(max)) COLLATE Latin1_General_100_BIN2
                WHERE CHARINDEX(N'"', @names COLLATE Latin1_General_100_BIN2) > 0
                UNION ALL
                -- Each step reads the current string up to its next quote or backslash ([At]). A quote ends the
                -- string: [Name] is emitted and [Position] moves past the next quote, or to 0 when none is left.
                -- A backslash appends its decoded escape to [Text] and [Position] moves past the escape.
                SELECT CASE
                           WHEN m.[Mark] = N'"' THEN ISNULL(NULLIF(CHARINDEX(N'"', @names COLLATE Latin1_General_100_BIN2, m.[At] + 1), 0) + 1, 0)
                           WHEN m.[Escape] = N'u' THEN m.[At] + 6
                           ELSE m.[At] + 2
                       END,
                       CASE
                           WHEN m.[Mark] = N'"' THEN N''
                           ELSE s.[Text] + m.[Chunk] + CASE m.[Escape]
                                                           WHEN N'u' THEN NCHAR(CONVERT(int, CONVERT(varbinary(2), N'0x' + SUBSTRING(@names COLLATE Latin1_General_100_BIN2, m.[At] + 2, 4), 1)))
                                                           WHEN N'b' THEN NCHAR(8)
                                                           WHEN N'f' THEN NCHAR(12)
                                                           WHEN N'n' THEN NCHAR(10)
                                                           WHEN N'r' THEN NCHAR(13)
                                                           WHEN N't' THEN NCHAR(9)
                                                           ELSE m.[Escape]
                                                       END
                       END,
                       CASE WHEN m.[Mark] = N'"' THEN s.[Text] + m.[Chunk] END
                FROM [Scan] AS s
                CROSS APPLY (SELECT p.[At],
                                    SUBSTRING(@names COLLATE Latin1_General_100_BIN2, s.[Position], p.[At] - s.[Position]) AS [Chunk],
                                    SUBSTRING(@names COLLATE Latin1_General_100_BIN2, p.[At], 1) AS [Mark],
                                    SUBSTRING(@names COLLATE Latin1_General_100_BIN2, p.[At] + 1, 1) AS [Escape]
                             FROM (VALUES (s.[Position] - 1 + NULLIF(PATINDEX(N'%["\]%', SUBSTRING(@names COLLATE Latin1_General_100_BIN2, s.[Position], 2147483647)), 0))) AS p ([At])) AS m
                -- [At] is NULL when the rest of the text holds no quote or backslash: an unterminated string ends the scan.
                WHERE s.[Position] > 0
                  AND m.[At] IS NOT NULL
            )
            {ColumnsSource}
                  (SELECT s.[Name] COLLATE Latin1_General_100_CI_AS FROM [Scan] AS s WHERE s.[Name] IS NOT NULL)
            ORDER BY t.[name], c.[column_id]
            OPTION (MAXRECURSION 0);
        """;

    /// <summary>
    /// Takes <c>@viewId</c> and <c>@baseName</c>; returns one row, <c>Fingerprint</c>: the SHA-256 digest
    /// (<c>varbinary</c>, 32 bytes) of every row <see cref="View"/>, <see cref="DataTableName"/>, <see cref="Pages"/>,
    /// <see cref="Fields"/>, <see cref="Columns"/> and <see cref="TableClaims"/> return for the form now, the form's
    /// own table names standing for <c>@names</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The record repository sends it inside a write's transaction, before any record statement, in place of those
    /// six statements, with the view id and base name of the form read the request made outside the transaction. The
    /// reader computes the same digest from the rows that read returned (<c>SqlMetadataReader</c>), so an equal
    /// digest shows that every row and column a form read consumes is unchanged, and the form, its catalogue schema
    /// and its ownership decision stand as that read made them; any other digest has the reader read the form again.
    /// </para>
    /// <para>
    /// Each row source of those statements is embedded unchanged as a derived table, so the probe selects exactly
    /// their rows. The columns follow from the form read's own: <see cref="Columns"/> reads the tables of the form's
    /// base and page names, and here <c>[Own]</c>, the names <see cref="TableClaims"/> derives from <c>@baseName</c>
    /// and the form's current <c>metaPages</c> rows, stands for that list; equal pages and data-table names give the
    /// same names, and different ones already change the digest.
    /// </para>
    /// <para>
    /// A row is the source's one-letter tag (<c>V</c>, <c>T</c>, <c>P</c>, <c>F</c>, <c>C</c>, <c>L</c>) followed by
    /// each column in the source's order: <c>-</c> for <c>NULL</c>, and otherwise the value's text preceded by its
    /// length in UTF-16 code units and a colon. A column the reader reads as a number or a bit gives its decimal text,
    /// converted to <c>nvarchar(41)</c>, which holds that text whole for every numeric type; any other column is cast
    /// to <c>nvarchar(max)</c>, whole. The encoding is prefix-free, so two rows encode alike only when every value is
    /// alike. Each branch hashes each of its encoded rows with SHA-256 over its UTF-16 code units, so only 32-byte
    /// digests leave the branches; the row digests, in ascending byte order so the order rows arrive in does not
    /// matter, are written as uppercase hexadecimal, concatenated, and hashed again. No row gives the digest of the
    /// empty text. The
    /// statement uses no <c>OPENJSON</c>, <c>STRING_AGG</c>, <c>TRY_CAST</c> or <c>STRING_SPLIT</c>, only
    /// <c>CONCAT</c>, <c>HASHBYTES</c> and <c>FOR XML PATH</c>, so it runs at every compatibility level SQL Server
    /// 2022 supports, and it reads only <c>meta*</c> tables and catalogue views, naming no record table.
    /// </para>
    /// </remarks>
    internal static readonly string FormFingerprint = $"""
        WITH {ClaimSources},
        [Fingerprinted] ([Hash]) AS
        (
            {FingerprintRows('V', ViewSource, Number("ViewId"), Text("Name"))}
            UNION ALL
            {FingerprintRows('T', DataTableNameSource, Text("DataTableName"))}
            UNION ALL
            {FingerprintRows('P', PagesSource, Number("PageId"), Text("Name"), Number("Position"))}
            UNION ALL
            {FingerprintRows(
                'F',
                FieldsSource,
                Number("FieldId"),
                Text("Name"),
                Text("Prompt"),
                Number("TabIndex"),
                Number("PageId"),
                Number("IsRequired"),
                Number("IsReadOnly"),
                Number("IsEncrypted"),
                Number("MaxLength"),
                Text("Lower"),
                Text("Upper"),
                Text("Pattern"),
                Text("SourceTableName"),
                Text("CodeColumnName"),
                Text("TextColumnName"),
                Number("Sort"),
                Text("List"),
                Number("FieldTypeId"),
                Text("FieldTypeName"))}
            UNION ALL
            {FingerprintRows(
                'C',
                ColumnsSource + " (SELECT o.[Name] COLLATE Latin1_General_100_CI_AS FROM [Own] AS o)",
                Text("TableName"),
                Text("ColumnName"),
                Text("TypeName"),
                Number("MaxLengthBytes"),
                Number("IsNullable"),
                Number("IsIdentity"),
                Number("ColumnId"),
                Number("ObjectId"),
                Text("ModifiedStamp"))}
            UNION ALL
            {FingerprintRows('L', ClaimsSource, Number("ViewId"), Text("DataTableName"), Number("PageId"))}
        )
        SELECT HASHBYTES('SHA2_256', ISNULL((SELECT CONVERT(nvarchar(64), h.[Hash], 2)
                                             FROM [Fingerprinted] AS h
                                             ORDER BY h.[Hash]
                                             FOR XML PATH('')), N'')) AS {FingerprintColumn};
        """;

    /// <summary>Takes <c>@viewId</c> and <c>@maxLength</c>; returns <c>CheckCode</c> (<c>nvarchar(max)</c>, possibly <c>NULL</c>), the form's program cut to its first <c>@maxLength</c> UTF-16 code units, or no row for an unknown form.</summary>
    /// <remarks>
    /// The column is <c>ntext</c>, which <c>LEFT</c> does not take and <c>SUBSTRING</c> returns as
    /// <c>nvarchar(4000)</c>, cutting any longer program at 4,000 code units, so the text is converted to
    /// <c>nvarchar(max)</c> first. The binary collation makes <c>LEFT</c> count UTF-16 code units under any column or
    /// database collation, because a supplementary-character (<c>_SC</c>) collation counts a surrogate pair as one
    /// character. The server cuts the text, so no more than <c>@maxLength</c> code units of an oversized program ever
    /// reach the client. A <c>NULL</c> program stays <c>NULL</c>.
    /// </remarks>
    internal const string CheckCode = """
        SELECT LEFT(CONVERT(nvarchar(max), [CheckCode]) COLLATE Latin1_General_100_BIN2, @maxLength) AS CheckCode
        FROM [metaViews]
        WHERE [ViewId] = @viewId;
        """;

    /// <summary>Takes <c>@tableName</c>; returns one unnamed <c>int</c> column, the <c>tempdb</c> object id of the calling session's temporary user table of that name, or <c>NULL</c> when the session has none.</summary>
    /// <remarks>
    /// Used only by the reserved-word test fixture, so a missing <c>#</c> table fails with a clear message. The
    /// reader admits only a session-local name: a single <c>#</c> and at most 116 characters, short enough that
    /// <c>QUOTENAME</c> quotes it whole as one bracket-quoted part with any <c>]</c> doubled. A <c>.</c> or <c>]</c>
    /// in the name therefore cannot split it into other name parts, and the <c>U</c> type restricts the result to a
    /// user table. SQL Server resolves a <c>#</c> name only among the calling session's own temporary tables.
    /// </remarks>
    internal const string TempTableObjectId = """
        SELECT OBJECT_ID(N'tempdb..' + QUOTENAME(@tableName), N'U');
        """;

    /// <summary>Takes <c>@objectId</c>; returns <c>ColumnName</c>, <c>TypeName</c>, <c>MaxLengthBytes</c>, <c>IsNullable</c>, <c>IsIdentity</c>, <c>ColumnId</c> of the <c>tempdb</c> table with that object id, ordered by <c>ColumnId</c>.</summary>
    /// <remarks>
    /// Used only by the reserved-word test fixture, with the id <see cref="TempTableObjectId"/> resolved on the same
    /// session, so the columns come from the very table that name resolution found and the name is not resolved a
    /// second time. No <c>TableName</c> column is returned: the <c>tempdb</c> catalogue holds a mangled name, so the
    /// reader keeps the session name it was given.
    /// </remarks>
    internal const string TempdbColumns = """
        SELECT c.[name] AS ColumnName,
               ty.[name] AS TypeName,
               c.[max_length] AS MaxLengthBytes,
               c.[is_nullable] AS IsNullable,
               c.[is_identity] AS IsIdentity,
               c.[column_id] AS ColumnId
        FROM tempdb.sys.columns AS c
        INNER JOIN tempdb.sys.types AS ty ON ty.[user_type_id] = c.[user_type_id]
        WHERE c.[object_id] = @objectId
        ORDER BY c.[column_id];
        """;

    // One branch of FormFingerprint's [Fingerprinted]: the SHA-256 digest of every row of source, read as the derived
    // table r, encoded as tag followed by each column in turn (FormFingerprint remarks). The names are this class's
    // own aliases.
    private static string FingerprintRows(char tag, string source, params DigestColumn[] columns)
    {
        IEnumerable<string> encoded = columns.Select(column =>
        {
            string text = column.IsNumber
                ? $"CONVERT(nvarchar(41), r.[{column.Name}])"
                : $"CAST(r.[{column.Name}] AS nvarchar(max))";
            return $"CASE WHEN r.[{column.Name}] IS NULL THEN N'-' " +
                $"ELSE CONCAT(DATALENGTH({text}) / 2, N':', {text}) END";
        });
        return $"SELECT HASHBYTES('SHA2_256', CONCAT(N'{tag}', {string.Join(", ", encoded)}))\nFROM ({source}) AS r";
    }

    // A column the reader reads as text: its value cast to nvarchar(max), whole.
    private static DigestColumn Text(string name) => new(name, IsNumber: false);

    // A column the reader reads only as a number or a bit: its decimal text, which for a value of any SQL Server
    // numeric type is at most 40 characters (a negative decimal(38, 37)), so nvarchar(41) holds it whole.
    private static DigestColumn Number(string name) => new(name, IsNumber: true);

    // One column of a FormFingerprint branch: its alias in the source, and whether its text is that of a number.
    private readonly record struct DigestColumn(string Name, bool IsNumber);
}
