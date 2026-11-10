using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Core.DataModeling;
using Meridian.Data;
using Meridian.Data.Databases;
using Meridian.Web.Services;

namespace Meridian.Web.Integrations.Databases;

/// <summary>The non-secret part of a database connection (stored as the integration's config JSON).</summary>
public sealed class DatabaseConnectionConfig
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = LiveProviders.PostgreSql;
    /// <summary>Host (or file name for SQLite) shown in lists; the connection string itself is the encrypted secret.</summary>
    [JsonPropertyName("display")] public string Display { get; set; } = "";
    /// <summary>Schemas to import (empty: all non-system schemas; MySQL: the connection's database).</summary>
    [JsonPropertyName("schemas")] public List<string> Schemas { get; set; } = new();
    [JsonPropertyName("timeout")] public int TimeoutSeconds { get; set; } = 30;
    /// <summary>Id of the database package in the model this connection was imported into (refresh target).</summary>
    [JsonPropertyName("database")] public string? DatabaseElementId { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static DatabaseConnectionConfig Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<DatabaseConnectionConfig>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }
}

/// <summary>
/// Live database connections for schema import and refresh ("database" integrations). The connection string is the
/// secret: encrypted with Data Protection, decrypted only to connect, never shown again or logged.
/// <list type="bullet">
/// <item>On a team server only server administrators may add or change connections, and hosts must pass the same outbound
/// guard as webhooks: loopback, private and link-local addresses are refused unless an administrator sets
/// <c>Meridian:Integrations:AllowPrivateNetworks=true</c>; the checked address is pinned for the connection; SQLite
/// files and Unix sockets (paths on the server) are refused.</item>
/// <item>In the desktop app's local mode connections are unrestricted (single user, own machine).</item>
/// <item>Meridian only reads catalog views in read-only transactions. Use a read-only database account.</item>
/// </list>
/// </summary>
public sealed class DatabaseConnectionService(IntegrationService integrations, IMeridianStore db, MeridianHostInfo host, IConfiguration config)
{
    public const string Kind = "database";

    public bool LocalMode => host.LocalMode;
    private bool AllowPrivate => string.Equals(config[OutboundGuard.AllowPrivateNetworksKey], "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Who may add or change connections: anyone in local mode, server administrators on a team server.</summary>
    public bool CanManage(bool isServerAdmin) => host.LocalMode || isServerAdmin;

    public Task<IReadOnlyList<IntegrationConnection>> ListAsync(Guid projectId) => integrations.ListAsync(projectId, Kind);

    public async Task<IntegrationConnection?> FindAsync(Guid projectId, Guid id) =>
        await db.FindIntegrationAsync(id) is { } c && c.ProjectId == projectId && c.Kind == Kind ? c : null;

    /// <summary>Saves a connection. <paramref name="connectionString"/> empty keeps the stored one.</summary>
    public async Task<IntegrationConnection> SaveAsync(Guid projectId, Guid? id, string name, DatabaseConnectionConfig cfg, string? connectionString, bool isServerAdmin)
    {
        if (!CanManage(isServerAdmin)) throw new DatabaseImportException("Only server administrators can add database connections on a team server.");
        if (!LiveProviders.All.Contains(cfg.Provider)) throw new DatabaseImportException($"Live connections to {cfg.Provider} are not supported; import a DDL script instead.");
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            var ep = Endpoint(cfg.Provider, connectionString);
            CheckAllowed(ep);
            cfg.Display = ep.Host is { } h ? $"{h}:{ep.Port}/{ep.Database}" : Path.GetFileName(ep.File ?? "");
        }
        else if (id == null) throw new DatabaseImportException("Enter the connection string.");
        cfg.TimeoutSeconds = Math.Clamp(cfg.TimeoutSeconds, 5, 600);
        return await integrations.SaveAsync(projectId, id, Kind, string.IsNullOrWhiteSpace(name) ? cfg.Display : name, cfg.ToJson(), connectionString);
    }

    public Task DeleteAsync(Guid projectId, Guid id, bool isServerAdmin) =>
        CanManage(isServerAdmin) ? integrations.DeleteAsync(projectId, id) : throw new DatabaseImportException("Only server administrators can remove database connections on a team server.");

    /// <summary>Remembers which database in the model a connection fills (for later refreshes).</summary>
    public async Task LinkAsync(Guid projectId, IntegrationConnection c, string databaseElementId)
    {
        var cfg = DatabaseConnectionConfig.Parse(c.ConfigJson);
        if (cfg.DatabaseElementId == databaseElementId) return;
        cfg.DatabaseElementId = databaseElementId;
        await integrations.SaveAsync(projectId, c.Id, Kind, c.Name, cfg.ToJson(), null);
    }

    /// <summary>Reads the schema through a saved connection.</summary>
    public async Task<DbCatalog> ReadAsync(IntegrationConnection c, CancellationToken ct)
    {
        var cfg = DatabaseConnectionConfig.Parse(c.ConfigJson);
        var cs = integrations.Unprotect(c.ProtectedSecret) ?? throw new DatabaseImportException($"The connection string of “{c.Name}” is missing or can no longer be decrypted — enter it again in Integrations ▸ Databases.");
        return await ReadAsync(cfg, cs, ct);
    }

    /// <summary>Tests unsaved settings (a stored connection string is used when <paramref name="connectionString"/> is empty).</summary>
    public async Task<string> TestAsync(Guid projectId, Guid? id, DatabaseConnectionConfig cfg, string? connectionString, bool isServerAdmin, CancellationToken ct)
    {
        if (!CanManage(isServerAdmin)) throw new DatabaseImportException("Only server administrators can test database connections on a team server.");
        var cs = connectionString;
        if (string.IsNullOrWhiteSpace(cs) && id is { } i && await FindAsync(projectId, i) is { } saved) cs = integrations.Unprotect(saved.ProtectedSecret);
        if (string.IsNullOrWhiteSpace(cs)) throw new DatabaseImportException("Enter the connection string.");
        var catalog = await ReadAsync(WithTimeout(cfg, 15), cs, ct);
        return $"Connected: {catalog.Schemas.Count} schema{(catalog.Schemas.Count == 1 ? "" : "s")}, {catalog.AllTables.Count()} tables and views.";
    }

    private static DatabaseConnectionConfig WithTimeout(DatabaseConnectionConfig c, int seconds) =>
        new() { Provider = c.Provider, Schemas = c.Schemas, TimeoutSeconds = Math.Min(c.TimeoutSeconds, seconds), Display = c.Display, DatabaseElementId = c.DatabaseElementId };

    private async Task<DbCatalog> ReadAsync(DatabaseConnectionConfig cfg, string connectionString, CancellationToken ct)
    {
        var ep = Endpoint(cfg.Provider, connectionString);
        CheckAllowed(ep);
        var pin = host.LocalMode || ep.Host == null ? null : await PinAsync(ep, ct);
        // VerifyFull checks the certificate against the host name: keep the name (the address was checked just now).
        if (pin != null && ep.Provider == LiveProviders.PostgreSql && new Npgsql.NpgsqlConnectionStringBuilder(connectionString).SslMode == Npgsql.SslMode.VerifyFull) pin = null;
        return await LiveSchemaReader.ReadAsync(cfg.Provider, connectionString, cfg.Schemas, TimeSpan.FromSeconds(Math.Clamp(cfg.TimeoutSeconds, 5, 600)), ct, pin);
    }

    private static DatabaseEndpoint Endpoint(string provider, string cs)
    {
        try { return LiveSchemaReader.Endpoint(provider, cs); }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException or NotSupportedException)
        {
            throw new DatabaseImportException("The connection string could not be read: " + ex.Message.Split('\n')[0]);
        }
    }

    /// <summary>Team servers: no files or sockets on the server, and a host name is required.</summary>
    private void CheckAllowed(DatabaseEndpoint ep)
    {
        if (host.LocalMode) return;
        if (ep.Provider == LiveProviders.Sqlite) throw new DatabaseImportException("SQLite files can only be imported in the desktop app (a file path would point at the server). Upload a DDL script instead.");
        if (string.IsNullOrWhiteSpace(ep.Host)) throw new DatabaseImportException("The connection string has no host.");
        if (ep.Host.Contains(',')) throw new DatabaseImportException("Connect to one host (multiple hosts are not supported on a team server).");
        if (ep.Host.StartsWith('/') || ep.Host.StartsWith('@')) throw new DatabaseImportException("Unix socket connections are not allowed on a team server.");
    }

    /// <summary>Resolves the host once, refuses blocked addresses (outbound guard rules) and returns the address to connect to.</summary>
    internal async Task<string> PinAsync(DatabaseEndpoint ep, CancellationToken ct)
    {
        var hostName = ep.Host!.Trim('[', ']');
        IPAddress[] addresses;
        try { addresses = IPAddress.TryParse(hostName, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(hostName, ct); }
        catch (System.Net.Sockets.SocketException) { throw new DatabaseImportException($"{hostName} could not be resolved."); }
        var allowed = addresses.FirstOrDefault(a => !OutboundGuard.IsBlocked(a, AllowPrivate));
        if (allowed == null)
            throw new DatabaseImportException(addresses.Length == 0 ? $"{hostName} could not be resolved."
                : $"Connections to {hostName} are not allowed: it is a loopback, link-local or private network address. A server administrator can allow private networks with {OutboundGuard.AllowPrivateNetworksKey}=true.");
        return allowed.ToString();
    }
}
