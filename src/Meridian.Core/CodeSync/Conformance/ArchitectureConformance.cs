using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.Model;

namespace Meridian.Core.CodeSync.Conformance;

/// <summary>
/// Checks code (a <see cref="CodeModel"/> from a code reader) against the architecture drawn in the model and lists the
/// violations: (a) dependencies the model does not permit, (b) layering, (c) interface conformance, (d) missing in code /
/// missing in model. Code types are mapped to model elements by <c>code.symbol</c> first, then by name (hand-made
/// elements). Only hand-made relationships, ports and parts count as architecture; the ones code sync created mirror
/// the code and prove nothing. The result is deterministic (sorted) and pure: the model is never changed.
/// See docs/ARCHITECTURE-CHECKS.md.
/// </summary>
public static class ArchitectureConformance
{
    /// <summary>Stereotype of packages that are layers.</summary>
    public const string LayerStereotype = "layer";
    /// <summary>Stereotype of a dependency from one layer package to another that the first layer may use.</summary>
    public const string AllowedStereotype = "allowed";
    /// <summary>Stereotype of a dependency that suppresses violations between its ends (and their contents).</summary>
    public const string AllowStereotype = "allow";
    /// <summary>Stereotype of blocks that must exist in code.</summary>
    public const string ImplementedStereotype = "implemented";
    /// <summary>Stereotype of a port whose interface block its owner must implement.</summary>
    public const string ProvidedStereotype = "provided";
    /// <summary>Tagged value of a «layer» package: the layers it may depend on ("Domain, Shared"; "none"; "*").</summary>
    public const string AllowedTag = "allowed";
    /// <summary>Tagged value of a «layer» package: code namespaces in the layer ("Sat.Domain, Sat.Model").</summary>
    public const string NamespacesTag = "namespaces";
    /// <summary>Tagged value of an «allow» dependency: the rule ids it suppresses (default: all).</summary>
    public const string RulesTag = "rules";

    public static string RuleId(ConformanceRule rule) => rule switch
    {
        ConformanceRule.Dependency => "dependency",
        ConformanceRule.Layering => "layering",
        ConformanceRule.InterfaceConformance => "interface",
        ConformanceRule.MissingInCode => "missing-in-code",
        _ => "missing-in-model",
    };

    public static string RuleTitle(ConformanceRule rule) => rule switch
    {
        ConformanceRule.Dependency => "Dependency not allowed",
        ConformanceRule.Layering => "Layering",
        ConformanceRule.InterfaceConformance => "Interface conformance",
        ConformanceRule.MissingInCode => "Missing in code",
        _ => "Missing in model",
    };

    public static ConformanceRule? ParseRule(string? id) => (id ?? "").Trim().ToLowerInvariant() switch
    {
        "dependency" or "dependencies" => ConformanceRule.Dependency,
        "layering" or "layer" or "layers" => ConformanceRule.Layering,
        "interface" or "interfaces" or "interface-conformance" => ConformanceRule.InterfaceConformance,
        "missing-in-code" => ConformanceRule.MissingInCode,
        "missing-in-model" => ConformanceRule.MissingInModel,
        _ => null,
    };

    /// <summary>Checks <paramref name="code"/> against <paramref name="model"/>. Options default to the model root's settings.</summary>
    public static ConformanceResult Check(ModelRepository model, CodeModel code, ConformanceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(code);
        return new Checker(model, code, options ?? ConformanceOptions.FromModel(model)).Run();
    }

    internal static bool HasStereotype(Element e, string name) =>
        e.Stereotypes.Any(s => s.Trim('«', '»', ' ').Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Created by code sync (it stamps code.symbol on everything it makes).</summary>
    internal static bool IsCodeMade(Element e) => e.Get(CodeFieldKeys.Symbol).Length > 0;

    /// <summary>"Thermal Control", "thermal_control" and "ThermalControl" match; generic parameters are ignored.</summary>
    internal static string NameKey(string name)
    {
        var lt = name.IndexOf('<');
        if (lt > 0) name = name[..lt];
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static readonly ElementKind[] ClassifierKinds =
    [
        ElementKind.Block, ElementKind.InterfaceBlock, ElementKind.Class, ElementKind.Interface, ElementKind.Component,
        ElementKind.Enumeration, ElementKind.DataType, ElementKind.ValueType,
    ];

    internal static bool IsClassifier(Element e) => Array.IndexOf(ClassifierKinds, e.Kind) >= 0;
    internal static bool IsInterface(Element e) => e.Kind is ElementKind.InterfaceBlock or ElementKind.Interface;

    private enum DepKind { Inherits, Implements, Member, Uses }

    /// <summary>One way the architecture permits a dependency: <see cref="From"/> may depend on <see cref="To"/> (and
    /// the reverse too when not <see cref="Directed"/>).</summary>
    private sealed record Link(Element From, Element To, PermittedPaths Path, bool Directed);

    private sealed record Layer(Element Package, IReadOnlyList<string> Namespaces)
    {
        public bool Restricted { get; set; }
        public bool AllowAll { get; set; }
        public HashSet<Element> Allowed { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed record Suppression(ConformanceRule? Rule, Regex Source, Regex? Target);

    private sealed class Checker
    {
        private readonly ModelRepository _m;
        private readonly CodeModel _code;
        private readonly ConformanceOptions _o;
        private readonly SortedDictionary<string, CodeType> _types = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Element>> _bySymbol = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<Element>> _byName = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<Element>> _cands = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<CodeType>> _codeOf = new(StringComparer.Ordinal);
        private readonly Dictionary<Element, List<Link>> _out = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Element, List<Link>> _undirectedIn = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Element> _linked = new(ReferenceEqualityComparer.Instance);
        private readonly List<Relationship> _allows = [];
        private readonly List<Relationship> _allowedDeps = [];
        private readonly List<Suppression> _suppressions = [];
        private readonly List<ArchitectureViolation> _violations = [];
        private readonly List<ArchitectureViolation> _suppressed = [];
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

        public Checker(ModelRepository m, CodeModel code, ConformanceOptions o)
        {
            _m = m;
            _code = code;
            _o = o;
        }

        public ConformanceResult Run()
        {
            foreach (var t in _code.Types) _types.TryAdd(t.Symbol, t);
            IndexModel();
            MapTypes();
            BuildLinks();
            ParseSuppressions();

            if (On(ConformanceRule.Dependency)) CheckDependencies();
            if (On(ConformanceRule.Layering)) CheckLayering();
            if (On(ConformanceRule.InterfaceConformance)) CheckInterfaces();
            if (On(ConformanceRule.MissingInCode) && _o.CodeIsComplete) CheckMissingInCode();
            if (On(ConformanceRule.MissingInModel)) CheckMissingInModel();

            return new ConformanceResult(Sort(_violations), Sort(_suppressed), _types.Count, _cands.Count(c => c.Value.Count > 0));
        }

        private bool On(ConformanceRule r) => !_o.DisabledRules.Contains(r);

        private static List<ArchitectureViolation> Sort(IEnumerable<ArchitectureViolation> list) =>
            list.OrderBy(v => v.Rule).ThenBy(v => v.File ?? "￿", StringComparer.Ordinal).ThenBy(v => v.Line)
                .ThenBy(v => v.CodeSymbol ?? "", StringComparer.Ordinal).ThenBy(v => v.TargetSymbol ?? "", StringComparer.Ordinal)
                .ThenBy(v => v.ElementId ?? "", StringComparer.Ordinal).ThenBy(v => v.Message, StringComparer.Ordinal).ToList();

        // ---------------------------------------------------------------- mapping

        private bool Live(Element e) => _m.Contains(e) && e.Get(CodeModelMapper.RemovedKey) != "true";

        private void IndexModel()
        {
            foreach (var e in _m.AllElements.OrderBy(e => e.QualifiedName, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal))
            {
                if (!IsClassifier(e) || !Live(e)) continue;
                var symbol = e.Get(CodeFieldKeys.Symbol);
                if (symbol.Length > 0)
                {
                    // Elements synced from another repository are not this code's.
                    var repo = e.Get(CodeFieldKeys.Repo);
                    if (_o.RepoId is { Length: > 0 } mine && repo.Length > 0 && repo != mine) continue;
                    Add(_bySymbol, symbol, e);
                }
                else if (e.Name.Length > 0) Add(_byName, NameKey(e.Name), e);
            }
        }

        private static void Add<TK, TV>(Dictionary<TK, List<TV>> d, TK key, TV value) where TK : notnull
        {
            if (!d.TryGetValue(key, out var list)) d[key] = list = [];
            list.Add(value);
        }

        /// <summary>Elements by code.symbol, then hand-made elements with the same name (the best namespace match).</summary>
        private void MapTypes()
        {
            foreach (var t in _types.Values)
            {
                var list = new List<Element>();
                if (_bySymbol.TryGetValue(t.Symbol, out var bySymbol)) list.AddRange(bySymbol);
                if (_byName.TryGetValue(NameKey(t.Name), out var byName))
                {
                    var ns = t.Namespace.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(NameKey).Reverse().ToList();
                    var scored = byName.Select(e => (e, Score: NamespaceScore(e, ns))).ToList();
                    var best = scored.Max(s => s.Score);
                    list.AddRange(scored.Where(s => s.Score == best).Select(s => s.e));
                }
                _cands[t.Symbol] = list;
                foreach (var e in list)
                {
                    if (!_codeOf.TryGetValue(e.Id, out var types)) _codeOf[e.Id] = types = [];
                    types.Add(t);
                }
            }
        }

        /// <summary>How many trailing namespace parts match the names of the element's owners.</summary>
        private static int NamespaceScore(Element e, List<string> reversedNamespace)
        {
            var score = 0;
            var owner = e.Owner;
            foreach (var part in reversedNamespace)
            {
                if (owner is null || NameKey(owner.Name) != part) break;
                score++;
                owner = owner.Owner;
            }
            return score;
        }

        private IReadOnlyList<Element> Cands(string symbol) => _cands.TryGetValue(symbol, out var l) ? l : [];

        /// <summary>The element a finding names: one the architecture constrains if there is one.</summary>
        private Element? Primary(IReadOnlyList<Element> cands) => cands.FirstOrDefault(Constrained) ?? cands.FirstOrDefault();

        private bool Constrained(Element e) => e.AncestorsAndSelf().Any(_linked.Contains);

        // ---------------------------------------------------------------- architecture links

        private void BuildLinks()
        {
            foreach (var r in _m.Relationships.OrderBy(r => r.Id, StringComparer.Ordinal))
            {
                if (!Live(r) || IsCodeMade(r)) continue;
                switch (r.Kind)
                {
                    case ElementKind.Association: AddLink(r.Source, r.Target, PermittedPaths.Association, false); break;
                    case ElementKind.Composition: AddLink(r.Source, r.Target, PermittedPaths.Composition, false); break;
                    case ElementKind.Aggregation: AddLink(r.Source, r.Target, PermittedPaths.Aggregation, false); break;
                    case ElementKind.Generalization: AddLink(r.Source, r.Target, PermittedPaths.Generalization, true); break;
                    case ElementKind.Realization: AddLink(r.Source, r.Target, PermittedPaths.Realization, true); break;
                    case ElementKind.Dependency:
                        if (HasStereotype(r, AllowStereotype)) _allows.Add(r);
                        else if (HasStereotype(r, AllowedStereotype)) _allowedDeps.Add(r);
                        else AddLink(r.Source, r.Target, HasStereotype(r, "use") || HasStereotype(r, "usage") ? PermittedPaths.Usage : PermittedPaths.Dependency, true);
                        break;
                    case ElementKind.Connector: AddConnector(r); break;
                }
            }
            // Hand-made parts, references and ports: the owner knows the type.
            foreach (var f in _m.AllElements.Where(e => e.Kind is ElementKind.Property or ElementKind.Port).OrderBy(e => e.Id, StringComparer.Ordinal))
            {
                if (!Live(f) || IsCodeMade(f) || f.Owner is not { } owner || !IsClassifier(owner) || _m.ResolveType(f) is not { } type || !IsClassifier(type)) continue;
                if (f.Kind == ElementKind.Port && IsInterface(type)) AddLink(owner, type, PermittedPaths.SharedInterface, true);
                else AddLink(owner, type, PermittedPaths.Association | PermittedPaths.Composition | PermittedPaths.Aggregation, true);
            }
        }

        private void AddLink(Element from, Element to, PermittedPaths path, bool directed)
        {
            var link = new Link(from, to, path, directed);
            Add(_out, from, link);
            if (!directed) Add(_undirectedIn, to, link);
            _linked.Add(from);
            _linked.Add(to);
        }

        /// <summary>A connector between parts permits their blocks to depend on each other; through ports typed by an
        /// interface block it only permits each side to depend on that interface.</summary>
        private void AddConnector(Relationship r)
        {
            var (a, ia) = End(r.Source, r.Get(FieldKeys.SourcePart));
            var (b, ib) = End(r.Target, r.Get(FieldKeys.TargetPart));
            if (ia is null && ib is null)
            {
                if (a is not null && b is not null) AddLink(a, b, PermittedPaths.Connector, false);
                return;
            }
            foreach (var i in new[] { ia, ib }.OfType<Element>().Distinct())
            {
                if (a is not null) AddLink(a, i, PermittedPaths.SharedInterface, true);
                if (b is not null) AddLink(b, i, PermittedPaths.SharedInterface, true);
            }
        }

        /// <summary>The block at a connector end, and the interface block when the end is a port typed by one.</summary>
        private (Element? Block, Element? Interface) End(Element end, string partId)
        {
            Element? block;
            Element? iface = null;
            if (end.Kind == ElementKind.Port)
            {
                block = _m.Find(partId) is { } part && _m.ResolveType(part) is { } pt ? pt : end.Owner;
                if (_m.ResolveType(end) is { } t && IsInterface(t)) iface = t;
            }
            else block = _m.ResolveType(end) ?? end.Owner;
            return (block is not null && IsClassifier(block) ? block : null, iface);
        }

        private bool Permits(Element a, Element b)
        {
            var aa = a.AncestorsAndSelf().ToList();
            var bb = b.AncestorsAndSelf().ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var x in aa)
            {
                if (_out.TryGetValue(x, out var outs) && outs.Any(l => (l.Path & _o.Permitted) != 0 && bb.Contains(l.To))) return true;
                if (_undirectedIn.TryGetValue(x, out var ins) && ins.Any(l => (l.Path & _o.Permitted) != 0 && bb.Contains(l.From))) return true;
            }
            return false;
        }

        /// <summary>An interface both blocks are linked to (the model connects them through it).</summary>
        private Element? SharedInterface(Element a, Element b)
        {
            IEnumerable<Element> Interfaces(Element x) => _out.TryGetValue(x, out var l)
                ? l.Where(k => IsInterface(k.To) && (k.Path & (PermittedPaths.SharedInterface | PermittedPaths.Realization | PermittedPaths.Generalization)) != 0).Select(k => k.To)
                : [];
            var mine = Interfaces(a).ToList();
            return Interfaces(b).OrderBy(i => i.QualifiedName, StringComparer.Ordinal).FirstOrDefault(i => mine.Contains(i));
        }

        // ---------------------------------------------------------------- code dependencies

        /// <summary>Scanned types a type depends on (inherits, implements, holds or uses), sorted, without itself or its nesting.</summary>
        private IEnumerable<(CodeType Target, DepKind Kind)> Deps(CodeType t)
        {
            var seen = new Dictionary<string, DepKind>(StringComparer.Ordinal);
            void Note(string? s, DepKind k)
            {
                if (s is null || s == t.Symbol || seen.ContainsKey(s) || !_types.ContainsKey(s)) return;
                if (s.StartsWith(t.Symbol + ".", StringComparison.Ordinal) || t.Symbol.StartsWith(s + ".", StringComparison.Ordinal)) return;
                seen[s] = k;
            }
            foreach (var b in t.BaseTypes) Note(b, DepKind.Inherits);
            foreach (var i in t.Interfaces) Note(i, DepKind.Implements);
            foreach (var m in t.Members) Note(m.TypeSymbol, DepKind.Member);
            foreach (var d in t.Dependencies) Note(d, DepKind.Uses);
            return seen.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (_types[kv.Key], kv.Value));
        }

        private static string Verb(DepKind k) => k switch
        {
            DepKind.Inherits => "inherits from",
            DepKind.Implements => "implements",
            DepKind.Member => "holds a",
            _ => "uses",
        };

        private static bool Related(Element a, Element b) => ReferenceEquals(a, b) || a.IsAncestorOf(b) || b.IsAncestorOf(a);

        // ---------------------------------------------------------------- (a) dependencies

        private void CheckDependencies()
        {
            foreach (var t in _types.Values)
            {
                var ca = Cands(t.Symbol);
                if (ca.Count == 0) continue;
                foreach (var (d, kind) in Deps(t))
                {
                    var cb = Cands(d.Symbol);
                    if (cb.Count == 0 || ca.Any(a => cb.Any(b => Related(a, b)))) continue;
                    if (!_o.Strict && (!ca.Any(Constrained) || !cb.Any(Constrained))) continue;
                    if (ca.Any(a => cb.Any(b => Permits(a, b)))) continue;

                    var a0 = Primary(ca)!;
                    var b0 = Primary(cb)!;
                    var via = ca.SelectMany(a => cb.Select(b => SharedInterface(a, b))).FirstOrDefault(i => i is not null);
                    var hint = via is not null
                        ? $"Depend on {CodeName(via)} instead: the model connects {a0.Name} and {b0.Name} only through it. If the direct dependency is intended, draw a dependency from {a0.Name} to {b0.Name} (or an «allow» dependency)."
                        : $"Remove the dependency from the code, or draw a relationship from {a0.Name} to {b0.Name} in the model (dependency, association, connector), or an «allow» dependency.";
                    Report(ConformanceRule.Dependency, ViolationSeverity.Error,
                        $"{t.Name} {Verb(kind)} {d.Name}, but the model has no relationship from {a0.Name} to {b0.Name} that permits it.",
                        hint, t, d, ca, cb);
                }
            }
        }

        /// <summary>The code name of a model element (its code type's name when one maps to it).</summary>
        private string CodeName(Element e) => _codeOf.TryGetValue(e.Id, out var types) && types.Count > 0 ? types[0].Name : e.Name;

        // ---------------------------------------------------------------- (b) layering

        private void CheckLayering()
        {
            var layers = _m.OfKind(ElementKind.Package).Where(p => Live(p) && HasStereotype(p, LayerStereotype))
                .OrderBy(p => p.QualifiedName, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal)
                .Select(p => new Layer(p, ConformanceOptions.Split(p.Get(FieldKeys.TagPrefix + NamespacesTag), ',', ';').ToList()))
                .ToList();
            if (layers.Count == 0) return;
            var byPackage = new Dictionary<Element, Layer>(ReferenceEqualityComparer.Instance);
            foreach (var l in layers) byPackage[l.Package] = l;
            Layer? Enclosing(Element e) => e.AncestorsAndSelf().Select(a => byPackage.GetValueOrDefault(a)).FirstOrDefault(l => l is not null);
            Layer? Named(string name) => layers.FirstOrDefault(l => l.Package.QualifiedName.Equals(name, StringComparison.OrdinalIgnoreCase))
                                         ?? layers.FirstOrDefault(l => l.Package.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            foreach (var l in layers)
            {
                var tag = l.Package.Get(FieldKeys.TagPrefix + AllowedTag);
                if (tag.Length == 0) continue;
                l.Restricted = true;
                foreach (var name in ConformanceOptions.Split(tag, ',', ';'))
                {
                    if (name == "*") l.AllowAll = true;
                    else if (Named(name) is { } other) l.Allowed.Add(other.Package);
                }
            }
            foreach (var d in _allowedDeps)
                if (Enclosing(d.Source) is { } from && Enclosing(d.Target) is { } to)
                {
                    from.Restricted = true;
                    from.Allowed.Add(to.Package);
                }

            Layer? LayerOf(CodeType t)
            {
                var byNamespace = layers.SelectMany(l => l.Namespaces.Select(ns => (l, ns)))
                    .Where(x => t.Namespace == x.ns || t.Namespace.StartsWith(x.ns + ".", StringComparison.Ordinal))
                    .OrderByDescending(x => x.ns.Length).Select(x => x.l).FirstOrDefault();
                return byNamespace ?? Cands(t.Symbol).Select(Enclosing).FirstOrDefault(l => l is not null);
            }

            foreach (var t in _types.Values)
            {
                if (LayerOf(t) is not { Restricted: true, AllowAll: false } lt) continue;
                foreach (var (d, kind) in Deps(t))
                {
                    if (LayerOf(d) is not { } ld || ReferenceEquals(ld, lt) || lt.Allowed.Contains(ld.Package)) continue;
                    if (ld.Package.IsAncestorOf(lt.Package) || lt.Package.IsAncestorOf(ld.Package)) continue;   // nested layers
                    var from = lt.Package.Name;
                    var to = ld.Package.Name;
                    Report(ConformanceRule.Layering, ViolationSeverity.Error,
                        $"{t.Name} (layer {from}) {Verb(kind)} {d.Name} (layer {to}), but {from} may not depend on {to}.",
                        $"Move the dependency behind an abstraction in a layer {from} may use, or allow it: add {to} to the “{AllowedTag}” tag of {from}, or draw an «allowed» dependency from {from} to {to}.",
                        t, d, Cands(t.Symbol), Cands(d.Symbol), lt.Package, ld.Package);
                }
            }
        }

        // ---------------------------------------------------------------- (c) interface conformance

        private void CheckInterfaces()
        {
            var pairs = new List<(Element Block, Element Interface)>();
            foreach (var r in _m.Relationships.Where(r => r.Kind is ElementKind.Realization or ElementKind.Generalization && Live(r) && !IsCodeMade(r)))
                if (!IsInterface(r.Source) && IsClassifier(r.Source) && IsInterface(r.Target)) pairs.Add((r.Source, r.Target));
            foreach (var p in _m.OfKind(ElementKind.Port).Where(p => Live(p) && !IsCodeMade(p) && HasStereotype(p, ProvidedStereotype)))
                if (p.Owner is { } owner && IsClassifier(owner) && !IsInterface(owner) && _m.ResolveType(p) is { } t && IsInterface(t)) pairs.Add((owner, t));

            foreach (var (block, iface) in pairs.DistinctBy(p => (p.Block.Id, p.Interface.Id))
                         .OrderBy(p => p.Block.QualifiedName, StringComparer.Ordinal).ThenBy(p => p.Interface.QualifiedName, StringComparer.Ordinal))
            {
                if (!_codeOf.TryGetValue(block.Id, out var blockTypes) || blockTypes.FirstOrDefault(t => t.Kind != CodeTypeKind.Interface) is not { } codeBlock) continue;
                var codeIface = _codeOf.TryGetValue(iface.Id, out var it) ? it.FirstOrDefault(t => t.Kind == CodeTypeKind.Interface) : null;
                var implemented = codeIface is not null && Implements(codeBlock, codeIface.Symbol);
                IReadOnlyList<Element> bc = [block], ic = [iface];
                if (codeIface is not null && !implemented)
                    Report(ConformanceRule.InterfaceConformance, ViolationSeverity.Error,
                        $"{block.Name} realizes {iface.Name} in the model, but {codeBlock.Name} does not implement {codeIface.Name}.",
                        $"Add {codeIface.Name} to the base list of {codeBlock.Name}, or remove the realization from the model.",
                        codeBlock, codeIface, bc, ic);

                var required = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var op in iface.OwnedElements.Where(e => e.Kind == ElementKind.Operation && Live(e) && e.Name.Length > 0)) required.Add(op.Name);
                if (codeIface is not null) foreach (var m in Methods(codeIface)) required.Add(m);
                var provided = new HashSet<string>(Methods(codeBlock), StringComparer.OrdinalIgnoreCase);
                if (implemented) provided.UnionWith(Methods(codeIface!));
                foreach (var op in required.Where(op => !provided.Contains(op)))
                    Report(ConformanceRule.InterfaceConformance, ViolationSeverity.Error,
                        $"{codeBlock.Name} is missing operation {op}() of {iface.Name}.",
                        $"Implement {op}() in {codeBlock.Name}, or remove it from {iface.Name} in the model.",
                        codeBlock, codeIface, bc, ic, detail: op);
            }
        }

        /// <summary>True when the type implements the interface directly, through base classes or through other interfaces.</summary>
        private bool Implements(CodeType t, string iface)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>([t.Symbol]);
            while (stack.Count > 0)
            {
                var s = stack.Pop();
                if (!seen.Add(s) || !_types.TryGetValue(s, out var x)) continue;
                if (x.Interfaces.Contains(iface) || x.BaseTypes.Contains(iface)) return true;
                foreach (var b in x.BaseTypes.Concat(x.Interfaces)) stack.Push(b);
            }
            return false;
        }

        /// <summary>Method names of a type and of its scanned base types and interfaces.</summary>
        private IEnumerable<string> Methods(CodeType t)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>([t.Symbol]);
            while (stack.Count > 0)
            {
                var s = stack.Pop();
                if (!seen.Add(s) || !_types.TryGetValue(s, out var x)) continue;
                foreach (var m in x.Members.Where(m => m.Kind == CodeMemberKind.Method)) yield return m.Name;
                foreach (var b in x.BaseTypes.Concat(x.Interfaces)) stack.Push(b);
            }
        }

        // ---------------------------------------------------------------- (d) missing in code / in model

        private void CheckMissingInCode()
        {
            foreach (var e in _m.AllElements.Where(e => IsClassifier(e) && Live(e) && HasStereotype(e, ImplementedStereotype))
                         .OrderBy(e => e.QualifiedName, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal))
            {
                if (_codeOf.ContainsKey(e.Id)) continue;
                Report(ConformanceRule.MissingInCode, ViolationSeverity.Warning,
                    $"«{ImplementedStereotype}» {e.Name} has no matching type in the code.",
                    $"Add the type to the code, check its name, set code.symbol on {e.Name} to the type's full name (Namespace.Type), or remove «{ImplementedStereotype}».",
                    null, null, [e], []);
            }
        }

        private void CheckMissingInModel()
        {
            foreach (var t in _types.Values)
            {
                if (Cands(t.Symbol).Count > 0) continue;
                Report(ConformanceRule.MissingInModel, ViolationSeverity.Warning,
                    $"{t.Symbol} has no element in the model.",
                    $"Accept the code-sync review that adds it, model it (a block named {t.Name}), or suppress it with “missing-in-model: {t.Symbol}”.",
                    t, null, [], []);
            }
        }

        // ---------------------------------------------------------------- reporting and suppression

        private void Report(ConformanceRule rule, ViolationSeverity severity, string message, string hint, CodeType? source, CodeType? target,
                            IReadOnlyList<Element> sourceElements, IReadOnlyList<Element> targetElements,
                            Element? sourceFallback = null, Element? targetFallback = null, string? detail = null)
        {
            var s = Primary(sourceElements) ?? sourceFallback;
            var t = Primary(targetElements) ?? targetFallback;
            if (!_seen.Add($"{rule}|{source?.Symbol}|{target?.Symbol}|{s?.Id}|{t?.Id}|{detail}")) return;
            var v = new ArchitectureViolation(rule, severity, message, hint, source?.File is { Length: > 0 } f ? f : null, source?.Line ?? 0,
                source?.Symbol, target?.Symbol, s?.Id, s?.Name, t?.Id, t?.Name);
            var suppressed = IsAllowed(rule, sourceElements.Append(sourceFallback), targetElements.Append(targetFallback))
                             || IsSuppressed(v, sourceElements.Append(sourceFallback), targetElements.Append(targetFallback));
            (suppressed ? _suppressed : _violations).Add(v);
        }

        /// <summary>An «allow» dependency from (an owner of) the source to (an owner of) the target.</summary>
        private bool IsAllowed(ConformanceRule rule, IEnumerable<Element?> sources, IEnumerable<Element?> targets)
        {
            if (_allows.Count == 0) return false;
            var src = sources.OfType<Element>().SelectMany(e => e.AncestorsAndSelf()).ToHashSet(ReferenceEqualityComparer.Instance);
            var tgt = targets.OfType<Element>().SelectMany(e => e.AncestorsAndSelf()).ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var a in _allows)
            {
                if (!src.Contains(a.Source) || !(tgt.Contains(a.Target) || (tgt.Count == 0 && ReferenceEquals(a.Source, a.Target)))) continue;
                var rules = ConformanceOptions.Split(a.Get(FieldKeys.TagPrefix + RulesTag), ',', ';').Select(ParseRule).ToList();
                if (rules.Count == 0 || rules.Contains(rule)) return true;
            }
            return false;
        }

        private void ParseSuppressions()
        {
            foreach (var entry in _o.Suppressions)
            {
                var text = entry;
                ConformanceRule? rule = null;
                var colon = text.IndexOf(':');
                if (colon > 0 && (colon + 1 >= text.Length || text[colon + 1] != ':'))
                {
                    var head = text[..colon].Trim();
                    if (head == "*") text = text[(colon + 1)..];
                    else if (ParseRule(head) is { } r) { rule = r; text = text[(colon + 1)..]; }
                }
                var arrow = text.IndexOf("->", StringComparison.Ordinal);
                var lhs = (arrow >= 0 ? text[..arrow] : text).Trim();
                var rhs = arrow >= 0 ? text[(arrow + 2)..].Trim() : null;
                if (lhs.Length == 0) continue;
                _suppressions.Add(new Suppression(rule, Pattern(lhs), rhs is { Length: > 0 } ? Pattern(rhs) : null));
            }
        }

        private static Regex Pattern(string glob) =>
            new("^" + Regex.Escape(glob).Replace("\\*", ".*", StringComparison.Ordinal) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private bool IsSuppressed(ArchitectureViolation v, IEnumerable<Element?> sources, IEnumerable<Element?> targets)
        {
            if (_suppressions.Count == 0) return false;
            var src = Names(v.CodeSymbol, sources);
            var tgt = Names(v.TargetSymbol, targets);
            return _suppressions.Any(s => (s.Rule is null || s.Rule == v.Rule) && src.Any(s.Source.IsMatch) && (s.Target is null || tgt.Any(s.Target.IsMatch)));
        }

        private static List<string> Names(string? symbol, IEnumerable<Element?> elements)
        {
            var names = new List<string>();
            if (symbol is { Length: > 0 }) names.Add(symbol);
            foreach (var e in elements.OfType<Element>())
            {
                names.Add(e.Name);
                names.Add(e.QualifiedName);
            }
            return names;
        }
    }
}
