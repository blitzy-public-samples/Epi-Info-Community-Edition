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
            Record + nameof(RecordSqlBuilder.SetPositionLookup),
            Record + nameof(RecordSqlBuilder.RecordAt),
            Record + nameof(RecordSqlBuilder.RecordById),
            Record + nameof(RecordSqlBuilder.RecordByUniqueKey),
            Record + nameof(RecordSqlBuilder.RecordSnapshot),
            Record + nameof(RecordSqlBuilder.AcquireFormWriteLock),
            Record + nameof(RecordSqlBuilder.InsertBase),
            Record + nameof(RecordSqlBuilder.InsertPage),
            Record + nameof(RecordSqlBuilder.LockBase),
            Record + nameof(RecordSqlBuilder.UpdateBaseStamp),
            Record + nameof(RecordSqlBuilder.UpdatePageColumns),
            Record + nameof(RecordSqlBuilder.InsertPageKey),
            Record + nameof(RecordSqlBuilder.SoftDelete),
            Search + nameof(SearchSqlBuilder.Search),
            Search + nameof(SearchSqlBuilder.ListPage),
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
/// Values the caller's own metadata accepts but the metadata read inside the write's transaction refuses, and an
/// insert whose form that read no longer finds, mean the metadata changed after the caller read it: the write fails
/// as a <see cref="DataStoreWriteException"/> reporting <see cref="WriteOutcome.NotWritten"/>, again before any
/// record command, while the same values with the reader's own form stay the caller's
/// <see cref="ArgumentException"/>. The caller's older metadata is built in memory from the reader's form, so no
/// metadata row changes.
/// </description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Each form is resolved by name through the reader, never by a hard-coded id. The selected-set cases read only
/// metadata and the catalogue and hold the shared test lock. The refusal cases hold the exclusive lock because they
/// compare before-and-after snapshots, which must show no change: the only writes they attempt are refused, every
/// update and soft delete names an id no row holds, and no record is created. The class writes nothing, edits no seeded
/// row, metadata row or schema object, and no failure message prints a parameter value.
/// </para>
/// <para>
/// The valid-save half of the provenance proof, that every identifier in the commands of an insert, an update and a
/// soft delete on each seeded form is a name the reader returned, runs through the API in <c>EpiInfo.Api.Tests</c>
/// (<c>RecordCommandIdentifierProvenanceTests</c> in <c>Persistence/SqlParameterizationTests.cs</c>): the only
/// records a test may write are records created through the API (AAP 0.3.7, "Data reach of tests"), and this
/// project references <c>EpiInfo.Data</c> alone.
/// </para>
/// </remarks>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class IdentifierProvenanceIntegrationTests
{
    // A values key shaped like an identifier that closes the bracket and appends a statement.
    private const string InjectedIdentifierKey = "LastName]; DROP TABLE x;--";

    // The text an unknown key is submitted with; it never reaches a statement.
    private const string UnknownKeyValue = "unknown";

    // The values a read-only field is submitted with, by its type; neither reaches a statement.
    private const string ReadOnlyFieldText = "R-1";
    private const double ReadOnlyFieldNumber = 17;

    // The two seeded forms, resolved by name through the reader.
    private static readonly string[] SeededForms = ["CaseReport", "FoodHistory"];

    // Covers the exclusive lock wait (up to 300 s) and the refused writes; xUnit 2.9.3 gives a test no cancellation
    // token.
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
    /// Each seeded form with one of its read-only data fields (testdata/sql/03_forms.sql): the text StateIDNumber on
    /// CaseReport1 and the number OnsetWeek on FoodHistory2.
    /// </summary>
    public static TheoryData<string, string> ReadOnlyFields => new()
    {
        { "CaseReport", "StateIDNumber" },
        { "FoodHistory", "OnsetWeek" },
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
                () => path.Repository.InsertAsync(
                    form.Metadata,
                    values,
                    NewStamp(),
                    permissions: null,
                    cancellationToken));

            AssertRefusedBeforeRecordSql(rejected, "values", unknownKey, path.Recorder, form);
        }

        AssertUnchanged(beforeInserts, await SqlSnapshot.CaptureAsync(cancellationToken));

        // Update: the same keys beside a valid one, on an id no row holds, so no record is created to refuse a write
        // to. The values are resolved before the locked read of the base row, so the refusal needs no stored record
        // and the required gate never runs either. Were the lock read first, the update would find no row and return
        // NotFound instead of throwing, and the recorder would show that read naming the base table.
        string unstoredId = Guid.NewGuid().ToString();
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
                    unstoredId,
                    submitted,
                    NewStamp,
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

    // A caller that validated its values against metadata read while the field was still writable holds that field
    // writable; the transaction's own metadata read finds it read-only. The same values are refused in both cases, but
    // only the reader's own form makes the refusal the caller's broken argument. With the caller's older copy the
    // metadata changed after the caller read it, so the write reports that it wrote nothing. The update runs on an id
    // no row holds, and both refusals come before the locked read, so the gate never runs and no record is touched.
    [Theory]
    [MemberData(nameof(ReadOnlyFields))]
    public async Task FieldMadeReadOnlyAfterTheCallersRead_IsRefusedAsNotWrittenBeforeAnyRecordSql(
        string formName,
        string readOnlyField)
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken cancellationToken = timeout.Token;
        await using var writeLock = await DatabaseWriteLock.AcquireExclusiveAsync(cancellationToken);

        RecordPath path = RecordPath.Create();
        ProvenanceForm form = await path.ReadFormAsync(formName, cancellationToken);
        DataFieldMetadata field = Assert.Single(
            FormDefinitionFactory.GetDataFields(form.Metadata),
            candidate => string.Equals(candidate.Name, readOnlyField, StringComparison.Ordinal));
        Assert.True(field.Field.IsReadOnly, "The seeded field is not read-only.");
        FormMetadata callersForm = WithWritableField(form.Metadata, readOnlyField);
        IReadOnlyDictionary<string, FieldValue> values = Values(
            (TestDatabase.MarkerField, FieldValue.FromText(TestDatabase.NewMarker())),
            (readOnlyField, SampleValue(field)));
        string unstoredId = Guid.NewGuid().ToString();
        int gateCalls = 0;

        Task Insert(FormMetadata callerForm) =>
            path.Repository.InsertAsync(callerForm, values, NewStamp(), permissions: null, cancellationToken);
        Task Update(FormMetadata callerForm) => path.Repository.UpdateAsync(
            callerForm,
            unstoredId,
            values,
            NewStamp,
            _ =>
            {
                gateCalls++;
                return RequiredGatePasses;
            },
            permissions: null,
            cancellationToken);

        SqlSnapshot before = await SqlSnapshot.CaptureAsync(cancellationToken);

        // The reader's own form refuses the read-only field, so the caller broke its contract.
        path.Recorder.Clear();
        AssertRefusedBeforeRecordSql(
            await Assert.ThrowsAsync<ArgumentException>(() => Insert(form.Metadata)),
            "values",
            readOnlyField,
            path.Recorder,
            form);

        path.Recorder.Clear();
        AssertRefusedBeforeRecordSql(
            await Assert.ThrowsAsync<ArgumentException>(() => Update(form.Metadata)),
            "submitted",
            readOnlyField,
            path.Recorder,
            form);

        // The caller's older copy accepts the same values, so the metadata changed after the caller read it.
        path.Recorder.Clear();
        AssertNotWrittenBeforeRecordSql(
            await Assert.ThrowsAsync<DataStoreWriteException>(() => Insert(callersForm)),
            RecordOperations.Insert,
            path.Recorder,
            form);

        path.Recorder.Clear();
        AssertNotWrittenBeforeRecordSql(
            await Assert.ThrowsAsync<DataStoreWriteException>(() => Update(callersForm)),
            RecordOperations.Update,
            path.Recorder,
            form);

        Assert.Equal(0, gateCalls);
        AssertUnchanged(before, await SqlSnapshot.CaptureAsync(cancellationToken));
    }

    // The caller's form came from the store's own metadata read, so an insert whose transaction no longer finds the
    // form was given a form removed after that read: it reports that it wrote nothing, before any record SQL. An update
    // and a soft delete of such a form keep their NotFound result. The removed form is the reader's form under a view
    // id no metaViews row holds, so no metadata row changes.
    [Theory]
    [MemberData(nameof(Forms))]
    public async Task FormRemovedAfterTheCallersRead_InsertIsRefusedAsNotWrittenBeforeAnyRecordSql(string formName)
    {
        await TestDatabase.EnsureReachableAsync();
        using var timeout = new CancellationTokenSource(TestTimeout);
        CancellationToken cancellationToken = timeout.Token;
        await using var writeLock = await DatabaseWriteLock.AcquireExclusiveAsync(cancellationToken);

        RecordPath path = RecordPath.Create();
        ProvenanceForm form = await path.ReadFormAsync(formName, cancellationToken);
        IReadOnlyList<FormSummary> forms = await path.Reader.ListFormsAsync(cancellationToken);
        FormMetadata removedForm = form.Metadata with { ViewId = forms.Max(summary => summary.ViewId) + 1 };
        IReadOnlyDictionary<string, FieldValue> values =
            Values((TestDatabase.MarkerField, FieldValue.FromText(TestDatabase.NewMarker())));
        string unstoredId = Guid.NewGuid().ToString();
        int gateCalls = 0;

        SqlSnapshot before = await SqlSnapshot.CaptureAsync(cancellationToken);

        path.Recorder.Clear();
        AssertNotWrittenBeforeRecordSql(
            await Assert.ThrowsAsync<DataStoreWriteException>(
                () => path.Repository.InsertAsync(
                    removedForm,
                    values,
                    NewStamp(),
                    permissions: null,
                    cancellationToken)),
            RecordOperations.Insert,
            path.Recorder,
            form);

        path.Recorder.Clear();
        RecordWriteResult update = await path.Repository.UpdateAsync(
            removedForm,
            unstoredId,
            values,
            NewStamp,
            _ =>
            {
                gateCalls++;
                return RequiredGatePasses;
            },
            permissions: null,
            cancellationToken);
        Assert.Equal(RecordWriteStatus.NotFound, update.Status);
        AssertNoRecordSql(path.Recorder, form);

        path.Recorder.Clear();
        SoftDeleteResult delete = await path.Repository.SoftDeleteAsync(
            removedForm,
            unstoredId,
            NewStamp,
            permissions: null,
            cancellationToken);
        Assert.Equal(SoftDeleteStatus.NotFound, delete.Status);
        Assert.Null(delete.GlobalRecordId);
        AssertNoRecordSql(path.Recorder, form);

        Assert.Equal(0, gateCalls);
        AssertUnchanged(before, await SqlSnapshot.CaptureAsync(cancellationToken));
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

    private static SaveStamp NewStamp() => new(Environment.UserName, SaveStamp.TruncateToSeconds(DateTime.Now));

    // The reader's form as a caller holds it that read the metadata while fieldName was still writable: the same rows,
    // with that one field's IsReadOnly cleared, built in memory.
    private static FormMetadata WithWritableField(FormMetadata form, string fieldName) =>
        form with
        {
            Fields =
            [
                .. form.Fields.Select(field => string.Equals(field.Name, fieldName, StringComparison.Ordinal)
                    ? field with { IsReadOnly = false }
                    : field),
            ],
        };

    // A value of the field's own type that its column holds, so only the field's read-only flag decides its refusal.
    private static FieldValue SampleValue(DataFieldMetadata field) => field.FieldType switch
    {
        FieldType.Text => FieldValue.FromText(ReadOnlyFieldText),
        FieldType.Number => FieldValue.FromNumber(ReadOnlyFieldNumber),
        _ => throw new ArgumentOutOfRangeException(
            nameof(field),
            field.FieldType,
            "The tests sample read-only text and number fields only."),
    };

    // The repository's documented refusal of values that break the caller's own contract, such as a key that names no
    // data field or a read-only field: an unwrapped ArgumentException for the values parameter, whose fixed message
    // names no key, sent after metadata reads only.
    private static void AssertRefusedBeforeRecordSql(
        ArgumentException rejected,
        string expectedParameter,
        string refusedKey,
        SqlCommandRecorder recorder,
        ProvenanceForm form)
    {
        Assert.Equal(expectedParameter, rejected.ParamName);
        Assert.DoesNotContain(refusedKey, rejected.Message, StringComparison.Ordinal);
        AssertNoRecordSql(recorder, form);
    }

    // The repository's refusal of a write whose metadata changed after the caller read it: a DataStoreWriteException
    // for the write's operation that reports NotWritten and carries no SQL error number, sent after metadata reads
    // only.
    private static void AssertNotWrittenBeforeRecordSql(
        DataStoreWriteException failure,
        string expectedOperation,
        SqlCommandRecorder recorder,
        ProvenanceForm form)
    {
        Assert.Equal(expectedOperation, failure.Operation);
        Assert.Equal(WriteOutcome.NotWritten, failure.Outcome);
        Assert.Null(failure.SqlErrorNumber);
        AssertNoRecordSql(recorder, form);
    }

    // No record SQL: a record command, a write, or any command under a record operation would mean the write reached
    // a base or page table.
    private static void AssertNoRecordSql(SqlCommandRecorder recorder, ProvenanceForm form)
    {
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

    // One form as the reader returned it, with the tables its record commands name.
    private sealed class ProvenanceForm
    {
        public ProvenanceForm(FormMetadata metadata, FormSchema schema)
        {
            Metadata = metadata;

            var tables = new HashSet<string>(StringComparer.Ordinal) { schema.BaseTable.Name };
            foreach (PageTableSchema page in schema.Pages)
            {
                if (page.Table is { } table)
                {
                    tables.Add(table.Name);
                }
            }

            Tables = tables;
        }

        public FormMetadata Metadata { get; }

        // The base table and every page table the catalogue returned.
        public IReadOnlySet<string> Tables { get; }

        // A record command names a base or page table of this form; metadata and catalogue reads are fixed text that
        // names only meta* tables and catalogue views.
        public bool IsRecordCommand(RecordedCommand command) =>
            SqlCommandRecorder.BracketedIdentifiers(command.CommandText).Any(Tables.Contains);
    }
}
