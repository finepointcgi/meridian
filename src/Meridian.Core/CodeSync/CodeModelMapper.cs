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

    private static string[] RootPackageNames(string? rootPackage)
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

            foreach (var t in code.Types)
            {
                if (string.IsNullOrEmpty(t.Symbol)) continue;
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

                foreach (var b in t.BaseTypes.Distinct())
                    if (_types.TryGetValue(b, out var be)) rels.Add(($"gen:{t.Symbol}->{b}", ElementKind.Generalization, t, te, be, null));
                foreach (var i in t.Interfaces.Distinct())
                    if (_types.TryGetValue(i, out var ie)) rels.Add(($"real:{t.Symbol}->{i}", ElementKind.Realization, t, te, ie, null));
                foreach (var (target, many) in parts)
                    rels.Add(($"comp:{t.Symbol}->{target}", ElementKind.Composition, t, te, _types[target], many ? "*" : "1"));
                foreach (var target in refs)
                    rels.Add(($"ref:{t.Symbol}->{target}", ElementKind.Association, t, te, _types[target], null));
                if (o.IncludeUsageDependencies)
                    foreach (var d in t.Dependencies) deps.Add((t.Symbol, d));
            }
            foreach (var r in rels) pairs.Add((r.From.Symbol, Target(r.Symbol)));
            if (o.IncludeUsageDependencies)
                foreach (var (from, to) in deps)
                {
                    if (from == to || pairs.Contains((from, to))) continue;
                    if (!_types.TryGetValue(from, out var s) || !_types.TryGetValue(to, out var tt)) continue;
                    rels.Add(($"dep:{from}->{to}", ElementKind.Dependency, _codeTypes[from], s, tt, null));
                }
            foreach (var r in rels) EnsureRelationship(r.Symbol, r.Kind, r.From, r.Source, r.Target, r.Multiplicity);
            EnsurePackageDependencies(rels.Select(r => (r.Source, r.Target)));
            EnsureArtifacts();
            EnsureBehaviour();

            RemoveGone();
            ReleaseCollapsedPackage();
            RemoveEmptyPackages();
            _trivialRemoved += CodeSyncDiagrams.RemoveTrivialIbds(m, o.RepoId, o.SyncedDiagrams);
        }

        /// <summary>
        /// The namespace whose package is left out (<see cref="CodeSyncOptions.CollapseRootNamespace"/>): the first segment
        /// every namespace starts with, when it equals the root package's name or no type is in the global namespace.
        /// </summary>
        private string? CollapsedNamespace()
        {
            if (!o.CollapseRootNamespace) return null;
            string? first = null;
            var global = false;
            foreach (var t in _codeTypes.Values)
            {
                var dot = t.Namespace.IndexOf('.');
                var segment = (dot < 0 ? t.Namespace : t.Namespace[..dot]).Trim();
                if (segment.Length == 0) global = true;
                else if (first is null) first = segment;
                else if (first != segment) return null;
            }
            if (first is null) return null;
            return !global || string.Equals(first, _root.Name, StringComparison.OrdinalIgnoreCase) ? first : null;
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
                owner = Ensure(symbol, ElementKind.Package, owner, segments[i], [], null, null);
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
            _types[t.Symbol] = Ensure(t.Symbol, kind, owner, t.Name, fields, t.File, t.Line);
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
                    if (target != null && mem.Kind != CodeMemberKind.Method && o.IncludeUsageDependencies) deps.Add((t.Symbol, mem.TypeSymbol!));
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
                        var fields = new List<(string, string?)>
                        {
                            (FieldKeys.Type, target?.Id ?? mem.TypeName), (FieldKeys.Multiplicity, multiplicity), (FieldKeys.Visibility, ModelVisibility(mem.Visibility)),
                            (IsStaticKey, mem.IsStatic ? "true" : null),
                        };
                        if (!string.IsNullOrEmpty(mem.Doc)) fields.Add((FieldKeys.Documentation, mem.Doc));
                        Ensure(symbol, ElementKind.Property, te, mem.Name, fields, t.File, mem.Line > 0 ? mem.Line : t.Line);
                        if (targetKind is CodeTypeKind.Class or CodeTypeKind.Struct or CodeTypeKind.Record)
                            parts[mem.TypeSymbol!] = (parts.TryGetValue(mem.TypeSymbol!, out var many) && many) || mem.IsCollection;
                        else if (targetKind == CodeTypeKind.Interface) refs.Add(mem.TypeSymbol!);
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
                    if (!ReferenceEquals(e.Owner, owner) && !e.IsAncestorOf(owner) && Metamodel.CanOwn(owner.Kind, e.Kind))
                    {
                        m.Move(e, owner);
                        changed = true;
                    }
                    if (e.Name != name) { m.Rename(e, name); changed = true; }
                    foreach (var (k, v) in fields)
                        if (e.Get(k) != (v ?? "")) { m.Set(e, k, v); changed = true; }
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
                foreach (var p in _root.OwnedElements.Where(c => c.Kind == ElementKind.Package && IsOurs(c) && _seen.Contains(c.Get(CodeFieldKeys.Symbol)))
                                                     .OrderBy(c => c.Get(CodeFieldKeys.Symbol), StringComparer.Ordinal).ToList())
                    targets.Add((p.Get(CodeFieldKeys.Symbol), p));
            foreach (var (key, pkg) in targets)
            {
                var symbol = "artifact:" + key;
                var artifact = Ensure(symbol, ElementKind.Artifact, pkg, pkg.Name, [], null, null);
                EnsureRelationship($"manifest:{symbol}->{(key.Length == 0 ? "root" : key)}", ElementKind.Manifestation, null, artifact, pkg, null);
            }
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
                                        List<(string Key, string? Value)>? fields = null)
        {
            if (!_seen.Add(symbol)) return;
            if (Metamodel.CheckEnds(kind, source.Kind, target.Kind) is { } problem)
            {
                _warnings.Add($"{symbol}: {problem}; skipped.");
                _seen.Remove(symbol);
                return;
            }
            var owner = m.DefaultOwnerFor(source.Owner ?? m.Root, kind);
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
