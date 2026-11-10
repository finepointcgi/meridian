using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>
/// Requirements → use case diagram: each functional requirement's "shall …" phrase names a use case (requirements with the
/// same phrase share one); actors come from a "stakeholder" attribute, from "allow the X to …" in the text, or from existing
/// actors the text names, else a placeholder "User". Every use case «refine»s its requirements.
/// </summary>
internal static partial class RequirementsToUseCases
{
    [GeneratedRegex(@"\b(?:shall|must|should|will)\b\s+(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Shall();

    [GeneratedRegex(@"^(?:allow|enable|permit|let)\s+(?:the\s+|an?\s+)?(?<actor>[A-Za-z][\w-]*(?:\s+[A-Za-z][\w-]*)?)\s+to\s+(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex AllowTo();

    [GeneratedRegex(@"^let\s+(?:the\s+|an?\s+)?(?<actor>[A-Za-z][\w-]*)\s+(?<rest>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex LetX();

    [GeneratedRegex(@"\b(?:at least|at most|less than|more than|no more than|no less than|within|not exceed|maximum of|minimum of|up to)\b[^.]*\d|\d+(?:[.,]\d+)?\s*(?:%|ms|s|min|h|km/h|km|kg|g|m|mm|cm|kw|w|v|a|hz|db|°c|n)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Quantitative();

    private static readonly HashSet<string> StateVerbs = new(StringComparer.OrdinalIgnoreCase)
        { "be", "have", "weigh", "meet", "comply", "conform", "last", "withstand", "cost", "not", "contain", "consist", "remain" };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "from", "to", "in", "within", "at", "by", "for", "with", "when", "while", "during", "under", "before", "after", "if", "using", "via", "on", "per", "than", "and", "or" };

    public sealed record Reading(string? UseCase, string? Actor, bool Functional, string? Why);

    /// <summary>What a requirement's text says: the use case it implies, an actor, and whether it reads as functional.</summary>
    public static Reading Read(Element req)
    {
        var text = req.Get(FieldKeys.Text);
        var kind = (req.Fields.Where(f => f.Key.StartsWith("attr:", StringComparison.OrdinalIgnoreCase) || f.Key.StartsWith(FieldKeys.TagPrefix, StringComparison.Ordinal))
                       .Where(f => f.Key.EndsWith(":type", StringComparison.OrdinalIgnoreCase) || f.Key.EndsWith(":kind", StringComparison.OrdinalIgnoreCase) || f.Key.EndsWith(":category", StringComparison.OrdinalIgnoreCase))
                       .Select(f => f.Value).FirstOrDefault() ?? "") + " " + string.Join(" ", req.Stereotypes);
        bool? declared = kind.Contains("non", StringComparison.OrdinalIgnoreCase) || kind.Contains("performance", StringComparison.OrdinalIgnoreCase) || kind.Contains("constraint", StringComparison.OrdinalIgnoreCase) || kind.Contains("physical", StringComparison.OrdinalIgnoreCase)
            ? false : kind.Contains("functional", StringComparison.OrdinalIgnoreCase) ? true : null;

        if (Shall().Match(text) is not { Success: true } m) return new Reading(null, null, declared ?? false, "no “shall” statement");
        var rest = m.Groups["rest"].Value.Trim();
        string? actor = null;
        if (rest.StartsWith("not ", StringComparison.OrdinalIgnoreCase)) return new Reading(null, null, declared ?? false, "a “shall not” constraint");
        foreach (var lead in new[] { "be able to ", "provide the ability to ", "be capable of " })
            if (rest.StartsWith(lead, StringComparison.OrdinalIgnoreCase)) rest = rest[lead.Length..];
        if (AllowTo().Match(rest) is { Success: true } am) { actor = DeriveText.Title(am.Groups["actor"].Value); rest = am.Groups["rest"].Value; }
        else if (LetX().Match(rest) is { Success: true } lm) { actor = DeriveText.Title(lm.Groups["actor"].Value); rest = lm.Groups["rest"].Value; }
        var words = rest.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(w => w.Trim(',', '.', ';', ':', '(', ')', '"')).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return new Reading(null, actor, declared ?? false, "no verb after “shall”");
        var verb = words[0];
        var phrase = new List<string> { verb };
        foreach (var w in words.Skip(1))
        {
            if (phrase.Count >= 4 || StopWords.Contains(w) || w.Any(char.IsDigit) || w is "a" or "an" or "the" && phrase.Count > 1) break;
            if (w is "a" or "an" or "the") continue;
            phrase.Add(w);
        }
        var useCase = DeriveText.Title(string.Join(' ', phrase));
        if (StateVerbs.Contains(verb)) return new Reading(useCase, actor, declared ?? false, "states a property rather than a behaviour");
        if (declared is { } d) return new Reading(useCase, actor, d, d ? null : "marked as non-functional");
        if (Quantitative().IsMatch(text)) return new Reading(useCase, actor, false, "a performance requirement (a number with a unit or bound)");
        return new Reading(useCase, actor, true, null);
    }

    public static IReadOnlyList<Element> Requirements(Element source) =>
        (source.Kind == ElementKind.Requirement ? source.Descendants().Prepend(source) : source.Descendants())
            .Where(e => e.Kind == ElementKind.Requirement && !e.OwnedElements.Any(c => c.Kind == ElementKind.Requirement) && e.Get(FieldKeys.Text).Length > 0)
            .ToList();

    private static string Stakeholder(Element req) =>
        req.Fields.Where(f => f.Key.EndsWith(":stakeholder", StringComparison.OrdinalIgnoreCase) || f.Key.Equals("stakeholder", StringComparison.OrdinalIgnoreCase))
                  .Select(f => f.Value.Trim()).FirstOrDefault(v => v.Length > 0) ?? "";

    public static string Digest(Element source)
    {
        var sb = new StringBuilder().Append(source.Name).Append('\n');
        foreach (var r in Requirements(source))
            sb.Append(r.Id).Append(' ').Append(r.Name).Append(' ').Append(r.Get(FieldKeys.Text)).Append(' ').Append(Stakeholder(r)).Append('\n');
        return sb.ToString();
    }

    public static void Build(ModelRepository m, PlanBuilder b, DerivationOptions o)
    {
        var source = b.Plan.Source;
        var reqs = Requirements(source);
        if (reqs.Count == 0) { b.Warn($"{source.DisplayName} has no requirements with text."); return; }
        var home = source.Kind == ElementKind.Requirement ? AutoDiagrammer.NearestPackage(source) ?? m.Root : source;
        var pkgOwner = home.Kind == ElementKind.Model ? home : home.Owner ?? m.Root;
        var pkgName = home.Kind == ElementKind.Model ? "Use cases" : $"{home.Name} use cases";
        var pkg = b.Element("package", ElementKind.Package, pkgOwner, pkgName, null,
            (FieldKeys.Documentation, $"Use cases derived from the requirements in {source.DisplayName}."));
        var pkgRef = ItemRef.To(pkg.Key);
        var existingActors = m.OfKind(ElementKind.Actor).ToList();

        ItemRef ActorRef(string name)
        {
            if (existingActors.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) is { } a)
            {
                b.Show(a, new PlannedShape("diagram"));
                return a;
            }
            var key = $"actor:{name.ToLowerInvariant()}";
            if (b.Plan[key] == null) b.Element(key, ElementKind.Actor, pkgRef, name, new PlannedShape("diagram")).OnlyIfUsed = true;
            return ItemRef.To(key);
        }

        var groups = new List<(string Name, List<Element> Reqs, HashSet<string> Actors, bool Functional, string? Why)>();
        var skipped = 0;
        foreach (var r in reqs)
        {
            var read = Read(r);
            var name = read.UseCase ?? r.Name;
            if (read.UseCase == null) skipped++;
            var actors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Stakeholder(r) is { Length: > 0 } st) actors.Add(DeriveText.Title(st));
            if (read.Actor != null) actors.Add(read.Actor);
            foreach (var a in existingActors.Where(a => DeriveText.Mentions(r.Get(FieldKeys.Text), a.Name))) actors.Add(a.Name);
            var g = groups.FindIndex(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (g < 0) groups.Add((name, [r], actors, read.Functional, read.Why));
            else
            {
                var x = groups[g];
                x.Reqs.Add(r);
                x.Actors.UnionWith(actors);
                groups[g] = (x.Name, x.Reqs, x.Actors, x.Functional || read.Functional, x.Functional || read.Functional ? null : x.Why);
            }
        }

        foreach (var (name, rs, actors, functional, why) in groups)
        {
            var key = $"usecase:{name.ToLowerInvariant()}";
            var uc = b.Element(key, ElementKind.UseCase, pkgRef, name, new PlannedShape("diagram"),
                (FieldKeys.Documentation, string.Join("\n", rs.Select(r => $"{r.Get(FieldKeys.RequirementId)} {r.Get(FieldKeys.Text)}".Trim()))));
            if (!functional) { uc.Selected = false; uc.Note = $"not ticked: {why}"; }
            if (actors.Count == 0) actors.Add("User");
            foreach (var a in actors.OrderBy(a => a, StringComparer.OrdinalIgnoreCase))
            {
                var ar = ActorRef(a);
                b.Rel($"association:{ar}:{key}", ElementKind.Association, ar, ItemRef.To(key));
            }
            foreach (var r in rs) b.Rel($"refine:{key}:{r.Id}", ElementKind.Refine, ItemRef.To(key), r);
        }
        if (b.Plan["actor:user"] is { } user) user.Note = "placeholder: no actor could be identified";
        if (skipped > 0) b.Warn($"{skipped} requirement{(skipped == 1 ? " has" : "s have")} no “shall” phrase; {(skipped == 1 ? "its" : "their")} name is used as the use case.");
        b.Diagram("diagram", DiagramType.UseCase, pkgRef, pkgName, autoLayout: true, $"Use cases derived from the requirements in {source.DisplayName}.");
    }
}

/// <summary>
/// Blocks / requirements → proposals: «satisfy» from blocks to requirements whose text names the block, and a test case with
/// «verify» for each requirement nothing verifies yet. Reviewed item by item.
/// </summary>
internal static class SatisfyAndVerify
{
    public static void Build(ModelRepository m, PlanBuilder b, DerivationOptions o)
    {
        var source = b.Plan.Source;
        var isBlock = source.Kind is ElementKind.Block or ElementKind.Component or ElementKind.Class;
        var reqs = isBlock ? m.OfKind(ElementKind.Requirement).Where(r => !r.OwnedElements.Any(c => c.Kind == ElementKind.Requirement)).ToList()
                           : RequirementsToUseCases.Requirements(source).ToList();
        var blocks = isBlock ? [source] : m.OfKind(ElementKind.Block).Where(x => !DeriveFields.IsDerived(x) || x.Get(DeriveFields.Key) != "context").ToList();
        var satisfied = new HashSet<Element>();
        foreach (var r in reqs)
        {
            var text = r.Get(FieldKeys.Text) + " " + r.Name;
            foreach (var blk in blocks)
            {
                if (!DeriveText.Mentions(text, blk.Name) && !DeriveText.Mentions(text, DeriveText.Words(blk.Name))) continue;
                if (m.Outgoing(blk, ElementKind.Satisfy).Any(x => ReferenceEquals(x.Target, r))) { satisfied.Add(r); continue; }
                var s = b.Rel($"satisfy:{blk.Id}:{r.Id}", ElementKind.Satisfy, blk, r);
                s.Note = $"the text of {r.Get(FieldKeys.RequirementId)} names {blk.Name}".Trim();
                satisfied.Add(r);
            }
        }
        var toVerify = reqs.Where(r => !m.Incoming(r, ElementKind.Verify).Any() && (!isBlock || satisfied.Contains(r))).ToList();
        if (toVerify.Count > 0)
        {
            var existing = m.OfKind(ElementKind.Package).Where(p => p.Name.Equals("Verification", StringComparison.OrdinalIgnoreCase))
                            .OrderBy(p => p.AncestorsAndSelf().Count()).FirstOrDefault();
            ItemRef home = existing != null ? existing : ItemRef.To(b.Element("package", ElementKind.Package, m.Root, "Verification").Key);
            foreach (var r in toVerify)
            {
                var id = r.Get(FieldKeys.RequirementId);
                var tc = b.Element($"testcase:{r.Id}", ElementKind.TestCase, home, $"Verify {r.Name}", null,
                    (FieldKeys.Documentation, $"Checks {id} {r.Name}: {r.Get(FieldKeys.Text)}".Trim()));
                tc.Note = "nothing verifies this requirement yet";
                b.Rel($"verify:{r.Id}", ElementKind.Verify, ItemRef.To(tc.Key), r);
            }
        }
        if (!b.Plan.Items.Any()) b.Warn("Nothing to propose: every requirement that names a block is satisfied by it, and every requirement is verified.");
    }
}
