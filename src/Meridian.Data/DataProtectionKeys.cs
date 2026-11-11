namespace Meridian.Data;

/// <summary>
/// The ASP.NET Data Protection key ring kept in the Meridian database (table <c>data_protection_keys</c>), so the keys that
/// encrypt stored tokens and passwords travel with the data: moving or upgrading the app, or running several server instances
/// against one database, keeps every stored secret readable. The web app wraps this in an <c>IXmlRepository</c>.
/// Synchronous on purpose: Data Protection reads and writes its key ring synchronously.
/// </summary>
public interface IDataProtectionKeyStore
{
    /// <summary>Every stored key-ring element (keys and revocations), oldest first.</summary>
    IReadOnlyList<DataProtectionKeyRecord> ListDataProtectionKeys();

    /// <summary>Stores an element, replacing one with the same name.</summary>
    void StoreDataProtectionKey(string name, string xml);

    /// <summary>Stores an element only when none with that name exists (copying file-based keys). True when it was added.</summary>
    bool AddDataProtectionKeyIfMissing(string name, string xml);
}

/// <summary>One key-ring element: its name (e.g. <c>key-{id}</c>) and its XML.</summary>
public sealed record DataProtectionKeyRecord(string Name, string Xml);
