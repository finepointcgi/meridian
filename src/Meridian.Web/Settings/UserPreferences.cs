using System.Text.Json;
using System.Text.Json.Serialization;
using Meridian.Core.Language;

namespace Meridian.Web.Settings;

public enum ThemeMode { Dark, Light, System }
public enum Density { Comfortable, Compact }
public enum FontScale { Small, Normal, Large }
public enum GridStyle { Dots, Lines }
public enum DefaultZoom { Fit, Actual }
/// <summary>Connector routing for new diagrams; <see cref="Automatic"/> keeps the per-type default (straight on whiteboards).</summary>
public enum ConnectorDefault { Automatic, Orthogonal, Straight, Curved }

/// <summary>
/// Per-user preferences, stored server-side as one JSON document (user_preferences) so they follow the user across
/// devices. Local (desktop) mode stores them for the local user in the same table. Unknown or invalid values fall back to
/// the defaults, so older and newer versions can share a document.
/// </summary>
public sealed class UserPreferences
{
    // ---------------------------------------------------------------- appearance
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public string Accent { get; set; } = AccentPresets.Default;
    public bool ShowGrid { get; set; } = true;
    public GridStyle GridStyle { get; set; } = GridStyle.Dots;
    public bool SnapToGrid { get; set; } = true;
    /// <summary>White diagrams even in dark mode (canvas, exports).</summary>
    public bool CanvasAlwaysLight { get; set; }
    public bool ReducedMotion { get; set; }

    // ---------------------------------------------------------------- diagrams
    public ConnectorDefault ConnectorStyle { get; set; } = ConnectorDefault.Automatic;
    public bool LineJumps { get; set; }
    public bool OpenDiagramsAsTabs { get; set; } = true;
    public bool ShowShapesPanel { get; set; } = true;
    public DefaultZoom DefaultZoom { get; set; } = DefaultZoom.Fit;

    // ---------------------------------------------------------------- editor
    public bool ConfirmDeleteFromModel { get; set; }
    /// <summary>Notify me when a project I edit uses a library whose source moved ahead of the pin (docs/LIBRARIES.md).</summary>
    public bool LibraryUpstreamNotices { get; set; } = true;
    public bool TourSeen { get; set; }
    /// <summary>The release whose "What's new" card the user has seen (or skipped by taking the tour), e.g. "0.5".</summary>
    public string? WhatsNewSeen { get; set; }

    // ---------------------------------------------------------------- AI assistant
    public bool AssistantEnabled { get; set; } = true;
    public string AssistantModel { get; set; } = AssistantModels.Default;

    // ---------------------------------------------------------------- state that follows the user
    public List<string> Favorites { get; set; } = new();
    public Guid? LastProjectId { get; set; }
    /// <summary>Browser-stored settings (tour flag, last project) were imported once.</summary>
    public bool MigratedLocalStorage { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a stored document; anything unreadable gives the defaults, and values are normalised.</summary>
    public static UserPreferences FromJson(string? json)
    {
        UserPreferences p;
        if (string.IsNullOrWhiteSpace(json)) p = new();
        else
        {
            try { p = JsonSerializer.Deserialize<UserPreferences>(json, Json) ?? new(); }
            catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException) { p = new(); }
        }
        return p.Normalize();
    }

    public UserPreferences Clone() => FromJson(ToJson());

    /// <summary>Repairs values a hand-edited or older document may carry.</summary>
    public UserPreferences Normalize()
    {
        Accent = AccentPresets.Normalize(Accent);
        if (!Enum.IsDefined(Theme)) Theme = ThemeMode.Dark;
        if (!Enum.IsDefined(GridStyle)) GridStyle = GridStyle.Dots;
        if (!Enum.IsDefined(ConnectorStyle)) ConnectorStyle = ConnectorDefault.Automatic;
        if (!Enum.IsDefined(DefaultZoom)) DefaultZoom = DefaultZoom.Fit;
        if (!AssistantModels.All.Any(m => m.Id == AssistantModel)) AssistantModel = AssistantModels.Default;
        if (WhatsNewSeen is { Length: > 16 }) WhatsNewSeen = null;
        Favorites = (Favorites ?? new()).Where(f => !string.IsNullOrEmpty(f) && f.Length <= 64).Distinct().Take(500).ToList();
        return this;
    }

    /// <summary>Puts one Settings section back to its defaults (keeps everything else).</summary>
    public void ResetSection(SettingsSection section)
    {
        var d = new UserPreferences();
        switch (section)
        {
            case SettingsSection.Appearance:
                Theme = d.Theme; Accent = d.Accent; ShowGrid = d.ShowGrid; GridStyle = d.GridStyle; SnapToGrid = d.SnapToGrid;
                CanvasAlwaysLight = d.CanvasAlwaysLight; ReducedMotion = d.ReducedMotion;
                break;
            case SettingsSection.Diagrams:
                ConnectorStyle = d.ConnectorStyle; LineJumps = d.LineJumps; OpenDiagramsAsTabs = d.OpenDiagramsAsTabs;
                ShowShapesPanel = d.ShowShapesPanel; DefaultZoom = d.DefaultZoom;
                break;
            case SettingsSection.Editor:
                ConfirmDeleteFromModel = d.ConfirmDeleteFromModel;
                LibraryUpstreamNotices = d.LibraryUpstreamNotices;
                break;
            case SettingsSection.Assistant:
                AssistantEnabled = d.AssistantEnabled; AssistantModel = d.AssistantModel;
                break;
        }
    }

    /// <summary>The routing value written on new diagrams, or null to keep the type's default.</summary>
    public string? ConnectorRoutingValue => ConnectorStyle switch
    {
        ConnectorDefault.Orthogonal => EdgeRouter.Format(EdgeRouting.Orthogonal),
        ConnectorDefault.Straight => EdgeRouter.Format(EdgeRouting.Straight),
        ConnectorDefault.Curved => EdgeRouter.Format(EdgeRouting.Curved),
        _ => null,
    };
}

/// <summary>
/// Per-device preferences: how big and dense the UI is depends on the screen, so these stay on this device (a cookie,
/// which also lets the server render them before the first paint).
/// </summary>
public sealed record DevicePreferences(Density Density = Density.Comfortable, FontScale FontScale = FontScale.Normal)
{
    public const string CookieName = "meridian.device";

    public string ToCookie() => $"{(Density == Density.Compact ? "compact" : "comfortable")}.{FontScale.ToString().ToLowerInvariant()}";

    public static DevicePreferences FromCookie(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 40) return new();
        var parts = value.Split('.');
        var density = parts[0] == "compact" ? Density.Compact : Density.Comfortable;
        var font = parts.Length > 1 && Enum.TryParse<FontScale>(parts[1], ignoreCase: true, out var f) && Enum.IsDefined(f) ? f : FontScale.Normal;
        return new(density, font);
    }

    public double Scale => FontScale switch { FontScale.Small => 0.92, FontScale.Large => 1.12, _ => 1.0 };
}

public enum SettingsSection { Appearance, Diagrams, Editor, Assistant, Account, Desktop, Server }

public static class AccentPresets
{
    public const string Default = "#4f8cff";

    /// <summary>Name and colour of the preset swatches.</summary>
    public static readonly (string Name, string Color)[] All =
    [
        ("Blue", "#4f8cff"), ("Teal", "#14b8a6"), ("Violet", "#8b5cf6"), ("Pink", "#ec4899"),
        ("Red", "#e5484d"), ("Orange", "#f97316"), ("Amber", "#d97706"), ("Green", "#22a06b"),
    ];

    public static string Normalize(string? color) =>
        color is { Length: 7 } c && c[0] == '#' && c.Skip(1).All(Uri.IsHexDigit) ? c.ToLowerInvariant() : Default;
}

public static class AssistantModels
{
    public const string Default = "claude-opus-5-5";

    public static readonly (string Id, string Name, string Note)[] All =
    [
        ("claude-opus-5-5", "Claude Opus 5.5", "Default: best balance of quality and cost"),
        ("claude-sonnet-5-5", "Claude Sonnet 5.5", "Faster and cheaper"),
        ("claude-haiku-5-5", "Claude Haiku 5.5", "Fastest, for quick questions"),
        ("claude-fable-5-2", "Claude Fable 5.2", "Most capable, slower and pricier"),
    ];
}
