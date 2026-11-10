using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

public sealed record SysmlSyncOptions
{
    /// <summary>Apply even when the text has syntax errors (import of foreign files). The editor leaves this off.</summary>
    public bool AllowErrors { get; init; }

    /// <summary>Undo-history label of the change.</summary>
    public string Description { get; init; } = "Edit SysML v2 text";
}

public enum SysmlChangeKind { Create, Rename, Update, Move, Delete, Connect, Disconnect }

public sealed record SysmlChange(SysmlChangeKind Kind, string Description);

public sealed class SysmlSyncResult
{
    public List<SysmlChange> Changes { get; } = new();
    public List<SysmlDiagnostic> Diagnostics { get; } = new();
    public bool Applied { get; internal set; }
    public int ElementsCreated => Changes.Count(c => c.Kind == SysmlChangeKind.Create);
    public int RelationshipsCreated => Changes.Count(c => c.Kind == SysmlChangeKind.Connect);
}

/// <summary>
/// Maps a parsed SysML v2 text onto an existing model scope (a package or the whole model) and applies only the
/// differences — create, rename, retype, set field, move, delete, connect, disconnect — through the repository's undoable,
/// replicated operations, as one undo step. Elements are matched by their <c>/* @id */</c> comment when present, else by
/// name within their owner (a lone renamed element of the same kind is recognised as a rename). Model content the
/// textual notation can't express (diagrams, operations, associations, pins …) is never touched.
/// </summary>
public static class SysMLv2Sync
{
    public static SysmlSyncResult Apply(ModelRepository model, Element scope, SysmlDocument doc, SysmlSyncOptions? options = null)
    {
        options ??= new SysmlSyncOptions();
        var result = new SysmlSyncResult();
        if (doc.HasErrors && !options.AllowErrors) return result;
        if (!model.Contains(scope)) throw new ArgumentException("Scope is not part of the model", nameof(scope));
        var mapper = new Mapper(model, scope, doc, result);
        mapper.Match();
        using (model.History.Transaction(options.Description))
            mapper.Apply();
        result.Applied = true;
        return result;
    }

    /// <summary>Element id → span of the declaring name in the text (for "reveal in editor"). Read-only.</summary>
    public static Dictionary<string, TextSpan> Locate(ModelRepository model, Element scope, SysmlDocument doc)
    {
        var mapper = new Mapper(model, scope, doc, new SysmlSyncResult());
        mapper.Match();
        var spans = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
        foreach (var (decl, el) in mapper.Map)
            spans.TryAdd(el.Id, decl.Name != null || decl.SyntheticName != null ? decl.NameSpan : decl.HeaderSpan);
        return spans;
    }

    /// <summary>The element declared or referenced at a text offset (for Ctrl/Cmd-click). Read-only.</summary>
    public static Element? ElementAt(ModelRepository model, Element scope, SysmlDocument doc, int offset)
    {
        var mapper = new Mapper(model, scope, doc, new SysmlSyncResult());
        mapper.Match();
        return mapper.ElementAt(offset);
    }

    /// <summary>A behavior node (action, state, decision …) addressed by its exported name within a behavior.</summary>
    internal static Element? FindNode(Element owner, string name) =>
        owner.Descendants().FirstOrDefault(c => c is not Relationship and not Diagram && SysmlNameIndex.ExportName(c) == name
                                               && c.Kind is not (ElementKind.InitialNode or ElementKind.InitialState or ElementKind.ActivityFinalNode or ElementKind.FinalState));

    // ====================================================================================================

    private sealed class Mapper(ModelRepository m, Element scope, SysmlDocument doc, SysmlSyncResult result)
    {
        public readonly Dictionary<SysmlDecl, Element> Map = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Element, SysmlDecl> _byElement = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SysmlComment, Element> _comments = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SysmlDecl> _created = new(ReferenceEqualityComparer.Instance);
        private SysmlDecl? _top;
        private List<SysmlMember> _topBody = new();
        private SysmlNameIndex _names = null!;

        // -------------------------------------------------------------- classification

        private static string? ElementFamily(Element e) => e.Kind switch
        {
            ElementKind.Package => "package",
            ElementKind.Block or ElementKind.Class or ElementKind.Component => "block",
            ElementKind.Actor => "actor",
            ElementKind.InterfaceBlock or ElementKind.Interface => "portdef",
            ElementKind.ValueType or ElementKind.DataType => "valuetype",
            ElementKind.Enumeration => "enum",
            ElementKind.EnumerationLiteral => "literal",
            ElementKind.ConstraintBlock => "constraint",
            ElementKind.Requirement => "requirement",
            ElementKind.TestCase => "testcase",
            ElementKind.UseCase => "usecase",
            ElementKind.Activity => "activity",
            ElementKind.StateMachine => "statemachine",
            ElementKind.Interaction => "interaction",
            ElementKind.Property or ElementKind.Port => "feature",
            ElementKind.Lifeline => "lifeline",
            ElementKind.Action => "action",
            ElementKind.State => "state",
            ElementKind.DecisionNode => "decision",
            ElementKind.ForkNode => "fork",
            _ => null,
        };

        /// <summary>The family a declaration maps to inside an owner of the given kind, or null if Meridian has no equivalent.</summary>
        private static string? DeclFamily(SysmlDecl d, ElementKind owner)
        {
            var kw = d.Keyword;
            if (d.IsDef)
                return kw switch
                {
                    "part" => d.Tags.Contains("actor") ? "actor" : "block",
                    "item" or "connection" or "allocation" => "block",
                    "port" or "interface" => "portdef",
                    "attribute" => "valuetype",
                    "enum" => "enum",
                    "constraint" => "constraint",
                    "requirement" or "concern" => "requirement",
                    "verification" => "testcase",
                    "use case" => "usecase",
                    "action" => "activity",
                    "state" => "statemachine",
                    "occurrence" => "interaction",
                    _ => null,
                };
            return kw switch
            {
                "package" => "package",
                "decide" or "merge" => owner == ElementKind.Activity ? "decision" : null,
                "fork" or "join" => owner == ElementKind.Activity ? "fork" : null,
                "action" or "perform action" when owner == ElementKind.Activity => "action",
                "state" or "exhibit state" when owner is ElementKind.StateMachine or ElementKind.State => "state",
                "enum" when owner == ElementKind.Enumeration => "literal",
                "requirement" or "concern" when owner is ElementKind.Package or ElementKind.Model or ElementKind.Requirement => "requirement",
                "verification" when owner is ElementKind.Package or ElementKind.Model => "testcase",
                "part" or "item" or "ref" when owner == ElementKind.Interaction => "lifeline",
                "part" or "item" when owner is ElementKind.Package or ElementKind.Model => "block",
                "part" or "item" or "attribute" or "port" or "ref" or "constraint" or "occurrence"
                    when Metamodel.CanOwn(owner, ElementKind.Property) || Metamodel.CanOwn(owner, ElementKind.Port) => "feature",
                "connection" or "interface" when d.Connect != null => null,
                _ => null,
            };
        }

        private static ElementKind KindFor(string family, SysmlDecl d, ElementKind owner) => family switch
        {
            "package" => ElementKind.Package,
            "block" => ElementKind.Block,
            "actor" => ElementKind.Actor,
            "portdef" => ElementKind.InterfaceBlock,
            "valuetype" => ElementKind.ValueType,
            "enum" => ElementKind.Enumeration,
            "literal" => ElementKind.EnumerationLiteral,
            "constraint" => ElementKind.ConstraintBlock,
            "requirement" => ElementKind.Requirement,
            "testcase" => ElementKind.TestCase,
            "usecase" => ElementKind.UseCase,
            "activity" => ElementKind.Activity,
            "statemachine" => ElementKind.StateMachine,
            "interaction" => ElementKind.Interaction,
            "feature" => d.Keyword == "port" && Metamodel.CanOwn(owner, ElementKind.Port) || !Metamodel.CanOwn(owner, ElementKind.Property) ? ElementKind.Port : ElementKind.Property,
            "lifeline" => ElementKind.Lifeline,
            "action" => ElementKind.Action,
            "state" => ElementKind.State,
            "decision" => ElementKind.DecisionNode,
            "fork" => ElementKind.ForkNode,
            _ => throw new InvalidOperationException(family),
        };

        /// <summary>Children the exporter writes as declarations (and that the text therefore owns).</summary>
        private static IEnumerable<Element> Managed(Element owner) =>
            owner.OwnedElements.Where(c => c is not Relationship and not Diagram && ElementFamily(c) != null);

        private static IEnumerable<Element> ManagedComments(Element owner) =>
            owner.Kind == ElementKind.Requirement ? [] : owner.OwnedElements.Where(c => c.Kind == ElementKind.Comment);

        private static string Name(SysmlDecl d) => d.EffectiveName ?? "";

        // -------------------------------------------------------------- matching

        public void Match()
        {
            var decls = doc.Members.OfType<SysmlDecl>().ToList();
            var otherTop = doc.Members.Any(x => x is SysmlRelation or SysmlSuccession);
            if (decls.Count == 1 && decls[0].Keyword == "package" && !otherTop)
            {
                _top = decls[0];
                _topBody = _top.Body;
                Bind(_top, scope);
            }
            else _topBody = doc.Members;

            // 1) identity comments win, wherever the element now lives
            foreach (var d in doc.AllDecls())
                if (d.Id != null && !Map.ContainsKey(d) && m.Find(d.Id) is { } e && !_byElement.ContainsKey(e) && e.Owner != null
                    && ElementFamily(e) is { } fam && (fam == FamilyIgnoringOwner(d) || d.Keyword is "part" or "item" or "attribute" or "port" or "ref" && fam is "feature" or "lifeline"))
                    Bind(d, e);

            // 2) by name within the owner, then lone renames
            MatchBody(_topBody, scope);
        }

        private static string? FamilyIgnoringOwner(SysmlDecl d) =>
            DeclFamily(d, ElementKind.Package) ?? DeclFamily(d, ElementKind.Block) ?? DeclFamily(d, ElementKind.Activity)
            ?? DeclFamily(d, ElementKind.StateMachine) ?? DeclFamily(d, ElementKind.Enumeration) ?? DeclFamily(d, ElementKind.Interaction);

        private void Bind(SysmlDecl d, Element e)
        {
            Map[d] = e;
            _byElement[e] = d;
        }

        /// <summary>Statements of a body, with <c>objective { … }</c> blocks flattened into it.</summary>
        private static IEnumerable<SysmlMember> Flat(IEnumerable<SysmlMember> body)
        {
            foreach (var x in body)
            {
                if (x is SysmlDecl { Keyword: "objective" } o) { foreach (var y in Flat(o.Body)) yield return y; }
                else yield return x;
            }
        }

        private void MatchBody(IEnumerable<SysmlMember> body, Element? owner)
        {
            var members = Flat(body).ToList();
            var decls = members.OfType<SysmlDecl>().ToList();
            if (owner != null)
            {
                var free = Managed(owner).Where(c => !_byElement.ContainsKey(c)).ToList();
                var pending = new List<(SysmlDecl d, string fam)>();
                foreach (var d in decls)
                {
                    if (Map.ContainsKey(d) || DeclFamily(d, owner.Kind) is not { } fam) continue;
                    var name = Name(d);
                    var hit = free.FirstOrDefault(c => ElementFamily(c) == fam && SysmlNameIndex.ExportName(c) == name);
                    if (hit != null) { Bind(d, hit); free.Remove(hit); }
                    else pending.Add((d, fam));
                }
                // A single unmatched declaration and a single unmatched element of the same family: a rename.
                foreach (var g in pending.GroupBy(p => p.fam))
                {
                    var olds = free.Where(c => ElementFamily(c) == g.Key).ToList();
                    if (g.Count() == 1 && olds.Count == 1 && g.First().d.Id == null) { Bind(g.First().d, olds[0]); free.Remove(olds[0]); }
                }
                // Comments: by body, then a lone pair is an edit.
                var comments = members.OfType<SysmlComment>().ToList();
                if (owner.Kind != ElementKind.Requirement)
                {
                    var freeC = ManagedComments(owner).ToList();
                    foreach (var c in comments)
                        if (freeC.FirstOrDefault(x => x.Get(FieldKeys.Body) == c.Body) is { } hit) { _comments[c] = hit; freeC.Remove(hit); }
                    var rest = comments.Where(c => !_comments.ContainsKey(c)).ToList();
                    if (rest.Count == 1 && freeC.Count == 1) _comments[rest[0]] = freeC[0];
                }
            }
            foreach (var d in decls)
                MatchBody(d.Body, Map.GetValueOrDefault(d));
        }

        // -------------------------------------------------------------- apply

        public void Apply()
        {
            if (_top != null) SyncFields(_top, scope, created: false);
            else if (doc.Members.OfType<SysmlDoc>().FirstOrDefault() is { } topDoc) Set(scope, FieldKeys.Documentation, topDoc.Text);

            CreateAndUpdate(_topBody, scope);
            DeleteUnmatched(scope);
            _names = new SysmlNameIndex(m);
            Typing();
            Relationships();
            Report(_topBody, scope);
        }

        private void CreateAndUpdate(IEnumerable<SysmlMember> body, Element owner)
        {
            var members = Flat(body).ToList();
            Element? previous = null;
            foreach (var x in members)
            {
                if (x is SysmlComment c && owner.Kind != ElementKind.Requirement && Metamodel.CanOwn(owner.Kind, ElementKind.Comment))
                {
                    if (_comments.TryGetValue(c, out var ce)) { Set(ce, FieldKeys.Body, c.Body); }
                    else
                    {
                        var ne = m.Create(owner, ElementKind.Comment);
                        m.Set(ne, FieldKeys.Body, c.Body);
                        _comments[c] = ne;
                        Change(SysmlChangeKind.Create, $"Add comment in {owner.DisplayName}");
                    }
                    continue;
                }
                if (x is not SysmlDecl d) continue;
                if (DeclFamily(d, owner.Kind) is not { } fam) continue;

                var created = false;
                if (!Map.TryGetValue(d, out var e))
                {
                    var kind = KindFor(fam, d, owner.Kind);
                    var target = owner;
                    if (!Metamodel.CanOwn(owner.Kind, kind))
                    {
                        target = m.DefaultOwnerFor(owner, kind);
                        if (!Metamodel.CanOwn(target.Kind, kind))
                        {
                            Diag(SysmlSeverity.Warning, $"A {Metamodel.DisplayName(owner.Kind)} cannot contain a {Metamodel.DisplayName(kind)}; '{Name(d)}' was skipped", d.NameSpan);
                            continue;
                        }
                        Diag(SysmlSeverity.Info, $"'{Name(d)}' was placed in {target.DisplayName}: a {Metamodel.DisplayName(owner.Kind)} cannot contain a {Metamodel.DisplayName(kind)}", d.NameSpan);
                    }
                    var index = previous != null && ReferenceEquals(previous.Owner, target) ? target.OwnedElements.ToList().IndexOf(previous) + 1 : -1;
                    var name = fam is "decision" or "fork" ? d.Name : Name(d);
                    e = m.Create(target, kind, string.IsNullOrEmpty(name) ? null : name, index);
                    Bind(d, e);
                    _created.Add(d);
                    created = true;
                    Change(SysmlChangeKind.Create, $"Create {Metamodel.DisplayName(kind)} {e.DisplayName}");
                }
                else if (!ReferenceEquals(e.Owner, owner) && e.Owner != null && Metamodel.CanOwn(owner.Kind, e.Kind) && !ReferenceEquals(e, owner) && !e.IsAncestorOf(owner))
                {
                    var from = e.Owner.DisplayName;
                    m.Move(e, owner);
                    Change(SysmlChangeKind.Move, $"Move {e.DisplayName} from {from} to {owner.DisplayName}");
                }
                SyncFields(d, e, created);
                previous = e;
                CreateAndUpdate(d.Body, e);
            }
        }

        private void SyncFields(SysmlDecl d, Element e, bool created)
        {
            // name
            if (e.Kind is ElementKind.DecisionNode or ElementKind.ForkNode)
            {
                if (d.Name != null && d.Name != SysmlNameIndex.ExportName(e)) Rename(e, d.Name);
            }
            else if (e.Kind != ElementKind.Model || d.Name != null)
            {
                var name = Name(d);
                if (name.Length > 0 && name != e.Name) Rename(e, name);
            }

            // documentation / requirement text
            var docs = Flat(d.Body).OfType<SysmlDoc>().Select(x => x.Text).ToList();
            var docText = docs.Count == 0 ? null : string.Join("\n\n", docs);
            if (e.Kind == ElementKind.Requirement)
            {
                Set(e, FieldKeys.Text, docText);
                var notes = d.Body.OfType<SysmlComment>().Select(c => c.Body).ToList();
                Set(e, FieldKeys.Documentation, notes.Count == 0 ? null : string.Join("\n\n", notes));
                if (d.ShortName != null) Set(e, FieldKeys.RequirementId, d.ShortName);
                else if (!created) Set(e, FieldKeys.RequirementId, null);
                var subject = d.Decls.FirstOrDefault(x => x.Keyword == "subject");
                Set(e, SysMLv2Text.SubjectField, subject == null ? null
                    : SysMLv2Text.N(subject.Name ?? "subject") + (subject.Types.Count > 0 ? " : " + string.Join(", ", subject.Types.Select(t => t.Text)) : ""));
            }
            else Set(e, FieldKeys.Documentation, docText);

            if (Metamodel.IsClassifier(e.Kind) && e.Kind is not (ElementKind.Requirement or ElementKind.Table or ElementKind.Stereotype))
                Set(e, FieldKeys.IsAbstract, d.IsAbstract ? "true" : null);
            if (e.Kind is ElementKind.Property or ElementKind.Port)
                Set(e, FieldKeys.Multiplicity, string.IsNullOrEmpty(d.Multiplicity) ? "1" : d.Multiplicity);
            if (e.Kind == ElementKind.Property) Set(e, FieldKeys.DefaultValue, d.Value);
            if (e.Kind == ElementKind.Port) Set(e, FieldKeys.Direction, d.Direction);
            if (e.Kind == ElementKind.ConstraintBlock)
            {
                var exprs = d.Body.OfType<SysmlExpression>().Select(x => x.Text).ToList();
                Set(e, FieldKeys.Body, exprs.Count == 0 ? null : string.Join("\n", exprs));
            }
        }

        private void DeleteUnmatched(Element owner)
        {
            var doomed = new List<Element>();
            void Walk(Element el)
            {
                foreach (var c in Managed(el).ToList())
                {
                    if (_byElement.ContainsKey(c)) Walk(c);
                    else doomed.Add(c);
                }
                foreach (var c in ManagedComments(el))
                    if (!_comments.ContainsValue(c)) doomed.Add(c);
            }
            Walk(owner);
            // Never delete something the text still maps (e.g. moved out of a deleted parent beforehand).
            doomed = doomed.Where(x => !x.Descendants().Any(_byElement.ContainsKey)).ToList();
            foreach (var x in doomed.Where(m.Contains).ToList())
            {
                var label = x.Kind == ElementKind.Comment ? "comment" : x.DisplayName;
                m.Delete(x);
                Change(SysmlChangeKind.Delete, $"Delete {Metamodel.DisplayName(x.Kind)} {label}");
            }
        }

        // -------------------------------------------------------------- typing & specialization

        private void Typing()
        {
            foreach (var (d, e) in Map.ToList())
            {
                if (!m.Contains(e)) continue;
                var context = e.Owner ?? m.Root;
                if (Metamodel.IsTypedFeature(e.Kind))
                {
                    string? value = null;
                    if (d.Types.FirstOrDefault() is { } t)
                    {
                        var hit = L(t, context, x => Metamodel.IsClassifier(x.Kind));
                        if (hit != null) value = hit.Id;
                        else if (Metamodel.PrimitiveTypes.Contains(t.Simple) && (t.Segments.Count == 1 || t.Segments[0] == "ScalarValues")) value = t.Simple;
                        else value = t.Text;
                    }
                    if (e.TypeRef != (value ?? ""))
                    {
                        m.Set(e, FieldKeys.Type, value);
                        Change(SysmlChangeKind.Update, $"Retype {e.DisplayName} to {(value != null && m.Find(value) is { } te ? te.DisplayName : value ?? "(none)")}");
                    }
                }
                if (e.Kind == ElementKind.Action)
                {
                    var b = d.Types.FirstOrDefault() is { } t ? L(t, context, x => x.Kind == ElementKind.Activity) : null;
                    if (d.Types.Count > 0 && b == null) Diag(SysmlSeverity.Warning, $"Unknown action definition '{d.Types[0].Text}'", d.Types[0].Span);
                    Set(e, FieldKeys.Behavior, b?.Id);
                }
                if (Metamodel.IsClassifier(e.Kind) && e.Kind != ElementKind.Model)
                {
                    var wanted = new List<Element>();
                    var refs = d.Specializes.Concat(!d.IsDef && e.Kind is ElementKind.Block ? d.Types : []).ToList();
                    foreach (var r in refs)
                    {
                        var g = L(r, context, x => x.Kind == e.Kind && !ReferenceEquals(x, e))
                                ?? L(r, context, x => Metamodel.IsClassifier(x.Kind) && !ReferenceEquals(x, e));
                        if (g == null) { Diag(SysmlSeverity.Warning, $"Unknown supertype '{r.Text}' of '{e.Name}'", r.Span); continue; }
                        wanted.Add(g);
                    }
                    var existing = m.Outgoing(e, ElementKind.Generalization).ToList();
                    foreach (var r in existing.Where(r => !wanted.Contains(r.Target)).ToList())
                    {
                        m.Delete(r);
                        Change(SysmlChangeKind.Disconnect, $"{e.DisplayName} no longer specializes {r.Target.DisplayName}");
                    }
                    foreach (var g in wanted.Distinct().Where(g => !existing.Any(r => ReferenceEquals(r.Target, g))))
                        TryConnect(ElementKind.Generalization, e, g, null, d.Specializes.FirstOrDefault()?.Span ?? d.NameSpan,
                                   $"{e.DisplayName} specializes {g.DisplayName}");
                }
            }
        }

        // -------------------------------------------------------------- relationships

        private sealed record Fact(ElementKind Kind, Element Source, Element Target, Element Owner, TextSpan Span)
        {
            public string? Name { get; init; }
            public string? Guard { get; init; }
            public string? Effect { get; init; }
            public string? Trigger { get; init; }
            public string? Sort { get; init; }
            public string? SourcePart { get; init; }
            public string? TargetPart { get; init; }
            public string? Item { get; init; }
        }

        private static ElementKind Family(ElementKind k) => k == ElementKind.ObjectFlow ? ElementKind.ControlFlow : k;

        private static readonly ElementKind[] OwnedKinds =
        [
            ElementKind.Satisfy, ElementKind.Allocate, ElementKind.Dependency, ElementKind.DeriveReqt, ElementKind.Refine, ElementKind.Trace,
            ElementKind.Message, ElementKind.Connector, ElementKind.Binding,
        ];

        /// <summary>Relationships the exporter writes for the elements in this scope (and which the text therefore owns).</summary>
        private List<Relationship> ExistingFacts()
        {
            var set = new HashSet<Relationship>(ReferenceEqualityComparer.Instance);
            var owners = new List<Element> { scope };
            owners.AddRange(Map.Values.Where(m.Contains));
            foreach (var e in owners.Distinct())
            {
                foreach (var r in e.OwnedElements.OfType<Relationship>())
                {
                    if (OwnedKinds.Contains(r.Kind)) set.Add(r);
                    if (r.Kind is ElementKind.Transition or ElementKind.ControlFlow or ElementKind.ObjectFlow
                        && e.Kind is ElementKind.Activity or ElementKind.StateMachine or ElementKind.State) set.Add(r);
                }
                if (e.Kind == ElementKind.TestCase) foreach (var r in m.Outgoing(e, ElementKind.Verify)) set.Add(r);
                if (SysMLv2Text.Keyword(e.Kind) != null)
                    foreach (var r in m.Outgoing(e).Where(r => r.Kind is ElementKind.Include or ElementKind.Extend)) set.Add(r);
            }
            return set.ToList();
        }

        private void Relationships()
        {
            var wanted = new List<Fact>();
            CollectFacts(_topBody, scope, wanted);
            var existing = ExistingFacts();

            var pool = existing.GroupBy(r => (Family(r.Kind), r.Source, r.Target))
                               .ToDictionary(g => g.Key, g => g.ToList());
            var keep = new HashSet<Relationship>(ReferenceEqualityComparer.Instance);
            var matched = new List<(Fact f, Relationship r)>();
            var fresh = new List<Fact>();
            foreach (var f in wanted)
            {
                if (pool.TryGetValue((Family(f.Kind), f.Source, f.Target), out var list) && list.Count > 0)
                {
                    var r = list.FirstOrDefault(x => Same(f, x)) ?? list[0];
                    list.Remove(r);
                    keep.Add(r);
                    matched.Add((f, r));
                }
                else fresh.Add(f);
            }

            foreach (var r in existing.Where(r => !keep.Contains(r) && m.Contains(r)))
            {
                var label = Describe(r.Kind, r.Source, r.Target);
                m.Delete(r);
                Change(SysmlChangeKind.Disconnect, "Remove " + label);
            }
            foreach (var (f, r) in matched) SetRelFields(r, f);
            foreach (var f in fresh)
            {
                if (TryConnect(f.Kind, f.Source, f.Target, f.Owner, f.Span, "Add " + Describe(f.Kind, f.Source, f.Target)) is { } r)
                    SetRelFields(r, f);
            }
        }

        private static string Describe(ElementKind k, Element s, Element t) =>
            $"{Metamodel.DisplayName(k).ToLowerInvariant()} {s.DisplayName} → {t.DisplayName}";

        private static bool Same(Fact f, Relationship r) =>
            (f.Name ?? "") == r.Name && (f.Guard ?? "") == r.Get(FieldKeys.Guard) && (f.SourcePart ?? "") == r.Get(FieldKeys.SourcePart)
            && (f.TargetPart ?? "") == r.Get(FieldKeys.TargetPart);

        private void SetRelFields(Relationship r, Fact f)
        {
            if (f.Kind is ElementKind.Transition or ElementKind.Message or ElementKind.Connector) Set(r, FieldKeys.Name, f.Name);
            if (f.Kind is ElementKind.Transition or ElementKind.ControlFlow) Set(r, FieldKeys.Guard, f.Guard);
            if (f.Kind == ElementKind.Transition)
            {
                Set(r, FieldKeys.Effect, f.Effect);
                Set(r, SysMLv2Text.TriggerField, f.Trigger);
            }
            if (f.Kind == ElementKind.Message) Set(r, FieldKeys.MessageSort, f.Sort ?? "synchCall");
            if (f.Kind is ElementKind.Connector or ElementKind.Binding)
            {
                Set(r, FieldKeys.SourcePart, f.SourcePart);
                Set(r, FieldKeys.TargetPart, f.TargetPart);
            }
            if (f.Kind == ElementKind.Connector)
            {
                var item = f.Item == null ? null : _names.Lookup(f.Item.Split("::"), r.Owner ?? scope, x => Metamodel.IsClassifier(x.Kind))?.Id ?? f.Item;
                if (item == null || m.Find(r.Get(FieldKeys.ItemFlow)) is not { } cur || cur.Id != item) Set(r, FieldKeys.ItemFlow, item);
            }
        }

        private Relationship? TryConnect(ElementKind kind, Element s, Element t, Element? owner, TextSpan span, string description)
        {
            try
            {
                Relationship r;
                if (kind is ElementKind.Connector or ElementKind.Binding)
                {
                    if (Metamodel.CheckEnds(kind, s.Kind, t.Kind) is { } problem) throw new InvalidOperationException(problem);
                    var o = owner != null && Metamodel.CanOwn(owner.Kind, kind) ? owner : m.DefaultOwnerFor(owner ?? s.Owner ?? m.Root, kind);
                    r = m.Add(o, new Relationship(kind, s, t));
                }
                else r = m.Connect(kind, s, t, owner != null && Metamodel.CanOwn(owner.Kind, kind) ? owner : null);
                Change(SysmlChangeKind.Connect, description);
                return r;
            }
            catch (InvalidOperationException ex)
            {
                Diag(SysmlSeverity.Warning, ex.Message, span);
                return null;
            }
        }

        private Element? Resolve(SysmlRef? r, Element context, string what)
        {
            if (r == null) return null;
            var hit = L(r, context, null);
            if (hit == null) Diag(SysmlSeverity.Warning, $"Unknown {what} '{r.Text}'", r.Span);
            return hit;
        }

        private (Element? End, string? Part) ResolveEnd(SysmlRef r, Element context)
        {
            if (!r.IsChain) return (Resolve(r, context, "connector end"), null);
            var path = _names.LookupChain(r.Segments, context);
            if (path == null && _renamedFrom.TryGetValue(r.Segments[0], out var renamed))
                path = _names.LookupChain([SysmlNameIndex.ExportName(renamed), .. r.Segments.Skip(1)], context);
            if (path == null) { Diag(SysmlSeverity.Warning, $"Unknown connector end '{r.Text}'", r.Span); return (null, null); }
            return (path[^1], path.Count > 1 ? path[^2].Id : null);
        }

        /// <summary>Resolves a succession end inside a behavior; <c>start</c> / <c>done</c> address (and create) its initial / final node.</summary>
        private Element? ResolveNode(SysmlRef r, Element behavior, bool create)
        {
            if (r.IsKeyword)
            {
                var start = r.Simple == "start";
                var host = behavior;
                var found = host.OwnedElements.FirstOrDefault(c => start ? c.Kind is ElementKind.InitialNode or ElementKind.InitialState
                                                                       : c.Kind is ElementKind.ActivityFinalNode or ElementKind.FinalState);
                if (found != null || !create) return found;
                var kind = host.Kind == ElementKind.Activity
                    ? start ? ElementKind.InitialNode : ElementKind.ActivityFinalNode
                    : start ? ElementKind.InitialState : ElementKind.FinalState;
                if (!Metamodel.CanOwn(host.Kind, kind)) return null;
                var n = m.Create(host, kind);
                Change(SysmlChangeKind.Create, $"Create {Metamodel.DisplayName(kind)} in {host.DisplayName}");
                return n;
            }
            if (!r.IsChain && r.Segments.Count == 1 && FindNode(behavior, r.Simple) is { } node) return node;
            return L(r, behavior, null);
        }

        private void CollectFacts(IEnumerable<SysmlMember> body, Element ctx, List<Fact> into)
        {
            foreach (var x in Flat(body))
            {
                switch (x)
                {
                    case SysmlDecl d:
                        if (d.Connect is { } conn && d.Keyword is "connection" or "interface")
                            AddConnector(into, ctx, conn.From, conn.To, d.Name, null, x.Span);
                        if (Map.TryGetValue(d, out var e) && m.Contains(e)) CollectFacts(d.Body, e, into);
                        break;
                    case SysmlRelation r:
                        AddRelation(into, ctx, r);
                        break;
                    case SysmlSuccession s:
                    {
                        var kind = ctx.Kind switch
                        {
                            ElementKind.Activity => ElementKind.ControlFlow,
                            ElementKind.StateMachine or ElementKind.State => ElementKind.Transition,
                            _ => (ElementKind?)null,
                        };
                        if (kind == null) { Diag(SysmlSeverity.Warning, $"Successions are only supported inside action and state definitions", s.Span); break; }
                        if (s.First == null || s.Then == null) break;
                        var a = ResolveNode(s.First, ctx, create: true);
                        var b = ResolveNode(s.Then, ctx, create: true);
                        if (a == null) { Diag(SysmlSeverity.Warning, $"Unknown {(kind == ElementKind.Transition ? "state" : "action")} '{s.First.Text}'", s.First.Span); break; }
                        if (b == null) { Diag(SysmlSeverity.Warning, $"Unknown {(kind == ElementKind.Transition ? "state" : "action")} '{s.Then.Text}'", s.Then.Span); break; }
                        into.Add(new Fact(kind.Value, a, b, a.Owner ?? ctx, s.Span) { Name = s.Name, Guard = s.Guard, Effect = s.Effect, Trigger = s.Trigger });
                        break;
                    }
                }
            }
        }

        private void AddConnector(List<Fact> into, Element ctx, SysmlRef from, SysmlRef to, string? name, string? item, TextSpan span, ElementKind kind = ElementKind.Connector)
        {
            var (a, ap) = ResolveEnd(from, ctx);
            var (b, bp) = ResolveEnd(to, ctx);
            if (a == null || b == null) return;
            into.Add(new Fact(kind, a, b, ctx, span) { Name = name, SourcePart = ap, TargetPart = bp, Item = item });
        }

        private void AddRelation(List<Fact> into, Element ctx, SysmlRelation r)
        {
            switch (r.Keyword)
            {
                case "connect": AddConnector(into, ctx, r.Source!, r.Target!, null, null, r.Span); return;
                case "flow": AddConnector(into, ctx, r.Source!, r.Target!, r.Name, r.Detail, r.Span); return;
                case "bind": AddConnector(into, ctx, r.Source!, r.Target!, null, null, r.Span, ElementKind.Binding); return;
            }
            var kind = r.Keyword switch
            {
                "satisfy" => ElementKind.Satisfy,
                "verify" => ElementKind.Verify,
                "allocate" => ElementKind.Allocate,
                "dependency" => ElementKind.Dependency,
                "derivation" => ElementKind.DeriveReqt,
                "refinement" => ElementKind.Refine,
                "trace" => ElementKind.Trace,
                "include" => ElementKind.Include,
                "extend" => ElementKind.Extend,
                "message" => ElementKind.Message,
                _ => (ElementKind?)null,
            };
            if (kind == null) return;
            if (kind == ElementKind.Verify && ctx.Kind != ElementKind.TestCase && r.Source == null)
            {
                Diag(SysmlSeverity.Info, "'verify' is only mapped inside verification definitions", r.Span);
                return;
            }
            var target = Resolve(r.Target, ctx, kind == ElementKind.Satisfy || kind == ElementKind.Verify ? "requirement" : "element");
            var source = r.Source == null ? ctx : Resolve(r.Source, ctx, "element");
            if (source == null || target == null) return;
            var owner = kind is ElementKind.Verify or ElementKind.Include or ElementKind.Extend ? m.DefaultOwnerFor(source.Owner ?? ctx, kind.Value) : ctx;
            into.Add(new Fact(kind.Value, source, target, owner, r.Span) { Name = r.Keyword == "message" ? r.Name : null, Sort = r.Detail });
        }

        // -------------------------------------------------------------- reporting & navigation

        private void Report(IEnumerable<SysmlMember> body, Element? ctx)
        {
            foreach (var x in body)
            {
                switch (x)
                {
                    case SysmlUnsupported u:
                        Diag(SysmlSeverity.Info, $"'{u.Keyword}' is not represented in the Meridian model and is ignored", u.Span);
                        break;
                    case SysmlDecl d:
                        var mapped = Map.ContainsKey(d) || d.Keyword is "objective" or "subject" || d.Connect != null;
                        if (!mapped && ctx != null && !ReferenceEquals(d, _top))
                            Diag(SysmlSeverity.Warning, $"'{d.Keyword}{(d.IsDef ? " def" : "")}'{(d.Name != null ? $" {d.Name}" : "")} has no Meridian equivalent inside a {Metamodel.DisplayName(ctx.Kind)} and is ignored", d.NameSpan);
                        Report(d.Body, Map.GetValueOrDefault(d) ?? (mapped ? ctx : null));
                        break;
                }
            }
        }

        public Element? ElementAt(int offset)
        {
            _names = new SysmlNameIndex(m);
            return Find(_topBody, scope, offset);
        }

        private Element? Find(IEnumerable<SysmlMember> body, Element ctx, int offset)
        {
            foreach (var x in Flat(body))
            {
                if (!x.Span.Contains(offset) && x is not SysmlDecl) continue;
                switch (x)
                {
                    case SysmlDecl d:
                    {
                        if (!d.Span.Contains(offset)) continue;
                        var e = Map.GetValueOrDefault(d);
                        if (d.NameSpan.Contains(offset) && e != null) return e;
                        var context = e?.Owner ?? ctx;
                        foreach (var t in d.Types.Where(t => t.Span.Contains(offset)))
                            return _names.Lookup(t, context, y => Metamodel.IsClassifier(y.Kind) || y.Kind == ElementKind.Activity) ?? _names.Lookup(t, context);
                        foreach (var t in d.Specializes.Concat(d.Redefines).Where(t => t.Span.Contains(offset)))
                            return _names.Lookup(t, context);
                        if (d.Connect is { } c)
                            foreach (var t in new[] { c.From, c.To }.Where(t => t.Span.Contains(offset)))
                                return t.IsChain ? _names.LookupChain(t.Segments, ctx)?.LastOrDefault() : _names.Lookup(t, ctx);
                        if (e != null && Find(d.Body, e, offset) is { } inner) return inner;
                        return e;
                    }
                    case SysmlRelation r:
                        foreach (var t in new[] { r.Source, r.Target }.OfType<SysmlRef>().Where(t => t.Span.Contains(offset)))
                            return t.IsChain ? _names.LookupChain(t.Segments, ctx)?.LastOrDefault() : _names.Lookup(t, ctx);
                        break;
                    case SysmlSuccession s:
                        foreach (var t in new[] { s.First, s.Then }.OfType<SysmlRef>().Where(t => t.Span.Contains(offset)))
                            return ResolveNode(t, ctx, create: false);
                        break;
                }
            }
            return null;
        }

        // -------------------------------------------------------------- helpers

        /// <summary>Elements renamed by this edit, by their old name: references still using it follow the rename.</summary>
        private readonly Dictionary<string, Element> _renamedFrom = new(StringComparer.Ordinal);

        private Element? L(SysmlRef r, Element context, Func<Element, bool>? filter)
        {
            var hit = _names.Lookup(r, context, filter);
            if (hit == null && !r.IsChain && _renamedFrom.TryGetValue(r.Simple, out var renamed) && m.Contains(renamed) && (filter == null || filter(renamed)))
                hit = renamed;
            return hit;
        }

        private void Rename(Element e, string name)
        {
            if (!string.IsNullOrEmpty(e.Name)) _renamedFrom.TryAdd(e.Name, e);
            var old = e.DisplayName;
            m.Rename(e, name);
            Change(SysmlChangeKind.Rename, $"Rename {old} → {name}");
        }

        private void Set(Element e, string key, string? value)
        {
            if (e.Get(key) == (value ?? "")) return;
            m.Set(e, key, value);
            if (!_createdElements.Contains(e)) Change(SysmlChangeKind.Update, $"Set {key} of {(e is Relationship r ? Describe(r.Kind, r.Source, r.Target) : e.DisplayName)}");
        }

        private HashSet<Element> _createdElements => _createdCache ??= new HashSet<Element>(_created.Select(d => Map[d]), ReferenceEqualityComparer.Instance);
        private HashSet<Element>? _createdCache;

        private void Change(SysmlChangeKind k, string text)
        {
            _createdCache = null;
            result.Changes.Add(new SysmlChange(k, text));
        }

        private void Diag(SysmlSeverity s, string message, TextSpan span) => result.Diagnostics.Add(new SysmlDiagnostic(s, message, span));
    }
}
