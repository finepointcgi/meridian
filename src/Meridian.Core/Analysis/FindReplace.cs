using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.Language;
using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Core.Permissions;

namespace Meridian.Core.Analysis;

/// <summary>Which kinds of text a find (and replace) looks in.</summary>
[Flags]
public enum SearchIn
{
    None = 0,
    /// <summary>Element, relationship and diagram names (not free shapes: those are <see cref="DiagramText"/>).</summary>
    Names = 1,
    Documentation = 2,
    /// <summary>Requirement text, id and rationale.</summary>
    Requirements = 4,
    /// <summary>Default values, constraint expressions and bodies, guards, effects, state behaviours, durations, distributions.</summary>
    Values = 8,
    /// <summary>Tagged values (stereotype tags) and custom requirement attributes.</summary>
    TaggedValues = 16,
    /// <summary>Applied stereotype names (find only).</summary>
    Stereotypes = 32,
    /// <summary>Diagram notes (comment bodies) and the text of free shapes.</summary>
    DiagramText = 64,
    /// <summary>Discussion comments (find only, except your own).</summary>
    Comments = 128,
    /// <summary>The SysML v2 text of the model (find only).</summary>
    SysmlText = 256,

    Default = Names | Documentation | Requirements | Values | TaggedValues | DiagramText,
    All = Default | Stereotypes | Comments | SysmlText,
}

/// <summary>Where a find looks.</summary>
public enum FindScope
{
    /// <summary>Every element of the model (and its used projects).</summary>
    Model,
    /// <summary>The given packages (<see cref="FindQuery.Roots"/>) and everything they contain.</summary>
    Package,
    /// <summary>The diagram (<see cref="FindQuery.Diagram"/>) and the elements it shows.</summary>
    Diagram,
    /// <summary>Exactly the given elements (<see cref="FindQuery.Roots"/>).</summary>
    Selection,
}

/// <summary>What to find, how to match it, what to replace it with and where to look.</summary>
public sealed record FindQuery(string Find)
{
    public string Replace { get; init; } = "";
    public bool MatchCase { get; init; }
    public bool WholeWord { get; init; }
    /// <summary>.NET regular expression; the replacement may use $1, ${name} and $0.</summary>
    public bool Regex { get; init; }
    public SearchIn In { get; init; } = SearchIn.Default;
    public FindScope Scope { get; init; } = FindScope.Model;
    /// <summary>The packages (<see cref="FindScope.Package"/>) or elements (<see cref="FindScope.Selection"/>) to look in.</summary>
    public IReadOnlyList<Element>? Roots { get; init; }
    public Diagram? Diagram { get; init; }
    /// <summary>Only elements of these kinds (null or empty: every kind).</summary>
    public IReadOnlySet<ElementKind>? Kinds { get; init; }
    /// <summary>The user's package permissions: hidden elements are never searched, read-only ones are never replaced.</summary>
    public AccessView? Access { get; init; }
    /// <summary>Nothing can be changed (view-only role, a pinned historical view).</summary>
    public bool ViewOnly { get; init; }
    /// <summary>Stop after this many matches (the UI asks the user to refine the search).</summary>
    public int MaxMatches { get; init; } = FindReplace.DefaultMaxMatches;
    /// <summary>Per-call limit of the regex engine (stops catastrophic backtracking).</summary>
    public TimeSpan RegexTimeout { get; init; } = FindReplace.DefaultRegexTimeout;
}

/// <summary>One occurrence in a text: where it is and what it would become.</summary>
public sealed record TextMatch(int Index, int Length, string Replacement);

/// <summary>
/// Text searched outside the element field bag (discussion comments, the SysML v2 text). <paramref name="ReadOnlyReason"/> null
/// means a replace may change it; the caller applies such changes itself (<see cref="ReplacePlan.External"/>).
/// </summary>
public sealed record ExternalText(string Key, SearchIn Category, string Label, string Text, string? ElementId = null, string? ReadOnlyReason = null);

/// <summary>The matches in one field of one element (or in one external text).</summary>
public sealed record FieldHit(string Key, SearchIn Category, string Label, string Value, IReadOnlyList<TextMatch> Matches, string? ReadOnlyReason)
{
    public bool ReadOnly => ReadOnlyReason != null;
}

/// <summary>The matches of one element (or one group of external texts with <see cref="Element"/> null).</summary>
public sealed record ElementHits(Element? Element, string Title, IReadOnlyList<FieldHit> Fields)
{
    public int Count => Fields.Sum(f => f.Matches.Count);
    public string Id => Element?.Id ?? "ext:" + Title;
}

/// <summary>The outcome of a search: the hits in model order, how many matches, and whether it stopped at the cap.</summary>
public sealed record FindResult(IReadOnlyList<ElementHits> Hits, int Matches, bool Truncated, string? Error)
{
    public int Elements => Hits.Count;
}

/// <summary>One field's change, as shown in the preview diff (before → after) and applied as part of one undo step.</summary>
public sealed record ReplaceChange(Element? Element, string Key, string Label, string Before, string After, string? SkipReason = null)
{
    public bool Skipped => SkipReason != null;
}

/// <summary>The changes a replace would make: model fields (one undo step) and external texts the caller applies itself.</summary>
public sealed record ReplacePlan(IReadOnlyList<ReplaceChange> Changes, IReadOnlyList<ReplaceChange> External)
{
    public IEnumerable<ReplaceChange> Applicable => Changes.Where(c => !c.Skipped);
    public int Occurrences { get; init; }
}

/// <summary>What a replace did: fields changed, and fields left alone with the reason (read-only, changed since the preview, a clash).</summary>
public sealed record ReplaceOutcome(int Applied, IReadOnlyList<ReplaceChange> Skipped);

/// <summary>
/// Model-wide find and replace (docs-site/docs/find-replace.md). Searches names, documentation, requirement text, values,
/// tagged values, stereotypes, diagram notes, discussion comments and the SysML v2 text; replaces in one undoable,
/// replicated batch through the ordinary <see cref="ModelRepository.Set"/> / <see cref="ModelRepository.Rename"/> path, so
/// suspect links, database column renames and permissions apply as they do for a hand edit.
/// <para>Searching reads the model only and can run off the UI thread (it takes a snapshot of the element list first);
/// applying must run where the model is edited.</para>
/// </summary>
public static class FindReplace
{
    public const int DefaultMaxMatches = 10_000;
    public static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);

    // ------------------------------------------------------------------ fields

    /// <summary>Value-like fields: expressions, defaults, bodies and behaviours.</summary>
    private static readonly HashSet<string> ValueKeys = new(StringComparer.Ordinal)
    {
        FieldKeys.DefaultValue, FieldKeys.Body, FieldKeys.Guard, FieldKeys.Effect, FieldKeys.Entry, FieldKeys.Exit, FieldKeys.DoActivity,
        InteractionNotation.Constraint, ExtFieldKeys.Duration, ExtFieldKeys.Distribution,
    };

    /// <summary>The category a field belongs to, or <see cref="SearchIn.None"/> for fields find never looks at (ids, references, layout).</summary>
    public static SearchIn CategoryOf(Element e, string key)
    {
        switch (key)
        {
            case FieldKeys.Name: return e.Kind == ElementKind.Shape ? SearchIn.DiagramText : SearchIn.Names;
            case FieldKeys.Documentation: return SearchIn.Documentation;
            case FieldKeys.Text or FieldKeys.RequirementId or ReqFields.Rationale: return SearchIn.Requirements;
            case FieldKeys.Body when e.Kind == ElementKind.Comment: return SearchIn.DiagramText;
            case FieldKeys.Stereotypes: return SearchIn.Stereotypes;
        }
        if (ValueKeys.Contains(key)) return SearchIn.Values;
        if (key.StartsWith(FieldKeys.TagPrefix, StringComparison.Ordinal) || key.StartsWith(ReqFields.CustomPrefix, StringComparison.Ordinal))
            return SearchIn.TaggedValues;
        return SearchIn.None;
    }

    /// <summary>The label of a field in results and the preview.</summary>
    public static string LabelOf(Element e, string key) => key switch
    {
        FieldKeys.Name => e.Kind == ElementKind.Shape ? "Shape text" : "Name",
        FieldKeys.Documentation => "Documentation",
        FieldKeys.Text => "Text",
        FieldKeys.RequirementId => "Id",
        ReqFields.Rationale => "Rationale",
        FieldKeys.Body => e.Kind == ElementKind.Comment ? "Note" : e.Kind == ElementKind.ConstraintBlock ? "Constraint" : "Body",
        FieldKeys.DefaultValue => "Default value",
        FieldKeys.Guard => "Guard",
        FieldKeys.Effect => "Effect",
        FieldKeys.Entry => "Entry",
        FieldKeys.Exit => "Exit",
        FieldKeys.DoActivity => "Do activity",
        FieldKeys.Stereotypes => "Stereotypes",
        InteractionNotation.Constraint => "Constraint",
        ExtFieldKeys.Duration => "Duration",
        ExtFieldKeys.Distribution => "Distribution",
        _ when key.StartsWith(FieldKeys.TagPrefix, StringComparison.Ordinal) => "Tag " + key[FieldKeys.TagPrefix.Length..],
        _ when key.StartsWith(ReqFields.CustomPrefix, StringComparison.Ordinal) => "Attribute " + key[ReqFields.CustomPrefix.Length..],
        _ => key,
    };

    /// <summary>Categories whose matches are shown but never replaced.</summary>
    public const SearchIn FindOnly = SearchIn.Stereotypes | SearchIn.SysmlText;

    public static string Describe(SearchIn c) => c switch
    {
        SearchIn.Names => "Names",
        SearchIn.Documentation => "Documentation",
        SearchIn.Requirements => "Requirement text and id",
        SearchIn.Values => "Values and expressions",
        SearchIn.TaggedValues => "Tagged values and attributes",
        SearchIn.Stereotypes => "Stereotype names",
        SearchIn.DiagramText => "Diagram notes and text",
        SearchIn.Comments => "Comments",
        SearchIn.SysmlText => "SysML v2 text",
        _ => c.ToString(),
    };

    // ------------------------------------------------------------------ matching

    /// <summary>A compiled find pattern. <see cref="Create"/> returns an error message instead for an invalid regular expression.</summary>
    public sealed class Matcher
    {
        private readonly string _find;
        private readonly string _replace;
        private readonly StringComparison _comparison;
        private readonly bool _wholeWord;
        private readonly Regex? _regex;

        private Matcher(FindQuery q, Regex? regex)
        {
            _find = q.Find;
            _replace = q.Replace ?? "";
            _comparison = q.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            _wholeWord = q.WholeWord;
            _regex = regex;
        }

        public static (Matcher? Matcher, string? Error) Create(FindQuery q)
        {
            if (string.IsNullOrEmpty(q.Find)) return (null, "Type something to find.");
            if (!q.Regex) return (new Matcher(q, null), null);
            var pattern = q.WholeWord ? $@"\b(?:{q.Find})\b" : q.Find;
            var options = RegexOptions.CultureInvariant | (q.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            try
            {
                var regex = new Regex(pattern, options, q.RegexTimeout);
                if (regex.IsMatch("")) return (null, "The pattern matches empty text; make it match at least one character.");
                return (new Matcher(q, regex), null);
            }
            catch (ArgumentException ex) { return (null, "Invalid regular expression: " + ex.Message); }
            catch (RegexMatchTimeoutException) { return (null, TimeoutMessage); }
        }

        public const string TimeoutMessage = "The regular expression took too long (catastrophic backtracking?). Simplify the pattern.";

        /// <summary>Every non-overlapping occurrence in <paramref name="text"/>, with its replacement. Throws <see cref="RegexMatchTimeoutException"/>.</summary>
        public List<TextMatch>? Matches(string text)
        {
            if (text.Length == 0) return null;
            List<TextMatch>? list = null;
            if (_regex != null)
            {
                for (var m = _regex.Match(text); m.Success; m = m.NextMatch())
                {
                    if (m.Length == 0) continue;
                    (list ??= []).Add(new TextMatch(m.Index, m.Length, m.Result(_replace)));
                }
                return list;
            }
            var at = 0;
            while (at <= text.Length - _find.Length)
            {
                var i = text.IndexOf(_find, at, _comparison);
                if (i < 0) break;
                if (_wholeWord && !IsWord(text, i, _find.Length)) { at = i + 1; continue; }
                (list ??= []).Add(new TextMatch(i, _find.Length, _replace));
                at = i + _find.Length;
            }
            return list;
        }

        private static bool IsWord(string text, int index, int length) =>
            (index == 0 || !IsWordChar(text[index - 1])) && (index + length >= text.Length || !IsWordChar(text[index + length]));

        private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    /// <summary>The text with the given matches replaced (matches must come from one <see cref="Matcher.Matches"/> call).</summary>
    public static string Apply(string text, IEnumerable<TextMatch> matches)
    {
        var sb = new StringBuilder(text.Length);
        var at = 0;
        foreach (var m in matches.OrderBy(m => m.Index))
        {
            if (m.Index < at || m.Index + m.Length > text.Length) continue;
            sb.Append(text, at, m.Index - at).Append(m.Replacement);
            at = m.Index + m.Length;
        }
        return sb.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>A one-line excerpt around a match: the text before it, the match, and the text after.</summary>
    public static (string Before, string Match, string After) Snippet(string text, TextMatch m, int context = 40)
    {
        var start = Math.Max(0, m.Index - context);
        var end = Math.Min(text.Length, m.Index + m.Length + context);
        static string Flat(string s) => s.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        var before = (start > 0 ? "…" : "") + Flat(text[start..m.Index]);
        var after = Flat(text[(m.Index + m.Length)..end]) + (end < text.Length ? "…" : "");
        return (before, Flat(text.Substring(m.Index, m.Length)), after);
    }

    // ------------------------------------------------------------------ scope

    /// <summary>
    /// The elements a query looks in, in containment order (parents before children). Cheap (a walk of the tree, no field
    /// reads); take it where the model is edited, then <see cref="Search"/> can run elsewhere.
    /// </summary>
    public static List<Element> ScopeOf(ModelRepository model, FindQuery q)
    {
        var list = new List<Element>();
        var kinds = q.Kinds is { Count: > 0 } k ? k : null;
        bool Wanted(Element e) => (kinds == null || kinds.Contains(e.Kind)) && model.Contains(e);
        void Walk(Element root)
        {
            var stack = new Stack<Element>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var e = stack.Pop();
                if (Wanted(e)) list.Add(e);
                var children = e.OwnedElements;
                for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
        }
        switch (q.Scope)
        {
            case FindScope.Model:
                Walk(model.Root);
                break;
            case FindScope.Package:
                var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
                foreach (var r in q.Roots ?? [])
                    if (!(q.Roots ?? []).Any(o => !ReferenceEquals(o, r) && o.IsAncestorOf(r)) && seen.Add(r)) Walk(r);
                break;
            case FindScope.Diagram when q.Diagram is { } d:
                var shown = new HashSet<Element>(ReferenceEqualityComparer.Instance);
                if (Wanted(d) && shown.Add(d)) list.Add(d);
                foreach (var n in d.Nodes) if (Wanted(n.Element) && shown.Add(n.Element)) list.Add(n.Element);
                foreach (var x in d.Edges) if (Wanted(x.Relationship) && shown.Add(x.Relationship)) list.Add(x.Relationship);
                break;
            case FindScope.Selection:
                foreach (var e in (q.Roots ?? []).Distinct()) if (Wanted(e)) list.Add(e);
                break;
        }
        if (q.Access is { HidesAnything: true } access) list.RemoveAll(access.IsHidden);
        return list;
    }

    /// <summary>Why <paramref name="e"/> can't be changed by a replace (library, view-only, package permissions), or null.</summary>
    public static string? ReadOnlyReason(ModelRepository model, Element e, FindQuery q)
    {
        if (model.LibraryOf(e) is { } lib) return $"Used project “{lib.Info.SourceProjectName}” (read-only)";
        if (q.ViewOnly) return "View only";
        if (q.Access is { } a && !a.CanEdit(e))
            return a.ProtectingPackage(e) is { } pkg ? $"Read-only for you (package “{pkg.DisplayName}”)" : "Read-only for you";
        return null;
    }

    // ------------------------------------------------------------------ search

    /// <summary>
    /// Streams the hits of <paramref name="elements"/> (from <see cref="ScopeOf"/>) and then of <paramref name="external"/>, in
    /// order. Stops when cancelled. Throws <see cref="RegexMatchTimeoutException"/> when a pattern runs away; <see cref="Run"/>
    /// turns that into an error.
    /// </summary>
    public static IEnumerable<ElementHits> Search(ModelRepository model, FindQuery q, Matcher matcher, IReadOnlyList<Element> elements,
                                                  IReadOnlyList<ExternalText>? external = null, CancellationToken ct = default)
    {
        var wanted = q.In;
        var fields = new List<FieldHit>();
        // Comments travel with the element they're about; grouped there when it is in scope.
        Dictionary<string, List<ExternalText>>? byElement = null;
        if (external != null)
            foreach (var x in external)
                if (x.ElementId != null && (wanted & x.Category) != 0)
                    ((byElement ??= new(StringComparer.Ordinal)).TryGetValue(x.ElementId, out var l) ? l : byElement[x.ElementId] = []).Add(x);
        var keys = new List<KeyValuePair<string, string>>(8);
        for (var i = 0; i < elements.Count; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var e = elements[i];
            fields.Clear();
            string? reason = null;
            var reasonKnown = false;
            keys.Clear();
            try { keys.AddRange(e.Fields); }
            catch (InvalidOperationException) { continue; }   // changed while we read it (searching off the UI thread)
            foreach (var (key, value) in keys)
            {
                var cat = CategoryOf(e, key);
                if ((cat & wanted) == 0 || value.Length == 0) continue;
                var matches = matcher.Matches(value);
                if (matches == null) continue;
                if (!reasonKnown) { reason = ReadOnlyReason(model, e, q); reasonKnown = true; }
                var fieldReason = (cat & FindOnly) != 0 ? "Find only" : reason
                    ?? (q.Access != null || q.ViewOnly || model.HasLibraries ? Blocker(model, e, key, x => ReadOnlyReason(model, x, q)) : null);
                fields.Add(new FieldHit(key, cat, LabelOf(e, key), value, matches, fieldReason));
            }
            if (byElement != null && byElement.Remove(e.Id, out var texts))
                foreach (var x in texts) if (matcher.Matches(x.Text) is { } ms) fields.Add(new FieldHit(x.Key, x.Category, x.Label, x.Text, ms, x.ReadOnlyReason));
            if (fields.Count > 0)
            {
                fields.Sort((a, b) => Order(a.Key).CompareTo(Order(b.Key)));
                yield return new ElementHits(e, e.Kind == ElementKind.Comment ? "Note" : e.DisplayName, fields.ToArray());
            }
        }
        if (external == null) yield break;
        // External texts of elements out of scope are skipped (scope applies to comments too); unattached ones form their own groups.
        foreach (var group in external.Where(x => x.ElementId == null && (wanted & x.Category) != 0).GroupBy(x => x.Category))
        {
            ct.ThrowIfCancellationRequested();
            var hits = new List<FieldHit>();
            foreach (var x in group)
                if (matcher.Matches(x.Text) is { } ms) hits.Add(new FieldHit(x.Key, x.Category, x.Label, x.Text, ms, x.ReadOnlyReason));
            if (hits.Count > 0) yield return new ElementHits(null, Describe(group.Key), hits);
        }
    }

    private static int Order(string key) => key switch
    {
        FieldKeys.Name => 0,
        FieldKeys.RequirementId => 1,
        FieldKeys.Text => 2,
        FieldKeys.Documentation => 3,
        _ => 4,
    };

    /// <summary>Searches and collects the hits, stopping at <see cref="FindQuery.MaxMatches"/>.</summary>
    public static FindResult Run(ModelRepository model, FindQuery q, IReadOnlyList<ExternalText>? external = null, CancellationToken ct = default)
    {
        var (matcher, error) = Matcher.Create(q);
        if (matcher == null) return new FindResult([], 0, false, error);
        var hits = new List<ElementHits>();
        var count = 0;
        try
        {
            foreach (var h in Search(model, q, matcher, ScopeOf(model, q), external, ct))
            {
                hits.Add(h);
                count += h.Count;
                if (count >= q.MaxMatches) return new FindResult(hits, count, true, null);
            }
        }
        catch (RegexMatchTimeoutException) { return new FindResult(hits, count, false, Matcher.TimeoutMessage); }
        return new FindResult(hits, count, false, null);
    }

    // ------------------------------------------------------------------ replace

    /// <summary>
    /// The changes replacing the chosen matches would make, checked against the model's rules: read-only and find-only fields
    /// are skipped, as are renames that would give two siblings of one kind the same name (tables: ignoring case), names
    /// emptied out, and requirement ids that would be used twice.
    /// </summary>
    /// <param name="selection">Per hit field, the matches to replace (null: all of them).</param>
    /// <param name="readOnlyReason">Why the user may not change an element (package permissions; see <see cref="Blocker"/>), or null.</param>
    public static ReplacePlan Plan(ModelRepository model, IEnumerable<(ElementHits Hit, FieldHit Field, IReadOnlyCollection<TextMatch>? Matches)> selection,
                                   Func<Element, string?>? readOnlyReason = null)
    {
        var changes = new List<ReplaceChange>();
        var external = new List<ReplaceChange>();
        var occurrences = 0;
        foreach (var (hit, field, chosen) in selection)
        {
            var ms = chosen ?? field.Matches;
            if (ms.Count == 0) continue;
            var after = Apply(field.Value, ms);
            if (after == field.Value) continue;
            var reason = field.ReadOnlyReason;
            if (hit.Element is not { } e || !IsModelField(hit, field))
            {
                if (reason == null) occurrences += ms.Count;
                external.Add(new ReplaceChange(hit.Element, field.Key, field.Label, field.Value, after, reason));
                continue;
            }
            reason ??= readOnlyReason == null ? null : Blocker(model, e, field.Key, readOnlyReason);
            if (reason == null) occurrences += ms.Count;
            changes.Add(new ReplaceChange(e, field.Key, field.Label, field.Value, after, reason));
        }
        CheckNames(model, changes);
        CheckRequirementIds(model, changes);
        return new ReplacePlan(changes, external) { Occurrences = occurrences };
    }

    private static bool IsModelField(ElementHits hit, FieldHit f) => (f.Category & (SearchIn.Comments | SearchIn.SysmlText)) == 0 && hit.Element != null;

    private static void CheckNames(ModelRepository model, List<ReplaceChange> changes)
    {
        var renamed = new Dictionary<Element, string>(ReferenceEqualityComparer.Instance);
        foreach (var c in changes)
            if (c is { Key: FieldKeys.Name, Skipped: false }) renamed[c.Element!] = c.After;
        if (renamed.Count == 0) return;
        string NameOf(Element x) => renamed.TryGetValue(x, out var n) ? n : x.Name;
        for (var i = 0; i < changes.Count; i++)
        {
            var c = changes[i];
            if (c.Key != FieldKeys.Name || c.Skipped) continue;
            var e = c.Element!;
            var name = c.After.Trim();
            if (name.Length == 0 && Metamodel.NeedsName(e.Kind)) { changes[i] = c with { SkipReason = "A name can't be empty" }; renamed.Remove(e); continue; }
            if (e.IsRelationship || e.Owner is not { } owner || name.Length == 0) continue;
            var cmp = e.Kind == ElementKind.Table ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var clash = owner.OwnedElements.FirstOrDefault(s => !ReferenceEquals(s, e) && s.Kind == e.Kind && string.Equals(NameOf(s), name, cmp));
            if (clash != null)
            {
                changes[i] = c with { SkipReason = $"“{owner.DisplayName}” already has a {Metamodel.DisplayName(e.Kind)} named “{NameOf(clash)}”" };
                renamed.Remove(e);
                i = -1;   // a skipped rename keeps its old name, which may clash with an earlier one: check again
            }
        }
    }

    private static void CheckRequirementIds(ModelRepository model, List<ReplaceChange> changes)
    {
        var idx = changes.Select((c, i) => (c, i)).Where(t => t.c is { Key: FieldKeys.RequirementId, Skipped: false }).ToList();
        if (idx.Count == 0) return;
        var changed = idx.Select(t => t.c.Element!).ToHashSet(ReferenceEqualityComparer.Instance);
        var used = new HashSet<string>(model.OfKind(ElementKind.Requirement).Where(r => !changed.Contains(r)).Select(r => r.Get(FieldKeys.RequirementId)).Where(s => s.Length > 0), StringComparer.Ordinal);
        foreach (var (c, i) in idx)
        {
            var id = c.After.Trim();
            if (id.Length == 0) changes[i] = c with { SkipReason = "A requirement id can't be empty" };
            else if (!used.Add(id)) changes[i] = c with { SkipReason = $"Requirement id “{id}” is already used" };
        }
        // Skipped ids keep their old value: it may now collide with one that is applied.
        foreach (var (c, i) in idx)
            if (changes[i].Skipped && c.Element!.Kind == ElementKind.Requirement) used.Add(c.Before);
    }

    /// <summary>
    /// Applies the plan's model changes as ONE undo step (one replicated batch). A field whose value no longer equals the
    /// preview's "before" (someone changed it since) is skipped and reported, as are fields that became read-only.
    /// Names go through <see cref="ModelRepository.Rename"/>, so column renames update foreign keys and indexes.
    /// </summary>
    public static ReplaceOutcome ApplyPlan(ModelRepository model, ReplacePlan plan, Func<Element, string?>? readOnlyReason = null, string? description = null)
    {
        var skipped = plan.Changes.Where(c => c.Skipped).ToList();
        var todo = plan.Applicable.ToList();
        if (todo.Count == 0) return new ReplaceOutcome(0, skipped);
        var applied = 0;
        using (model.History.Transaction(description ?? $"Replace in {todo.Count} field{(todo.Count == 1 ? "" : "s")}"))
        {
            foreach (var c in todo)
            {
                var e = c.Element!;
                if (!model.Contains(e)) { skipped.Add(c with { SkipReason = "Deleted since the preview" }); continue; }
                if (e.Get(c.Key) != c.Before) { skipped.Add(c with { SkipReason = "Changed by someone else since the preview" }); continue; }
                if (readOnlyReason != null && Blocker(model, e, c.Key, readOnlyReason) is { } why) { skipped.Add(c with { SkipReason = why }); continue; }
                try
                {
                    if (c.Key == FieldKeys.Name) model.Rename(e, c.After.Trim());
                    else model.Set(e, c.Key, c.After);
                    applied++;
                }
                catch (ReadOnlyElementException ex) { skipped.Add(c with { SkipReason = ex.Message }); }
                catch (InvalidOperationException ex) { skipped.Add(c with { SkipReason = ex.Message }); }
            }
        }
        return new ReplaceOutcome(applied, skipped);
    }

    /// <summary>Plans and applies replacing every replaceable match of <paramref name="result"/> (convenience for scripts and tests).</summary>
    public static ReplaceOutcome ReplaceAll(ModelRepository model, FindResult result, Func<Element, string?>? readOnlyReason = null) =>
        ApplyPlan(model, Plan(model, result.Hits.SelectMany(h => h.Fields.Select(f => (h, f, (IReadOnlyCollection<TextMatch>?)null))), readOnlyReason), readOnlyReason);

    /// <summary>
    /// Why changing <paramref name="key"/> of <paramref name="e"/> would be refused: the element itself is read-only, or an
    /// element the change propagates to is (a requirement's trace links get a suspect-link baseline; a column rename updates
    /// the foreign keys, indexes and constraints that list it). A collaboration server refuses a whole batch for one such
    /// field, so these are left out of it.
    /// </summary>
    public static string? Blocker(ModelRepository model, Element e, string key, Func<Element, string?> readOnlyReason)
    {
        if (readOnlyReason(e) is { } own) return own;
        if (SuspectLinks.Watches(e, key))
            foreach (var r in model.RelationshipsOf(e))
                if (SuspectLinks.IsTraceKind(r.Kind) && readOnlyReason(r) is { } why)
                    return $"Its {Metamodel.DisplayName(r.Kind).ToLowerInvariant()} link with “{(ReferenceEquals(r.Source, e) ? r.Target : r.Source).DisplayName}” is read-only for you ({why})";
        if (key == FieldKeys.Name && e.Kind == ElementKind.Column && e.Owner is { Kind: ElementKind.Table } table)
        {
            var dependents = model.Outgoing(table, ElementKind.ForeignKey).Concat(model.Incoming(table, ElementKind.ForeignKey))
                .Concat(DataModeling.DbModel.Indexes(table)).Concat(DataModeling.DbModel.Constraints(table));
            foreach (var d in dependents)
                if (readOnlyReason(d) is { } why) return $"“{d.DisplayName}”, which lists this column, is read-only for you ({why})";
        }
        return null;
    }
}
