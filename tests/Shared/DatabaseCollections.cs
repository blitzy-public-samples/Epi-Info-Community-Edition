namespace EpiInfo.Tests.Shared;

/// <summary>
/// Names of the xUnit collections shared by the database-backed test projects.
/// </summary>
public static class DatabaseCollections
{
    /// <summary>
    /// The collection name every database-backed test class uses in
    /// <c>[Collection(DatabaseCollections.Database)]</c>.
    /// </summary>
    public const string Database = "Database";
}

/// <summary>
/// The xUnit collection for test classes that touch <c>EpiRehearsal</c>.
/// </summary>
/// <remarks>
/// <para>
/// Join it with every test class in <c>EpiInfo.Data.Tests</c> and <c>EpiInfo.Api.Tests</c> that touches
/// the database, which is every <c>[Trait("Category", "Integration")]</c> class. Tests that never touch
/// the database need not join it.
/// </para>
/// <para>
/// Within one assembly the collection runs its tests one at a time and never in parallel with another
/// collection, so a test never waits on a <c>DatabaseWriteLock</c> its own assembly holds.
/// </para>
/// <para>
/// <c>dotnet test EpiInfo.Web.sln</c> runs the test projects concurrently, each in its own process,
/// against the one database. Isolation across those processes comes from <c>DatabaseWriteLock</c> (the
/// application lock <c>EpiInfo.Web.Tests</c>), not from this collection.
/// </para>
/// <para>
/// The collection holds no fixture: each integration test class calls
/// <c>TestDatabase.EnsureReachableAsync()</c> itself, so a test case that runs on an in-memory store is
/// never forced to reach the database. xUnit v2 honours a collection definition only on a public type
/// in the test assembly, which is why this file is linked source compiled into both test projects.
/// </para>
/// </remarks>
[CollectionDefinition(DatabaseCollections.Database, DisableParallelization = true)]
public sealed class DatabaseCollection
{
}
