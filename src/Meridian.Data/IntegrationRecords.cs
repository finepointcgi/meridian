using System.Security.Cryptography;
using System.Text;

namespace Meridian.Data;

/// <summary>Kinds of per-project integration rows.</summary>
public static class IntegrationKinds
{
    public const string IssueTracker = "issue-tracker";
    public const string Webhook = "webhook";
    /// <summary>A live database connection for schema import (the connection string is the encrypted secret).</summary>
    public const string Database = "database";
}

/// <summary>
/// A per-project integration (an issue tracker connection or an outgoing webhook). <see cref="ConfigJson"/> holds the
/// non-secret settings; <see cref="ProtectedSecret"/> holds the API token / webhook secret already encrypted by the
/// web host (ASP.NET Data Protection). Stores never see plaintext secrets.
/// </summary>
public sealed record IntegrationConnection(Guid Id, Guid ProjectId, string Kind, string Name, string ConfigJson, string? ProtectedSecret, DateTime CreatedAt);

/// <summary>One attempt to deliver a webhook event.</summary>
public sealed record WebhookDelivery(long Id, Guid ProjectId, Guid HookId, string DeliveryId, string Event, int Attempt, int? StatusCode, bool Success, string? Error, DateTime CreatedAt);

/// <summary>A personal access token (the token itself is only ever shown once; the store keeps a SHA-256 hash).</summary>
public sealed record ApiTokenInfo(Guid Id, Guid UserId, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt);

/// <summary>Generation and hashing of personal access tokens.</summary>
public static class ApiTokens
{
    public const string Marker = "mrd_";

    /// <summary>A new random token: "mrd_" + 43 url-safe base64 characters (256 bits).</summary>
    public static string Generate() =>
        Marker + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Tokens carry 256 bits of entropy, so an unsalted SHA-256 is sufficient (and allows indexed lookup).</summary>
    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()))).ToLowerInvariant();

    public static string PrefixOf(string token) => token.Length > 12 ? token[..12] : token;
}
