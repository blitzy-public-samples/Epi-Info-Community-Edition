using System.Collections;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text;
using EpiInfo.Data.Metadata;
using EpiInfo.Data.Records;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Forms;
using EpiInfo.Domain.Metadata;
using EpiInfo.Tests.Shared;
using Microsoft.Data.SqlClient;

namespace EpiInfo.Data.Tests.Records;

/// <summary>
/// Provenance tests that need no database: the catalogue schema types offer no way to obtain or change an instance
/// except through <c>SqlMetadataReader</c>, and the selected-set predicate that <see cref="RecordSqlBuilder"/> and
/// <c>SearchSqlBuilder</c> share takes no SQL text from its caller. They inspect the types by reflection and build no
/// schema.
/// </summary>
public sealed class IdentifierProvenanceTests
{
    // The declared types a public collection-typed property may have: read-only views and plain sequences.
    private static readonly Type[] ReadOnlyCollectionDefinitions =
    [
        typeof(IReadOnlyList<>),
        typeof(IReadOnlyCollection<>),
        typeof(IReadOnlyDictionary<,>),
        typeof(IEnumerable<>),
    ];

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
/// Provenance tests of the selected-set predicate on each seeded form's schema as <c>SqlMetadataReader</c> builds it
/// from the catalogue: the keyword is fixed text, the column is the reader-built <c>RecStatus</c> column quoted by
/// <see cref="SqlIdentifier"/>, and the active status is bound as a parameter of that column's type.
/// </summary>
/// <remarks>
/// Each form is resolved by name through the reader, never by a hard-coded id. The tests read only metadata and the
/// catalogue, write nothing, and hold the shared test lock.
/// </remarks>
[Collection(DatabaseCollections.Database)]
[Trait("Category", "Integration")]
public sealed class IdentifierProvenanceIntegrationTests
{
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
}
