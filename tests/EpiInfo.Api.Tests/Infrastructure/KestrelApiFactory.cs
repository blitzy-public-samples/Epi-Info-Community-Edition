using EpiInfo.Api.Hosting;
using EpiInfo.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace EpiInfo.Api.Tests.Infrastructure;

/// <summary>
/// The API host on real Kestrel, for the checks TestServer cannot make: the bound listen addresses, Kestrel's own
/// request body limit and media-type handling over a socket. Options, replaced services and doubles are exactly those
/// of <see cref="ApiFactory"/>, because both derive from <see cref="ApiFactoryBase"/>.
/// </summary>
/// <remarks>
/// <para>
/// The listen address comes from configuration only, so <see cref="LoopbackBindingGuard"/> checks it before any socket
/// opens: the committed <c>Kestrel:Endpoints:Http:Url</c> is replaced by <see cref="EphemeralListenUrl"/>, and the
/// operating system picks a free port, so factories alive at once, in this process or beside a running API, never
/// collide. The committed <c>Kestrel:Limits:MaxRequestBodySize</c> is left as it is, so the enforced limit is the one
/// production applies. Disposing the factory stops Kestrel and frees the port.
/// </para>
/// <para>
/// Create it per test or in <c>IAsyncLifetime</c>, as <see cref="ApiFactory"/>; it cannot be an xUnit class fixture,
/// which supplies no value for the options parameter.
/// </para>
/// </remarks>
public sealed class KestrelApiFactory : ApiFactoryBase
{
    /// <summary>The configuration key of the one listen endpoint <c>appsettings.json</c> commits.</summary>
    public const string ListenUrlKey = "Kestrel:Endpoints:Http:Url";

    /// <summary>
    /// The listen URL this factory sets: IPv4 loopback exactly, which the guard allows, on a port the operating system
    /// chooses.
    /// </summary>
    public const string EphemeralListenUrl = "http://127.0.0.1:0";

    /// <summary>Creates the factory; Kestrel starts on <see cref="Start"/>, the first client or <c>Services</c> access.</summary>
    /// <param name="options">
    /// The host configuration; the defaults when <see langword="null"/>. An <see cref="ApiFactoryOptions.ExtraSettings"/>
    /// entry for <see cref="ListenUrlKey"/> replaces <see cref="EphemeralListenUrl"/>, and every other entry, such as a
    /// <c>urls</c> value the guard must reject, reaches the host unchanged.
    /// </param>
    /// <exception cref="ArgumentException">The options are inconsistent.</exception>
    public KestrelApiFactory(ApiFactoryOptions? options = null)
        : base(options)
    {
        // The parameterless overload sets no listen address of its own, so configuration alone decides what is bound.
        UseKestrel();
    }

    private KestrelApiFactory(DatabaseHost host)
        : base(host)
    {
        UseKestrel();
    }

    /// <summary>
    /// The base address of a client that reaches this host over a socket: the first address Kestrel bound.
    /// </summary>
    /// <exception cref="InvalidOperationException">The server reports no bound address.</exception>
    public Uri BaseAddress => new(BoundAddresses[0]);

    /// <summary>
    /// A copy of every address Kestrel bound, as <see cref="IServerAddressesFeature"/> reports it once the server has
    /// started, with the port it actually listens on. Reading it starts the server.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The server has no addresses feature, or it reports no address.
    /// </exception>
    public IReadOnlyList<string> BoundAddresses
    {
        get
        {
            Start();
            IServerAddressesFeature? feature =
                Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            if (feature is null || feature.Addresses.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The started server reports no bound address through {nameof(IServerAddressesFeature)}.");
            }

            return Array.AsReadOnly(feature.Addresses.ToArray());
        }
    }

    /// <summary>
    /// Creates a factory on real Kestrel whose host uses the SQL Server store and the rehearsal database, after proving
    /// that database reachable, exactly as <see cref="ApiFactory.ForDatabaseAsync"/> does: the host connects with the
    /// connection string the <see cref="TestDatabase"/> probe proved reachable, and no setting can replace it.
    /// </summary>
    /// <param name="options">
    /// The host configuration, under the same rules as <see cref="ApiFactory.ForDatabaseAsync"/>; the defaults when
    /// <see langword="null"/>.
    /// </param>
    /// <returns>The factory, not yet started.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/> is not valid for a database host. It is thrown before the probe.
    /// </exception>
    /// <exception cref="TestDatabaseUnavailableException">
    /// The database is not configured or cannot be reached. Integration tests let it fail the test; they never skip.
    /// </exception>
    public static async Task<KestrelApiFactory> ForDatabaseAsync(ApiFactoryOptions? options = null)
    {
        // PrepareAsync awaits the TestDatabase probe after validating the options, and pins the probed target.
        DatabaseHost host = await DatabaseHost.PrepareAsync(options).ConfigureAwait(false);
        return new KestrelApiFactory(host);
    }

    /// <summary>
    /// Starts Kestrel if it has not started. A listen setting the guard rejects fails here, before any socket opens.
    /// </summary>
    public void Start() => StartServer();

    /// <summary>
    /// Creates a client that sends real HTTP over a socket to <see cref="BaseAddress"/>, without following redirects.
    /// The factory disposes it with the host.
    /// </summary>
    /// <returns>The client.</returns>
    /// <exception cref="InvalidOperationException">
    /// The server reports no bound address, or it is bound to a host other than <c>127.0.0.1</c>.
    /// </exception>
    public HttpClient CreateKestrelClient()
    {
        Start();
        Uri baseAddress = BaseAddress;
        if (!string.Equals(baseAddress.Host, LoopbackBindingGuard.AllowedHost, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Kestrel is bound to host '{baseAddress.Host}', not {LoopbackBindingGuard.AllowedHost}.");
        }

        return CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = baseAddress,
            AllowAutoRedirect = false,
        });
    }

    /// <inheritdoc />
    protected override void ConfigureHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureHost(builder);

        // A test that sets the endpoint deliberately wins. Configuration keys compare case-insensitively.
        bool listenUrlSupplied = Options.ExtraSettings.Keys.Any(
            key => string.Equals(key, ListenUrlKey, StringComparison.OrdinalIgnoreCase));
        if (!listenUrlSupplied)
        {
            builder.UseSetting(ListenUrlKey, EphemeralListenUrl);
        }
    }
}
