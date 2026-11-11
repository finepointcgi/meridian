using System.Net;
using System.Text.Json;

namespace Meridian.Web.Integrations.Requirements;

/// <summary>Integration kind and tool types of requirements-tool connections.</summary>
public static class RequirementsToolKinds
{
    /// <summary>The <c>project_integrations.kind</c> of a DOORS Next or Polarion connection.</summary>
    public const string RequirementsTool = "requirements-tool";

    public const string DoorsNext = "doors-next";
    public const string Polarion = "polarion";

    public static readonly string[] All = [DoorsNext, Polarion];

    public static string Display(string type) => type switch
    {
        DoorsNext => "IBM DOORS Next",
        Polarion => "Siemens Polarion",
        _ => type,
    };
}

/// <summary>How Meridian signs in to DOORS Next (Jazz).</summary>
public static class JazzAuth
{
    /// <summary>Jazz form login (<c>j_security_check</c>) with a username and password; the session cookie is kept per sync.</summary>
    public const string Form = "form";
    /// <summary>HTTP Basic with username and password (servers configured for Basic, e.g. behind LDAP).</summary>
    public const string Basic = "basic";
    /// <summary>A Jazz Authorization Server app password, sent as HTTP Basic.</summary>
    public const string AppPassword = "app-password";

    public static readonly string[] All = [Form, Basic, AppPassword];

    public static string Display(string a) => a switch
    {
        Form => "Jazz form login (username + password)",
        Basic => "HTTP Basic (username + password)",
        AppPassword => "App password (Jazz Authorization Server)",
        _ => a,
    };
}

/// <summary>Link roles used when Meridian publishes its links back to Polarion.</summary>
public sealed class PolarionLinkRoles
{
    /// <summary>Linked work item roles (both ends imported from the same Polarion project).</summary>
    public string Satisfy { get; set; } = "implements";
    public string Verify { get; set; } = "verifies";
    public string Trace { get; set; } = "relates_to";
    /// <summary>Roles of linked OSLC resources (the Meridian end is a block, test case… served by Meridian's OSLC provider).</summary>
    public string OslcSatisfy { get; set; } = "http://open-services.net/ns/rm#satisfiedBy";
    public string OslcVerify { get; set; } = "http://open-services.net/ns/rm#validatedBy";
    public string OslcTrace { get; set; } = "http://open-services.net/ns/rm#elaboratedBy";
}

/// <summary>What happened in the most recent sync of a connection (kept in the connection's config).</summary>
public sealed class RequirementsLastSync
{
    public DateTime At { get; set; }
    /// <summary>"ok", "conflicts" or "failed".</summary>
    public string Status { get; set; } = "";
    public string Summary { get; set; } = "";
    public int Conflicts { get; set; }
}

/// <summary>Non-secret settings of a DOORS Next or Polarion connection (the integration's config JSON).</summary>
public sealed class RequirementsToolConfig
{
    public string Type { get; set; } = RequirementsToolKinds.DoorsNext;
    /// <summary>DOORS Next: the RM application root, e.g. https://jazz.example.com/rm. Polarion: the server, e.g. https://polarion.example.com.</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>DOORS Next: <see cref="JazzAuth"/>. Polarion always uses a personal access token (Bearer).</summary>
    public string Auth { get; set; } = JazzAuth.Form;
    /// <summary>DOORS Next username (all auth modes).</summary>
    public string? User { get; set; }
    /// <summary>DOORS Next form login: where <c>j_security_check</c> lives when it is not the RM root (e.g. https://jazz.example.com/jts).</summary>
    public string? AuthUrl { get; set; }
    /// <summary>DOORS Next: a global or local configuration URL sent as <c>Configuration-Context</c> (configuration-managed projects).</summary>
    public string? ConfigurationContext { get; set; }

    /// <summary>DOORS Next: service provider URL of the project area. Polarion: project id.</summary>
    public string Project { get; set; } = "";
    public string? ProjectTitle { get; set; }
    /// <summary>DOORS Next: module URL. Polarion: document id (project/space/name). Empty: the whole project.</summary>
    public string? Scope { get; set; }
    public string? ScopeTitle { get; set; }
    /// <summary>Extra filter: an OSLC <c>oslc.where</c> clause (DOORS Next) or a Lucene query (Polarion).</summary>
    public string? Query { get; set; }
    /// <summary>Polarion work item type to import (default <c>requirement</c>).</summary>
    public string? WorkItemType { get; set; }

    /// <summary>Package (element id) imported requirements go to; empty: a package named after the connection.</summary>
    public string? TargetPackage { get; set; }
    /// <summary>Publish Meridian «satisfy», «verify» and «trace» links back to the tool. Off by default.</summary>
    public bool PublishLinks { get; set; }
    public PolarionLinkRoles? LinkRoles { get; set; }
    /// <summary>Automatic sync interval in minutes; 0 = only "Sync now".</summary>
    public int ScheduleMinutes { get; set; }
    /// <summary>The account scheduled syncs edit the model as (the owner who saved the connection).</summary>
    public Guid? RunAs { get; set; }

    public RequirementsLastSync? LastSync { get; set; }

    public static readonly int[] Schedules = [0, 15, 60, 240, 1440];

    public static RequirementsToolConfig Parse(string json)
    {
        try { return JsonSerializer.Deserialize<RequirementsToolConfig>(json, IntegrationJson.Options) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public string ToJson() => JsonSerializer.Serialize(this, IntegrationJson.Options);

    public RequirementsToolConfig Clone() => Parse(ToJson());

    public bool IsDoors => Type == RequirementsToolKinds.DoorsNext;

    /// <summary>Validates the settings; returns an error message or null.</summary>
    public string? Validate(bool needProject = true)
    {
        if (!RequirementsToolKinds.All.Contains(Type)) return "Choose DOORS Next or Polarion.";
        if (!Uri.TryCreate(BaseUrl?.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) || u.Host.Length == 0)
            return "The server URL must be an absolute http:// or https:// URL.";
        if (IsDoors)
        {
            if (!JazzAuth.All.Contains(Auth)) return "Choose how to sign in.";
            if (string.IsNullOrWhiteSpace(User)) return "Enter the Jazz username.";
            if (AuthUrl is { Length: > 0 } a && !Uri.TryCreate(a, UriKind.Absolute, out _)) return "The login URL must be absolute.";
        }
        if (needProject && string.IsNullOrWhiteSpace(Project)) return IsDoors ? "Choose a DOORS Next project." : "Enter or choose the Polarion project id.";
        if (!Schedules.Contains(ScheduleMinutes)) return "Choose a schedule.";
        return null;
    }

    public static string ScheduleLabel(int minutes) => minutes switch
    {
        0 => "Manual (Sync now only)",
        < 60 => $"Every {minutes} minutes",
        60 => "Every hour",
        1440 => "Daily",
        _ => $"Every {minutes / 60} hours",
    };
}

/// <summary>A project, module or document in the remote tool.</summary>
public sealed record RemoteContainer(string Id, string Title);

/// <summary>A requirement as read from the remote tool.</summary>
/// <param name="ExternalId">Stable id: the OSLC resource URL (DOORS Next) or <c>project/WI-ID</c> (Polarion).</param>
/// <param name="Url">Where a person opens it.</param>
/// <param name="Identifier">The tool's human id (DOORS Next "1234", Polarion "DP-12").</param>
/// <param name="Revision">dcterms:modified (DOORS Next) or <c>updated</c> (Polarion).</param>
public sealed record RemoteRequirement(string ExternalId, string Url, string Identifier, string Title, string Text, string Revision);

/// <summary>A Meridian link to publish on a remote requirement.</summary>
/// <param name="Kind">"satisfy", "verify" or "trace".</param>
/// <param name="TargetUri">The other end: Meridian's OSLC URL of the element, or the remote URL when it is imported too.</param>
/// <param name="TargetExternalId">Set when the other end was imported from the same connection (Polarion linked work items).</param>
public sealed record OutboundLink(string Kind, string TargetUri, string Label, string? TargetExternalId);

public enum PublishOutcome { Created, AlreadyPresent }

/// <summary>A failure talking to DOORS Next or Polarion. The message never contains credentials.</summary>
public sealed class RequirementsToolException(string message, HttpStatusCode? status = null) : Exception(message)
{
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>What Meridian needs from a requirements tool.</summary>
public interface IRequirementsSource
{
    /// <summary>Checks the server and credentials. Returns a short description.</summary>
    Task<string> TestAsync(CancellationToken ct = default);
    Task<IReadOnlyList<RemoteContainer>> ProjectsAsync(CancellationToken ct = default);
    /// <summary>DOORS Next modules or Polarion documents of a project.</summary>
    Task<IReadOnlyList<RemoteContainer>> ScopesAsync(string project, CancellationToken ct = default);
    /// <summary>Every requirement in the configured project / scope, all pages.</summary>
    Task<IReadOnlyList<RemoteRequirement>> RequirementsAsync(CancellationToken ct = default);
    /// <summary>Adds a link on the remote requirement <paramref name="externalId"/> (idempotent where the API allows checking).</summary>
    Task<PublishOutcome> PublishLinkAsync(string externalId, OutboundLink link, CancellationToken ct = default);
}
