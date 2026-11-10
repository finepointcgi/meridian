using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Meridian.Web.CodeSync.Hosts;

/// <summary>A push to a branch, as parsed from a GitHub or GitLab webhook.</summary>
public sealed record PushEvent(string Ref, string? Branch, string After, bool Deleted, string? Message, string? Author, string? CommitUrl);

/// <summary>Kinds of incoming webhook requests.</summary>
public enum WebhookEventKind { Push, Ping, PullRequest, Other }

/// <summary>What happened to a pull / merge request. <see cref="Other"/> covers edits, reviews, labels…</summary>
public enum PullRequestAction { Opened, Updated, Merged, Closed, Other }

/// <summary>A pull request (GitHub, Bitbucket) or merge request (GitLab) event, as parsed from a webhook.</summary>
public sealed record PullRequestEvent(
    int Number, PullRequestAction Action, string? HeadSha, string? SourceBranch, string? TargetBranch,
    string? Title, string? Url, string? Author);

/// <summary>Signature checks and payload parsing for incoming git host webhooks.</summary>
public static class CodeWebhooks
{
    public const string GitHubSignatureHeader = "X-Hub-Signature-256";
    public const string GitHubEventHeader = "X-GitHub-Event";
    public const string GitLabTokenHeader = "X-Gitlab-Token";
    public const string GitLabEventHeader = "X-Gitlab-Event";
    /// <summary>Bitbucket Cloud and Data Center name the event here ("repo:push", "pr:opened" …).</summary>
    public const string BitbucketEventHeader = "X-Event-Key";
    /// <summary>Bitbucket Data Center (and Cloud, when a secret is set there): <c>sha256=&lt;hex HMAC-SHA256&gt;</c>.</summary>
    public const string BitbucketSignatureHeader = "X-Hub-Signature";
    /// <summary>Query parameter that carries the secret for Bitbucket Cloud webhooks (which are not signed by default).</summary>
    public const string UrlTokenParameter = "token";

    /// <summary>A new random webhook secret (192 bits, hex).</summary>
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>GitHub: <c>X-Hub-Signature-256: sha256=&lt;hex HMAC-SHA256(secret, raw body)&gt;</c>, compared in constant time.</summary>
    public static bool VerifyGitHub(string secret, ReadOnlySpan<byte> body, string? header) => VerifyHmacSha256(secret, body, header);

    /// <summary>Bitbucket Data Center: <c>X-Hub-Signature: sha256=&lt;hex HMAC-SHA256(secret, raw body)&gt;</c>, constant time.</summary>
    public static bool VerifyBitbucketSignature(string secret, ReadOnlySpan<byte> body, string? header) => VerifyHmacSha256(secret, body, header);

    /// <summary>Bitbucket Cloud: the secret travels in the webhook URL (<c>?token=…</c>); compared in constant time.</summary>
    public static bool VerifyUrlToken(string secret, string? token) => VerifyGitLab(secret, token);

    private static bool VerifyHmacSha256(string secret, ReadOnlySpan<byte> body, string? header)
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
        "pull_request" => WebhookEventKind.PullRequest,
        _ => WebhookEventKind.Other,
    };

    public static WebhookEventKind GitLabEvent(string? header) => header?.Trim() switch
    {
        "Push Hook" => WebhookEventKind.Push,
        "Merge Request Hook" => WebhookEventKind.PullRequest,
        _ => WebhookEventKind.Other,
    };

    /// <summary>Bitbucket Cloud ("repo:push", "pullrequest:*") and Data Center ("repo:refs_changed", "pr:*", "diagnostics:ping").</summary>
    public static WebhookEventKind BitbucketEvent(string? header) => header?.Trim() switch
    {
        "repo:push" or "repo:refs_changed" => WebhookEventKind.Push,
        "diagnostics:ping" => WebhookEventKind.Ping,
        "pullrequest:created" or "pullrequest:updated" or "pullrequest:fulfilled" or "pullrequest:rejected"
            or "pr:opened" or "pr:from_ref_updated" or "pr:merged" or "pr:declined" or "pr:deleted" => WebhookEventKind.PullRequest,
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

    /// <summary>Parses a Bitbucket push: Cloud <c>repo:push</c> (<c>push.changes[].new</c>) or Data Center
    /// <c>repo:refs_changed</c> (<c>changes[].ref</c>). One event per changed ref; empty if it is not a push.</summary>
    public static IReadOnlyList<PushEvent> ParseBitbucketPush(string json)
    {
        if (Parse(json) is not JsonObject o) return [];
        var list = new List<PushEvent>();
        if (o["push"]?["changes"] is JsonArray cloud)
        {
            foreach (var c in cloud.OfType<JsonObject>())
            {
                var neu = c["new"] as JsonObject;
                var old = c["old"] as JsonObject;
                var type = Str(neu?["type"]) ?? Str(old?["type"]);
                if (type != "branch") continue;
                var name = Str(neu?["name"]) ?? Str(old?["name"]);
                if (name is null) continue;
                var target = neu?["target"];
                var deleted = neu is null || c["closed"] is JsonValue cl && cl.TryGetValue<bool>(out var closed) && closed;
                list.Add(new PushEvent("refs/heads/" + name, name, Str(target?["hash"]) ?? "", deleted,
                    Str(target?["message"]), Str(target?["author"]?["user"]?["display_name"]) ?? Str(target?["author"]?["raw"]) ?? Str(o["actor"]?["display_name"]),
                    Str(target?["links"]?["html"]?["href"])));
            }
        }
        else if (o["changes"] is JsonArray server)
        {
            foreach (var c in server.OfType<JsonObject>())
            {
                if (Str(c["ref"]?["id"]) is not { } gitRef) continue;
                var to = Str(c["toHash"]) ?? "";
                var deleted = Str(c["type"]) == "DELETE" || IsZero(to);
                list.Add(new PushEvent(gitRef, BranchOf(gitRef), to, deleted, null, Str(o["actor"]?["displayName"]) ?? Str(o["actor"]?["name"]), null));
            }
        }
        return list;
    }

    /// <summary>Parses a GitHub <c>pull_request</c> payload; null if it is not one.</summary>
    public static PullRequestEvent? ParseGitHubPullRequest(string json)
    {
        if (Parse(json) is not JsonObject o || o["pull_request"] is not JsonObject pr || (Int(o["number"]) ?? Int(pr["number"])) is not { } number) return null;
        var merged = pr["merged"] is JsonValue m && m.TryGetValue<bool>(out var mm) && mm;
        var action = Str(o["action"]) switch
        {
            "opened" or "reopened" or "ready_for_review" => PullRequestAction.Opened,
            "synchronize" => PullRequestAction.Updated,
            "closed" => merged ? PullRequestAction.Merged : PullRequestAction.Closed,
            _ => PullRequestAction.Other,
        };
        return new PullRequestEvent(number, action, Str(pr["head"]?["sha"]), Str(pr["head"]?["ref"]), Str(pr["base"]?["ref"]),
            Str(pr["title"]), Str(pr["html_url"]), Str(pr["user"]?["login"]));
    }

    /// <summary>Parses a GitLab <c>Merge Request Hook</c> payload; null if it is not one. An "update" without new
    /// commits (title, labels, approvals) reads as <see cref="PullRequestAction.Other"/>.</summary>
    public static PullRequestEvent? ParseGitLabMergeRequest(string json)
    {
        if (Parse(json) is not JsonObject o || o["object_attributes"] is not JsonObject a || Int(a["iid"]) is not { } iid) return null;
        var action = Str(a["action"]) switch
        {
            "open" or "reopen" => PullRequestAction.Opened,
            "update" when Str(a["oldrev"]) is not null => PullRequestAction.Updated,
            "merge" => PullRequestAction.Merged,
            "close" => PullRequestAction.Closed,
            _ => PullRequestAction.Other,
        };
        return new PullRequestEvent(iid, action, Str(a["last_commit"]?["id"]), Str(a["source_branch"]), Str(a["target_branch"]),
            Str(a["title"]), Str(a["url"]), Str(o["user"]?["name"]) ?? Str(a["last_commit"]?["author"]?["name"]));
    }

    /// <summary>Parses a Bitbucket Cloud (<c>pullrequest:*</c>) or Data Center (<c>pr:*</c>) pull request payload.</summary>
    public static PullRequestEvent? ParseBitbucketPullRequest(string json, string? eventKey)
    {
        if (Parse(json) is not JsonObject o) return null;
        var action = eventKey?.Trim() switch
        {
            "pullrequest:created" or "pr:opened" => PullRequestAction.Opened,
            "pullrequest:updated" or "pr:from_ref_updated" => PullRequestAction.Updated,
            "pullrequest:fulfilled" or "pr:merged" => PullRequestAction.Merged,
            "pullrequest:rejected" or "pr:declined" or "pr:deleted" => PullRequestAction.Closed,
            _ => PullRequestAction.Other,
        };
        if (o["pullrequest"] is JsonObject cloud && Int(cloud["id"]) is { } cid)
            return new PullRequestEvent(cid, action, Str(cloud["source"]?["commit"]?["hash"]), Str(cloud["source"]?["branch"]?["name"]),
                Str(cloud["destination"]?["branch"]?["name"]), Str(cloud["title"]), Str(cloud["links"]?["html"]?["href"]),
                Str(cloud["author"]?["display_name"]));
        if (o["pullRequest"] is JsonObject server && Int(server["id"]) is { } sid)
            return new PullRequestEvent(sid, action, Str(server["fromRef"]?["latestCommit"]), Str(server["fromRef"]?["displayId"]),
                Str(server["toRef"]?["displayId"]) ?? BranchOf(Str(server["toRef"]?["id"]) ?? ""), Str(server["title"]),
                Str((server["links"]?["self"] as JsonArray)?.FirstOrDefault()?["href"]), Str(server["author"]?["user"]?["displayName"]));
        return null;
    }

    private static JsonNode? Parse(string json)
    {
        try { return JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { return null; }
    }

    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) && i > 0 ? i : null;

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
