using System.Text.Json;
using System.Text.RegularExpressions;

namespace Meridian.Data;

/// <summary>Rules shared by every store for the per-user preferences document and profile edits.</summary>
public static partial class Preferences
{
    /// <summary>Largest preferences document accepted (favourites included); settings are small.</summary>
    public const int MaxJsonLength = 64 * 1024;

    /// <summary>Checks that <paramref name="json"/> is a JSON object of reasonable size; returns it unchanged.</summary>
    public static string CheckJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaxJsonLength) throw new InvalidOperationException("Preferences are too large.");
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException("Preferences must be a JSON object.");
        }
        catch (JsonException) { throw new InvalidOperationException("Preferences must be valid JSON."); }
        return json;
    }

    /// <summary>A display name: trimmed, 1–80 characters, no control characters.</summary>
    public static string NormalizeDisplayName(string displayName)
    {
        var d = (displayName ?? "").Trim();
        if (d.Length is < 1 or > 80 || d.Any(char.IsControl)) throw new InvalidOperationException("Display name must be 1–80 characters.");
        return d;
    }

    /// <summary>An avatar colour: #rrggbb.</summary>
    public static string NormalizeColor(string color)
    {
        var c = (color ?? "").Trim().ToLowerInvariant();
        if (!HexColor().IsMatch(c)) throw new InvalidOperationException("Colour must be a hex colour like #4f8cff.");
        return c;
    }

    [GeneratedRegex("^#[0-9a-f]{6}$")]
    private static partial Regex HexColor();
}
