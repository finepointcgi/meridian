using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>A push to a branch, as parsed from a GitHub or GitLab webhook.</summary>
public sealed record PushEvent(string Ref, string? Branch, string After, bool Deleted, string? Message, string? Author, string? CommitUrl);

/// <summary>Kinds of incoming webhook requests.</summary>
public enum WebhookEventKind { Push, Ping, Other }

/// <summary>Signature checks and payload parsing for incoming git host webhooks.</summary>
public static class CodeWebhooks
{
    public const string GitHubSignatureHeader = "X-Hub-Signature-256";
    public const string GitHubEventHeader = "X-GitHub-Event";
    public const string GitLabTokenHeader = "X-Gitlab-Token";
    public const string GitLabEventHeader = "X-Gitlab-Event";

    /// <summary>A new random webhook secret (192 bits, hex).</summary>
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>GitHub: <c>X-Hub-Signature-256: sha256=&lt;hex HMAC-SHA256(secret, raw body)&gt;</c>, compared in constant time.</summary>
    public static bool VerifyGitHub(string secret, ReadOnlySpan<byte> body, string? header)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(header)) return false;
        var h = header.Trim();
        if (!h.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) return false;
        byte[] given;
        try { given = Convert.FromHexString(h[7..]); }
        catch (FormatException) { return false; }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body);
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    public static string SignGitHub(string secret, ReadOnlySpan<byte> body) =>
        "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

    /// <summary>GitLab sends the secret itself in <c>X-Gitlab-Token</c>; compared in constant time (via hashes, so lengths do not leak).</summary>
    public static bool VerifyGitLab(string secret, string? header)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(header)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), SHA256.HashData(Encoding.UTF8.GetBytes(header)));
    }

    public static WebhookEventKind GitHubEvent(string? header) => header?.Trim().ToLowerInvariant() switch
    {
        "push" => WebhookEventKind.Push,
        "ping" => WebhookEventKind.Ping,
        _ => WebhookEventKind.Other,
    };

    public static WebhookEventKind GitLabEvent(string? header) => header?.Trim() switch
    {
        "Push Hook" => WebhookEventKind.Push,
        _ => WebhookEventKind.Other,
    };

    private static bool IsZero(string sha) => sha.Length > 0 && sha.All(c => c == '0');

    private static string? BranchOf(string gitRef) =>
        gitRef.StartsWith("refs/heads/", StringComparison.Ordinal) ? gitRef["refs/heads/".Length..] : null;

    /// <summary>Parses a GitHub push payload; null if it is not one.</summary>
    public static PushEvent? ParseGitHubPush(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { return null; }
        if (root is not JsonObject o || Str(o["ref"]) is not { } gitRef || Str(o["after"]) is not { } after) return null;
        var head = o["head_commit"] as JsonObject;
        var deleted = o["deleted"] is JsonValue d && d.TryGetValue<bool>(out var del) && del || IsZero(after);
        return new PushEvent(gitRef, BranchOf(gitRef), after, deleted,
            Str(head?["message"]), Str(head?["author"]?["name"]) ?? Str(o["pusher"]?["name"]),
            Str(head?["url"]) ?? Str(o["compare"]));
    }

    /// <summary>Parses a GitLab push payload; null if it is not one.</summary>
    public static PushEvent? ParseGitLabPush(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { return null; }
        if (root is not JsonObject o || Str(o["ref"]) is not { } gitRef) return null;
        var after = Str(o["checkout_sha"]) ?? Str(o["after"]);
        if (after is null) return null;
        var deleted = IsZero(Str(o["after"]) ?? after);
        JsonObject? head = null;
        if (o["commits"] is JsonArray commits)
            head = commits.OfType<JsonObject>().FirstOrDefault(c => Str(c["id"]) == after) ?? commits.OfType<JsonObject>().LastOrDefault();
        return new PushEvent(gitRef, BranchOf(gitRef), after, deleted,
            Str(head?["message"]), Str(head?["author"]?["name"]) ?? Str(o["user_name"]), Str(head?["url"]));
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
