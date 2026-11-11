using System.Text.RegularExpressions;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Permissions;

namespace Meridian.Core.Libraries;

/// <summary>
/// What a library update stored in a project element's suspect mark (<see cref="ReqFields.LibraryChange"/>, field
/// <c>suspectLib</c>): only stable ids and the kind of change — the library element's id, changed or removed, the source
/// project's id and the pins before and after ("version:3", "baseline:17", "branch:dev"). No names: they are rendered when the
/// mark is shown, through the viewer's own model (<see cref="Describe"/>), so an element in a package hidden from the viewer
/// shows as <see cref="Restricted"/> and a library they can't read as "a used project". Snapshots, redacted copies and exports
/// therefore never carry a hidden name.
/// <para>
/// Marks written before this format held a sentence with names. <see cref="UpgradeLegacy"/> turns them into this form (target
/// from the element's own reference, the change kind from the sentence, pins from the library) when a session loads a model
/// and whenever a model is redacted or shown from history.
/// </para>
/// </summary>
public sealed record LibraryChangeMark(string TargetId, bool Removed, Guid SourceProjectId, string FromPin, string ToPin)
{
    private const char Sep = '\u001f';

    /// <summary>Every mark in this format starts with it; anything else in the field is an older, named mark.</summary>
    public const string Prefix = "lib1\u001f";

    /// <summary>How a library element the viewer may not see is named.</summary>
    public const string Restricted = "a restricted library element";

    /// <summary>How a library the viewer can't read is named.</summary>
    public const string UnknownLibrary = "a used project";

    public static string Pin(LibraryPinKind kind, string value) => $"{LibraryPins.Code(kind)}:{value}";

    public string Encode() => string.Join(Sep, "lib1", Clean(TargetId), Removed ? "removed" : "changed",
        SourceProjectId == Guid.Empty ? "" : SourceProjectId.ToString("N"), Clean(FromPin), Clean(ToPin));

    private static string Clean(string s) => s.Replace(Sep, ' ');

    public static LibraryChangeMark? Parse(string? value)
    {
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var p = value.Split(Sep);
        if (p.Length < 6) return null;
        return new LibraryChangeMark(p[1], p[2] == "removed", Guid.TryParse(p[3], out var g) ? g : Guid.Empty, p[4], p[5]);
    }

    /// <summary>A mark in the field that isn't in this format (written by an earlier version: it may name hidden elements).</summary>
    public static bool IsLegacy(string? value) => value is { Length: > 0 } && !value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// The sentence shown for the mark, rendered with what <paramref name="viewer"/> — the viewer's own, filtered model — has:
    /// "Wheel changed in Parts (v3 → v4)", "a library element was removed from Parts (…)", "a restricted library element
    /// changed in a used project (…)". Without a model nothing is named.
    /// </summary>
    public string Describe(ModelRepository? viewer)
    {
        var mount = viewer?.Libraries.FirstOrDefault(m => m.Info.SourceProjectId == SourceProjectId && SourceProjectId != Guid.Empty);
        var library = mount?.Info.SourceProjectName ?? UnknownLibrary;
        var pins = $"({PinLabel(FromPin, mount)} → {PinLabel(ToPin, mount)})";
        if (Removed) return $"a library element was removed from {library} {pins}";
        var target = viewer?.Find(TargetId);
        var visible = target is not null && !MissingReference.Is(target) && !ModelRedactor.IsPlaceholder(target)
                      && (viewer!.IsLibraryElement(target) || mount is null);
        return $"{(visible ? target!.DisplayName : Restricted)} changed in {library} {pins}";
    }

    /// <summary>"v3", "dev (head)", "baseline PDR" (the name only when the viewer's library is pinned to it), "an earlier pin".</summary>
    private static string PinLabel(string pin, LibraryMount? mount)
    {
        var at = pin.IndexOf(':');
        if (at <= 0) return "an earlier pin";
        var kind = LibraryPins.Kind(pin[..at]);
        var value = pin[(at + 1)..];
        if (kind == LibraryPinKind.Baseline)
            return mount is { Info.PinKind: LibraryPinKind.Baseline } m && m.Info.PinValue == value ? m.Info.PinLabel : "a baseline";
        return LibraryPins.Label(kind, value);
    }

    private static readonly Regex LegacyPins = new(@"\((v(?<from>\d+)|[^()]*?) → (v(?<to>\d+)|[^()]*?)\)\s*$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Rewrites marks of the earlier, named format into this one, without undo, events or replication (a load-time
    /// normalisation, like mounting libraries): the target is the element's own reference to a library element (a relationship
    /// end, or the type of a feature), the source project and the current pin come from that library, the earlier pin from the
    /// sentence when it was a version. Returns how many marks were rewritten. Models without such marks are untouched.
    /// </summary>
    public static int UpgradeLegacy(ModelRepository model)
    {
        List<Element>? legacy = null;
        foreach (var e in model.AllElements)
            if (e.Fields.Count > 0 && IsLegacy(e.Get(ReqFields.LibraryChange))) (legacy ??= []).Add(e);
        if (legacy is null) return 0;
        foreach (var e in legacy)
        {
            var text = e.Get(ReqFields.LibraryChange);
            var target = TargetOf(model, e);
            var mount = model.LibraryOf(target is null ? null : model.Find(target));
            var pins = LegacyPins.Match(text);
            var from = pins.Success && pins.Groups["from"].Success ? Pin(LibraryPinKind.Version, pins.Groups["from"].Value) : "";
            var to = pins.Success && pins.Groups["to"].Success ? Pin(LibraryPinKind.Version, pins.Groups["to"].Value)
                   : mount is null ? "" : Pin(mount.Info.PinKind, mount.Info.PinValue);
            var mark = new LibraryChangeMark(target ?? "", text.Contains(" was removed from ", StringComparison.Ordinal), mount?.Info.SourceProjectId ?? Guid.Empty, from, to);
            e.SetRaw(ReqFields.LibraryChange, mark.Encode());
        }
        return legacy.Count;
    }

    /// <summary>The id of the library element <paramref name="e"/> refers to (missing references included), if any.</summary>
    private static string? TargetOf(ModelRepository model, Element e)
    {
        if (e is Relationship r)
        {
            foreach (var end in new[] { r.Target, r.Source })
                if (model.IsLibraryElement(end) || MissingReference.Is(end)) return end.Id;
            return null;
        }
        return Metamodel.IsTypedFeature(e.Kind) && e.TypeRef.Length > 0 ? e.TypeRef : null;
    }
}
