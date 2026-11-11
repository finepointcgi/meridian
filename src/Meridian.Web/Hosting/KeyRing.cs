using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Meridian.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.Infrastructure;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace Meridian.Web.Hosting;

/// <summary>
/// Where the ASP.NET Data Protection key ring lives, and how secrets written by older versions stay readable.
/// <list type="bullet">
/// <item>The application name is always <see cref="ApplicationName"/>. Before, ASP.NET used the content-root path, so moving
/// the install, unpacking a new version elsewhere or running another build made every stored secret unreadable.</item>
/// <item>The key ring lives in the Meridian database (table <c>data_protection_keys</c>), for SQLite, PostgreSQL and the desktop
/// app alike, unless <c>Meridian:KeysPath</c> names a folder.</item>
/// <item>On start, file-based keys from older versions (DataPath/keys, and the per-user ASP.NET folder on a server, or
/// <c>Meridian:LegacyKeysPath</c>) are copied into the table. Nothing is deleted.</item>
/// <item>Secrets protected under the old content-root discriminator are read through <see cref="LegacySecretKeys"/> and
/// re-protected under the stable name (see <see cref="SecretProtector"/>).</item>
/// <item>Optional encryption of the stored keys: <c>Meridian:KeyEncryptionCertificate</c> (a .pfx, password in
/// <c>Meridian:KeyEncryptionCertificatePassword</c>) or a 256-bit master key in <c>MERIDIAN_KEY_ENCRYPTION_KEY</c> /
/// <c>Meridian:KeyEncryptionKey</c> (base64).</item>
/// </list>
/// docs/DEPLOYMENT.md ("Keys and secrets") describes this for administrators.
/// </summary>
public static class KeyRing
{
    public const string ApplicationName = "Meridian";
    public const string KeysPathKey = "Meridian:KeysPath";
    public const string LegacyKeysPathKey = "Meridian:LegacyKeysPath";
    public const string LegacyApplicationNamesKey = "Meridian:LegacyApplicationNames";
    public const string CertificateKey = "Meridian:KeyEncryptionCertificate";
    public const string CertificatePasswordKey = "Meridian:KeyEncryptionCertificatePassword";
    public const string MasterKeyKey = "Meridian:KeyEncryptionKey";
    public const string MasterKeyEnvironment = "MERIDIAN_KEY_ENCRYPTION_KEY";

    /// <summary>What <see cref="Configure"/> decided, logged once the app is built.</summary>
    public sealed record Setup(string Location, string? KeysFolder, IDataProtectionKeyStore? Database, string? Encryption, IReadOnlyList<string> ImportFolders);

    /// <summary>Sets up Data Protection for the app: stable name, key location, optional key encryption and the legacy reader.</summary>
    public static Setup Configure(WebApplicationBuilder builder, MeridianHostInfo host, IMeridianStore store)
    {
        var config = builder.Configuration;
        var dp = builder.Services.AddDataProtection().SetApplicationName(ApplicationName);

        var keysFolder = config[KeysPathKey] is { Length: > 0 } kp ? kp : null;
        var database = keysFolder is null ? store as IDataProtectionKeyStore : null;
        if (keysFolder is null && database is null) keysFolder = Path.Combine(host.DataPath, "keys");   // a store without a key table
        string location;
        Action<IServiceCollection> repository;
        if (database is not null)
        {
            location = "the Meridian database (table data_protection_keys)";
            repository = s => s.Configure<KeyManagementOptions>(o => o.XmlRepository = new DatabaseXmlRepository(database));
        }
        else
        {
            location = $"the folder {keysFolder}";
            var dir = new DirectoryInfo(keysFolder!);
            repository = s => s.Configure<KeyManagementOptions>(o => o.XmlRepository = new FileSystemXmlRepository(dir, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));
        }
        repository(builder.Services);

        var encryption = ConfigureEncryption(config, builder.Services);

        // Older versions kept keys in files: copied into the database on start (never moved or deleted).
        var imports = new List<string>();
        if (database is not null)
        {
            imports.Add(Path.Combine(host.DataPath, "keys"));
            if (config[LegacyKeysPathKey] is { Length: > 0 } legacy) imports.Add(legacy);
            else if (!host.LocalMode && FileSystemXmlRepository.DefaultKeyStorageDirectory is { } d) imports.Add(d.FullName);   // the per-user ASP.NET folder
        }

        var names = LegacyApplicationNames(builder.Environment.ContentRootPath).ToList();
        if (config[LegacyApplicationNamesKey] is { Length: > 0 } extra)
            foreach (var e in extra.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) names.AddRange(LegacyApplicationNames(e));
        builder.Services.AddSingleton(new LegacySecretKeys(names.Where(n => n.Length > 0 && n != ApplicationName).Distinct(StringComparer.Ordinal).ToList(), s =>
        {
            repository(s);
            ConfigureEncryption(config, s);
        }));
        builder.Services.AddSingleton<LegacySecretMigration>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LegacySecretMigration>());
        return new Setup(location, keysFolder, database, encryption, imports);
    }

    /// <summary>
    /// The application discriminators an older version used when run from <paramref name="contentRoot"/>: ASP.NET took the
    /// content-root path (.NET 10 adds a trailing separator; earlier releases differed), so every spelling is tried.
    /// </summary>
    public static IEnumerable<string> LegacyApplicationNames(string contentRoot)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(contentRoot);
        return new[] { trimmed + Path.DirectorySeparatorChar, contentRoot, trimmed }.Distinct(StringComparer.Ordinal);
    }

    /// <summary>Encrypts stored keys when a certificate or master key is configured; returns a description, or null.</summary>
    private static string? ConfigureEncryption(IConfiguration config, IServiceCollection services)
    {
        if (config[CertificateKey] is { Length: > 0 } pfx)
        {
            var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, config[CertificatePasswordKey], X509KeyStorageFlags.EphemeralKeySet);
            services.AddDataProtection().ProtectKeysWithCertificate(cert);
            return $"certificate {cert.Thumbprint}";
        }
        var master = Environment.GetEnvironmentVariable(MasterKeyEnvironment) is { Length: > 0 } env ? env : config[MasterKeyKey];
        if (master is { Length: > 0 })
        {
            var key = MasterKey.Parse(master);
            services.AddSingleton(key);
            services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new MasterKeyXmlEncryptor(key));
            return "master key";
        }
        return null;
    }

    /// <summary>Copies file-based keys from older versions into the database table (only names it lacks). Returns how many.</summary>
    public static int ImportFileKeys(Setup setup, ILogger log)
    {
        if (setup.Database is null) return 0;
        var added = 0;
        foreach (var folder in setup.ImportFolders.Distinct())
        {
            try
            {
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.EnumerateFiles(folder, "*.xml"))
                {
                    try
                    {
                        var xml = XElement.Load(file).ToString(SaveOptions.DisableFormatting);
                        if (setup.Database.AddDataProtectionKeyIfMissing(Path.GetFileNameWithoutExtension(file), xml)) added++;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
                    {
                        log.LogWarning("Could not copy the Data Protection key file {File}: {Error}", file, ex.Message);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Could not read the Data Protection key folder {Folder}: {Error}", folder, ex.Message);
            }
        }
        if (added > 0) log.LogInformation("Copied {Count} Data Protection key file(s) from earlier versions into the database (the files were left in place)", added);
        return added;
    }
}

/// <summary>The key ring in the Meridian database.</summary>
public sealed class DatabaseXmlRepository(IDataProtectionKeyStore store) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements() =>
        store.ListDataProtectionKeys().Select(k => XElement.Parse(k.Xml)).ToList();

    public void StoreElement(XElement element, string friendlyName) =>
        store.StoreDataProtectionKey(string.IsNullOrWhiteSpace(friendlyName) ? Guid.NewGuid().ToString() : friendlyName,
            element.ToString(SaveOptions.DisableFormatting));
}

/// <summary>A 256-bit key that encrypts the stored key ring (AES-GCM).</summary>
public sealed class MasterKey
{
    internal byte[] Bytes { get; }
    private MasterKey(byte[] bytes) => Bytes = bytes;

    public static MasterKey Parse(string base64)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64.Trim()); }
        catch (FormatException) { throw new InvalidOperationException($"{KeyRing.MasterKeyEnvironment} / {KeyRing.MasterKeyKey} must be 32 random bytes in base64 (e.g. openssl rand -base64 32)."); }
        if (bytes.Length != 32) throw new InvalidOperationException($"{KeyRing.MasterKeyEnvironment} / {KeyRing.MasterKeyKey} must be 32 bytes (base64 of 32 random bytes), not {bytes.Length}.");
        return new MasterKey(bytes);
    }
}

/// <summary>Encrypts a key-ring element with the <see cref="MasterKey"/>.</summary>
public sealed class MasterKeyXmlEncryptor(MasterKey key) : IXmlEncryptor
{
    internal static readonly XName Element = "meridianEncryptedKey";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key.Bytes, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag);
        var element = new XElement(Element, new XAttribute("alg", "AES-256-GCM"), Convert.ToBase64String([.. nonce, .. tag, .. cipher]));
        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }
}

/// <summary>Decrypts elements written by <see cref="MasterKeyXmlEncryptor"/> (activated by Data Protection by type name).</summary>
public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var key = services.GetService<MasterKey>()
            ?? throw new CryptographicException($"The key ring is encrypted with a master key; set {KeyRing.MasterKeyEnvironment} (or {KeyRing.MasterKeyKey}).");
        var data = Convert.FromBase64String(encryptedElement.Value);
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        var plain = new byte[data.Length - nonceSize - tagSize];
        using (var aes = new AesGcm(key.Bytes, tagSize))
            aes.Decrypt(data.AsSpan(0, nonceSize), data.AsSpan(nonceSize + tagSize), data.AsSpan(nonceSize, tagSize), plain);
        return XElement.Parse(System.Text.Encoding.UTF8.GetString(plain));
    }
}

/// <summary>
/// Reads secrets that older versions protected under the content-root discriminator. One read-only provider (no key
/// generation, no discriminator) over the same key ring; each legacy name is the first purpose, as ASP.NET applied it.
/// </summary>
public sealed class LegacySecretKeys(IReadOnlyList<string> applicationNames, Action<IServiceCollection> configureRepository) : IDisposable
{
    private readonly Lazy<ServiceProvider> _services = new(() =>
    {
        var s = new ServiceCollection();
        s.AddSingleton<IApplicationDiscriminator>(new NoDiscriminator());
        s.AddDataProtection().DisableAutomaticKeyGeneration();
        configureRepository(s);
        return s.BuildServiceProvider();
    });

    public IReadOnlyList<string> ApplicationNames => applicationNames;

    /// <summary>The protectors an older version would have used for <paramref name="purpose"/>.</summary>
    public IReadOnlyList<IDataProtector> For(string purpose)
    {
        var root = _services.Value.GetRequiredService<IDataProtectionProvider>();
        return applicationNames.Select(n => root.CreateProtector(n).CreateProtector(purpose)).ToList();
    }

    public void Dispose()
    {
        if (_services.IsValueCreated) _services.Value.Dispose();
    }

    private sealed class NoDiscriminator : IApplicationDiscriminator
    {
        public string? Discriminator => null;
    }
}

/// <summary>
/// Protects one kind of secret under the stable application name, and still reads ones protected by an older version
/// (returning them re-protected so the caller can store them the new way).
/// </summary>
public sealed class SecretProtector
{
    private readonly IDataProtector _current;
    private readonly Lazy<IReadOnlyList<IDataProtector>> _legacy;

    public SecretProtector(IDataProtectionProvider provider, string purpose, LegacySecretKeys? legacy)
    {
        _current = provider.CreateProtector(purpose);
        _legacy = new(() => legacy?.For(purpose) ?? []);
    }

    public string Protect(string plaintext) => _current.Protect(plaintext);

    /// <summary>The plaintext, or null when it is empty or can't be decrypted by any key (it must then be entered again).</summary>
    public string? Unprotect(string? protectedData) => Unprotect(protectedData, out _);

    /// <param name="migrated">Set when the secret was written under an older application name: the same secret, protected
    /// again under the stable name, to store in place of <paramref name="protectedData"/>.</param>
    public string? Unprotect(string? protectedData, out string? migrated)
    {
        migrated = null;
        if (string.IsNullOrEmpty(protectedData)) return null;
        try { return _current.Unprotect(protectedData); }
        catch (CryptographicException) { }
        IReadOnlyList<IDataProtector> legacy;
        try { legacy = _legacy.Value; }
        catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or IOException or UnauthorizedAccessException) { return null; }
        foreach (var p in legacy)
        {
            try
            {
                var plain = p.Unprotect(protectedData);
                migrated = _current.Protect(plain);
                return plain;
            }
            // A legacy key ring with no usable key throws InvalidOperationException; a wrong name, CryptographicException.
            catch (Exception ex) when (ex is CryptographicException or InvalidOperationException or IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}

/// <summary>On start, re-protects every stored integration secret (and the desktop's AI keys) written by an older version.</summary>
public sealed class LegacySecretMigration(IServiceProvider services, ILogger<LegacySecretMigration> log) : BackgroundService
{
    private readonly TaskCompletionSource<int> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes with the number of secrets migrated (tests).</summary>
    public Task<int> Completion => _done.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            var n = await services.GetRequiredService<Integrations.IntegrationService>().MigrateLegacySecretsAsync(stoppingToken);
            n += services.GetService<Services.AssistantSettings>()?.MigratedSecrets ?? 0;
            if (n > 0) log.LogInformation("Re-protected {Count} stored secret(s) written by an earlier version under the stable key name", n);
            _done.TrySetResult(n);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { _done.TrySetCanceled(stoppingToken); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Re-protecting stored secrets from an earlier version failed; they are still read and migrated on use");
            _done.TrySetResult(0);
        }
    }
}
