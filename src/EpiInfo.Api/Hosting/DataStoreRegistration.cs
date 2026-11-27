using EpiInfo.Data;
using EpiInfo.Data.Sql;
using EpiInfo.Domain.Storage;

namespace EpiInfo.Api.Hosting;

/// <summary>
/// Registers the SQL Server data store and the services it is built from. <c>EpiInfo.Data</c> reads no configuration
/// and takes no dependency-injection dependency, so this class turns the API's configuration into the plain
/// <see cref="DatabaseOptions"/> record the store is constructed with.
/// </summary>
/// <remarks>
/// <para>
/// Each service is registered once, under the exact service type a test host replaces with <c>RemoveAll</c>:
/// <see cref="IDataStore"/>, <see cref="ISqlCommandObserver"/> and <see cref="TimeProvider"/>. The store is built from
/// the registered observer, so a replacement observer sees every command the store executes.
/// </para>
/// <para>
/// The options and the store this class registers are created only on first resolution, from the built configuration,
/// so settings a test host applies during <c>Build</c> are honoured. A missing connection string is not a registration
/// or startup error: each store call that needs a connection then throws <see cref="DataStoreUnavailableException"/>
/// before any connection object is created, so database-backed endpoints and <c>/health</c> answer 503 and the process
/// keeps serving. A call that needs no connection, such as an options lookup for a form with no code table to read,
/// still succeeds. Nothing here logs, formats or stores the connection string anywhere but in the options records.
/// </para>
/// </remarks>
public static class DataStoreRegistration
{
    /// <summary>
    /// Registers, each once as a singleton: <see cref="ApiOptions"/> from the built configuration,
    /// <see cref="DatabaseOptions"/> from those options, <see cref="NullSqlCommandObserver"/> as
    /// <see cref="ISqlCommandObserver"/>, <see cref="SqlServerDataStore"/> as <see cref="IDataStore"/>, and
    /// <see cref="TimeProvider.System"/> as <see cref="TimeProvider"/>.
    /// </summary>
    /// <param name="services">The application's service collection.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddEpiInfoDataStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(provider => ApiOptions.FromConfiguration(provider.GetRequiredService<IConfiguration>()));
        services.AddSingleton(provider =>
        {
            ApiOptions options = provider.GetRequiredService<ApiOptions>();
            return new DatabaseOptions(options.ConnectionString, options.CommandTimeoutSeconds);
        });
        services.AddSingleton<ISqlCommandObserver>(NullSqlCommandObserver.Instance);
        services.AddSingleton<IDataStore>(provider => new SqlServerDataStore(
            provider.GetRequiredService<DatabaseOptions>(),
            provider.GetRequiredService<ISqlCommandObserver>()));
        services.AddSingleton<TimeProvider>(TimeProvider.System);

        return services;
    }

    /// <summary>
    /// Whether <paramref name="configuration"/> holds a non-blank <see cref="ApiOptions.ConnectionStringKey"/>. Only
    /// that key is read, and its value is never returned, logged or formatted.
    /// </summary>
    /// <param name="configuration">The configuration to inspect.</param>
    /// <returns><see langword="false"/> when the key is absent, empty or whitespace.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    public static bool HasConnectionString(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[ApiOptions.ConnectionStringKey]);
    }
}
