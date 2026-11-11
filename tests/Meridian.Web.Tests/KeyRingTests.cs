using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web;
using Meridian.Web.CodeSync.Hosts;
using Meridian.Web.Hosting;
using Meridian.Web.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Testcontainers.PostgreSql;

namespace Meridian.Web.Tests;

/// <summary>
/// The Data Protection key ring: a stable application name, keys kept in the database, older file-based keys copied in and
/// secrets protected by older versions (content-root discriminator) still read and re-protected. Every folder is a temp
/// folder: no test reads or writes a real key folder (the per-user ASP.NET location is replaced by Meridian:LegacyKeysPath).
/// </summary>
public sealed class KeyRingTests : IDisposable
{
    private const string IntegrationsPurpose = "Meridian.Integrations.Secrets.v1";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-keyring-");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_dir.FullName, name)).FullName;

    /// <summary>The real app on SQLite in <paramref name="data"/>, run from <paramref name="contentRoot"/> (as from another install folder).</summary>
    private static async Task<WebApplication> StartAsync(string data, string contentRoot, string legacyKeys, IDictionary<string, string?>? extra = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Meridian:Storage"] = "sqlite",
            ["Meridian:DataPath"] = data,
            [KeyRing.LegacyKeysPathKey] = legacyKeys,   // never the user's own ASP.NET key folder
            ["Logging:LogLevel:Default"] = "Warning",
        };
        if (extra != null) foreach (var kv in extra) settings[kv.Key] = kv.Value;
        var app = await MeridianServer.CreateAsync([], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        }, new WebApplicationOptions { ApplicationName = "Meridian.Web", EnvironmentName = "Production", ContentRootPath = contentRoot });
        await app.StartAsync();
        await app.Services.GetRequiredService<LegacySecretMigration>().Completion;
        return app;
    }

    private static async Task StopAsync(WebApplication app)
    {
        await app.StopAsync();
        await app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    private static CodeRepoConfig Repo() => new() { Host = GitHostKind.GitHub, Owner = "finepointcgi", Repo = "meridian", Branch = "main" };

    /// <summary>What an older Meridian did: AddDataProtection with no application name (so the content-root path) and keys in <paramref name="keys"/>.</summary>
    private static IDataProtectionProvider OldVersion(string contentRoot, string keys)
    {
        var s = new ServiceCollection();
        s.AddSingleton<IHostEnvironment>(new OldHost(contentRoot));
        s.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys));
        return s.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
    }

    private sealed class OldHost(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "Meridian.Web";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static async Task<(Guid Project, UserAccount User)> SeedProjectAsync(IMeridianStore db)
    {
        var user = await db.CreateUserAsync("alice", "Alice", "correct horse");
        return (await db.CreateProjectAsync("Plant", ProjectFile.Serialize(SampleModels.Vehicle()), user.Id), user);
    }

    // ------------------------------------------------------------------ the bug: two install folders, one database

    [Fact]
    public async Task A_Token_Saved_From_One_Install_Folder_Is_Read_From_Another()
    {
        var data = Folder("data");
        var legacy = Path.Combine(_dir.FullName, "no-legacy-keys");
        Guid connection;

        var first = await StartAsync(data, Folder("install-a"), legacy);
        try
        {
            var db = first.Services.GetRequiredService<IMeridianStore>();
            var (project, _) = await SeedProjectAsync(db);
            var saved = await first.Services.GetRequiredService<CodeSyncHostService>().SaveAsync(project, null, "meridian", Repo(), "ghp_token_from_install_a");
            connection = saved.Connection.Id;
        }
        finally { await StopAsync(first); }

        var second = await StartAsync(data, Folder("install-b"), legacy);
        try
        {
            var db = second.Services.GetRequiredService<IMeridianStore>();
            var c = (await db.FindIntegrationAsync(connection))!;
            Assert.Equal("ghp_token_from_install_a", second.Services.GetRequiredService<IntegrationService>().Unprotect(c.ProtectedSecret));
            Assert.Equal("finepointcgi/meridian", second.Services.GetRequiredService<CodeSyncHostService>().Host(c).RepoDisplay);   // the call that failed
            var webhook = CodeRepoConfig.Parse(c.ConfigJson).ProtectedWebhookSecret;
            Assert.NotNull(second.Services.GetRequiredService<IntegrationService>().Unprotect(webhook));

            // The key ring is in the database, not in a folder next to it.
            Assert.NotEmpty(((IDataProtectionKeyStore)db).ListDataProtectionKeys());
            Assert.False(Directory.Exists(Path.Combine(data, "keys")));
        }
        finally { await StopAsync(second); }
    }

    // ------------------------------------------------------------------ secrets from older versions

    [Fact]
    public async Task Secrets_From_An_Older_Version_Are_Migrated_On_Start_And_Keep_Working_After_A_Move()
    {
        var data = Folder("data");
        var oldRoot = Folder("old-install") + Path.DirectorySeparatorChar;   // as AppContext.BaseDirectory: with the separator
        var oldKeys = Path.Combine(data, "keys");   // where desktop local mode and Meridian:KeysPath kept them
        var old = OldVersion(oldRoot, oldKeys).CreateProtector(IntegrationsPurpose);
        var legacyToken = old.Protect("ghp_written_by_0_9");
        var legacyWebhook = old.Protect("webhook-secret-0-9");
        Guid connection, project;
        {
            var seed = new SqliteStore(Path.Combine(data, "meridian.db"));
            await seed.MigrateAsync();
            (project, _) = await SeedProjectAsync(seed);
            var cfg = Repo();
            cfg.ProtectedWebhookSecret = legacyWebhook;
            var c = new IntegrationConnection(Guid.NewGuid(), project, CodeRepoKinds.CodeRepo, "meridian", cfg.ToJson(), legacyToken, DateTime.UtcNow);
            await seed.SaveIntegrationAsync(c);
            connection = c.Id;
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var keyFiles = Directory.GetFiles(oldKeys).Select(File.ReadAllText).ToList();

        // The upgraded version, started from the same folder: copies the key files in and re-protects on start.
        var upgraded = await StartAsync(data, oldRoot, Path.Combine(_dir.FullName, "no-legacy-keys"));
        try
        {
            var db = upgraded.Services.GetRequiredService<IMeridianStore>();
            var c = (await db.FindIntegrationAsync(connection))!;
            Assert.NotEqual(legacyToken, c.ProtectedSecret);
            Assert.NotEqual(legacyWebhook, CodeRepoConfig.Parse(c.ConfigJson).ProtectedWebhookSecret);
            Assert.Equal(1, await upgraded.Services.GetRequiredService<LegacySecretMigration>().Completion);
            var copied = ((IDataProtectionKeyStore)db).ListDataProtectionKeys().Select(k => k.Name).ToHashSet();
            Assert.All(Directory.GetFiles(oldKeys), f => Assert.Contains(Path.GetFileNameWithoutExtension(f), copied));
        }
        finally { await StopAsync(upgraded); }

        // The old key files are untouched (copied, never moved or deleted).
        Assert.Equal(keyFiles, Directory.GetFiles(oldKeys).Select(File.ReadAllText).ToList());

        // Moved to another folder: the re-protected secrets use the stable name, so the content root no longer matters.
        var moved = await StartAsync(data, Folder("moved-install"), Path.Combine(_dir.FullName, "no-legacy-keys"));
        try
        {
            var c = (await moved.Services.GetRequiredService<IMeridianStore>().FindIntegrationAsync(connection))!;
            var integrations = moved.Services.GetRequiredService<IntegrationService>();
            Assert.Equal("ghp_written_by_0_9", integrations.Unprotect(c.ProtectedSecret));
            Assert.Equal("webhook-secret-0-9", integrations.Unprotect(CodeRepoConfig.Parse(c.ConfigJson).ProtectedWebhookSecret));
        }
        finally { await StopAsync(moved); }
    }

    [Fact]
    public async Task Keys_In_The_Per_User_Folder_Of_An_Older_Server_Are_Copied_And_Its_Secrets_Read()
    {
        var data = Folder("data");
        var root = Folder("server");   // without a trailing separator this time
        var perUser = Folder("aspnet-DataProtection-Keys");   // stands in for ~/.aspnet/DataProtection-Keys
        var legacySecret = OldVersion(root, perUser).CreateProtector(IntegrationsPurpose).Protect("doors-password");
        var before = Directory.GetFiles(perUser).Select(File.ReadAllText).ToList();

        var app = await StartAsync(data, root, perUser);
        try
        {
            var integrations = app.Services.GetRequiredService<IntegrationService>();
            Assert.Equal("doors-password", integrations.Unprotect(legacySecret));
            var keys = ((IDataProtectionKeyStore)app.Services.GetRequiredService<IMeridianStore>()).ListDataProtectionKeys();
            Assert.Contains(keys, k => before.Any(f => f.Contains(k.Name[4..], StringComparison.Ordinal)));
        }
        finally { await StopAsync(app); }
        Assert.Equal(before, Directory.GetFiles(perUser).Select(File.ReadAllText).ToList());
    }

    [Fact]
    public async Task A_Legacy_Secret_Is_Re_Protected_And_Saved_On_First_Read()
    {
        var keys = Folder("keys");
        var oldRoot = Folder("old");
        var legacySecret = OldVersion(oldRoot, keys).CreateProtector(IntegrationsPurpose).Protect("glpat-legacy");
        var current = DataProtectionProvider.Create(new DirectoryInfo(keys), b => b.SetApplicationName(KeyRing.ApplicationName));
        var legacy = new LegacySecretKeys([.. KeyRing.LegacyApplicationNames(oldRoot)], s => s.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)));
        var db = new SqliteStore(Path.Combine(_dir.FullName, "m.db"));
        await db.MigrateAsync();
        var (project, _) = await SeedProjectAsync(db);
        var c = new IntegrationConnection(Guid.NewGuid(), project, IntegrationKinds.IssueTracker, "GitLab", "{}", legacySecret, DateTime.UtcNow);
        await db.SaveIntegrationAsync(c);

        var integrations = new IntegrationService(db, current, new FakeHttpClientFactory(new HttpClientHandler()), legacy);
        Assert.Equal("glpat-legacy", integrations.Unprotect(c.ProtectedSecret, c.Id));
        await integrations.PendingMigration;

        var stored = (await db.FindIntegrationAsync(c.Id))!.ProtectedSecret;
        Assert.NotEqual(legacySecret, stored);
        Assert.Equal("glpat-legacy", current.CreateProtector(IntegrationsPurpose).Unprotect(stored!));   // no fallback needed any more
        Assert.False(await integrations.MigrateConnectionAsync(c.Id));   // nothing left to migrate

        // Without the legacy reader the old secret is unreadable: the clear "enter it again" path still applies.
        Assert.Null(new IntegrationService(db, current, new FakeHttpClientFactory(new HttpClientHandler())).Unprotect(legacySecret));
        Assert.Null(integrations.Unprotect("not-a-protected-payload"));
        legacy.Dispose();
    }

    [Fact]
    public void The_Desktop_AI_Key_From_An_Older_Version_Is_Re_Protected_When_Loaded()
    {
        var data = Folder("desktop-data");
        var keys = Path.Combine(data, "keys");
        var oldRoot = Folder("Meridian.app");
        var legacyKey = OldVersion(oldRoot, keys).CreateProtector("Meridian.Assistant.ApiKey.v1").Protect("sk-ant-0123456789abcdefghij");
        File.WriteAllText(Path.Combine(data, "assistant.json"), System.Text.Json.JsonSerializer.Serialize(new { ProtectedKey = legacyKey }));
        var current = DataProtectionProvider.Create(new DirectoryInfo(keys), b => b.SetApplicationName(KeyRing.ApplicationName));
        using var legacy = new LegacySecretKeys([.. KeyRing.LegacyApplicationNames(oldRoot)], s => s.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)));
        var config = new ConfigurationBuilder().Build();

        var settings = new Meridian.Web.Services.AssistantSettings(config, new MeridianHostInfo(true, data, null), current, legacy) { ReadEnvironment = _ => null };
        Assert.Equal(1, settings.MigratedSecrets);
        Assert.Equal("sk-ant-0123456789abcdefghij", settings.ApiKey());
        Assert.DoesNotContain(legacyKey, File.ReadAllText(Path.Combine(data, "assistant.json")));

        // Read again without the legacy reader (as after a move): the re-protected key needs only the stable name.
        var again = new Meridian.Web.Services.AssistantSettings(config, new MeridianHostInfo(true, data, null), current) { ReadEnvironment = _ => null };
        Assert.Equal("sk-ant-0123456789abcdefghij", again.ApiKey());
    }

    // ------------------------------------------------------------------ encryption at rest

    [Fact]
    public async Task A_Master_Key_Encrypts_The_Stored_Key_Ring()
    {
        var data = Folder("data");
        var masterKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var extra = new Dictionary<string, string?> { [KeyRing.MasterKeyKey] = masterKey };
        var legacy = Path.Combine(_dir.FullName, "no-legacy-keys");
        string protectedValue;

        var first = await StartAsync(data, Folder("a"), legacy, extra);
        try
        {
            protectedValue = first.Services.GetRequiredService<IntegrationService>().Protect("s3cret");
            var stored = ((IDataProtectionKeyStore)first.Services.GetRequiredService<IMeridianStore>()).ListDataProtectionKeys();
            Assert.NotEmpty(stored);
            Assert.All(stored, k =>
            {
                Assert.Contains("meridianEncryptedKey", k.Xml);
                Assert.DoesNotContain("<masterKey", k.Xml);
            });
        }
        finally { await StopAsync(first); }

        var second = await StartAsync(data, Folder("b"), legacy, extra);
        try { Assert.Equal("s3cret", second.Services.GetRequiredService<IntegrationService>().Unprotect(protectedValue)); }
        finally { await StopAsync(second); }

        // Without the master key the stored keys can't be read.
        var third = await StartAsync(data, Folder("c"), legacy);
        try { Assert.Null(third.Services.GetRequiredService<IntegrationService>().Unprotect(protectedValue)); }
        finally { await StopAsync(third); }
    }

    // ------------------------------------------------------------------ the database key store

    [Fact]
    public async Task Sqlite_Key_Store_Contract() => await KeyStoreContractAsync(
        () => new SqliteStore(Path.Combine(_dir.FullName, "contract.db")));

    /// <summary>The same against PostgreSQL in a throwaway Testcontainers server (random host port). Skipped without Docker.</summary>
    [SkippableFact]
    public async Task Postgres_Key_Store_Contract()
    {
        PostgreSqlContainer container;
        try
        {
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("MERIDIAN_REQUIRE_POSTGRES") is not ("1" or "true"))
        {
            Skip.If(true, $"Docker is not available ({ex.GetType().Name}); set MERIDIAN_REQUIRE_POSTGRES=1 to make this a failure.");
            return;
        }
        await using (container)
        {
            var cs = container.GetConnectionString();
            await KeyStoreContractAsync(() => new PostgresStore(cs));
        }
    }

    private static async Task KeyStoreContractAsync(Func<IMeridianStore> open)
    {
        var stores = Enumerable.Range(0, 4).Select(_ => open()).ToList();
        try
        {
            // The table is created on first use, before any migration, and concurrent creation / migration is safe.
            var ks = (IDataProtectionKeyStore)stores[0];
            Assert.Empty(ks.ListDataProtectionKeys());
            await Task.WhenAll(stores.Select(s => Task.Run(s.MigrateAsync)).Concat(stores.Select(s => Task.Run(() => ((IDataProtectionKeyStore)s).ListDataProtectionKeys()))));
            await stores[1].MigrateAsync();

            ks.StoreDataProtectionKey("key-1", "<key id=\"1\" />");
            ks.StoreDataProtectionKey("key-1", "<key id=\"1\" v=\"2\" />");   // replaces
            Assert.True(ks.AddDataProtectionKeyIfMissing("key-2", "<key id=\"2\" />"));
            Assert.False(ks.AddDataProtectionKeyIfMissing("key-2", "<key id=\"2\" v=\"other\" />"));   // copies never overwrite
            var other = (IDataProtectionKeyStore)stores[2];
            var all = other.ListDataProtectionKeys();
            Assert.Equal(["key-1", "key-2"], all.Select(k => k.Name).Order());
            Assert.Equal("<key id=\"1\" v=\"2\" />", all.Single(k => k.Name == "key-1").Xml);
            Assert.Equal("<key id=\"2\" />", all.Single(k => k.Name == "key-2").Xml);

            // Two "server instances" sharing the database share keys: one protects, the other unprotects.
            IDataProtectionProvider Instance(IDataProtectionKeyStore store)
            {
                var s = new ServiceCollection();
                s.AddDataProtection().SetApplicationName(KeyRing.ApplicationName);
                s.Configure<KeyManagementOptions>(o => o.XmlRepository = new DatabaseXmlRepository(store));
                return s.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
            }
            var a = Instance((IDataProtectionKeyStore)stores[1]).CreateProtector("p");
            var b = Instance((IDataProtectionKeyStore)stores[3]).CreateProtector("p");
            Assert.Equal("shared", b.Unprotect(a.Protect("shared")));
            Assert.Contains(other.ListDataProtectionKeys(), k => k.Name.StartsWith("key-", StringComparison.Ordinal) && k.Xml.Contains("<key ", StringComparison.Ordinal) && k.Xml.Contains("creationDate", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var s in stores) if (s is IAsyncDisposable d) await d.DisposeAsync();
        }
    }
}
