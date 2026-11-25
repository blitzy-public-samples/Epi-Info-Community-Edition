namespace EpiInfo.Data.Metadata;

/// <summary>
/// Fixed SQL read by <c>SqlMetadataReader</c> from the Epi Info <c>meta*</c> tables and the SQL Server catalogue.
/// Every statement is a <c>SELECT</c> whose only variable input is a parameter, and <see cref="Columns"/> picks its
/// <c>OPENJSON</c> path or its fallback path by the database's compatibility level; the column aliases are the
/// contract with the reader's Dapper row classes.
/// </summary>
/// <remarks>
/// The metadata tables are referenced unqualified, as legacy Epi Info does, so they resolve as any unqualified
/// name does: in the caller's default schema first, then in <c>dbo</c>. The parameter names below are spelled
/// literally inside the statements, because a constant built from them would need interpolation; keep both
/// spellings identical.
/// </remarks>
internal static class MetadataQueries
{
    /// <summary>Name of the form id parameter, bound as <c>DbType.Int32</c>.</summary>
    public const string ViewIdParameter = "@viewId";

    /// <summary>Name of the table-name list parameter: a JSON array of strings, bound as <c>DbType.String</c> with size -1 (<c>nvarchar(max)</c>).</summary>
    public const string NamesParameter = "@names";

    /// <summary>Name of the session temporary table parameter: the whole session name, including its single leading <c>#</c>, bound as <c>DbType.String</c> and quoted as one identifier part by the statement.</summary>
    public const string TableNameParameter = "@tableName";

    /// <summary>Name of the <c>tempdb</c> object id parameter, the id <see cref="TempTableObjectId"/> resolved, bound as <c>DbType.Int32</c>.</summary>
    public const string ObjectIdParameter = "@objectId";

    /// <summary>No parameters; returns <c>ViewId</c>, <c>Name</c>, <c>PageCount</c>, one row per <c>metaViews</c> row ordered by <c>ViewId</c>.</summary>
    /// <remarks>Related views are listed too: legacy does not filter on <c>IsRelatedView</c>.</remarks>
    internal const string ListForms = """
        SELECT v.[ViewId] AS ViewId,
               v.[Name] AS Name,
               (SELECT COUNT(*) FROM [metaPages] AS p WHERE p.[ViewId] = v.[ViewId]) AS PageCount
        FROM [metaViews] AS v
        ORDER BY v.[ViewId];
        """;

    /// <summary>Takes <c>@viewId</c>; returns <c>ViewId</c>, <c>Name</c> of that <c>metaViews</c> row, or no row for an unknown form.</summary>
    internal const string View = """
        SELECT v.[ViewId] AS ViewId,
               v.[Name] AS Name
        FROM [metaViews] AS v
        WHERE v.[ViewId] = @viewId;
        """;

    /// <summary>Takes <c>@viewId</c>; returns the distinct non-null <c>DataTableName</c> values of the form's <c>metaFields</c> rows.</summary>
    /// <remarks>
    /// Same statement as legacy <c>MetadataDbProvider.GetDataTableName</c>. Legacy takes the first row; the reader
    /// instead requires exactly one non-blank row and treats anything else as broken metadata.
    /// </remarks>
    internal const string DataTableName = """
        SELECT DISTINCT [DataTableName] AS DataTableName
        FROM [metaFields]
        WHERE [ViewId] = @viewId
          AND [DataTableName] IS NOT NULL;
        """;

    /// <summary>Takes <c>@viewId</c>; returns <c>PageId</c>, <c>Name</c>, <c>Position</c> (<c>smallint</c>, 0-based in the seed) ordered by <c>Position</c>, then <c>PageId</c>.</summary>
    internal const string Pages = """
        SELECT [PageId] AS PageId,
               [Name] AS Name,
               [Position] AS Position
        FROM [metaPages]
        WHERE [ViewId] = @viewId
        ORDER BY [Position], [PageId];
        """;

    /// <summary>Takes <c>@viewId</c>; returns every <c>metaFields</c> row of the form with its <c>metaFieldTypes</c> name as <c>FieldTypeName</c>, ordered by <c>FieldId</c>.</summary>
    /// <remarks>
    /// System rows with a null <c>PageId</c> (<c>UniqueKey</c>, <c>RecStatus</c>, <c>GlobalRecordId</c>) are included;
    /// the reader and the form-definition factory decide what is listed. <c>PromptText</c> is aliased <c>Prompt</c>.
    /// </remarks>
    internal const string Fields = """
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
        ORDER BY f.[FieldId];
        """;

    /// <summary>Takes <c>@names</c>; returns <c>TableName</c>, <c>ColumnName</c>, <c>TypeName</c>, <c>MaxLengthBytes</c>, <c>IsNullable</c>, <c>IsIdentity</c>, <c>ColumnId</c> for each named table that its unqualified name reaches for the caller, ordered by table, then <c>ColumnId</c>.</summary>
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
    /// table is the one returned, and a shadowing view or synonym, which is not in <c>sys.tables</c>, leaves the name
    /// with no row.
    /// </para>
    /// <para>
    /// <c>sys.types</c> is joined on <c>user_type_id</c>, so a column of an alias type reports the alias name, which
    /// the field-type mapper does not admit, and the field ends up unsupported.
    /// </para>
    /// </remarks>
    internal const string Columns = """
        IF (SELECT d.[compatibility_level] FROM sys.databases AS d WHERE d.[database_id] = DB_ID()) >= 130
            SELECT t.[name] AS TableName,
                   c.[name] AS ColumnName,
                   ty.[name] AS TypeName,
                   c.[max_length] AS MaxLengthBytes,
                   c.[is_nullable] AS IsNullable,
                   c.[is_identity] AS IsIdentity,
                   c.[column_id] AS ColumnId
            FROM sys.tables AS t
            INNER JOIN sys.columns AS c ON c.[object_id] = t.[object_id]
            INNER JOIN sys.types AS ty ON ty.[user_type_id] = c.[user_type_id]
            WHERE t.[object_id] = OBJECT_ID(QUOTENAME(t.[name]))
              AND t.[name] COLLATE Latin1_General_100_CI_AS IN
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
            SELECT t.[name] AS TableName,
                   c.[name] AS ColumnName,
                   ty.[name] AS TypeName,
                   c.[max_length] AS MaxLengthBytes,
                   c.[is_nullable] AS IsNullable,
                   c.[is_identity] AS IsIdentity,
                   c.[column_id] AS ColumnId
            FROM sys.tables AS t
            INNER JOIN sys.columns AS c ON c.[object_id] = t.[object_id]
            INNER JOIN sys.types AS ty ON ty.[user_type_id] = c.[user_type_id]
            WHERE t.[object_id] = OBJECT_ID(QUOTENAME(t.[name]))
              AND t.[name] COLLATE Latin1_General_100_CI_AS IN
                  (SELECT s.[Name] COLLATE Latin1_General_100_CI_AS FROM [Scan] AS s WHERE s.[Name] IS NOT NULL)
            ORDER BY t.[name], c.[column_id]
            OPTION (MAXRECURSION 0);
        """;

    /// <summary>Takes <c>@viewId</c>; returns <c>CheckCode</c> (<c>ntext</c>, possibly <c>NULL</c>) of the form, or no row for an unknown form.</summary>
    internal const string CheckCode = """
        SELECT [CheckCode] AS CheckCode
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
}
