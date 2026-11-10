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
/// <para>Text with no Meridian equivalent (imports, aliases, views, calc results, requirement constraints …) is kept
/// verbatim on the nearest mapped owner in <see cref="SysMLv2Text.UnmappedField"/> and written back by the exporter, so
/// round trips don't lose content. Header details Meridian doesn't model (subsetting, redefinition, prefixes, keywords …)
/// are kept in <c>sysml:*</c> fields the same way.</para>
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

    /// <summary><c>#tag</c> words with a meaning of their own (not stereotype applications).</summary>
    internal static readonly HashSet<string> SpecialTags = new(StringComparer.Ordinal)
        { "actor", "derivation", "refinement", "trace", "extend", "derive", "refine", "reply", "async" };

    // ====================================================================================================

    private sealed class Mapper(ModelRepository m, Element scope, SysmlDocument doc, SysmlSyncResult result)
    {
        public readonly Dictionary<SysmlDecl, Element> Map = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Element, SysmlDecl> _byElement = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<SysmlComment, Element> _comments = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SysmlDecl> _created = new(ReferenceEqualityComparer.Instance);
        /// <summary>Members represented in the model other than through <see cref="Map"/> (relationships, fields …).</summary>
        private readonly HashSet<SysmlMember> _mapped = new(ReferenceEqualityComparer.Instance);
        private SysmlDecl? _top;
        private List<SysmlMember> _topBody = new();
        private SysmlNameIndex _names = null!;
        private HashSet<string>? _docNames;
        private static SysMLv2Library Lib => SysMLv2Library.Standard;

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
            if (d.OpaqueHeader || d.Types.Count > 1 || d.EffectiveName == null) return null;
            var kw = d.Keyword;
            if (d.IsDef)
                return kw switch
                {
                    "part" => d.Tags.Contains("actor") ? "actor" : "block",
                    "item" or "connection" or "allocation" or "flow" => "block",
                    "port" or "interface" => "portdef",
                    "attribute" => "valuetype",
                    "enum" => "enum",
                    "constraint" or "calc" => "constraint",
                    "requirement" or "concern" => "requirement",
                    "verification" or "analysis" or "case" => "testcase",
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
                "accept" or "send" or "assign" or "if" or "while" or "loop" or "for" or "terminate" when owner == ElementKind.Activity => "action",
                "state" or "exhibit state" when owner is ElementKind.StateMachine or ElementKind.State => "state",
                "enum" when owner == ElementKind.Enumeration => "literal",
                "requirement" or "concern" when owner is ElementKind.Package or ElementKind.Model or ElementKind.Requirement => "requirement",
                "verification" when owner is ElementKind.Package or ElementKind.Model => "testcase",
                "part" or "item" or "ref" when owner == ElementKind.Interaction && !d.ImplicitKeyword => "lifeline",
                "part" or "item" when owner is ElementKind.Package or ElementKind.Model => "block",
                "part" or "item" or "attribute" or "port" or "ref" or "constraint" or "occurrence"
                    when Metamodel.CanOwn(owner, ElementKind.Property) || Metamodel.CanOwn(owner, ElementKind.Port) => "feature",
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

        private static bool IsPlain(SysmlComment c) => c.Name == null && c.About.Count == 0;

        // -------------------------------------------------------------- matching

        public void Match()
        {
            var decls = doc.Members.OfType<SysmlDecl>().ToList();
            var otherTop = doc.Members.Any(x => x is SysmlRelation or SysmlSuccession or SysmlImport or SysmlUnsupported or SysmlMetadata);
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
                if (IsVerifyObjective(x)) { foreach (var y in ((SysmlDecl)x).Body) yield return y; }
                else yield return x;
            }
        }

        /// <summary><c>objective { verify r; }</c>: the form the exporter writes for a test case's verify relationships.</summary>
        private static bool IsVerifyObjective(SysmlMember x) =>
            x is SysmlDecl { Keyword: "objective", Name: null, Types.Count: 0, Modifiers.Count: 0 } o
            && o.Body.All(b => b is SysmlRelation { Keyword: "verify", Complex: false } or SysmlComment);

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
                var comments = members.OfType<SysmlComment>().Where(IsPlain).ToList();
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
            CheckImports(doc.Members);
            StoreUnmapped();
        }

        private void CreateAndUpdate(IEnumerable<SysmlMember> body, Element owner)
        {
            var members = Flat(body).ToList();
            Element? previous = null;
            foreach (var x in members)
            {
                if (x is SysmlComment c && IsPlain(c) && owner.Kind != ElementKind.Requirement && Metamodel.CanOwn(owner.Kind, ElementKind.Comment))
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
                            Diag(SysmlSeverity.Info, $"A {Metamodel.DisplayName(owner.Kind)} cannot contain a {Metamodel.DisplayName(kind)}; '{Name(d)}' is kept as text", d.NameSpan);
                            continue;
                        }
                        Diag(SysmlSeverity.Info, $"'{Name(d)}' was placed in {target.DisplayName}: a {Metamodel.DisplayName(owner.Kind)} cannot contain a {Metamodel.DisplayName(kind)}", d.NameSpan);
                    }
                    var index = previous != null && ReferenceEquals(previous.Owner, target) ? target.OwnedElements.ToList().IndexOf(previous) + 1 : -1;
                    var name = fam is "decision" or "fork" || d.Name == null && d.SyntheticName != null && fam == "action" ? d.Name : Name(d);
                    e = m.Create(target, kind, string.IsNullOrEmpty(name) ? null : name, index);
                    if (fam == "action" && d.Name == null && d.SyntheticName != null && e.Name.Length > 0) m.Set(e, FieldKeys.Name, null);
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
            else if (e.Kind == ElementKind.Action && SysMLv2Parser.ActionBodyKeywords.Contains(d.Keyword) && d.Name == null)
            {
                if (e.Name.Length > 0) Rename(e, "");
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
                var notes = d.Body.OfType<SysmlComment>().Where(IsPlain).Select(c => c.Body).ToList();
                Set(e, FieldKeys.Documentation, notes.Count == 0 ? null : string.Join("\n\n", notes));
                if (d.ShortName != null) Set(e, FieldKeys.RequirementId, d.ShortName);
                else if (!created) Set(e, FieldKeys.RequirementId, null);
                var subject = d.Decls.FirstOrDefault(x => x.Keyword == "subject" && IsSimpleSubject(x));
                if (subject != null) _mapped.Add(subject);
                Set(e, SysMLv2Text.SubjectField, subject == null ? null
                    : SysMLv2Text.N(subject.Name ?? "subject") + (subject.Types.Count > 0 ? " : " + string.Join(", ", subject.Types.Select(t => t.Text)) : ""));
            }
            else Set(e, FieldKeys.Documentation, docText);

            if (Metamodel.IsClassifier(e.Kind) && e.Kind is not (ElementKind.Requirement or ElementKind.Table or ElementKind.Stereotype))
                Set(e, FieldKeys.IsAbstract, d.IsAbstract ? "true" : null);
            if (e.Kind is ElementKind.Property or ElementKind.Port)
                Set(e, FieldKeys.Multiplicity, string.IsNullOrEmpty(d.Multiplicity) ? "1" : d.Multiplicity);
            else Set(e, SysMLv2Text.MultiplicityField, d.Multiplicity);
            if (e.Kind == ElementKind.Property) Set(e, FieldKeys.DefaultValue, d.Value);
            else Set(e, SysMLv2Text.ValueField, d.Value);
            Set(e, SysMLv2Text.ValueOpField, d.Value != null && d.ValueOp != "=" ? d.ValueOp : null);
            if (e.Kind is ElementKind.Port or ElementKind.Property) Set(e, FieldKeys.Direction, d.Direction);
            if (e.Kind == ElementKind.ConstraintBlock)
            {
                var exprs = d.Body.OfType<SysmlExpression>().ToList();
                foreach (var x in exprs) _mapped.Add(x);
                Set(e, FieldKeys.Body, exprs.Count == 0 ? null : string.Join("\n", exprs.Select(x => x.Text)));
            }
            if (e.Kind is ElementKind.State or ElementKind.StateMachine)
            {
                foreach (var (kw, key) in new[] { ("entry", FieldKeys.Entry), ("do", FieldKeys.DoActivity), ("exit", FieldKeys.Exit) })
                {
                    var a = d.Decls.FirstOrDefault(x => x.Keyword == kw && x.Raw != null && x.Tags.Count == 0 && x.Modifiers.Count == 0);
                    if (a != null) _mapped.Add(a);
                    Set(e, key, a?.Raw);
                }
            }
            if (e.Kind == ElementKind.Action) Set(e, SysMLv2Text.RawField, d.Raw);

            // header details Meridian doesn't model, kept for the exporter
            var prefix = d.Modifiers.Where(x => x is not ("public" or "private" or "protected") && !(x == "abstract" && Metamodel.IsClassifier(e.Kind))).ToList();
            Set(e, SysMLv2Text.PrefixField, prefix.Count == 0 ? null : string.Join(" ", prefix));
            var vis = d.Visibility;
            if (vis != null) Set(e, FieldKeys.Visibility, vis);
            else if (e.Get(FieldKeys.Visibility) is "private" or "protected") Set(e, FieldKeys.Visibility, null);
            if (e.Kind != ElementKind.Requirement) Set(e, SysMLv2Text.ShortNameField, d.ShortName);
            Set(e, SysMLv2Text.FlagsField, d.Flags.Count == 0 ? null : string.Join(" ", d.Flags));
            Set(e, SysMLv2Text.RedefinesField, d.Redefines.Count == 0 ? null : string.Join(", ", d.Redefines.Select(RefText)));
            Set(e, SysMLv2Text.UnnamedField, d.Name == null && d.Redefines.Count > 0 ? "true" : null);
            Set(e, SysMLv2Text.ReferencesField, d.References.Count == 0 ? null : string.Join(", ", d.References.Select(RefText)));
            Set(e, SysMLv2Text.ConjugatedField, d.Types.Count > 0 && d.Types[0].Conjugated ? "true" : null);

            // metadata: #Tag prefixes and @Tag { name = value; } annotations → stereotypes and tagged values
            var stereotypes = d.Tags.Where(t => !SpecialTags.Contains(t)).ToList();
            var annotations = new List<string>();
            var claimed = SysMLv2Text.AnnotationKeys(e).ToHashSet(StringComparer.Ordinal);
            foreach (var md in d.Body.OfType<SysmlMetadata>().Where(x => !x.Complex))
            {
                _mapped.Add(md);
                var n = md.Type.Text;
                if (!stereotypes.Contains(n)) stereotypes.Add(n);
                annotations.Add(n + ":" + string.Join(",", md.Values.Select(v => v.Name)));
                foreach (var (k, v) in md.Values)
                {
                    claimed.Remove(k);
                    Set(e, FieldKeys.TagPrefix + k, v);
                }
            }
            foreach (var k in claimed) Set(e, FieldKeys.TagPrefix + k, null);   // removed from an annotation
            Set(e, FieldKeys.Stereotypes, stereotypes.Count == 0 ? null : string.Join(",", stereotypes));
            Set(e, SysMLv2Text.MetadataField, annotations.Count == 0 ? null : string.Join(";", annotations));
        }

        private static bool IsSimpleSubject(SysmlDecl s) => !s.HasBody && s.Value == null && s.Modifiers.Count == 0 && s.Specializes.Count == 0
                                                             && s.Redefines.Count == 0 && s.Multiplicity == null && s.Tags.Count == 0;

        private static string RefText(SysmlRef r) => (r.Conjugated ? "~" : "") + string.Join(r.IsChain ? "." : "::", r.Segments.Select(SysMLv2Text.N));

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

        /// <summary>True when a reference names something: the model, a declaration in this text, or the standard library.</summary>
        private bool Known(SysmlRef r, Element context)
        {
            if (r.IsKeyword) return true;
            if (L(r, context, null) != null) return true;
            _docNames ??= doc.AllDecls().SelectMany(d => new[] { d.Name, d.ShortName, d.EffectiveName })
                             .Concat(Aliases(doc.Members)).OfType<string>().ToHashSet(StringComparer.Ordinal);
            if (_docNames.Contains(r.IsChain ? r.Segments[0] : r.Segments[^1])) return true;
            if (Metamodel.PrimitiveTypes.Contains(r.Simple) && (r.Segments.Count == 1 || r.Segments[0] == "ScalarValues")) return true;
            return Lib.Contains(r.IsChain ? [r.Segments[0]] : r.Segments);
        }

        private void Typing()
        {
            foreach (var (d, e) in Map.ToList())
            {
                if (!m.Contains(e)) continue;
                var context = e.Owner ?? m.Root;
                string? typeText = null;
                if (Metamodel.IsTypedFeature(e.Kind))
                {
                    string? value = null;
                    if (d.Types.FirstOrDefault() is { } t)
                    {
                        var hit = L(t, context, x => Metamodel.IsClassifier(x.Kind));
                        if (hit != null) value = hit.Id;
                        else if (Metamodel.PrimitiveTypes.Contains(t.Simple) && (t.Segments.Count == 1 || t.Segments[0] == "ScalarValues")) value = t.Simple;
                        else
                        {
                            value = t.Text;
                            if (!Known(t, context)) Diag(SysmlSeverity.Warning, $"Unknown type '{t.Text}'", t.Span);
                        }
                    }
                    if (e.TypeRef != (value ?? ""))
                    {
                        m.Set(e, FieldKeys.Type, value);
                        Change(SysmlChangeKind.Update, $"Retype {e.DisplayName} to {(value != null && m.Find(value) is { } te ? te.DisplayName : value ?? "(none)")}");
                    }
                }
                else if (e.Kind == ElementKind.Action)
                {
                    var b = d.Types.FirstOrDefault() is { } t ? L(t, context, x => x.Kind == ElementKind.Activity) : null;
                    if (d.Types.Count > 0 && b == null)
                    {
                        typeText = RefText(d.Types[0]);
                        if (!Known(d.Types[0], context)) Diag(SysmlSeverity.Warning, $"Unknown action definition '{d.Types[0].Text}'", d.Types[0].Span);
                    }
                    Set(e, FieldKeys.Behavior, b?.Id);
                }
                var subsets = new List<SysmlRef>();
                var extraSupers = new List<SysmlRef>();
                if (Metamodel.IsClassifier(e.Kind) && e.Kind != ElementKind.Model)
                {
                    var wanted = new List<Element>();
                    var blockUsage = !d.IsDef && e.Kind is ElementKind.Block;
                    var refs = d.IsDef ? d.Specializes.ToList() : blockUsage ? d.Types.ToList() : [];
                    if (!d.IsDef) subsets.AddRange(d.Specializes);
                    foreach (var r in refs)
                    {
                        var g = L(r, context, x => x.Kind == e.Kind && !ReferenceEquals(x, e))
                                ?? L(r, context, x => Metamodel.IsClassifier(x.Kind) && !ReferenceEquals(x, e));
                        if (g == null || Metamodel.CheckEnds(ElementKind.Generalization, e.Kind, g.Kind) != null)
                        {
                            if (!Known(r, context)) Diag(SysmlSeverity.Warning, $"Unknown supertype '{r.Text}' of '{e.Name}'", r.Span);
                            if (blockUsage) typeText = typeText == null ? RefText(r) : typeText + ", " + RefText(r);
                            else extraSupers.Add(r);
                            continue;
                        }
                        wanted.Add(g);
                    }
                    if (!blockUsage && d.Types.Count > 0)
                    {
                        typeText = string.Join(", ", d.Types.Select(RefText));
                        foreach (var t in d.Types.Where(t => !Known(t, context))) Diag(SysmlSeverity.Warning, $"Unknown type '{t.Text}'", t.Span);
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
                else if (!Metamodel.IsTypedFeature(e.Kind) && e.Kind != ElementKind.Action && d.Types.Count > 0)
                    typeText = string.Join(", ", d.Types.Select(RefText));
                else if (e.Kind != ElementKind.Model) subsets.AddRange(d.Specializes);
                if (e.Kind is ElementKind.Package or ElementKind.Model) extraSupers.AddRange(d.Specializes);

                Set(e, SysMLv2Text.TypeField, typeText);
                Set(e, SysMLv2Text.SubsetsField, subsets.Count == 0 ? null : string.Join(", ", subsets.Select(RefText)));
                Set(e, SysMLv2Text.SpecializesField, extraSupers.Count == 0 ? null : string.Join(", ", extraSupers.Select(RefText)));

                // the keyword as written, when the exporter would choose another one
                var written = d.KeywordText.Length == 0 ? SysMLv2Text.NoKeyword : d.KeywordText + (d.IsDef ? " def" : "");
                if (d.Tags.Contains("actor") && e.Kind == ElementKind.Actor) written = d.KeywordText + (d.IsDef ? " def" : "");
                Set(e, SysMLv2Text.KeywordField, written == SysMLv2Text.DefaultKeyword(m, e) ? null : written);
            }
        }

        private static IEnumerable<string?> Aliases(IEnumerable<SysmlMember> body)
        {
            foreach (var x in body)
            {
                if (x is SysmlUnsupported { Keyword: "alias" } a) yield return a.Name;
                if (x is SysmlDecl d) foreach (var n in Aliases(d.Body)) yield return n;
            }
        }

        private void CheckImports(IEnumerable<SysmlMember> body)
        {
            foreach (var x in body)
            {
                if (x is SysmlImport { Namespace: { } ns } && !Known(ns, scope))
                    Diag(SysmlSeverity.Warning, $"Unknown namespace '{ns.Text}' in import", ns.Span);
                if (x is SysmlDecl d) CheckImports(d.Body);
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
            public string? Via { get; init; }
            public string? Usage { get; init; }
            public SysmlMember? Member { get; init; }
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
            foreach (var (f, r) in matched)
            {
                SetRelFields(r, f);
                if (f.Member != null) _mapped.Add(f.Member);
            }
            foreach (var f in fresh)
            {
                if (TryConnect(f.Kind, f.Source, f.Target, f.Owner, f.Span, "Add " + Describe(f.Kind, f.Source, f.Target)) is { } r)
                {
                    SetRelFields(r, f);
                    if (f.Member != null) _mapped.Add(f.Member);
                }
            }
        }

        private static string Describe(ElementKind k, Element s, Element t) =>
            $"{Metamodel.DisplayName(k).ToLowerInvariant()} {s.DisplayName} → {t.DisplayName}";

        private static bool Same(Fact f, Relationship r) =>
            (f.Name ?? "") == r.Name && (f.Guard ?? "") == r.Get(FieldKeys.Guard) && (f.SourcePart ?? "") == r.Get(FieldKeys.SourcePart)
            && (f.TargetPart ?? "") == r.Get(FieldKeys.TargetPart) && (f.Via ?? "") == r.Get(SysMLv2Text.ViaField);

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
            if (f.Kind == ElementKind.Allocate)
            {
                Set(r, SysMLv2Text.ViaField, f.Via);
                Set(r, SysMLv2Text.UsageField, f.Usage);
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
                Diag(SysmlSeverity.Warning, ex.Message + " (kept as text)", span);
                return null;
            }
        }

        /// <summary>Resolves a reference; an unknown name is a warning unless the text or the library declares it (then it's kept as text).</summary>
        private Element? Resolve(SysmlRef? r, Element context, string what)
        {
            if (r == null) return null;
            var hit = L(r, context, null);
            if (hit == null) Unresolved(r, context, what);
            return hit;
        }

        private void Unresolved(SysmlRef r, Element context, string what)
        {
            if (Known(r, context)) Diag(SysmlSeverity.Info, $"'{r.Text}' is not a Meridian model element; the statement is kept as text", r.Span);
            else Diag(SysmlSeverity.Warning, $"Unknown {what} '{r.Text}'", r.Span);
        }

        private (Element? End, string? Part) ResolveEnd(SysmlRef r, Element context)
        {
            if (!r.IsChain) return (Resolve(r, context, "connector end"), null);
            var path = _names.LookupChain(r.Segments, context);
            if (path == null && _renamedFrom.TryGetValue(r.Segments[0], out var renamed))
                path = _names.LookupChain([SysmlNameIndex.ExportName(renamed), .. r.Segments.Skip(1)], context);
            if (path == null) { Unresolved(r, context, "connector end"); return (null, null); }
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

        private static bool IsStructural(Element e) =>
            e.Kind is ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.Actor or ElementKind.InterfaceBlock or ElementKind.UseCase;

        private void CollectFacts(IEnumerable<SysmlMember> body, Element ctx, List<Fact> into)
        {
            foreach (var x in Flat(body))
            {
                switch (x)
                {
                    case SysmlDecl d:
                        if (d.Connect is { } conn && d.Keyword is "connection" or "interface" && !Map.ContainsKey(d) && IsBareUsage(d, allowTypes: false))
                            AddConnector(into, ctx, conn.From, conn.To, d.Name, null, x.Span, member: d);
                        else if (d.Keyword is "perform action" or "exhibit state" && !Map.ContainsKey(d) && IsStructural(ctx) && d.Types.Count == 1 && IsBareUsage(d, allowTypes: true))
                        {
                            var perform = d.Keyword == "perform action";
                            var behavior = L(d.Types[0], ctx, perform ? y => y.Kind == ElementKind.Activity : y => y.Kind == ElementKind.StateMachine);
                            if (behavior != null)
                                into.Add(new Fact(ElementKind.Allocate, behavior, ctx, ctx, d.Span) { Via = perform ? "perform" : "exhibit", Usage = d.Name, Member = d });
                        }
                        if (Map.TryGetValue(d, out var e) && m.Contains(e)) CollectFacts(d.Body, e, into);
                        break;
                    case SysmlRelation { Complex: false } r:
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
                        if (kind == null || s.First == null || s.Then == null) break;   // kept as text
                        var a = ResolveNode(s.First, ctx, create: true);
                        var b = ResolveNode(s.Then, ctx, create: true);
                        var what = kind == ElementKind.Transition ? "state" : "action";
                        if (a == null) { Unresolved(s.First, ctx, what); break; }
                        if (b == null) { Unresolved(s.Then, ctx, what); break; }
                        into.Add(new Fact(kind.Value, a, b, a.Owner ?? ctx, s.Span) { Name = s.Name, Guard = s.Guard, Effect = s.Effect, Trigger = s.Trigger, Member = s });
                        break;
                    }
                }
            }
        }

        private static bool IsBareUsage(SysmlDecl d, bool allowTypes) =>
            !d.HasBody && d.Value == null && d.Multiplicity == null && d.Specializes.Count == 0 && d.Redefines.Count == 0 && d.References.Count == 0
            && d.Modifiers.Count == 0 && d.Tags.Count == 0 && d.Direction == null && d.Flags.Count == 0 && d.ShortName == null && (allowTypes || d.Types.Count == 0);

        private void AddConnector(List<Fact> into, Element ctx, SysmlRef from, SysmlRef to, string? name, string? item, TextSpan span,
                                  ElementKind kind = ElementKind.Connector, SysmlMember? member = null)
        {
            var (a, ap) = ResolveEnd(from, ctx);
            var (b, bp) = ResolveEnd(to, ctx);
            if (a == null || b == null) return;
            into.Add(new Fact(kind, a, b, ctx, span) { Name = name, SourcePart = ap, TargetPart = bp, Item = item, Member = member });
        }

        private void AddRelation(List<Fact> into, Element ctx, SysmlRelation r)
        {
            switch (r.Keyword)
            {
                case "connect": AddConnector(into, ctx, r.Source!, r.Target!, null, null, r.Span, member: r); return;
                case "flow": AddConnector(into, ctx, r.Source!, r.Target!, r.Name, r.Detail, r.Span, member: r); return;
                case "bind": AddConnector(into, ctx, r.Source!, r.Target!, null, null, r.Span, ElementKind.Binding, member: r); return;
                case "perform" or "exhibit":
                {
                    if (!IsStructural(ctx) || r.Target == null) return;
                    var behavior = L(r.Target, ctx, y => y.Kind is ElementKind.Activity or ElementKind.StateMachine or ElementKind.Action or ElementKind.State);
                    if (behavior == null) return;
                    into.Add(new Fact(ElementKind.Allocate, behavior, ctx, ctx, r.Span) { Via = r.Keyword, Member = r });
                    return;
                }
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
            if (kind == ElementKind.Verify && ctx.Kind != ElementKind.TestCase && r.Source == null) return;   // kept as text
            if (kind == ElementKind.Dependency && r.Name != null) return;                                     // named dependency: kept as text
            var target = Resolve(r.Target, ctx, kind == ElementKind.Satisfy || kind == ElementKind.Verify ? "requirement" : "element");
            var source = r.Source == null ? ctx : Resolve(r.Source, ctx, "element");
            if (source == null || target == null) return;
            var owner = kind is ElementKind.Verify or ElementKind.Include or ElementKind.Extend ? m.DefaultOwnerFor(source.Owner ?? ctx, kind.Value) : ctx;
            into.Add(new Fact(kind.Value, source, target, owner, r.Span) { Name = r.Keyword == "message" ? r.Name : null, Sort = r.Detail, Member = r });
        }

        // -------------------------------------------------------------- text kept verbatim

        /// <summary>Stores every member that isn't represented in the model as verbatim text on its nearest mapped owner:
        /// before the first mapped declaration in <see cref="SysMLv2Text.UnmappedHeadField"/>, after it in <see cref="SysMLv2Text.UnmappedField"/>.</summary>
        private void StoreUnmapped()
        {
            var visited = new HashSet<Element>(ReferenceEqualityComparer.Instance);
            void Visit(IEnumerable<SysmlMember> body, Element owner)
            {
                visited.Add(owner);
                var head = new List<string>();
                var tail = new List<string>();
                Collect(body, owner, head, tail, false);
                Set(owner, SysMLv2Text.UnmappedHeadField, head.Count == 0 ? null : string.Join("\n", head));
                Set(owner, SysMLv2Text.UnmappedField, tail.Count == 0 ? null : string.Join("\n", tail));
            }
            bool Collect(IEnumerable<SysmlMember> body, Element owner, List<string> head, List<string> tail, bool seen)
            {
                var list = body.ToList();
                var covered = new HashSet<SysmlDecl>(list.OfType<SysmlSuccession>().Where(s => s.Shorthand != null && !IsMapped(s, owner) && !IsMapped(s.Shorthand, owner))
                                                         .Select(s => s.Shorthand!), ReferenceEqualityComparer.Instance);
                foreach (var x in list)
                {
                    if (IsVerifyObjective(x)) { seen = Collect(((SysmlDecl)x).Body, owner, head, tail, seen); continue; }
                    if (IsMapped(x, owner))
                    {
                        if (x is SysmlDecl d && Map.TryGetValue(d, out var e) && m.Contains(e)) Visit(d.Body, e);
                        if (x is SysmlDecl or SysmlRelation or SysmlSuccession or SysmlExpression) seen = true;
                        continue;
                    }
                    if (x is SysmlDecl cd && covered.Contains(cd)) continue;
                    if (x is SysmlSuccession { Shorthand: { } sh } && IsMapped(sh, owner)) continue;   // its target is mapped; the bare "then" is implied
                    (seen ? tail : head).Add(MemberText(x));
                    if (x is SysmlDecl or SysmlUnsupported { Keyword: not ("alias" or "rep") } && result.Diagnostics.Count < 500)
                        Diag(SysmlSeverity.Info, $"{Label(x)} has no Meridian model equivalent here; it is kept as text", x is SysmlDecl dd ? dd.NameSpan : x.Span);
                }
                return seen;
            }
            Visit(_topBody, scope);
            foreach (var e in Map.Values.Where(e => !visited.Contains(e) && m.Contains(e)))
            {
                Set(e, SysMLv2Text.UnmappedHeadField, null);
                Set(e, SysMLv2Text.UnmappedField, null);
            }
        }

        private static string Label(SysmlMember x) => x switch
        {
            SysmlDecl d => $"'{d.KeywordText}{(d.IsDef ? " def" : "")}{(d.Name != null ? " " + d.Name : "")}'".Replace("' ", "'"),
            SysmlUnsupported u => $"'{u.Keyword}'",
            _ => "This statement",
        };

        private bool IsMapped(SysmlMember x, Element owner) => x switch
        {
            SysmlDoc => true,
            SysmlUnsupported { Keyword: "first" } => owner.Kind is ElementKind.Activity or ElementKind.StateMachine or ElementKind.State,
            SysmlComment c => IsPlain(c),
            SysmlMetadata md => _mapped.Contains(md),
            SysmlDecl d => Map.TryGetValue(d, out var e) && m.Contains(e) || _mapped.Contains(d),
            SysmlRelation or SysmlSuccession or SysmlExpression => _mapped.Contains(x),
            _ => false,
        };

        private string MemberText(SysmlMember x)
        {
            var s = doc.Text[x.Span.Start..Math.Min(doc.Text.Length, x.Span.End)];
            var lineStart = x.Span.Start == 0 ? 0 : doc.Text.LastIndexOf('\n', x.Span.Start - 1) + 1;
            var indent = 0;
            while (lineStart + indent < doc.Text.Length && doc.Text[lineStart + indent] is ' ' or '\t') indent++;
            return SysMLv2Parser.Dedent(s.TrimEnd(), indent);
        }

        // -------------------------------------------------------------- navigation

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
            Change(SysmlChangeKind.Rename, $"Rename {old} → {(name.Length == 0 ? "(anonymous)" : name)}");
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
