namespace EpiInfo.Data.Metadata;

/// <summary>
/// Fixed SQL read by <c>SqlMetadataReader</c> from the Epi Info <c>meta*</c> tables and the SQL Server catalogue.
/// Every statement is a <c>SELECT</c> whose only variable input is a parameter; the column aliases are the
/// contract with the reader's Dapper row classes.
/// </summary>
/// <remarks>
/// The metadata tables are referenced unqualified, as legacy Epi Info does, so they resolve through the
/// caller's default schema. The parameter names below are spelled literally inside the statements, because a
/// constant built from them would need interpolation; keep both spellings identical.
/// </remarks>
internal static class MetadataQueries
{
    /// <summary>Name of the form id parameter, bound as <c>DbType.Int32</c>.</summary>
    public const string ViewIdParameter = "@viewId";

    /// <summary>Name of the table-name list parameter: a JSON array of strings, bound as <c>DbType.String</c> with size -1 (<c>nvarchar(max)</c>).</summary>
    public const string NamesParameter = "@names";

    /// <summary>Name of the session temporary table parameter, including its leading <c>#</c>, bound as <c>DbType.String</c>.</summary>
    public const string TableNameParameter = "@tableName";

    /// <summary>No parameters; returns <c>ViewId</c>, <c>Name</c>, <c>PageCount</c>, one row per <c>metaViews</c> row ordered by <c>ViewId</c>.</summary>
    /// <remarks>Related views are listed too: legacy does not filter on <c>IsRelatedView</c>.</remarks>
    public const string ListForms = """
        SELECT v.[ViewId] AS ViewId,
               v.[Name] AS Name,
               (SELECT COUNT(*) FROM [metaPages] AS p WHERE p.[ViewId] = v.[ViewId]) AS PageCount
        FROM [metaViews] AS v
        ORDER BY v.[ViewId];
        """;

    /// <summary>Takes <c>@viewId</c>; returns <c>ViewId</c>, <c>Name</c> of that <c>metaViews</c> row, or no row for an unknown form.</summary>
    public const string View = """
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
    public const string DataTableName = """
        SELECT DISTINCT [DataTableName] AS DataTableName
        FROM [metaFields]
        WHERE [ViewId] = @viewId
          AND [DataTableName] IS NOT NULL;
        """;

    /// <summary>Takes <c>@viewId</c>; returns <c>PageId</c>, <c>Name</c>, <c>Position</c> (<c>smallint</c>, 0-based in the seed) ordered by <c>Position</c>, then <c>PageId</c>.</summary>
    public const string Pages = """
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
    public const string Fields = """
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

    /// <summary>Takes <c>@names</c>; returns <c>TableName</c>, <c>ColumnName</c>, <c>TypeName</c>, <c>MaxLengthBytes</c>, <c>IsNullable</c>, <c>IsIdentity</c>, <c>ColumnId</c> for the named tables of the caller's default schema, ordered by table, then <c>ColumnId</c>.</summary>
    /// <remarks>
    /// <para>
    /// The names arrive as one JSON array read with <c>OPENJSON</c>, so the text stays constant and the list stays a
    /// single typed parameter; Dapper's list expansion is not used because the reader binds every parameter with an
    /// explicit <c>DbType</c>. <c>OPENJSON</c> needs compatibility level 130 or later; the rehearsal database uses 160.
    /// </para>
    /// <para>
    /// Names are compared under an explicit case-insensitive collation whatever the database collation is, because
    /// seeded metadata spells code tables differently from the catalogue (<c>codeState</c> against <c>codestate</c>).
    /// The reader prefers an exact ordinal match among the rows returned.
    /// </para>
    /// <para>
    /// <c>sys.types</c> is joined on <c>user_type_id</c>, so a column of an alias type reports the alias name, which
    /// the field-type mapper does not admit, and the field ends up unsupported.
    /// </para>
    /// </remarks>
    public const string Columns = """
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
        WHERE t.[schema_id] = SCHEMA_ID()
          AND t.[name] COLLATE Latin1_General_100_CI_AS IN
              (SELECT n.[value] COLLATE Latin1_General_100_CI_AS FROM OPENJSON(@names) AS n)
        ORDER BY t.[name], c.[column_id];
        """;

    /// <summary>Takes <c>@viewId</c>; returns <c>CheckCode</c> (<c>ntext</c>, possibly <c>NULL</c>) of the form, or no row for an unknown form.</summary>
    public const string CheckCode = """
        SELECT [CheckCode] AS CheckCode
        FROM [metaViews]
        WHERE [ViewId] = @viewId;
        """;

    /// <summary>Takes <c>@tableName</c>; returns one unnamed column, the <c>tempdb</c> object id of the session temporary table, or <c>NULL</c> when it does not exist.</summary>
    /// <remarks>Used only by the reserved-word test fixture, so a missing <c>#</c> table fails with a clear message.</remarks>
    public const string TempTableObjectId = """
        SELECT OBJECT_ID(N'tempdb..' + @tableName);
        """;

    /// <summary>Takes <c>@tableName</c>; returns <c>ColumnName</c>, <c>TypeName</c>, <c>MaxLengthBytes</c>, <c>IsNullable</c>, <c>IsIdentity</c>, <c>ColumnId</c> of the session temporary table, ordered by <c>ColumnId</c>.</summary>
    /// <remarks>
    /// Used only by the reserved-word test fixture. No <c>TableName</c> column is returned: the <c>tempdb</c>
    /// catalogue holds a mangled name, so the reader keeps the session name it was given.
    /// </remarks>
    public const string TempdbColumns = """
        SELECT c.[name] AS ColumnName,
               ty.[name] AS TypeName,
               c.[max_length] AS MaxLengthBytes,
               c.[is_nullable] AS IsNullable,
               c.[is_identity] AS IsIdentity,
               c.[column_id] AS ColumnId
        FROM tempdb.sys.columns AS c
        INNER JOIN tempdb.sys.types AS ty ON ty.[user_type_id] = c.[user_type_id]
        WHERE c.[object_id] = OBJECT_ID(N'tempdb..' + @tableName)
        ORDER BY c.[column_id];
        """;
}
