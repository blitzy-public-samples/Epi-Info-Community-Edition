using System.Collections;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Records;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Domain.Records;
using EpiInfo.Domain.Storage;
using EpiInfo.Domain.Validation;
using EpiInfo.Domain.Values;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Tests.Records;

/// <summary>
/// Provenance tests that need no database. The catalogue schema types offer no way to obtain or change an instance
/// except through <c>SqlMetadataReader</c>; <see cref="RecordSqlBuilder"/>, <see cref="SearchSqlBuilder"/> and the
/// helper types of their files take a string only as a value the statement binds, never as a table or column name;
/// every statement builder takes the reader-built schema its identifiers come from; and <see cref="SqlIdentifier"/>
/// quotes a name publicly only from a schema type. They inspect the types by reflection and build no schema.
/// </summary>
/// <remarks>
/// <c>SqlIdentifierTests</c> owns the quoting itself and <c>ReservedWordIdentifierTests</c> the reserved-word round
/// trip; these tests prove only where identifiers can come from.
/// </remarks>
public sealed class IdentifierProvenanceTests
{
    // Every declared member, whatever its accessibility; the tests keep the ones callable from outside the type.
    private const BindingFlags DeclaredMembers =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance |
        BindingFlags.DeclaredOnly;

    // The declared types a public collection-typed property may have: read-only views and plain sequences.
    private static readonly Type[] ReadOnlyCollectionDefinitions =
    [
        typeof(IReadOnlyList<>),
        typeof(IReadOnlyCollection<>),
        typeof(IReadOnlyDictionary<,>),
        typeof(IEnumerable<>),
    ];

    // The string parameters the builders and the helper types of their files may take, by name. Each is a value the
    // statement binds as a positional parameter, never text placed in the statement.
    private static readonly HashSet<string> ValueStringParameters = new(StringComparer.Ordinal)
    {
        // A record's GlobalRecordId: @pN of each lookup by id, the base-row lock and key, and the base and page writes.
        "globalRecordId",

        // The open record's id a Find leaves out: @pN of the exclusion AppendGlobalRecordIdExclusion renders.
        "excludeGlobalRecordId",
    };

    // The one string parameter that is neither a value nor a name: the statement text a builder has finished, from
    // quoted schema names, fixed literals and positional parameter names, which PositionalParameters.ToStatement pairs
    // with the parameters and does not change. It is exempt on that member alone, so no other member may take text.
    private static readonly (Type Type, string Member, string Parameter) ComposedStatementText =
        (typeof(PositionalParameters), nameof(PositionalParameters.ToStatement), "text");

    // The schema types the metadata reader alone builds: the only carriers of table and column names.
    private static readonly Type[] CatalogueSchemaTypes =
    [
        typeof(FormSchema),
        typeof(PageTableSchema),
        typeof(TableSchema),
        typeof(ColumnSchema),
    ];

    // What a statement builder returns: a statement, a chunked statement list, or a statement with its result layout.
    private static readonly Type[] StatementTypes =
    [
        typeof(SqlStatement),
        typeof(IReadOnlyList<SqlStatement>),
        typeof(RecordSelect),
        typeof(SearchSelect),
        typeof(ListSelect),
    ];

    // The types RecordSqlBuilder.cs and SearchSqlBuilder.cs define: the two builders and the helper types their
    // statements are built and returned with. Reflection cannot tell a type's file, so a type added to either file is
    // added here.
    private static readonly Type[] BuilderFileTypeList =
    [
        typeof(RecordSqlBuilder),
        typeof(PositionalParameters),
        typeof(ProjectedColumn),
        typeof(PagePresence),
        typeof(RecordSelect),
        typeof(SelectedSetClause),
        typeof(SearchSqlBuilder),
        typeof(SearchSelect),
        typeof(ListSelect),
    ];

    /// <summary>The types the two builder files define, one theory row each.</summary>
    public static TheoryData<Type> BuilderFileTypes => new(BuilderFileTypeList);

    /// <summary>The catalogue schema types, whose constructors only the metadata reader calls.</summary>
    public static TheoryData<Type> SchemaTypes => new()
    {
        typeof(TableSchema),
        typeof(ColumnSchema),
        typeof(PageTableSchema),
        typeof(FormSchema),
    };

    [Fact]
    public void AppendSelectedSetPredicate_TakesNoStringParameter()
    {
        MethodInfo[] overloads =
        [
            .. typeof(RecordSqlBuilder)
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.Name == nameof(RecordSqlBuilder.AppendSelectedSetPredicate)),
        ];

        Assert.NotEmpty(overloads);
        Assert.All(overloads, method =>
        {
            Type[] parameterTypes = [.. method.GetParameters().Select(parameter => parameter.ParameterType)];
            Assert.DoesNotContain(typeof(string), parameterTypes);
            Assert.Contains(typeof(SelectedSetClause), parameterTypes);
        });
    }

    [Theory]
    [MemberData(nameof(SchemaTypes))]
    public void SchemaType_OffersNoWayToObtainOrChangeAnInstanceOutsideTheReader(Type schemaType)
    {
        Assert.True(schemaType.IsSealed, "The schema type is not sealed.");
        Assert.True(
            schemaType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0,
            "The schema type has a public constructor.");

        // A public static member that hands out the type, alone or in a collection, would be a factory beside the
        // reader.
        Assert.True(
            !schemaType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Any(method => Yields(method.ReturnType, schemaType)),
            "A public static method of the schema type returns the type.");
        Assert.True(
            !schemaType.GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Any(property => Yields(property.PropertyType, schemaType)),
            "A public static property of the schema type returns the type.");

        // An init accessor is a set method, so a public one fails here as a public setter does.
        Assert.True(
            schemaType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Select(property => property.GetSetMethod(nonPublic: true))
                .All(setter => setter is null || !(setter.IsPublic || setter.IsFamily || setter.IsFamilyOrAssembly)),
            "A property of the schema type has a public or protected setter.");
        Assert.True(
            schemaType.GetFields(BindingFlags.Public | BindingFlags.Instance).All(field => field.IsInitOnly),
            "A public field of the schema type is writable.");
        Assert.True(
            schemaType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(property => property.PropertyType)
                .Where(IsCollection)
                .All(IsReadOnlyCollectionDeclaration),
            "A public collection property of the schema type is not declared as a read-only view.");
    }

    [Theory]
    [MemberData(nameof(BuilderFileTypes))]
    public void BuilderFileType_TakesStringsOnlyAsBoundValues(Type builderFileType)
    {
        string[] violations =
        [
            .. WithReachableNestedTypes(builderFileType)
                .SelectMany(CallableMembers)
                .SelectMany(member => member.GetParameters()
                    .Select(parameter => (Member: member, Parameter: parameter)))
                .Where(entry => CarriesStrings(entry.Parameter.ParameterType))
                .Where(entry => !IsAllowedStringParameter(entry.Member, entry.Parameter))
                .Select(entry => Describe(entry.Member) + " takes string parameter '" + entry.Parameter.Name + "'"),
        ];

        Assert.True(
            violations.Length == 0,
            "A builder-file member takes a string that is not a bound value, so it could carry a table or column " +
            "name the reader never returned: " + string.Join("; ", violations));
    }

    [Fact]
    public void ValueStringParameters_NameNoIdentifierAndAreEachInUse()
    {
        // Each entry names a value; a name that reads as an identifier would let one in under the allow-list.
        Assert.All(ValueStringParameters, name =>
        {
            Assert.DoesNotMatch(new Regex("(?i)(table|column|identifier|schema|alias)"), name);
            Assert.NotEqual("name", name);
        });

        // An entry no member uses is stale. Finding each entry, and the exempt text, also shows that the scan the
        // theory above runs reaches the builders' string parameters.
        HashSet<string> used =
        [
            .. BuilderFileTypeList
                .SelectMany(WithReachableNestedTypes)
                .SelectMany(CallableMembers)
                .SelectMany(member => member.GetParameters())
                .Where(parameter => CarriesStrings(parameter.ParameterType))
                .Select(parameter => parameter.Name ?? string.Empty),
        ];
        Assert.Subset(used, ValueStringParameters);
        Assert.Contains(ComposedStatementText.Parameter, used);
    }

    [Fact]
    public void StatementBuilders_EachTakeTheReaderBuiltSchema()
    {
        MethodInfo[] builders =
        [
            .. new[] { typeof(RecordSqlBuilder), typeof(SearchSqlBuilder) }
                .SelectMany(CallableMembers)
                .OfType<MethodInfo>()
                .Where(BuildsStatementText),
        ];

        // The derivation must find every statement the record and search paths send, or the check below is vacuous.
        const string Record = nameof(RecordSqlBuilder) + ".";
        const string Search = nameof(SearchSqlBuilder) + ".";
        HashSet<string> expected = new(StringComparer.Ordinal)
        {
            Record + nameof(RecordSqlBuilder.AppendSelectedSetPredicate),
            Record + nameof(RecordSqlBuilder.AppendGlobalRecordIdExclusion),
            Record + nameof(RecordSqlBuilder.Count),
            Record + nameof(RecordSqlBuilder.Position),
            Record + nameof(RecordSqlBuilder.SetPositionLookup),
            Record + nameof(RecordSqlBuilder.RecordAt),
            Record + nameof(RecordSqlBuilder.RecordById),
            Record + nameof(RecordSqlBuilder.RecordByUniqueKey),
            Record + nameof(RecordSqlBuilder.InsertBase),
            Record + nameof(RecordSqlBuilder.InsertPage),
            Record + nameof(RecordSqlBuilder.LockBase),
            Record + nameof(RecordSqlBuilder.UpdateBaseStamp),
            Record + nameof(RecordSqlBuilder.UpdatePageColumns),
            Record + nameof(RecordSqlBuilder.InsertPageKey),
            Record + nameof(RecordSqlBuilder.SoftDelete),
            Search + nameof(SearchSqlBuilder.Search),
            Search + nameof(SearchSqlBuilder.ListPage),
            Search + nameof(SearchSqlBuilder.ListCount),
        };
        Assert.Subset(builders.Select(Describe).ToHashSet(StringComparer.Ordinal), expected);

        string[] withoutSchema = [.. builders.Where(builder => !TakesCatalogueSchema(builder)).Select(Describe)];
        Assert.True(
            withoutSchema.Length == 0,
            "A statement builder takes no reader-built schema, so its identifiers come from somewhere else: " +
            string.Join("; ", withoutSchema));
    }

    [Fact]
    public void SqlIdentifier_QuotesNamesPubliclyOnlyFromSchemaTypes()
    {
        MethodInfo[] publicMethods =
            typeof(SqlIdentifier).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);

        MethodInfo[] quotes = [.. publicMethods.Where(method => method.Name == nameof(SqlIdentifier.Quote))];
        Assert.All(quotes, quote => Assert.Single(quote.GetParameters()));
        Assert.Equal(
            new[] { typeof(ColumnSchema), typeof(TableSchema) },
            quotes
                .Select(quote => quote.GetParameters()[0].ParameterType)
                .OrderBy(type => type.Name, StringComparer.Ordinal));

        // No public entry of any name turns raw text into an identifier.
        Assert.DoesNotContain(
            publicMethods.SelectMany(method => method.GetParameters()),
            parameter => CarriesStrings(parameter.ParameterType));

        MethodInfo? quoteName = typeof(SqlIdentifier).GetMethod(
            nameof(SqlIdentifier.QuoteName),
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(string)]);
        Assert.NotNull(quoteName);
        Assert.True(quoteName.IsAssembly, "SqlIdentifier.QuoteName is not internal.");
        Assert.False(quoteName.IsPublic, "SqlIdentifier.QuoteName is public.");
    }

    // The type and every nested type callable from outside it. A private nested type, such as a builder's private
    // record of rendered fragments, is reachable only from inside its builder, so its members are not entry points.
    private static IEnumerable<Type> WithReachableNestedTypes(Type type) =>
        type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
            .Where(nested => (nested.IsNestedPublic || nested.IsNestedAssembly || nested.IsNestedFamORAssem)
                && !IsCompilerGenerated(nested))
            .SelectMany(WithReachableNestedTypes)
            .Prepend(type);

    // The methods and constructors another type of the assembly can call, compiler-generated members aside.
    private static IEnumerable<MethodBase> CallableMembers(Type type) =>
        type.GetMethods(DeclaredMembers)
            .Cast<MethodBase>()
            .Concat(type.GetConstructors(DeclaredMembers))
            .Where(member => (member.IsPublic || member.IsAssembly || member.IsFamilyOrAssembly)
                && !IsCompilerGenerated(member));

    private static bool IsCompilerGenerated(MemberInfo member) =>
        member.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) ||
        member.Name.Contains('<', StringComparison.Ordinal);

    // A string, or any sequence of strings such as string[], IEnumerable<string> or IReadOnlyList<string>; an out or
    // ref parameter is judged by the type it refers to.
    private static bool CarriesStrings(Type declared)
    {
        Type type = declared.IsByRef ? declared.GetElementType()! : declared;
        return type == typeof(string) || typeof(IEnumerable<string>).IsAssignableFrom(type);
    }

    private static bool IsAllowedStringParameter(MethodBase member, ParameterInfo parameter) =>
        parameter.Name is { } name &&
        (ValueStringParameters.Contains(name) ||
            (member.DeclaringType == ComposedStatementText.Type &&
                member.Name == ComposedStatementText.Member &&
                name == ComposedStatementText.Parameter));

    private static bool BuildsStatementText(MethodInfo method) =>
        StatementTypes.Contains(method.ReturnType) ||
        method.GetParameters().Any(parameter => parameter.ParameterType == typeof(StringBuilder));

    private static bool TakesCatalogueSchema(MethodBase method) =>
        method.GetParameters().Any(parameter =>
            CatalogueSchemaTypes.Any(schemaType => Yields(parameter.ParameterType, schemaType)));

    private static string Describe(MethodBase member) =>
        (member.DeclaringType?.Name ?? "?") + "." + (member.IsConstructor ? "ctor" : member.Name);

    // Whether a member declared with this type hands out the schema type: the type itself, or an array or a generic
    // type that carries it, such as a collection or a task of it.
    private static bool Yields(Type declared, Type schemaType) =>
        declared == schemaType ||
        (declared.GetElementType() is { } element && Yields(element, schemaType)) ||
        (declared.IsGenericType && declared.GetGenericArguments().Any(argument => Yields(argument, schemaType)));

    private static bool IsCollection(Type declared) =>
        declared != typeof(string) && typeof(IEnumerable).IsAssignableFrom(declared);

    private static bool IsReadOnlyCollectionDeclaration(Type declared) =>
        declared.IsGenericType && ReadOnlyCollectionDefinitions.Contains(declared.GetGenericTypeDefinition());
}

/// <summary>
/// Provenance tests against each seeded form's schema as <c>SqlMetadataReader</c> builds it from the catalogue:
/// <list type="bullet">
/// <item><description>
/// The selected-set predicate: the keyword is fixed text, the column is the reader-built <c>RecStatus</c> column
/// quoted by <see cref="SqlIdentifier"/>, and the active status is bound as a parameter of that column's type.
/// </description></item>
/// <item><description>
/// A <c>values</c> key the reader did not return is refused by the repository before any command names a base or
/// page table, on insert and on update.
/// </description></item>
/// <item><description>
/// Every bracketed identifier in the commands the repository sends to insert, update and soft-delete a record is a
/// table or column name the reader returned for that form, or one of the builders' fixed aliases.
/// </description></item>
/// </list>
/// </summary>
/// <remarks>
/// Each form is resolved by name through the reader, never by a hard-coded id. The selected-set cases read only
/// metadata and the catalogue and hold the shared test lock. The repository cases hold the exclusive lock: they write
/// only records they create through the repository, each with a test marker in <c>LastName</c>, which stay in the
/// database. No case edits a seeded row, a metadata row or the schema, and no failure message prints a parameter
/// value.
/// </remarks>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed partial class IdentifierProvenanceIntegrationTests
{
    // A values key shaped like an identifier that closes the bracket and appends a statement.
    private const string InjectedIdentifierKey = "LastName]; DROP TABLE x;--";

    // The text an unknown key is submitted with; it never reaches a statement.
    private const string UnknownKeyValue = "unknown";

    // The two seeded forms, resolved by name through the reader.
    private static readonly string[] SeededForms = ["CaseReport", "FoodHistory"];

    // Covers the exclusive lock wait (up to 300 s) and the writes; xUnit 2.9.3 gives a test no cancellation token.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    // The operations the repository reports for record writes: every command sent under one names a record table.
    private static readonly HashSet<string> RecordWriteOperations = new(StringComparer.Ordinal)
    {
        RecordOperations.Insert,
        RecordOperations.Update,
        RecordOperations.Delete,
    };

    // No seeded field is required, so the stored-state gate passes; it is not what these tests exercise.
    private static readonly RecordValidationResult RequiredGatePasses =
        RecordValidationResult.Success(new Dictionary<string, FieldValue>(StringComparer.Ordinal));

    /// <summary>The seeded forms.</summary>
    public static TheoryData<string> Forms => new(SeededForms);

    /// <summary>
    /// Each seeded form with the text field it also writes beyond <c>LastName</c>, so every page table gets a column
    /// write: none on single-page <c>CaseReport</c>; <c>Lab</c>, an <c>nvarchar(4000)</c> column of
    /// <c>FoodHistory3</c> with no pattern or maximum length (<c>testdata/sql/03_forms.sql</c>, field 1149), on
    /// <c>FoodHistory</c>, whose <c>LastName</c> is on <c>FoodHistory2</c>.
    /// </summary>
    public static TheoryData<string, string?> RecordWrites => new()
    {
        { "CaseReport", null },
        { "FoodHistory", "Lab" },
    };

    /// <summary>
    /// Each seeded form with each clause and set, and the keyword expected before the predicate:
    /// <see langword="null"/> when every row is selected and nothing is appended.
    /// </summary>
    public static TheoryData<string, string, bool, string?> Renderings => new()
    {
        { "CaseReport", nameof(SelectedSetClause.Where), false, " WHERE " },
        { "CaseReport", nameof(SelectedSetClause.And), false, " AND " },
        { "CaseReport", nameof(SelectedSetClause.Where), true, null },
        { "CaseReport", nameof(SelectedSetClause.And), true, null },
        { "FoodHistory", nameof(SelectedSetClause.Where), false, " WHERE " },
        { "FoodHistory", nameof(SelectedSetClause.And), false, " AND " },
        { "FoodHistory", nameof(SelectedSetClause.Where), true, null },
        { "FoodHistory", nameof(SelectedSetClause.And), true, null },
    };

    /// <summary>Each seeded form with values outside the clause enumeration, for either set.</summary>
    public static TheoryData<string, int, bool> UndefinedClauses => new()
    {
        { "CaseReport", -1, false },
        { "CaseReport", 2, false },
        { "CaseReport", 2, true },
        { "FoodHistory", -1, false },
        { "FoodHistory", 2, false },
        { "FoodHistory", 2, true },
    };

    [Theory]
    [MemberData(nameof(Renderings))]
    public async Task AppendSelectedSetPredicate_RendersTheFixedKeywordAndBindsTheActiveStatus(
        string formName,
        string clause,
        bool includeDeleted,
        string? expectedKeyword)
    {
        await TestDatabase.EnsureReachableAsync();
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync();

        FormSchema schema = await ReadSchemaAsync(formName);
        string statementStart = StatementStart(schema);
        var sql = new StringBuilder(statementStart);
        var parameters = new PositionalParameters();

        bool appended = RecordSqlBuilder.AppendSelectedSetPredicate(
            sql, schema, includeDeleted, parameters, Enum.Parse<SelectedSetClause>(clause));

        bool expectedAppended = expectedKeyword is not null;
        Assert.Equal(expectedAppended, appended);

        // The column text is the reader-built RecStatus column, quoted; the keyword, the alias and the parameter name
        // are fixed text.
        string expectedPredicate = expectedKeyword is null
            ? string.Empty
            : expectedKeyword + "b." + SqlIdentifier.Quote(RecordSqlBuilder.Require(schema.RecStatus)) + " = @p0";
        Assert.Equal(statementStart + expectedPredicate, sql.ToString());

        // The active status is bound with the catalogue type of RecStatus, smallint on both seeded base tables, so
        // exactly (short)1.
        SqlParameterValue[] expectedParameters = expectedAppended ? [new("@p0", DbType.Int16, (short)1, null)] : [];
        Assert.Equal(expectedParameters, parameters.ToStatement(sql.ToString()).Parameters);
    }

    [Theory]
    [MemberData(nameof(UndefinedClauses))]
    public async Task AppendSelectedSetPredicate_UndefinedClause_ThrowsBeforeAppendingAnything(
        string formName,
        int clause,
        bool includeDeleted)
    {
        await TestDatabase.EnsureReachableAsync();
        await using var dbLock = await DatabaseWriteLock.AcquireSharedAsync();

        FormSchema schema = await ReadSchemaAsync(formName);
        string statementStart = StatementStart(schema);
        var sql = new StringBuilder(statementStart);
        var parameters = new PositionalParameters();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => RecordSqlBuilder.AppendSelectedSetPredicate(
            sql, schema, includeDeleted, parameters, (SelectedSetClause)clause));

        Assert.Equal("clause", exception.ParamName);
        Assert.Null(exception.ActualValue);
        Assert.DoesNotContain(clause.ToString(CultureInfo.InvariantCulture), exception.Message);
        Assert.Equal(statementStart, sql.ToString());
        Assert.Equal(0, parameters.Count);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task UnknownValuesKeyIsRejectedBeforeAnyRecordSql(string formName)
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken cancellationToken = timeout.Token;
        await using var writeLock = await DatabaseWriteLock.AcquireExclusiveAsync(cancellationToken);

        RecordPath path = RecordPath.Create();
        ProvenanceForm form = await path.ReadFormAsync(formName, cancellationToken);
        string[] unknownKeys = ["NoSuchField_" + Guid.NewGuid().ToString("N"), InjectedIdentifierKey];

        // Insert: a valid marker beside each unknown key, so only the unknown key can be the reason for the refusal.
        SqlSnapshot beforeInserts = await SqlSnapshot.CaptureAsync(cancellationToken);
        foreach (string unknownKey in unknownKeys)
        {
            path.Recorder.Clear();
            IReadOnlyDictionary<string, FieldValue> values = Values(
                (TestDatabase.MarkerField, FieldValue.FromText(TestDatabase.NewMarker())),
                (unknownKey, FieldValue.FromText(UnknownKeyValue)));

            ArgumentException rejected = await Assert.ThrowsAsync<ArgumentException>(
                () => path.Repository.InsertAsync(form.Metadata, values, NewStamp(), cancellationToken));

            AssertRefusedBeforeRecordSql(rejected, "values", unknownKey, path.Recorder, form);
        }

        AssertUnchanged(beforeInserts, await SqlSnapshot.CaptureAsync(cancellationToken));

        // Update: the same keys beside a valid one, on a record this test created. The refusal comes before the
        // locked read of the base row, so the required gate never runs either.
        RecordDto created = await path.Repository.InsertAsync(
            form.Metadata,
            Values((TestDatabase.MarkerField, FieldValue.FromText(TestDatabase.NewMarker()))),
            NewStamp(),
            cancellationToken);
        SqlSnapshot beforeUpdates = await SqlSnapshot.CaptureAsync(cancellationToken);
        int gateCalls = 0;
        foreach (string unknownKey in unknownKeys)
        {
            path.Recorder.Clear();
            IReadOnlyDictionary<string, FieldValue> submitted = Values(
                (TestDatabase.MarkerField, FieldValue.FromText(TestDatabase.NewMarker())),
                (unknownKey, FieldValue.FromText(UnknownKeyValue)));

            ArgumentException rejected = await Assert.ThrowsAsync<ArgumentException>(
                () => path.Repository.UpdateAsync(
                    form.Metadata,
                    created.GlobalRecordId,
                    submitted,
                    NewStamp(),
                    _ =>
                    {
                        gateCalls++;
                        return RequiredGatePasses;
                    },
                    permissions: null,
                    cancellationToken));

            AssertRefusedBeforeRecordSql(rejected, "submitted", unknownKey, path.Recorder, form);
        }

        Assert.Equal(0, gateCalls);
        AssertUnchanged(beforeUpdates, await SqlSnapshot.CaptureAsync(cancellationToken));
    }

    [Theory]
    [MemberData(nameof(RecordWrites))]
    public async Task RecordCommandsUseOnlyReaderReturnedIdentifiers(string formName, string? laterPageTextField)
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken cancellationToken = timeout.Token;
        await using var writeLock = await DatabaseWriteLock.AcquireExclusiveAsync(cancellationToken);

        RecordPath path = RecordPath.Create();
        ProvenanceForm form = await path.ReadFormAsync(formName, cancellationToken);
        ProvenanceForm otherForm = await path.ReadFormAsync(
            Assert.Single(SeededForms, name => !string.Equals(name, formName, StringComparison.Ordinal)),
            cancellationToken);
        Assert.Empty(form.Tables.Intersect(otherForm.Tables, StringComparer.Ordinal));

        string[] textFields = laterPageTextField is null
            ? [TestDatabase.MarkerField]
            : [TestDatabase.MarkerField, laterPageTextField];
        form.RequireWritableTextFieldsOnEveryPage(textFields);

        path.Recorder.Clear();
        RecordDto created = await path.Repository.InsertAsync(
            form.Metadata,
            MarkedValues(textFields),
            NewStamp(),
            cancellationToken);
        RecordWriteResult updated = await path.Repository.UpdateAsync(
            form.Metadata,
            created.GlobalRecordId,
            MarkedValues(textFields),
            NewStamp(),
            _ => RequiredGatePasses,
            permissions: null,
            cancellationToken);
        Assert.Equal(RecordWriteStatus.Success, updated.Status);
        SoftDeleteResult deleted = await path.Repository.SoftDeleteAsync(
            form.Metadata,
            created.GlobalRecordId,
            NewStamp(),
            cancellationToken);
        Assert.Equal(SoftDeleteResult.Deleted, deleted);

        IReadOnlyList<RecordedCommand> commands = path.Recorder.Commands;
        RecordedCommand[] recordCommands = [.. commands.Where(form.IsRecordCommand)];
        Assert.NotEmpty(recordCommands);

        // A write, or a command sent under a record operation, that names no table of the form would be record SQL
        // whose tables escaped the provenance check below.
        Assert.All(path.Recorder.Writes(), command => Assert.True(
            form.IsRecordCommand(command),
            "A write names no table of the form: " + command.Describe(includeParameterValues: false)));
        Assert.All(
            commands.Where(command => RecordWriteOperations.Contains(command.Operation)),
            command => Assert.True(
                form.IsRecordCommand(command),
                "A record command names no table of the form: " + command.Describe(includeParameterValues: false)));

        // The insert writes the base row and a row on every page; the update stamps the base row and writes every
        // page, because a submitted field sits on each; the soft delete updates the base row.
        string[] formTables = [.. form.Tables.Order(StringComparer.Ordinal)];
        Assert.Equal(formTables, WrittenTables(recordCommands, "insert"));
        Assert.Equal(formTables, WrittenTables(recordCommands, "update"));

        foreach (RecordedCommand command in recordCommands)
        {
            foreach (string identifier in SqlCommandRecorder.BracketedIdentifiers(command.CommandText))
            {
                Assert.True(
                    form.Allowed.Contains(identifier) || form.Ignored.Contains(identifier),
                    $"[{identifier}] is not a name the metadata reader returned for {formName}, nor a fixed alias: " +
                    command.Describe(includeParameterValues: false));
                Assert.False(
                    otherForm.Tables.Contains(identifier),
                    $"[{identifier}] is a table of another form: " + command.Describe(includeParameterValues: false));
            }
        }
    }

    // The text a caller has written before the predicate: the reader-built base table and its alias, as
    // RecordSqlBuilder.Count writes them.
    private static string StatementStart(FormSchema schema) =>
        "SELECT COUNT_BIG(*) FROM " + SqlIdentifier.Quote(schema.BaseTable) + " b";

    // Resolves the form by name through the metadata reader and returns the schema the reader built from the
    // catalogue.
    private static async Task<FormSchema> ReadSchemaAsync(string formName)
    {
        var options = new DatabaseOptions(TestDatabase.ConnectionString);
        var factory = new SqlConnectionFactory(options);
        var reader = new SqlMetadataReader(factory, options, NullSqlCommandObserver.Instance);

        IReadOnlyList<FormSummary> forms = await reader.ListFormsAsync(CancellationToken.None);
        FormSummary? summary =
            forms.FirstOrDefault(form => string.Equals(form.Name, formName, StringComparison.Ordinal));
        Assert.True(summary is not null, "The seeded form is not listed.");

        await using SqlConnection connection = await TestDatabase.OpenConnectionAsync();
        (FormMetadata Form, FormSchema Schema)? read =
            await reader.ReadFormAsync(connection, transaction: null, summary.ViewId, CancellationToken.None);
        Assert.True(read is not null, "The seeded form has no metadata.");

        return read.Value.Schema;
    }

    // The values of a write keyed ordinally by canonical field name.
    private static IReadOnlyDictionary<string, FieldValue> Values(params (string Name, FieldValue Value)[] entries)
    {
        var values = new Dictionary<string, FieldValue>(entries.Length, StringComparer.Ordinal);
        foreach ((string name, FieldValue value) in entries)
        {
            values.Add(name, value);
        }

        return values;
    }

    // A new test marker in each named text field.
    private static IReadOnlyDictionary<string, FieldValue> MarkedValues(IEnumerable<string> textFields) =>
        Values([.. textFields.Select(name => (name, FieldValue.FromText(TestDatabase.NewMarker())))]);

    private static SaveStamp NewStamp() => new(Environment.UserName, SaveStamp.TruncateToSeconds(DateTime.Now));

    // The repository's documented refusal of a values key that names no data field: an unwrapped ArgumentException
    // for the values parameter, whose fixed message names no key, sent after metadata reads only. A record command,
    // a write, or any command under a record operation would mean the key reached record SQL.
    private static void AssertRefusedBeforeRecordSql(
        ArgumentException rejected,
        string expectedParameter,
        string unknownKey,
        SqlCommandRecorder recorder,
        ProvenanceForm form)
    {
        Assert.Equal(expectedParameter, rejected.ParamName);
        Assert.DoesNotContain(unknownKey, rejected.Message, StringComparison.Ordinal);

        IReadOnlyList<RecordedCommand> commands = recorder.Commands;
        string[] reachedRecordSql =
        [
            .. commands
                .Where(command => command.IsWrite
                    || form.IsRecordCommand(command)
                    || RecordWriteOperations.Contains(command.Operation))
                .Select(command => command.Describe(includeParameterValues: false)),
        ];
        Assert.True(
            reachedRecordSql.Length == 0,
            "The refused write sent record SQL: " + string.Join(" || ", reachedRecordSql));
        Assert.Empty(recorder.Writes());
    }

    // Exactly no change in any record table. The message gives counts and tables only, since a listing would print
    // stored values.
    private static void AssertUnchanged(SqlSnapshot before, SqlSnapshot after)
    {
        SnapshotDiff diff = SqlSnapshot.Compare(before, after);
        string[] tables =
        [
            .. diff.AddedRows.Select(change => change.Table)
                .Concat(diff.RemovedRows.Select(change => change.Table))
                .Concat(diff.ChangedCells.Select(change => change.Table))
                .Distinct(StringComparer.Ordinal),
        ];
        Assert.True(
            diff.IsEmpty,
            $"The refused writes changed the record tables: {diff.AddedRows.Count} rows added, " +
            $"{diff.RemovedRows.Count} removed, {diff.ChangedCells.Count} cells changed and " +
            $"{diff.SchemaChanges.Count} column sets changed, in {string.Join(", ", tables)}.");
    }

    // The tables the record commands of one kind write, sorted: the first bracketed identifier of each INSERT INTO [t]
    // or UPDATE [t] SET statement, the form every write statement of the builders starts with. The kind is the
    // statement group of WriteStatement: "insert" or "update".
    private static string[] WrittenTables(IEnumerable<RecordedCommand> recordCommands, string kind) =>
    [
        .. recordCommands
            .Where(command => WriteStatement().Match(command.CommandText).Groups[kind].Success)
            .Select(command => SqlCommandRecorder.BracketedIdentifiers(command.CommandText)[0])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
    ];

    [GeneratedRegex(
        @"^\s*(?:(?<insert>INSERT\s+INTO)|(?<update>UPDATE))\s+\[",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WriteStatement();

    // The metadata reader and the record repository over one connection factory, both reporting every command they
    // send to one recorder, as the store composes them.
    private sealed class RecordPath
    {
        private RecordPath(SqlCommandRecorder recorder, SqlMetadataReader reader, SqlRecordRepository repository)
        {
            Recorder = recorder;
            Reader = reader;
            Repository = repository;
        }

        public SqlCommandRecorder Recorder { get; }

        public SqlMetadataReader Reader { get; }

        public SqlRecordRepository Repository { get; }

        public static RecordPath Create()
        {
            var options = new DatabaseOptions(TestDatabase.ConnectionString);
            var recorder = new SqlCommandRecorder();
            var factory = new SqlConnectionFactory(options);
            var reader = new SqlMetadataReader(factory, options, recorder);
            return new RecordPath(recorder, reader, new SqlRecordRepository(factory, reader, options, recorder));
        }

        // Resolves the form by name through ListFormsAsync and reads its metadata and schema on a connection of its
        // own, outside any transaction.
        public async Task<ProvenanceForm> ReadFormAsync(string formName, CancellationToken cancellationToken)
        {
            IReadOnlyList<FormSummary> forms = await Reader.ListFormsAsync(cancellationToken);
            FormSummary summary =
                Assert.Single(forms, form => string.Equals(form.Name, formName, StringComparison.Ordinal));

            await using SqlConnection connection = await TestDatabase.OpenConnectionAsync(cancellationToken);
            (FormMetadata Form, FormSchema Schema)? read =
                await Reader.ReadFormAsync(connection, transaction: null, summary.ViewId, cancellationToken);
            Assert.True(read is not null, "The seeded form has no metadata.");

            return new ProvenanceForm(read.Value.Form, read.Value.Schema);
        }
    }

    // One form as the reader returned it, with the names its record commands may hold.
    private sealed class ProvenanceForm
    {
        public ProvenanceForm(FormMetadata metadata, FormSchema schema)
        {
            Metadata = metadata;
            Schema = schema;

            var tables = new HashSet<string>(StringComparer.Ordinal) { schema.BaseTable.Name };
            var allowed = new HashSet<string>(StringComparer.Ordinal) { schema.BaseTable.Name };
            allowed.UnionWith(schema.BaseTable.Columns.Select(column => column.Name));
            foreach (PageTableSchema page in schema.Pages)
            {
                if (page.Table is { } table)
                {
                    tables.Add(table.Name);
                    allowed.Add(table.Name);
                    allowed.UnionWith(table.Columns.Select(column => column.Name));
                }
            }

            // The fixed names the record builders write, which no reader returns and the check does not compare.
            var ignored = new HashSet<string>(StringComparer.Ordinal)
            {
                // A schema qualifier: the builders write one-part names, so it is listed only as never a table name.
                "dbo",

                // The base-table alias of every read, written unbracketed as b.[column].
                RecordSqlBuilder.BaseAlias,

                // RecordSqlBuilder's names for the intrinsic position and count that RecordAt and RecordById compute.
                "ip",
                "ic",
            };

            // The page aliases p{i}, i being the page's 0-based index in FormSchema.Pages, written unbracketed.
            for (int index = 0; index < schema.Pages.Count; index++)
            {
                ignored.Add(RecordSqlBuilder.PageAlias(index));
            }

            // The result-column aliases [c{n}]: a record SELECT projects at most the base columns and the intrinsic
            // coordinates (ordinals 0 to 4), one key per page and one column per data field.
            int ordinals = RecordSqlBuilder.RecordIntrinsicCountOrdinal + 1 + schema.Pages.Count +
                FormDefinitionFactory.GetDataFields(metadata).Count;
            for (int ordinal = 0; ordinal < ordinals; ordinal++)
            {
                string alias = RecordSqlBuilder.ColumnAlias(ordinal);
                ignored.Add(Assert.Single(SqlCommandRecorder.BracketedIdentifiers(alias)));
            }

            Tables = tables;
            Allowed = allowed;
            Ignored = ignored;
        }

        public FormMetadata Metadata { get; }

        public FormSchema Schema { get; }

        // The base table and every page table the catalogue returned.
        public IReadOnlySet<string> Tables { get; }

        // Those tables and every column of each.
        public IReadOnlySet<string> Allowed { get; }

        public IReadOnlySet<string> Ignored { get; }

        // A record command names a base or page table of this form; metadata and catalogue reads are fixed text that
        // names only meta* tables and catalogue views.
        public bool IsRecordCommand(RecordedCommand command) =>
            SqlCommandRecorder.BracketedIdentifiers(command.CommandText).Any(Tables.Contains);

        // Each name is a writable text field stored in a column the reader found, and together they sit on every page
        // that has a table, so a write of all of them sends a column write to each page table.
        public void RequireWritableTextFieldsOnEveryPage(IEnumerable<string> fieldNames)
        {
            DataFieldSet dataFields = FormDefinitionFactory.GetDataFields(Metadata);
            var coveredPages = new HashSet<int>();
            foreach (string name in fieldNames)
            {
                Assert.True(dataFields.TryGet(name, out DataFieldMetadata? field), $"{name} is not a data field.");
                Assert.False(field.Field.IsReadOnly, $"{name} is read-only.");
                Assert.Equal(FieldType.Text, field.FieldType);
                Assert.NotNull(Schema.FindDataColumn(field));
                coveredPages.Add(field.Page.PageId);
            }

            int[] pagesWithTables =
            [
                .. Schema.Pages.Where(page => page.Table is not null).Select(page => page.Page.PageId).Order(),
            ];
            Assert.Equal(pagesWithTables, coveredPages.Order());
        }
    }
}
