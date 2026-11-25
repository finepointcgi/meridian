using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.CodeSync;

/// <summary>
/// Applies a <see cref="CodeModel"/> to a model as SysML v1 structure, in one undo step:
/// <list type="bullet">
/// <item>namespaces → nested packages under <see cref="CodeSyncOptions.RootPackage"/> (symbol "ns:Sat.Power");</item>
/// <item>classes, structs and records → blocks; interfaces → interface blocks; enums → enumerations with literals;
/// nested types inside their outer block;</item>
/// <item>fields / properties → properties: typed by a scanned class, struct or record = part property (plus a
/// composition), by a scanned interface = reference property (plus an association), anything else = value property
/// with the code type as text; methods → operations (one per overload, symbol "Type.Method(int,string)", signature with parameters);</item>
/// <item>base classes → generalizations, implemented interfaces → realizations, usage → dependencies.</item>
/// </list>
/// Every element it creates carries <see cref="CodeFieldKeys"/>; a re-sync matches by (repo, symbol), updates only the
/// fields it owns (name, documentation, type, multiplicity, visibility, signature, the code.* keys), moves elements whose
/// namespace changed and removes (or marks) what disappeared. Elements without a code symbol are never changed. Applying
/// the same code model twice changes nothing the second time.
/// </summary>
public static class CodeModelMapper
{
    /// <summary>Field set to "true" on elements whose code is gone when they are kept rather than removed.</summary>
    public const string RemovedKey = "code.removed";

    /// <summary>
    /// A readable name written for a code element by the optional AI pass (or by hand through the API): when set, code
    /// sync names the element with it instead of the identifier or <see cref="CodeNames"/>'s words. The code symbol is
    /// unchanged, so re-syncs still match.
    /// </summary>
    public const string LabelKey = "code.label";

    /// <summary>Field set to "true" on the key blocks of a namespace: the public, top-level types the rest of the code
    /// uses most (<see cref="KeyBlocksPerPackage"/> at most). Code sync's structure diagrams show only these.</summary>
    public const string KeyKey = "code.keyBlock";

    /// <summary>Most key blocks per namespace package.</summary>
    public const int KeyBlocksPerPackage = 12;

    /// <summary>Symbol prefix of the system architecture (subsystems from projects): "arch:", "arch:system", "arch:subsystem:X" …</summary>
    public const string ArchitecturePrefix = "arch:";

    /// <summary>Most ports a subsystem gets for the interfaces it provides or requires (the most used first).</summary>
    public const int MaxPortsPerSubsystem = 5;

    /// <summary>Most derived connectors between the parts of one block.</summary>
    public const int MaxConnectorsPerBlock = 10;

    /// <summary>Field set to "true" on static (class-level) properties and operations. The metamodel has no static
    /// flag of its own, so code sync keeps it under the UML attribute name.</summary>
    public const string IsStaticKey = "isStatic";

    /// <summary>
    /// The symbol of an operation: "Type.Method(int,List&lt;string&gt;)", the parameter types as written with whitespace
    /// removed ("Type.Method()" without parameters, "?" for an untyped parameter). Return parameters are not part of it.
    /// </summary>
    public static string OperationSymbol(string typeSymbol, CodeMember method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var types = method.Parameters.Where(p => p.Direction != CodeParameterDirection.Return)
            .Select(p => string.Concat((p.TypeName ?? "").Where(c => !char.IsWhiteSpace(c))))
            .Select(t => t.Length == 0 ? "?" : t);
        return $"{typeSymbol}.{method.Name}({string.Join(",", types)})";
    }

    /// <summary>
    /// The operation signature in the model's notation: "(a : int, out b : string) : bool". "in" is implied, "out" and
    /// "inout" are written; an untyped parameter is just its name. Null for a method without parameters that returns
    /// nothing (an empty signature shows as "()").
    /// </summary>
    public static string? Signature(CodeMember method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var ret = method.TypeName;
        var pars = new List<string>();
        foreach (var p in method.Parameters)
        {
            if (p.Direction == CodeParameterDirection.Return)
            {
                if (IsVoid(ret)) ret = p.TypeName;
                continue;
            }
            var dir = p.Direction switch { CodeParameterDirection.Out => "out ", CodeParameterDirection.InOut => "inout ", _ => "" };
            pars.Add(string.IsNullOrWhiteSpace(p.TypeName) ? dir + p.Name : $"{dir}{p.Name} : {p.TypeName}");
        }
        if (pars.Count == 0 && IsVoid(ret)) return null;
        return $"({string.Join(", ", pars)})" + (IsVoid(ret) ? "" : $" : {ret}");

        static bool IsVoid(string? t) => string.IsNullOrWhiteSpace(t) || t is "void" or "None";
    }

    private const string NamespacePrefix = "ns:";

    /// <summary>Symbol prefix of the derived «use» dependencies between namespace packages ("pkgdep:Sat.Power->Sat.Comms").</summary>
    public const string PackageDependencyPrefix = "pkgdep:";

    /// <summary>Field set to "true" on a derived package dependency that is part of a dependency cycle.</summary>
    public const string CycleKey = "code.cycle";

    /// <summary>Most packages the package overview shows with their sub-namespaces; above it only the top level is shown.</summary>
    public const int OverviewMaxPackages = 40;

    /// <summary>Whether <paramref name="e"/> is a package code sync made for a namespace.</summary>
    public static bool IsNamespacePackage(Element e) =>
        e.Kind == ElementKind.Package && e.Get(CodeFieldKeys.Symbol).StartsWith(NamespacePrefix, StringComparison.Ordinal);

    /// <summary>
    /// The namespace packages a package overview of <paramref name="root"/> (a code-sync root package) shows: the top-level
    /// ones, plus the level below while all of them together stay within <see cref="OverviewMaxPackages"/>. Packages
    /// marked removed are left out. Top level first, each level by name.
    /// </summary>
    public static IReadOnlyList<Element> OverviewPackages(Element root)
    {
        ArgumentNullException.ThrowIfNull(root);
        return OverviewPackages(root, p => IsNamespacePackage(p) && p.Get(RemovedKey) != "true");
    }

    private static List<Element> OverviewPackages(Element root, Func<Element, bool> include)
    {
        var top = root.OwnedElements.Where(c => c.Kind == ElementKind.Package && include(c)).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
        var below = top.SelectMany(t => t.OwnedElements.Where(c => c.Kind == ElementKind.Package && include(c)).OrderBy(c => c.Name, StringComparer.Ordinal)).ToList();
        return top.Count + below.Count <= OverviewMaxPackages ? [.. top, .. below] : top;
    }

    /// <summary>The package <see cref="CodeSyncOptions.RootPackage"/> names ("Software::Code"), or null when it does not exist yet.</summary>
    public static Element? FindRootPackage(ModelRepository model, string? rootPackage)
    {
        ArgumentNullException.ThrowIfNull(model);
        Element? owner = model.Root;
        foreach (var name in RootPackageNames(rootPackage))
        {
            owner = owner.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Package && c.Name == name);
            if (owner is null) return null;
        }
        return owner;
    }

    /// <summary>The package path of a root package setting ("Acme::Sat" → Acme, Sat; empty → Code).</summary>
    public static string[] RootPackageNames(string? rootPackage)
    {
        var names = (string.IsNullOrWhiteSpace(rootPackage) ? "Code" : rootPackage)
            .Split("::", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return names.Length == 0 ? ["Code"] : names;
    }

    public static CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(code);
        ArgumentNullException.ThrowIfNull(options);
        var run = new Run(model, code, options);
        using (model.History.Transaction(string.IsNullOrEmpty(options.Commit) ? "Code sync" : $"Code sync {Short(options.Commit)}"))
            run.Execute();
        return run.Result();
    }

    private static string Short(string commit) => commit.Length > 8 ? commit[..8] : commit;

    private sealed class Run(ModelRepository m, CodeModel code, CodeSyncOptions o)
    {
        private readonly List<string> _warnings = [.. code.Warnings];
        private readonly HashSet<string> _idsBefore = new(StringComparer.Ordinal);
        private readonly List<string> _added = [];
        private readonly List<string> _changed = [];
        private readonly HashSet<string> _changedSet = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Element> _existing = new(StringComparer.Ordinal);
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Element> _types = new(StringComparer.Ordinal);
        private readonly Dictionary<string, CodeType> _codeTypes = new(StringComparer.Ordinal);
        private Element _root = null!;
        private int _trivialRemoved;
        /// <summary>The root namespace left out of the package tree (<see cref="CodeSyncOptions.CollapseRootNamespace"/>), or null.</summary>
        private string? _collapsed;

        public void Execute()
        {
            foreach (var e in m.AllElements) _idsBefore.Add(e.Id);
            IndexExisting();
            _root = EnsureRootPackage();

            var modules = code.Modules.GroupBy(x => x.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var left = new HashSet<string>(StringComparer.Ordinal);
            foreach (var t in code.Types.OrderBy(t => t.Symbol, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(t.Symbol)) continue;
                // Noise: test code, and nested types that are not public (with the types nested in them).
                if (!o.IncludeTests && CodeSyncNoise.IsTest(t, modules) || !o.IncludeNonPublic && t.Visibility is "private" or "protected"
                    || OuterSymbols(t.Symbol).Any(left.Contains))
                {
                    left.Add(t.Symbol);
                    continue;
                }
                if (!_codeTypes.TryAdd(t.Symbol, t)) _warnings.Add($"Type {t.Symbol} is declared more than once; the first declaration was used.");
            }

            var ordered = _codeTypes.Values.OrderBy(t => t.Symbol, StringComparer.Ordinal).ToList();
            _collapsed = CollapsedNamespace();
            DetectMoves(ordered);
            foreach (var t in ordered) EnsureType(t, 0);

            var rels = new List<(string Symbol, ElementKind Kind, CodeType From, Element Source, Element Target, string? Multiplicity)>();
            var pairs = new HashSet<(string, string)>();
            var deps = new SortedSet<(string, string)>();
            foreach (var t in ordered)
            {
                if (!_types.TryGetValue(t.Symbol, out var te)) continue;
                var parts = new SortedDictionary<string, bool>(StringComparer.Ordinal);   // target -> any collection
                var refs = new SortedSet<string>(StringComparer.Ordinal);
                EnsureMembers(t, te, parts, refs, deps);
                // Usage counts for the architecture whether or not it becomes model dependencies.
                foreach (var d in t.Dependencies) deps.Add((t.Symbol, d));

                foreach (var b in t.BaseTypes.Distinct())
                    if (_types.TryGetValue(b, out var be)) rels.Add(($"gen:{t.Symbol}->{b}", ElementKind.Generalization, t, te, be, null));
                foreach (var i in t.Interfaces.Distinct())
                    if (_types.TryGetValue(i, out var ie)) rels.Add(($"real:{t.Symbol}->{i}", ElementKind.Realization, t, te, ie, null));
                foreach (var (target, many) in parts)
                    rels.Add(($"comp:{t.Symbol}->{target}", ElementKind.Composition, t, te, _types[target], many ? "*" : "1"));
                foreach (var target in refs)
                    rels.Add(($"ref:{t.Symbol}->{target}", ElementKind.Association, t, te, _types[target], null));
            }
            foreach (var r in rels) pairs.Add((r.From.Symbol, Target(r.Symbol)));
            var usage = new List<(Element Source, Element Target)>();
            foreach (var (from, to) in deps)
            {
                if (from == to || pairs.Contains((from, to))) continue;
                if (!_types.TryGetValue(from, out var s) || !_types.TryGetValue(to, out var tt)) continue;
                if (o.IncludeUsageDependencies) rels.Add(($"dep:{from}->{to}", ElementKind.Dependency, _codeTypes[from], s, tt, null));
                else usage.Add((s, tt));
            }
            foreach (var r in rels) EnsureRelationship(r.Symbol, r.Kind, r.From, r.Source, r.Target, r.Multiplicity);
            var links = rels.Select(r => (r.Source, r.Target)).Concat(usage).ToList();
            EnsurePackageDependencies(links);
            var linked = new HashSet<(string, string)>(rels.Select(r => (r.From.Symbol, Target(r.Symbol))).Concat(deps));
            EnsurePartConnectors(ordered, linked);
            MarkKeyBlocks(ordered, linked);
            EnsureSubsystems(linked, modules);
            EnsureArtifacts();
            EnsureBehaviour();

            RemoveGone();
            ReleaseCollapsedPackage();
            RemoveEmptyPackages();
            _trivialRemoved += CodeSyncDiagrams.RemoveTrivialIbds(m, o.RepoId, o.SyncedDiagrams);
        }

        /// <summary>
        /// The namespace whose package is left out (<see cref="CodeSyncOptions.CollapseRootNamespace"/>): the first segment
        /// every namespace starts with, when it equals the root package's name or no type is in the global namespace; or the
        /// first segment named like the root package when at least 80% of the types are in it.
        /// </summary>
        private string? CollapsedNamespace()
        {
            if (!o.CollapseRootNamespace) return null;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var global = false;
            foreach (var t in _codeTypes.Values)
            {
                var dot = t.Namespace.IndexOf('.');
                var segment = (dot < 0 ? t.Namespace : t.Namespace[..dot]).Trim();
                if (segment.Length == 0) global = true;
                else counts[segment] = counts.GetValueOrDefault(segment) + 1;
            }
            if (counts.Count == 0) return null;
            var named = string.Equals(counts.Keys.First(), _root.Name, StringComparison.OrdinalIgnoreCase);
            if (counts.Count == 1) return !global || named ? counts.Keys.First() : null;
            // A few outliers (a sample, a tool, a vendored file) do not stop the namespace named like the model package
            // from collapsing when it holds most of the code; the outliers keep their own packages beside it.
            var main = counts.FirstOrDefault(kv => string.Equals(kv.Key, _root.Name, StringComparison.OrdinalIgnoreCase));
            return main.Key is not null && main.Value >= 0.8 * _codeTypes.Count ? main.Key : null;
        }

        /// <summary>
        /// When the root namespace is collapsed, its old package (from a sync before the option, or while it was off) is no
        /// longer used: its sub-packages and types have already moved to the root package. What else it holds (diagrams,
        /// hand-made elements) moves along, auto-diagram markers following the owner, so the empty package can go.
        /// </summary>
        private void ReleaseCollapsedPackage()
        {
            if (_collapsed is null || !_existing.TryGetValue(NamespacePrefix + _collapsed, out var old) || _seen.Contains(NamespacePrefix + _collapsed)
                || !m.Contains(old) || ReferenceEquals(old, _root) || old.IsAncestorOf(_root)) return;
            foreach (var c in old.OwnedElements.ToList())
            {
                if (!Metamodel.CanOwn(_root.Kind, c.Kind)) continue;
                m.Move(c, _root);
                if (c is Diagram d && d.Get(AutoDiagrammer.MarkerKey) is var key && key.EndsWith(":" + old.Id, StringComparison.Ordinal))
                    m.Set(d, AutoDiagrammer.MarkerKey, key[..^old.Id.Length] + _root.Id);
            }
        }

        /// <summary>
        /// «use» dependencies between the packages of <see cref="OverviewPackages(Element)"/>: one per ordered pair whose
        /// types are related in code (inheritance, parts, references, usage), lifted to the packages shown. Those in a
        /// cycle carry <see cref="CycleKey"/> and say so in their documentation.
        /// </summary>
        private void EnsurePackageDependencies(IEnumerable<(Element Source, Element Target)> typeLinks)
        {
            if (!o.PackageDependencies) return;
            var shown = OverviewPackages(_root, p => IsOurs(p) && IsNamespacePackage(p) && _seen.Contains(p.Get(CodeFieldKeys.Symbol)))
                .ToHashSet(ReferenceEqualityComparer.Instance);
            if (shown.Count < 2) return;
            Element? Bucket(Element e) => e.AncestorsAndSelf().FirstOrDefault(shown.Contains);
            string Ns(Element p) => p.Get(CodeFieldKeys.Symbol)[NamespacePrefix.Length..];

            var pairs = new SortedDictionary<(string From, string To), (Element From, Element To)>();
            foreach (var (s, t) in typeLinks)
            {
                if (Bucket(s) is not { } a || Bucket(t) is not { } b || ReferenceEquals(a, b) || a.IsAncestorOf(b) || b.IsAncestorOf(a)) continue;
                pairs.TryAdd((Ns(a), Ns(b)), (a, b));
            }
            if (pairs.Count == 0) return;

            var cycles = PackageCycles(pairs.Keys);
            foreach (var ((from, to), (a, b)) in pairs)
            {
                var cycle = cycles.TryGetValue(from, out var c1) && cycles.TryGetValue(to, out var c2) && ReferenceEquals(c1, c2) ? c1 : null;
                EnsureRelationship($"{PackageDependencyPrefix}{from}->{to}", ElementKind.Dependency, null, a, b, null,
                [
                    (FieldKeys.Stereotypes, "use"),
                    (CycleKey, cycle is null ? null : "true"),
                    (FieldKeys.Documentation, cycle is null ? null : $"Derived from code. Part of a dependency cycle between {string.Join(", ", cycle)}."),
                ]);
            }
        }

        /// <summary>Strongly connected components with more than one package (Tarjan), by member.</summary>
        private static Dictionary<string, List<string>> PackageCycles(IEnumerable<(string From, string To)> edges)
        {
            var next = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var (f, t) in edges)
            {
                if (!next.TryGetValue(f, out var l)) next[f] = l = [];
                l.Add(t);
                next.TryAdd(t, []);
            }
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            var low = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new Stack<string>();
            var onStack = new HashSet<string>(StringComparer.Ordinal);
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var counter = 0;

            void Visit(string v)
            {
                index[v] = low[v] = counter++;
                stack.Push(v);
                onStack.Add(v);
                foreach (var w in next[v])
                {
                    if (!index.ContainsKey(w))
                    {
                        Visit(w);
                        low[v] = Math.Min(low[v], low[w]);
                    }
                    else if (onStack.Contains(w)) low[v] = Math.Min(low[v], index[w]);
                }
                if (low[v] != index[v]) return;
                var component = new List<string>();
                string x;
                do
                {
                    x = stack.Pop();
                    onStack.Remove(x);
                    component.Add(x);
                } while (x != v);
                if (component.Count < 2) return;
                component.Sort(StringComparer.Ordinal);
                foreach (var member in component) result[member] = component;
            }

            foreach (var v in next.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList())
                if (!index.ContainsKey(v)) Visit(v);
            return result;
        }

        /// <summary>
        /// A type that moved to another namespace gets a new symbol. When exactly one vanished type and exactly one new
        /// type share the same kind and the same name path inside their namespace, the existing element is re-keyed to the
        /// new symbol (members, nested types and code relationships along with it), so it is moved rather than replaced
        /// and keeps its diagrams, hand-made content and relationships.
        /// </summary>
        private void DetectMoves(List<CodeType> ordered)
        {
            static ElementKind KindOf(CodeTypeKind k) => k switch
            {
                CodeTypeKind.Interface => ElementKind.InterfaceBlock,
                CodeTypeKind.Enum => ElementKind.Enumeration,
                _ => ElementKind.Block,
            };
            // A type directly in the root package with a dotted symbol was in a collapsed root namespace ("Meridian.Foo").
            string CollapsedNamespaceOf(string symbol, Element e) =>
                ReferenceEquals(e.Owner, _root) && symbol.IndexOf('.') is > 0 and var dot ? symbol[..dot] : "";
            static string LocalPath(string symbol, string ns) => ns.Length > 0 && symbol.StartsWith(ns + ".", StringComparison.Ordinal) ? symbol[(ns.Length + 1)..] : symbol;

            var fresh = ordered.Where(t => !_existing.ContainsKey(t.Symbol))
                .GroupBy(t => (LocalPath(t.Symbol, t.Namespace), KindOf(t.Kind))).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
            if (fresh.Count == 0) return;
            var vanished = _existing
                .Where(kv => !_codeTypes.ContainsKey(kv.Key) && !kv.Key.StartsWith(NamespacePrefix, StringComparison.Ordinal) && m.Contains(kv.Value)
                             && kv.Value.Kind is ElementKind.Block or ElementKind.InterfaceBlock or ElementKind.Enumeration)
                .Select(kv => (Symbol: kv.Key, Element: kv.Value,
                               Ns: kv.Value.AncestorsAndSelf().Skip(1).Select(a => a.Get(CodeFieldKeys.Symbol))
                                   .FirstOrDefault(x => x.StartsWith(NamespacePrefix, StringComparison.Ordinal))?[NamespacePrefix.Length..]
                                   ?? CollapsedNamespaceOf(kv.Key, kv.Value)))
                .Where(v => !HasVanishedOuter(v.Symbol))   // outermost vanished types only
                .GroupBy(v => (LocalPath(v.Symbol, v.Ns), v.Element.Kind)).Where(g => g.Count() == 1).Select(g => g.Single()).ToList();
            var renames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var v in vanished)
                if (fresh.TryGetValue((LocalPath(v.Symbol, v.Ns), v.Element.Kind), out var t) && v.Symbol != t.Symbol) renames[v.Symbol] = t.Symbol;
            if (renames.Count == 0) return;

            // The symbol itself or its nearest renamed prefix ("Old.Member" -> "New.Member").
            string Rekey(string symbol)
            {
                for (var prefix = symbol; ;)
                {
                    if (renames.TryGetValue(prefix, out var target)) return target + symbol[prefix.Length..];
                    var dot = prefix.LastIndexOf('.');
                    if (dot <= 0) return symbol;
                    prefix = prefix[..dot];
                }
            }

            bool HasVanishedOuter(string symbol)
            {
                for (var dot = symbol.LastIndexOf('.'); dot > 0; dot = symbol.LastIndexOf('.', dot - 1))
                {
                    var outer = symbol[..dot];
                    if (!_codeTypes.ContainsKey(outer) && _existing.TryGetValue(outer, out var oe)
                        && oe.Kind is ElementKind.Block or ElementKind.InterfaceBlock or ElementKind.Enumeration) return true;
                }
                return false;
            }
            var rekeyed = new Dictionary<string, Element>(StringComparer.Ordinal);
            foreach (var (symbol, e) in _existing.ToList())
            {
                string next;
                var arrow = symbol.IndexOf("->", StringComparison.Ordinal);
                var colon = symbol.IndexOf(':');
                // Namespace packages and the dependencies between them are keyed by namespace, which a type move does not change.
                if (symbol.StartsWith(NamespacePrefix, StringComparison.Ordinal) || symbol.StartsWith(PackageDependencyPrefix, StringComparison.Ordinal)) next = symbol;
                else if (e is Relationship && arrow > 0 && colon > 0 && colon < arrow)
                    next = symbol[..(colon + 1)] + Rekey(symbol[(colon + 1)..arrow]) + "->" + Rekey(symbol[(arrow + 2)..]);
                else next = Rekey(symbol);
                if (next == symbol || _existing.ContainsKey(next) && !ReferenceEquals(_existing[next], e)) continue;
                _existing.Remove(symbol);
                rekeyed[next] = e;
                m.Set(e, CodeFieldKeys.Symbol, next);
                if (renames.ContainsKey(symbol)) Changed(e);
            }
            foreach (var (k, e) in rekeyed) _existing[k] = e;
        }

        private static string Target(string relSymbol) => relSymbol[(relSymbol.IndexOf("->", StringComparison.Ordinal) + 2)..];

        public CodeSyncChanges Result()
        {
            var after = m.AllElements.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            var removed = _idsBefore.Where(id => !after.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
            var added = _added.Where(after.Contains).Distinct().ToList();
            var addedSet = added.ToHashSet(StringComparer.Ordinal);
            var changed = _changed.Where(id => after.Contains(id) && !addedSet.Contains(id)).Distinct().ToList();
            return new CodeSyncChanges(added, changed, removed, _warnings) { TrivialDiagramsRemoved = _trivialRemoved };
        }

        // ------------------------------------------------------------------ lookup

        private bool IsOurs(Element e) => e.Get(CodeFieldKeys.Symbol).Length > 0 && e.Get(CodeFieldKeys.Repo) == o.RepoId;

        private void IndexExisting()
        {
            foreach (var e in m.Root.Descendants())
            {
                if (!IsOurs(e)) continue;
                var s = e.Get(CodeFieldKeys.Symbol);
                if (CodeBehaviourMapper.IsBehaviourSymbol(s)) continue;   // kept by the behaviour pass (CodeBehaviourMapper)
                if (!_existing.TryAdd(s, e))
                    _warnings.Add($"Several elements carry the code symbol {s}; only '{_existing[s].QualifiedName}' is kept in sync.");
            }
        }

        private Element EnsureRootPackage()
        {
            var owner = m.Root;
            foreach (var name in RootPackageNames(o.RootPackage))
            {
                var next = owner.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Package && c.Name == name);
                if (next == null)
                {
                    next = m.Create(owner, ElementKind.Package, name);
                    _added.Add(next.Id);
                }
                owner = next;
            }
            return owner;
        }

        // ------------------------------------------------------------------ elements

        private Element NamespacePackage(string ns)
        {
            var owner = _root;
            if (string.IsNullOrEmpty(ns)) return owner;
            var segments = ns.Split('.', StringSplitOptions.RemoveEmptyEntries);
            // A collapsed root namespace has no package: its children (and its own types) live in the root package. The
            // symbols stay the full namespace, so collapsing or not only moves packages.
            var first = _collapsed is not null && segments.Length > 0 && segments[0] == _collapsed ? 1 : 0;
            for (var i = first; i < segments.Length; i++)
            {
                var symbol = NamespacePrefix + string.Join(".", segments.Take(i + 1));
                owner = Ensure(symbol, ElementKind.Package, owner, o.ReadableNames ? CodeNames.Humanize(segments[i]) : segments[i], [], null, null);
            }
            return owner;
        }

        private void EnsureType(CodeType t, int depth)
        {
            if (_types.ContainsKey(t.Symbol) || depth > 64) return;
            var kind = t.Kind switch
            {
                CodeTypeKind.Interface => ElementKind.InterfaceBlock,
                CodeTypeKind.Enum => ElementKind.Enumeration,
                _ => ElementKind.Block,
            };
            // Nested types live in their outer type when it can own them, otherwise in the namespace package.
            Element owner = NamespacePackage(t.Namespace);
            var dot = t.Symbol.LastIndexOf('.');
            if (dot > 0 && t.Symbol[..dot] is var outer && outer != t.Namespace && _codeTypes.TryGetValue(outer, out var ot))
            {
                EnsureType(ot, depth + 1);
                if (_types.TryGetValue(outer, out var oe) && Metamodel.CanOwn(oe.Kind, kind)) owner = oe;
            }
            var fields = new List<(string, string?)>();
            if (!string.IsNullOrEmpty(t.Doc)) fields.Add((FieldKeys.Documentation, t.Doc));
            if (kind == ElementKind.Block) fields.Add((FieldKeys.IsAbstract, t.IsAbstract ? "true" : null));
            _types[t.Symbol] = Ensure(t.Symbol, kind, owner, TypeName(t), fields, t.File, t.Line);
        }

        private void EnsureMembers(CodeType t, Element te, SortedDictionary<string, bool> parts, SortedSet<string> refs, SortedSet<(string, string)> deps)
        {
            var symbols = new HashSet<string>(StringComparer.Ordinal);
            MigrateOperations(t);
            foreach (var mem in t.Members)
            {
                if (string.IsNullOrEmpty(mem.Name)) continue;
                var isPublic = mem.Kind == CodeMemberKind.EnumValue || mem.Visibility == "public";
                var target = mem.TypeSymbol != null && _types.TryGetValue(mem.TypeSymbol, out var tgt) ? tgt : null;
                var targetKind = target != null ? _codeTypes[mem.TypeSymbol!].Kind : (CodeTypeKind?)null;
                if (!isPublic && !o.IncludeNonPublic)
                {
                    // A hidden member's type is still a usage of that type.
                    if (target != null && mem.Kind != CodeMemberKind.Method) deps.Add((t.Symbol, mem.TypeSymbol!));
                    continue;
                }
                switch (mem.Kind)
                {
                    case CodeMemberKind.EnumValue:
                    {
                        var symbol = $"{t.Symbol}.{mem.Name}";
                        if (te.Kind != ElementKind.Enumeration || !symbols.Add(symbol)) continue;
                        Ensure(symbol, ElementKind.EnumerationLiteral, te, mem.Name, [], t.File, t.Line);
                        break;
                    }
                    case CodeMemberKind.Method:
                    {
                        var symbol = OperationSymbol(t.Symbol, mem);
                        if (!Metamodel.CanOwn(te.Kind, ElementKind.Operation) || !symbols.Add(symbol)) continue;
                        var fields = new List<(string, string?)>
                        {
                            (FieldKeys.Signature, Signature(mem)), (FieldKeys.Visibility, ModelVisibility(mem.Visibility)),
                            // Interface operations are abstract by nature; only a class's abstract operations are flagged.
                            (FieldKeys.IsAbstract, mem.IsAbstract && te.Kind != ElementKind.InterfaceBlock ? "true" : null),
                            (IsStaticKey, mem.IsStatic ? "true" : null),
                        };
                        if (!string.IsNullOrEmpty(mem.Doc)) fields.Add((FieldKeys.Documentation, mem.Doc));
                        Ensure(symbol, ElementKind.Operation, te, mem.Name, fields, t.File, mem.Line > 0 ? mem.Line : t.Line);
                        break;
                    }
                    default:
                    {
                        var symbol = $"{t.Symbol}.{mem.Name}";
                        if (!Metamodel.CanOwn(te.Kind, ElementKind.Property) || !symbols.Add(symbol)) continue;
                        var multiplicity = mem.IsCollection ? "*" : mem.IsOptional || mem.TypeName.EndsWith('?') ? "0..1" : "1";
                        var classTyped = targetKind is CodeTypeKind.Class or CodeTypeKind.Struct or CodeTypeKind.Record;
                        var owned = classTyped && Owns(t, mem.TypeSymbol!, targetKind!.Value);
                        var fields = new List<(string, string?)>
                        {
                            (FieldKeys.Type, target?.Id ?? mem.TypeName), (FieldKeys.Multiplicity, multiplicity), (FieldKeys.Visibility, ModelVisibility(mem.Visibility)),
                            (IsStaticKey, mem.IsStatic ? "true" : null),
                            // A class-typed member the type does not create points at an instance owned elsewhere: a reference.
                            (FieldKeys.Aggregation, classTyped && !owned ? "none" : null),
                        };
                        if (!string.IsNullOrEmpty(mem.Doc)) fields.Add((FieldKeys.Documentation, mem.Doc));
                        Ensure(symbol, ElementKind.Property, te, mem.Name, fields, t.File, mem.Line > 0 ? mem.Line : t.Line);
                        if (owned)
                            parts[mem.TypeSymbol!] = (parts.TryGetValue(mem.TypeSymbol!, out var many) && many) || mem.IsCollection;
                        else if (classTyped || targetKind == CodeTypeKind.Interface) refs.Add(mem.TypeSymbol!);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Keeps operations in place when their symbol changes: per method name, when exactly one existing operation of the
        /// type has no counterpart in the code any more and at least one overload of that name is new, the existing element
        /// is re-keyed to the first new overload (declaration order) instead of being deleted and created again. This
        /// migrates the single "Type.Method()" operation that earlier syncs made for all overloads, and keeps an operation
        /// (its id, diagrams and hand-made content) when a parameter type changes.
        /// </summary>
        private void MigrateOperations(CodeType t)
        {
            var methods = t.Members.Where(x => x.Kind == CodeMemberKind.Method && !string.IsNullOrEmpty(x.Name) && (x.Visibility == "public" || o.IncludeNonPublic)).ToList();
            if (methods.Count == 0) return;
            var current = methods.Select(x => OperationSymbol(t.Symbol, x)).ToHashSet(StringComparer.Ordinal);
            var prefix = t.Symbol + ".";
            foreach (var group in methods.GroupBy(x => x.Name, StringComparer.Ordinal))
            {
                var head = prefix + group.Key + "(";
                var stale = _existing.Where(kv => kv.Key.StartsWith(head, StringComparison.Ordinal) && kv.Key.EndsWith(')')
                                                 && !current.Contains(kv.Key) && kv.Value.Kind == ElementKind.Operation && m.Contains(kv.Value))
                    .ToList();
                if (stale.Count != 1) continue;
                var fresh = group.Select(x => OperationSymbol(t.Symbol, x)).Where(s => !_existing.ContainsKey(s)).ToList();
                if (fresh.Count == 0) continue;
                var (old, e) = stale[0];
                _existing.Remove(old);
                _existing[fresh[0]] = e;
                m.Set(e, CodeFieldKeys.Symbol, fresh[0]);
                Changed(e);
            }
        }

        /// <summary>
        /// Whether a type owns the values of a class-typed member (a part, with a composition) rather than pointing at
        /// instances made elsewhere (a reference, with an association): structs are values, and a class or record is owned
        /// when the type creates it (<c>new</c>). Code read without use locations keeps the old rule: every class-typed member is a part.
        /// </summary>
        private static bool Owns(CodeType t, string target, CodeTypeKind targetKind) =>
            targetKind == CodeTypeKind.Struct || t.DependencyLocations.Count == 0
            || t.DependencyLocations.Any(u => u.Symbol == target && u.Kind == CodeDependencyKind.New);

        /// <summary>"A.B.C" → "A.B", "A" (every dotted prefix, longest first).</summary>
        private static IEnumerable<string> OuterSymbols(string symbol)
        {
            for (var dot = symbol.LastIndexOf('.'); dot > 0; dot = symbol.LastIndexOf('.', dot - 1)) yield return symbol[..dot];
        }

        /// <summary>The name a type's element gets: words (<see cref="CodeNames"/>) unless readable names are off; generic
        /// parameters are kept ("Box&lt;T&gt;" → "Box&lt;T&gt;", "TypedList&lt;T&gt;" → "Typed list&lt;T&gt;").</summary>
        private string TypeName(CodeType t)
        {
            if (!o.ReadableNames) return t.Name;
            var lt = t.Name.IndexOf('<');
            var readable = CodeNames.Humanize(lt > 0 ? t.Name[..lt] : t.Name, t.Kind == CodeTypeKind.Interface);
            return lt > 0 ? readable + t.Name[lt..] : readable;
        }

        private static string ModelVisibility(string v) => v switch
        {
            "public" => "public",
            "protected" or "protected internal" => "protected",
            "internal" => "package",
            _ => "private",
        };

        /// <summary>Finds or creates the element for a symbol and brings its owned fields, name and owner up to date.</summary>
        private Element Ensure(string symbol, ElementKind kind, Element owner, string name, List<(string Key, string? Value)> fields, string? file, int? line)
        {
            _seen.Add(symbol);
            if (_existing.TryGetValue(symbol, out var e) && m.Contains(e))
            {
                if (e.Kind != kind)
                {
                    if (!Replaceable(e))
                    {
                        _warnings.Add($"{symbol} changed from {Metamodel.DisplayName(e.Kind)} to {Metamodel.DisplayName(kind)}, but '{e.QualifiedName}' holds model content; it was kept and marked removed.");
                        MarkRemoved(e);
                        _existing.Remove(symbol);
                        // The old element keeps its symbol; give it a distinct one so the new element owns the symbol.
                        m.Set(e, CodeFieldKeys.Symbol, symbol + "#" + e.Kind);
                    }
                    else m.Delete(e);
                }
                else
                {
                    var changed = false;
                    if (e.Get(LabelKey) is { Length: > 0 } label) name = label;   // written by the AI pass
                    if (!ReferenceEquals(e.Owner, owner) && !e.IsAncestorOf(owner) && Metamodel.CanOwn(owner.Kind, e.Kind))
                    {
                        m.Move(e, owner);
                        changed = true;
                    }
                    if (e.Name != name) { m.Rename(e, name); changed = true; }
                    foreach (var (k, v) in fields)
                    {
                        if (k == FieldKeys.Documentation && e.Get(CodeAiDescriptions.WrittenKey) == "true")
                        {
                            // A purpose the AI pass wrote stays over code sync's own summary; a doc comment in the code wins.
                            if (symbol.StartsWith(ArchitecturePrefix, StringComparison.Ordinal) || string.IsNullOrEmpty(v)) continue;
                            m.Set(e, CodeAiDescriptions.WrittenKey, null);
                        }
                        if (e.Get(k) != (v ?? "")) { m.Set(e, k, v); changed = true; }
                    }
                    if (e.Get(RemovedKey).Length > 0) { m.Set(e, RemovedKey, null); changed = true; }
                    // Location-only edits (lines shifting) are kept current but are not reported as changes.
                    if (file != null) m.Set(e, CodeFieldKeys.File, file);
                    if (line != null) m.Set(e, CodeFieldKeys.Line, line.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    StampSubmodule(e, file);
                    if (changed) Changed(e);
                    return e;
                }
            }

            var created = m.Create(owner, kind, name);
            foreach (var (k, v) in fields) m.Set(created, k, v);
            Stamp(created, symbol, file, line);
            _existing[symbol] = created;
            _added.Add(created.Id);
            return created;
        }

        /// <summary>
        /// Deployment artifacts (<see cref="CodeSyncOptions.Artifacts"/>): one for the repository, owned by and «manifest»ing
        /// the root package, or one per top-level namespace package, owned by and «manifest»ing that package. Symbols
        /// "artifact:" / "artifact:ns:X" and "manifest:artifact:…->…"; switching the option off removes them like any other gone code
        /// element (kept and marked removed when someone deployed them).
        /// </summary>
        private void EnsureArtifacts()
        {
            if (o.Artifacts == CodeArtifactMode.None) return;
            var targets = new List<(string Key, Element Package)>();
            if (o.Artifacts == CodeArtifactMode.Repository) targets.Add(("", _root));
            else
                foreach (var p in _root.OwnedElements.Where(c => IsNamespacePackage(c) && IsOurs(c) && _seen.Contains(c.Get(CodeFieldKeys.Symbol)))
                                                     .OrderBy(c => c.Get(CodeFieldKeys.Symbol), StringComparer.Ordinal).ToList())
                    targets.Add((p.Get(CodeFieldKeys.Symbol), p));
            foreach (var (key, pkg) in targets)
            {
                var symbol = "artifact:" + key;
                var artifact = Ensure(symbol, ElementKind.Artifact, pkg, pkg.Name, [], null, null);
                EnsureRelationship($"manifest:{symbol}->{(key.Length == 0 ? "root" : key)}", ElementKind.Manifestation, null, artifact, pkg, null);
            }
        }

        /// <summary>
        /// Connectors between the parts of a block whose types use each other in code (symbol "conn:A.p->A.q"), so the
        /// block's internal structure shows how its parts work together. At most <see cref="MaxConnectorsPerBlock"/> per block.
        /// </summary>
        private void EnsurePartConnectors(List<CodeType> ordered, HashSet<(string, string)> linked)
        {
            foreach (var t in ordered)
            {
                if (!_types.TryGetValue(t.Symbol, out var te) || te.Kind != ElementKind.Block) continue;
                var parts = AutoDiagrammer.Parts(m, te)
                    .Select(p => (Part: p, Symbol: p.Get(CodeFieldKeys.Symbol), Type: m.ResolveType(p)?.Get(CodeFieldKeys.Symbol) ?? ""))
                    .Where(x => x.Symbol.StartsWith(t.Symbol + ".", StringComparison.Ordinal) && x.Type.Length > 0 && IsOurs(x.Part))
                    .OrderBy(x => x.Symbol, StringComparer.Ordinal).ToList();
                if (parts.Count < 2) continue;
                var made = 0;
                for (var i = 0; i < parts.Count && made < MaxConnectorsPerBlock; i++)
                    for (var j = i + 1; j < parts.Count && made < MaxConnectorsPerBlock; j++)
                    {
                        var (a, b) = (parts[i], parts[j]);
                        if (a.Type == b.Type) continue;
                        var forward = linked.Contains((a.Type, b.Type));
                        if (!forward && !linked.Contains((b.Type, a.Type))) continue;
                        if (!forward) (a, b) = (b, a);
                        var (an, bn) = (TypeName(_codeTypes[a.Type]), TypeName(_codeTypes[b.Type]));
                        EnsureRelationship($"conn:{a.Symbol}->{b.Symbol}", ElementKind.Connector, t, a.Part, b.Part, null,
                            [(FieldKeys.Documentation, $"Derived from code: {an} uses {bn}.")], te);
                        made++;
                    }
            }
        }

        /// <summary>
        /// Marks the key blocks of each namespace package (<see cref="KeyKey"/>): its public or internal top-level classes,
        /// structs, records and interfaces, all of them when there are at most <see cref="KeyBlocksPerPackage"/>, otherwise
        /// the ones the most other types use (ties by symbol). Not reported as a change.
        /// </summary>
        private void MarkKeyBlocks(List<CodeType> ordered, HashSet<(string, string)> linked)
        {
            var users = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (from, to) in linked)
                if (from != to && _types.ContainsKey(from) && _types.ContainsKey(to)) users[to] = users.GetValueOrDefault(to) + 1;
            var key = new HashSet<string>(StringComparer.Ordinal);
            var candidates = ordered.Where(t => t.Kind != CodeTypeKind.Enum && t.Visibility is "public" or "internal"
                                                && _types.TryGetValue(t.Symbol, out var e) && e.Owner?.Kind is ElementKind.Package or ElementKind.Model);
            foreach (var g in candidates.GroupBy(t => _types[t.Symbol].Owner!.Id, StringComparer.Ordinal))
            {
                var list = g.ToList();
                var chosen = list.Count <= KeyBlocksPerPackage ? list
                    : list.Where(t => users.GetValueOrDefault(t.Symbol) > 0).OrderByDescending(t => users.GetValueOrDefault(t.Symbol))
                          .ThenBy(t => t.Symbol, StringComparer.Ordinal).Take(KeyBlocksPerPackage);
                foreach (var t in chosen) key.Add(t.Symbol);
            }
            foreach (var (symbol, e) in _types)
            {
                var value = key.Contains(symbol) ? "true" : null;
                if (e.Get(KeyKey) != (value ?? "")) m.Set(e, KeyKey, value);
            }
        }

        /// <summary>
        /// The system architecture from the projects (<see cref="CodeSyncOptions.Subsystems"/>): an "Architecture" package
        /// with a «system» block (named like the root package) whose parts are one «subsystem» block per non-test project
        /// holding synced types. Where one subsystem uses an interface another implements, the user gets a port requiring it,
        /// the implementer a port providing it (both typed by the interface block) and a connector joins them; other use
        /// between subsystems is a plain connector between their parts, documented with the types it goes through.
        /// </summary>
        private void EnsureSubsystems(HashSet<(string, string)> linked, Dictionary<string, CodeModule> modules)
        {
            if (!o.Subsystems) return;
            var moduleOf = _codeTypes.Values.Where(t => t.Module is not null && _types.ContainsKey(t.Symbol)
                                                        && modules.TryGetValue(t.Module, out var md) && (!md.IsTest || o.IncludeTests))
                .ToDictionary(t => t.Symbol, t => t.Module!, StringComparer.Ordinal);
            var names = moduleOf.Values.Distinct().Order(StringComparer.Ordinal).ToList();
            if (names.Count < 2) return;
            // Projects are named by their project file ("Meridian.Desktop.csproj" builds the assembly "Meridian"), without the
            // prefix most of them share ("Meridian.").
            string Title(string n) => modules[n].File is { Length: > 0 } f ? System.IO.Path.GetFileNameWithoutExtension(f) : n;
            var prefix = CommonPrefix(names.Select(Title).ToList());
            string Readable(string n)
            {
                var title = Title(n);
                var core = title.StartsWith(prefix, StringComparison.Ordinal) && title.Length > prefix.Length ? title[prefix.Length..] : title;
                return o.ReadableNames ? string.Join(" ", core.Split('.', StringSplitOptions.RemoveEmptyEntries).Select((x, i) => i == 0 ? CodeNames.Humanize(x) : CodeNames.Lower(CodeNames.Humanize(x)))) : core;
            }

            var pkg = Ensure(ArchitecturePrefix, ElementKind.Package, _root, "Architecture",
                [(FieldKeys.Documentation, "The system and its subsystems, derived from the projects in the code: one subsystem per project, connected where one uses another.")], null, null);
            var system = Ensure(ArchitecturePrefix + "system", ElementKind.Block, pkg, o.ReadableNames ? CodeNames.Humanize(_root.Name) : _root.Name,
                [(FieldKeys.Stereotypes, "system"), (FieldKeys.Documentation, $"The system built from this repository: {names.Count} subsystems, one per project, and the interfaces between them.")], null, null);
            var blocks = new Dictionary<string, Element>(StringComparer.Ordinal);
            var parts = new Dictionary<string, Element>(StringComparer.Ordinal);
            foreach (var n in names)
            {
                var md = modules[n];
                var types = moduleOf.Where(kv => kv.Value == n).Select(kv => _codeTypes[kv.Key]).ToList();
                var namespaces = types.Select(t => t.Namespace).Distinct().Count();
                var summary = $"Project {n} ({md.File}): {types.Count} type{(types.Count == 1 ? "" : "s")} in {namespaces} namespace{(namespaces == 1 ? "" : "s")}.";
                var doc = md.Description is { Length: > 0 } d ? $"{d}\n\n{summary}" : summary;
                var b = blocks[n] = Ensure(ArchitecturePrefix + "subsystem:" + n, ElementKind.Block, pkg, Readable(n),
                    [(FieldKeys.Stereotypes, "subsystem"), (FieldKeys.Documentation, doc)], md.File.Length > 0 ? md.File : null, null);
                parts[n] = Ensure(ArchitecturePrefix + "part:" + n, ElementKind.Property, system, CodeNames.Lower(Readable(n)),
                    [(FieldKeys.Type, b.Id), (FieldKeys.Multiplicity, "1")], md.File.Length > 0 ? md.File : null, null);
                EnsureRelationship($"{ArchitecturePrefix}comp:system->{n}", ElementKind.Composition, null, system, b, "1");
            }

            // Which subsystem uses which, through which types (counted by the user types).
            var use = new SortedDictionary<(string A, string B), Dictionary<string, int>>();
            foreach (var (from, to) in linked)
            {
                if (!moduleOf.TryGetValue(from, out var a) || !moduleOf.TryGetValue(to, out var b) || a == b) continue;
                if (!use.TryGetValue((a, b), out var targets)) use[(a, b)] = targets = new Dictionary<string, int>(StringComparer.Ordinal);
                targets[to] = targets.GetValueOrDefault(to) + 1;
            }
            var implementers = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            foreach (var (symbol, module) in moduleOf)
                foreach (var i in _codeTypes[symbol].Interfaces)
                {
                    if (!implementers.TryGetValue(i, out var set)) implementers[i] = set = new SortedSet<string>(StringComparer.Ordinal);
                    set.Add(module);
                }
            var edges = new List<(string A, string P, string I, int Count)>();
            foreach (var ((a, _), targets) in use)
                foreach (var (target, count) in targets)
                {
                    if (_codeTypes[target].Kind != CodeTypeKind.Interface) continue;
                    // Provided by the subsystems that implement it (the one that declares it when none does); a subsystem
                    // that implements it itself does not need it from elsewhere.
                    var impl = implementers.TryGetValue(target, out var set) ? set : [];
                    if (impl.Contains(a)) continue;
                    var providers = impl.Count > 0 ? impl.ToList() : moduleOf[target] != a ? [moduleOf[target]] : [];
                    foreach (var p in providers) edges.Add((a, p, target, count));
                }
            var ports = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            bool Room(string module, string port) =>
                ports.TryGetValue(module, out var set) ? set.Contains(port) || set.Count < MaxPortsPerSubsystem : true;
            void Take(string module, string port)
            {
                if (!ports.TryGetValue(module, out var set)) ports[module] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(port);
            }
            var covered = new HashSet<(string, string)>();
            foreach (var (a, p, i, _) in edges.OrderByDescending(x => x.Count).ThenBy(x => x.A, StringComparer.Ordinal).ThenBy(x => x.P, StringComparer.Ordinal).ThenBy(x => x.I, StringComparer.Ordinal).ToList())
            {
                var (req, prov) = ("requires:" + i, "provides:" + i);
                if (!Room(a, req) || !Room(p, prov)) continue;
                Take(a, req);
                Take(p, prov);
                // Names and texts from the code (not the elements' current names, which the AI pass may have reworded),
                // so a re-sync of unchanged code changes nothing.
                var iface = _types[i];
                var ifaceName = TypeName(_codeTypes[i]);
                var provided = Ensure($"{ArchitecturePrefix}port:{p}:{prov}", ElementKind.Port, blocks[p], ifaceName,
                    [(FieldKeys.Type, iface.Id), (FieldKeys.Documentation, $"{Readable(p)} provides {ifaceName} to the other subsystems.")], null, null);
                // Named like the interface on both ends (a subsystem never both provides and requires one); the
                // connector's direction and the documentation say who uses whom.
                var required = Ensure($"{ArchitecturePrefix}port:{a}:{req}", ElementKind.Port, blocks[a], ifaceName,
                    [(FieldKeys.Type, iface.Id), (FieldKeys.Documentation, $"{Readable(a)} needs {ifaceName} from another subsystem.")], null, null);
                EnsureRelationship($"{ArchitecturePrefix}conn:{a}->{p}:{i}", ElementKind.Connector, null, required, provided, null,
                    [(FieldKeys.SourcePart, parts[a].Id), (FieldKeys.TargetPart, parts[p].Id)], system);
                covered.Add((a, p));
            }
            foreach (var ((a, b), targets) in use)
            {
                if (covered.Contains((a, b))) continue;
                var top = targets.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => TypeName(_codeTypes[kv.Key])).ToList();
                var list = string.Join(", ", top.Take(5)) + (top.Count > 5 ? $" and {top.Count - 5} more" : "");
                EnsureRelationship($"{ArchitecturePrefix}conn:{a}->{b}", ElementKind.Connector, null, parts[a], parts[b], null,
                    [(FieldKeys.Documentation, $"Derived from code: {Readable(a)} uses {Readable(b)} through {list}.")], system);
            }
        }

        /// <summary>The first dotted segment most names start with ("Meridian." for Meridian.Core, Meridian.Web and
        /// HelloPlugin), when at least half of them do; otherwise "".</summary>
        private static string CommonPrefix(IReadOnlyList<string> names)
        {
            var best = names.Where(n => n.Contains('.')).GroupBy(n => n[..(n.IndexOf('.') + 1)], StringComparer.Ordinal)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).FirstOrDefault();
            return best is not null && best.Count() * 2 >= names.Count ? best.Key : "";
        }

        /// <summary>Activities and interactions from method bodies (<see cref="CodeBehaviourMapper"/>), in the same undo step.</summary>
        private void EnsureBehaviour()
        {
            var b = CodeBehaviourMapper.Sync(m, code, o);
            _trivialRemoved += b.TrivialRemoved;
            _added.AddRange(b.Added);
            foreach (var id in b.Changed)
                if (m.Find(id) is { } e) Changed(e);
            _warnings.AddRange(b.Warnings);
        }

        private void EnsureRelationship(string symbol, ElementKind kind, CodeType? from, Element source, Element target, string? multiplicity,
                                        List<(string Key, string? Value)>? fields = null, Element? owner = null)
        {
            if (!_seen.Add(symbol)) return;
            if (Metamodel.CheckEnds(kind, source.Kind, target.Kind) is { } problem)
            {
                _warnings.Add($"{symbol}: {problem}; skipped.");
                _seen.Remove(symbol);
                return;
            }
            owner ??= m.DefaultOwnerFor(source.Owner ?? m.Root, kind);
            // Connectors live with their source end, as ModelRepository.Connect places them (a port's block, a part's block).
            if (kind == ElementKind.Connector) owner = source.Owner ?? m.Root;
            if (_existing.TryGetValue(symbol, out var e) && m.Contains(e))
            {
                if (e is Relationship r && r.Kind == kind && ReferenceEquals(r.Source, source) && ReferenceEquals(r.Target, target))
                {
                    var changed = false;
                    if (!ReferenceEquals(r.Owner, owner)) { m.Move(r, owner); changed = true; }
                    if (multiplicity != null && r.Get(FieldKeys.Multiplicity) != multiplicity) { m.Set(r, FieldKeys.Multiplicity, multiplicity); changed = true; }
                    if (r.Get(RemovedKey).Length > 0) { m.Set(r, RemovedKey, null); changed = true; }
                    foreach (var (k, v) in fields ?? [])
                        if (r.Get(k) != (v ?? "")) { m.Set(r, k, v); changed = true; }
                    if (from != null)
                    {
                        m.Set(r, CodeFieldKeys.File, from.File);
                        m.Set(r, CodeFieldKeys.Line, from.Line.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        StampSubmodule(r, from.File);
                    }
                    if (changed) Changed(r);
                    return;
                }
                m.Delete(e);
            }
            var created = m.Connect(kind, source, target, owner);
            if (multiplicity != null) m.Set(created, FieldKeys.Multiplicity, multiplicity);
            foreach (var (k, v) in fields ?? []) m.Set(created, k, v);
            Stamp(created, symbol, from?.File, from?.Line);
            _existing[symbol] = created;
            _added.Add(created.Id);
        }

        private void Stamp(Element e, string symbol, string? file, int? line)
        {
            m.Set(e, CodeFieldKeys.Symbol, symbol);
            m.Set(e, CodeFieldKeys.Repo, o.RepoId);
            if (file != null) m.Set(e, CodeFieldKeys.File, file);
            if (line != null) m.Set(e, CodeFieldKeys.Line, line.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            StampSubmodule(e, file);
            m.Set(e, CodeFieldKeys.Commit, o.Commit);
        }

        /// <summary>"path@sha" of the submodule the file is in; like the file and line it is kept current without
        /// counting as a change (a bump alone shows as the bump, not as every element of the submodule).</summary>
        private void StampSubmodule(Element e, string? file)
        {
            if (file is null) return;
            var label = CodeSubmodulePins.Containing(o.Submodules, file)?.Label ?? "";
            if (e.Get(CodeFieldKeys.Submodule) != label) m.Set(e, CodeFieldKeys.Submodule, label.Length == 0 ? null : label);
        }

        private void Changed(Element e)
        {
            m.Set(e, CodeFieldKeys.Commit, o.Commit);
            if (_changedSet.Add(e.Id)) _changed.Add(e.Id);
        }

        private void MarkRemoved(Element e)
        {
            if (e.Get(RemovedKey) == "true") return;
            m.Set(e, RemovedKey, "true");
            Changed(e);
        }

        // ------------------------------------------------------------------ removal

        /// <summary>
        /// Can the element go without touching anything the user made? Its subtree must hold only elements from this
        /// repository (no diagrams, no hand-made elements) and no hand-made relationship may touch it.
        /// </summary>
        private bool Replaceable(Element e)
        {
            var subtree = e.Descendants().Prepend(e).ToList();
            foreach (var x in subtree)
            {
                if (x is Diagram || !IsOurs(x)) return false;
                foreach (var r in m.RelationshipsOf(x))
                    if (!IsOurs(r)) return false;
            }
            return true;
        }

        private void RemoveGone()
        {
            var gone = _existing.Where(kv => !_seen.Contains(kv.Key) && m.Contains(kv.Value) && !kv.Key.StartsWith(NamespacePrefix, StringComparison.Ordinal))
                .Select(kv => kv.Value).ToList();
            // Relationships first, then outermost elements first (a deleted element takes its subtree with it); an element
            // that has to stay is only marked, and its own gone members are then judged one by one.
            var order = gone.Where(e => e is Relationship)
                .Concat(gone.Where(e => e is not Relationship).OrderBy(e => e.AncestorsAndSelf().Count()))
                .ToList();
            foreach (var e in order)
            {
                if (!m.Contains(e)) continue;
                if (o.RemoveDeleted && Replaceable(e)) m.Delete(e);
                else
                {
                    if (o.RemoveDeleted && e.Get(RemovedKey) != "true")
                        _warnings.Add($"'{e.QualifiedName}' is no longer in the code but holds model content (diagrams, hand-made elements or relationships); it was marked removed instead of deleted.");
                    MarkRemoved(e);
                }
            }
        }

        private void RemoveEmptyPackages()
        {
            var packages = _existing.Where(kv => kv.Key.StartsWith(NamespacePrefix, StringComparison.Ordinal) && !_seen.Contains(kv.Key) && m.Contains(kv.Value))
                .Select(kv => kv.Value).OrderByDescending(p => p.AncestorsAndSelf().Count()).ThenBy(p => p.Get(CodeFieldKeys.Symbol), StringComparer.Ordinal).ToList();
            foreach (var p in packages)
            {
                if (p.OwnedElements.Count == 0) m.Delete(p);
                else MarkRemoved(p);
            }
        }
    }
}
