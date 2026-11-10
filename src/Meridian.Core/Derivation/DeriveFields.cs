using System.Security.Cryptography;
using System.Text;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>
/// Field keys recording where a derived diagram or element came from. They live in the ordinary field bag, so they save,
/// replicate, undo and diff like every other field. An element without <see cref="Key"/> was added by a person and is never
/// changed or removed by "Update from source".
/// </summary>
public static class DeriveFields
{
    /// <summary>Id of the element the derivation read (a use case, an activity, an interaction, a requirements package…).</summary>
    public const string Source = "derive.source";

    /// <summary>Which derivation made it (<see cref="DerivationKinds.Id"/>, e.g. "activity-from-usecase").</summary>
    public const string Kind = "derive.kind";

    /// <summary>Hash of the source content when the element was last derived or updated; differs from the source's current hash → "source changed".</summary>
    public const string Fingerprint = "derive.fingerprint";

    /// <summary>Stable key of the element within its derivation ("step:3", "msg:2", "diagram"); update matches by it.</summary>
    public const string Key = "derive.key";

    /// <summary>The block a state machine was derived for (state machine derivations only).</summary>
    public const string Target = "derive.target";

    /// <summary>Hash of the element's own fields as the derivation last wrote them; differs → the user edited it.</summary>
    public const string ContentHash = "derive.hash";

    public const string Prefix = "derive.";

    public static readonly IReadOnlyList<string> All = [Source, Kind, Fingerprint, Key, Target, ContentHash];

    /// <summary>Fields written by the tool itself rather than the user (never count as an edit).</summary>
    private static readonly HashSet<string> Housekeeping = new(StringComparer.Ordinal)
    {
        ReqFields.SourceFingerprint, ReqFields.TargetFingerprint, Language.EdgeRouter.DiagramKey, Language.AutoDiagrammer.MarkerKey,
    };

    public static bool IsDerived(Element e) => e.Get(Key).Length > 0 && e.Get(Source).Length > 0;

    /// <summary>Short stable hash (16 hex characters) of a text.</summary>
    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0, 8).ToLowerInvariant();

    /// <summary>Hash of the element's user-visible fields (everything but the derivation's own and the tool's housekeeping fields).</summary>
    public static string ContentOf(Element e)
    {
        var sb = new StringBuilder();
        foreach (var (k, v) in e.Fields.Where(f => !f.Key.StartsWith(Prefix, StringComparison.Ordinal) && !Housekeeping.Contains(f.Key))
                                       .OrderBy(f => f.Key, StringComparer.Ordinal))
            sb.Append(k).Append('\u001f').Append(v).Append('\u001e');
        if (e is Relationship r) sb.Append(r.Source.Id).Append('>').Append(r.Target.Id);
        return Hash(sb.ToString());
    }

    /// <summary>True when the user changed a derived element since the derivation last wrote it.</summary>
    public static bool IsEdited(Element e) => e.Get(ContentHash) is { Length: > 0 } h && h != ContentOf(e);
}
