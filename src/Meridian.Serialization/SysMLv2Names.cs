using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>
/// Name resolution shared by the SysML v2 exporter and the text-to-model sync, so a reference the exporter writes as a
/// simple name resolves back to the same element: nearest enclosing namespace first, then a model-wide unique match;
/// qualified names (A::B) match the owner chain; feature chains (a.b) walk owned and typed features.
/// </summary>
internal sealed class SysmlNameIndex
{
    private readonly ModelRepository _m;
    private readonly Dictionary<string, List<Element>> _byName = new(StringComparer.Ordinal);

    public SysmlNameIndex(ModelRepository m)
    {
        _m = m;
        Add(m.Root);
    }

    private void Add(Element e)
    {
        if (e is not Relationship and not Diagram)
        {
            var n = ExportName(e);
            if (n.Length > 0)
            {
                if (!_byName.TryGetValue(n, out var l)) _byName[n] = l = new List<Element>();
                l.Add(e);
            }
        }
        foreach (var c in e.OwnedElements) Add(c);
    }

    public static bool Addressable(Element e) => e is not Relationship and not Diagram;

    /// <summary>The name the exporter writes: the element name, or a positional name for anonymous decision / fork nodes.</summary>
    public static string ExportName(Element e)
    {
        if (!string.IsNullOrEmpty(e.Name)) return e.Name;
        if (e.Kind is ElementKind.DecisionNode or ElementKind.ForkNode && e.Owner is { } o)
            return (e.Kind == ElementKind.DecisionNode ? "decision" : "fork") + (o.OwnedElements.Where(c => c.Kind == e.Kind).ToList().IndexOf(e) + 1);
        return "";
    }

    public Element? Lookup(SysmlRef r, Element context, Func<Element, bool>? filter = null) =>
        r.IsChain ? LookupChain(r.Segments, context, filter)?.LastOrDefault() : Lookup(r.Segments, context, filter);

    public Element? Lookup(IReadOnlyList<string> segs, Element context, Func<Element, bool>? filter = null)
    {
        filter ??= Addressable;
        if (segs.Count == 0) return null;
        var simple = segs[^1];
        if (segs.Count == 1)
            return ScopeWalk(simple, context, filter)
                   ?? (_byName.TryGetValue(simple, out var all) ? all.FirstOrDefault(c => _m.Contains(c) && filter(c)) : null);
        if (!_byName.TryGetValue(simple, out var cands)) return null;
        var hits = cands.Where(c => _m.Contains(c) && filter(c) && MatchesQualified(c, segs)).ToList();
        if (hits.Count <= 1) return hits.FirstOrDefault();
        // Prefer the candidate sharing the deepest ancestor with the context.
        var ctxAnc = context.AncestorsAndSelf().ToList();
        return hits.OrderBy(h => h.AncestorsAndSelf().Select(a => ctxAnc.IndexOf(a)).Where(i => i >= 0).DefaultIfEmpty(int.MaxValue).Min()).First();
    }

    private static Element? ScopeWalk(string simple, Element context, Func<Element, bool> filter)
    {
        for (var s = context; s != null; s = s.Owner)
            foreach (var c in s.OwnedElements)
                if (Addressable(c) && ExportName(c) == simple && filter(c)) return c;
        return null;
    }

    /// <summary>True when only one element is visible under this simple name from anywhere (used to decide whether to qualify).</summary>
    public bool IsUnique(string simple, Func<Element, bool> filter) =>
        !_byName.TryGetValue(simple, out var l) || l.Count(c => _m.Contains(c) && filter(c)) <= 1;

    private static bool MatchesQualified(Element e, IReadOnlyList<string> segs)
    {
        var cur = e;
        for (var k = segs.Count - 1; k >= 0; k--)
        {
            if (cur == null) return false;
            if (ExportName(cur) != segs[k]) return false;
            cur = cur.Owner;
        }
        return true;
    }

    /// <summary>Resolves a feature chain a.b.c, returning every element along the path (null if any step fails).</summary>
    public List<Element>? LookupChain(IReadOnlyList<string> segs, Element context, Func<Element, bool>? lastFilter = null)
    {
        var first = Lookup([segs[0]], context, segs.Count == 1 ? lastFilter : null);
        if (first == null) return null;
        var path = new List<Element> { first };
        for (var k = 1; k < segs.Count; k++)
        {
            var next = Member(path[^1], segs[k], new HashSet<Element>(ReferenceEqualityComparer.Instance));
            if (next == null) return null;
            path.Add(next);
        }
        return path;
    }

    private Element? Member(Element owner, string name, HashSet<Element> seen)
    {
        if (!seen.Add(owner)) return null;
        foreach (var c in owner.OwnedElements)
            if (Addressable(c) && ExportName(c) == name) return c;
        if (_m.ResolveType(owner) is { } t && Member(t, name, seen) is { } viaType) return viaType;
        foreach (var g in _m.General(owner))
            if (Member(g, name, seen) is { } inherited) return inherited;
        return null;
    }

    /// <summary>The text that refers to <paramref name="target"/> from <paramref name="context"/>: its simple name when that
    /// resolves back to it, otherwise its qualified name.</summary>
    public string RefText(Element target, Element context, Func<Element, bool>? filter = null)
    {
        filter ??= Addressable;
        var simple = ExportName(target);
        var scoped = ScopeWalk(simple, context, filter);
        if (ReferenceEquals(scoped, target) || scoped == null && IsUnique(simple, filter)) return SysMLv2Text.N(simple);
        var path = target.AncestorsAndSelf().TakeWhile(a => a.Owner != null).Reverse().Select(ExportName).ToList();
        if (path.Count == 0) return SysMLv2Text.N(simple);
        return string.Join("::", path.Select(SysMLv2Text.N));
    }
}
