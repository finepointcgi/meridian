using Meridian.Core.CodeSync;
using Meridian.Serialization.CodeSync.Parsing;

namespace Meridian.Serialization.CodeSync;

/// <summary>
/// Reads TypeScript (<c>.ts</c>, <c>.tsx</c>, <c>.mts</c>, <c>.cts</c>) with a hand-written, tolerant parser (no Node.js):
/// classes, interfaces, enums, type aliases of object types and namespaces, with their properties, methods (parameters,
/// overloads), <c>extends</c> / <c>implements</c> and constructor parameter properties. Folders become packages
/// ("src/power/battery.ts" → package <c>src.power</c>); a <c>namespace</c> adds packages below that. Names resolve
/// through the file's own declarations, relative imports (named, default, namespace, re-exports and <c>index.ts</c>
/// barrels) and the declarations of script files (files without imports or exports are global). Decorators are ignored;
/// everything under <c>node_modules/</c> is skipped.
/// </summary>
public sealed class TypeScriptCodeReader : HandParsedCodeReader
{
    public override string Language => "typescript";

    public override bool Handles(string path)
    {
        var p = "/" + (path ?? "").Replace('\\', '/').TrimStart('/');
        if (p.Contains("/node_modules/", StringComparison.OrdinalIgnoreCase)) return false;
        return p.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".tsx", StringComparison.OrdinalIgnoreCase)
            || p.EndsWith(".mts", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".cts", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly TypeVocabulary Vocab = new()
    {
        Collections = new(StringComparer.Ordinal) { "Array", "ReadonlyArray", "Set", "ReadonlySet", "WeakSet", "Iterable", "IterableIterator", "AsyncIterable", "AsyncIterableIterator", "Generator", "AsyncGenerator" },
        Maps = new(StringComparer.Ordinal) { "Map", "ReadonlyMap", "WeakMap", "Record" },
        Wrappers = new(StringComparer.Ordinal) { "Promise", "PromiseLike", "Readonly", "Partial", "Required", "NonNullable", "Awaited" },
        NullNames = new(StringComparer.Ordinal) { "null", "undefined", "void" },
    };

    internal override TypeVocabulary Vocabulary => Vocab;

    internal override ParseSession CreateSession(IReadOnlyList<CodeFile> files) => new Session(files);

    private sealed record Import(string? File, string Name, bool IsNamespace, bool IsAlias = false);

    private sealed class TsFile(string path, string ns)
    {
        public string Path { get; } = path;
        public string Namespace { get; } = ns;
        public Dictionary<string, string> Locals { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Import> Imports { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (string? File, string Name)> ReExports { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> NamespaceReExports { get; } = new(StringComparer.Ordinal);
        public List<string?> StarReExports { get; } = [];
        public Dictionary<string, string> ExportAliases { get; } = new(StringComparer.Ordinal);
        public string? DefaultExport { get; set; }
        public bool IsModule { get; set; }
    }

    private sealed record Scope(TsFile File, string[] NsPath);

    private sealed class Session(IReadOnlyList<CodeFile> all) : ParseSession
    {
        private readonly HashSet<string> _paths = all.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        private readonly Dictionary<string, TsFile> _files = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _globals = new(StringComparer.Ordinal);

        public override void Parse(CodeFile file, List<RawType> types, ParseIssues issues, CancellationToken ct)
        {
            var f = new TsFile(file.Path, FolderNamespace(file.Path));
            _files[file.Path] = f;
            var toks = Lexer.CLike(file.Content, typeScript: true);
            new Parser(this, f, toks, types, issues, ct).Statements(0, toks.Count, []);
            if (!f.IsModule)
                foreach (var (name, symbol) in f.Locals)
                {
                    if (!_globals.TryGetValue(name, out var list)) _globals[name] = list = [];
                    list.Add(symbol);
                }
        }

        public override string? Resolve(RawType from, string name)
        {
            if (from.Scope is not Scope scope) return null;
            for (var k = scope.NsPath.Length; k >= 0; k--)
            {
                var key = k == 0 ? name : string.Join(".", scope.NsPath.Take(k)) + "." + name;
                if (scope.File.Locals.TryGetValue(key, out var s)) return s;
            }
            if (ViaImport(scope.File, name, 0, []) is { } imported) return imported;
            if (_globals.TryGetValue(name, out var g) && g.Count == 1) return g[0];
            return null;
        }

        private string? ViaImport(TsFile f, string name, int depth, HashSet<string> visited)
        {
            var (first, rest) = Split(name);
            if (!f.Imports.TryGetValue(first, out var imp)) return null;
            if (imp.IsAlias)
            {
                var target = imp.Name + (rest.Length > 0 ? "." + rest : "");
                return f.Locals.TryGetValue(target, out var s) ? s : _globals.TryGetValue(target, out var g) && g.Count == 1 ? g[0] : null;
            }
            if (imp.File == null) return null;
            if (imp.IsNamespace) return rest.Length == 0 ? null : Export(imp.File, rest, depth + 1, visited);
            return Export(imp.File, rest.Length > 0 ? imp.Name + "." + rest : imp.Name, depth + 1, visited);
        }

        /// <summary>What <paramref name="name"/> exported from <paramref name="path"/> is, following re-exports.</summary>
        private string? Export(string path, string name, int depth, HashSet<string> visited)
        {
            if (depth > 16 || !_files.TryGetValue(path, out var f) || !visited.Add(path + "|" + name)) return null;
            var (first, rest) = Split(name);
            if (first == "default")
            {
                if (f.DefaultExport == null) return null;
                name = f.DefaultExport + (rest.Length > 0 ? "." + rest : "");
                (first, rest) = Split(name);
            }
            if (f.Locals.TryGetValue(name, out var s)) return s;
            string Join(string head) => head + (rest.Length > 0 ? "." + rest : "");
            if (f.ExportAliases.TryGetValue(first, out var local))
            {
                if (f.Locals.TryGetValue(Join(local), out var al)) return al;
                if (ViaImport(f, Join(local), depth + 1, visited) is { } ai) return ai;
            }
            if (f.ReExports.TryGetValue(first, out var re) && re.File != null) return Export(re.File, Join(re.Name), depth + 1, visited);
            if (f.NamespaceReExports.TryGetValue(first, out var nsFile) && nsFile != null && rest.Length > 0) return Export(nsFile, rest, depth + 1, visited);
            if (ViaImport(f, name, depth + 1, visited) is { } viaImport) return viaImport;
            foreach (var star in f.StarReExports)
                if (star != null && Export(star, name, depth + 1, visited) is { } found) return found;
            return null;
        }

        private static (string Head, string Tail) Split(string name)
        {
            var dot = name.IndexOf('.');
            return dot < 0 ? (name, "") : (name[..dot], name[(dot + 1)..]);
        }

        /// <summary>The scanned file a module specifier names (relative, root-relative or "@/" → "src/"), or null.</summary>
        public string? ResolveModule(string fromPath, string spec)
        {
            if (string.IsNullOrWhiteSpace(spec)) return null;
            string p;
            if (spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal) || spec is "." or "..")
            {
                var dir = fromPath.Contains('/') ? fromPath[..fromPath.LastIndexOf('/')] : "";
                p = Normalize(dir.Length == 0 ? spec : dir + "/" + spec);
            }
            else if (spec.StartsWith("@/", StringComparison.Ordinal) || spec.StartsWith("~/", StringComparison.Ordinal)) p = Normalize("src/" + spec[2..]);
            else p = Normalize(spec);
            if (p.Length == 0 && !spec.StartsWith('.')) return null;
            var stem = p;
            foreach (var ext in new[] { ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts" })
                if (stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { stem = stem[..^ext.Length]; break; }
            var prefix = stem.Length == 0 ? "" : stem + "/";
            foreach (var c in new[] { p, stem + ".ts", stem + ".tsx", stem + ".d.ts", stem + ".mts", stem + ".cts", prefix + "index.ts", prefix + "index.tsx", prefix + "index.d.ts" })
                if (_paths.Contains(c)) return c;
            return null;
        }

        private static string Normalize(string path)
        {
            var parts = new List<string>();
            foreach (var s in path.Replace('\\', '/').Split('/'))
            {
                if (s is "" or ".") continue;
                if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
                parts.Add(s);
            }
            return string.Join("/", parts);
        }
    }

    /// <summary>"src/power/battery.ts" → "src.power" (dots inside folder names become '_').</summary>
    internal static string FolderNamespace(string path)
    {
        var p = path.Replace('\\', '/').Trim('/');
        var slash = p.LastIndexOf('/');
        if (slash < 0) return "";
        return string.Join(".", p[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Replace('.', '_')));
    }

    private sealed class Parser(Session session, TsFile f, List<Tok> toks, List<RawType> types, ParseIssues issues, CancellationToken ct)
    {
        private string? _pendingDoc;

        private static readonly HashSet<string> MemberModifiers = new(StringComparer.Ordinal)
        { "public", "private", "protected", "static", "readonly", "abstract", "declare", "override", "async", "accessor", "get", "set" };

        private static readonly HashSet<string> NotTypeEnd = new(StringComparer.Ordinal)
        { "keyof", "typeof", "readonly", "extends", "is", "infer", "new", "unique", "asserts" };

        private static readonly HashSet<string> TypeContinuation = new(StringComparer.Ordinal) { "|", "&", ".", "=>", "?", "extends", "is" };

        private static readonly HashSet<string> ValueKeywords = new(StringComparer.Ordinal)
        { "new", "return", "typeof", "await", "in", "of", "instanceof", "as", "satisfies", "void", "delete", "yield" };

        // ------------------------------------------------------------------ statements

        public void Statements(int from, int end, string[] ns)
        {
            var i = from;
            while (i < end)
            {
                ct.ThrowIfCancellationRequested();
                var t = toks[i];
                if (t.Kind != TokKind.Ident)
                {
                    if (t.Is("@")) { _pendingDoc ??= t.Doc; i = SkipDecorator(i, end); continue; }
                    _pendingDoc = null;
                    i = t.Is("{") ? Math.Min(end, TypeExprs.Matching(toks, i, end) + 1) : i + 1;
                    continue;
                }
                if (i > 0 && (toks[i - 1].Is(".") || toks[i - 1].Is("?."))) { i++; continue; }
                int next;
                if (t.Text == "import") next = ImportStatement(i, end);
                else if (t.Text == "export")
                {
                    // Exports inside a namespace do not make the file a module.
                    if (ns.Length == 0) f.IsModule = true;
                    next = Export(i, end, ns);
                }
                else next = Declaration(i, end, ns, false, i);
                _pendingDoc = null;
                i = next > i ? next : i + 1;
            }
        }

        private int Declaration(int i, int end, string[] ns, bool isDefault, int declStart)
        {
            var j = i;
            var isAbstract = false;
            while (j < end && toks[j].Kind == TokKind.Ident && toks[j].Text is "declare" or "abstract" or "async" or "const")
            {
                if (toks[j].Text == "const" && !(j + 1 < end && toks[j + 1].Is("enum"))) return i;
                if (toks[j].Text == "abstract") isAbstract = true;
                j++;
            }
            if (j >= end || toks[j].Kind != TokKind.Ident) return i;
            var kw = toks[j].Text;
            var nextTok = j + 1 < end ? toks[j + 1] : null;
            switch (kw)
            {
                case "class":
                    return Class(j, end, ns, isAbstract, isDefault, declStart);
                case "interface" when nextTok?.Kind == TokKind.Ident:
                    return Interface(j, end, ns, isDefault, declStart);
                case "enum" when nextTok?.Kind == TokKind.Ident:
                    return Enum(j, end, ns, declStart);
                case "type" when nextTok?.Kind == TokKind.Ident && j + 2 < end && (toks[j + 2].Is("=") || toks[j + 2].Is("<")):
                    return TypeAlias(j, end, ns, declStart);
                case "namespace" or "module" when nextTok?.Kind == TokKind.Ident:
                {
                    var k = j + 1;
                    var segs = new List<string> { toks[k].Text };
                    k++;
                    while (k + 1 < end && toks[k].Is(".") && toks[k + 1].Kind == TokKind.Ident) { segs.Add(toks[k + 1].Text); k += 2; }
                    if (k >= end || !toks[k].Is("{")) return k;
                    var close = TypeExprs.Matching(toks, k, end);
                    if (close >= end) issues.Add(toks[k].Line, "unclosed namespace");
                    Statements(k + 1, close, [.. ns, .. segs]);
                    return Math.Min(end, close + 1);
                }
                case "module" when nextTok?.Kind == TokKind.String:
                {
                    // An ambient external module ("declare module 'x' { … }") describes another package: skipped.
                    var k = j + 2;
                    return k < end && toks[k].Is("{") ? Math.Min(end, TypeExprs.Matching(toks, k, end) + 1) : k;
                }
                case "global" when nextTok?.Is("{") == true:
                {
                    var close = TypeExprs.Matching(toks, j + 1, end);
                    Statements(j + 2, close, ns);
                    return Math.Min(end, close + 1);
                }
                default:
                    return i;
            }
        }

        private int ImportStatement(int i, int end)
        {
            var j = i + 1;
            if (j >= end || toks[j].Is("(") || toks[j].Is(".")) return j;   // import("x"), import.meta
            f.IsModule = true;
            // import X = require("x") / import X = A.B
            if (toks[j].Kind == TokKind.Ident && j + 1 < end && toks[j + 1].Is("="))
            {
                var name = toks[j].Text;
                var k = j + 2;
                if (k + 2 < end && toks[k].Is("require") && toks[k + 1].Is("(") && toks[k + 2].Kind == TokKind.String)
                {
                    f.Imports[name] = new Import(session.ResolveModule(f.Path, toks[k + 2].Text), "", IsNamespace: true);
                    return k + 3;
                }
                var dotted = TypeExprs.NamesIn(toks, k, Math.Min(end, k + 1 + 2 * 32)).FirstOrDefault();
                if (dotted != null) f.Imports[name] = new Import(null, dotted, false, IsAlias: true);
                return k + 1;
            }
            var stop = j;
            while (stop < end && stop - j < 2000 && toks[stop].Kind != TokKind.String && !toks[stop].Is(";")) stop++;
            if (stop >= end || toks[stop].Kind != TokKind.String) { issues.Add(toks[i].Line, "import"); return stop + 1; }
            var target = session.ResolveModule(f.Path, toks[stop].Text);
            var p = j;
            if (toks[p].Is("type") && p + 1 < stop && !toks[p + 1].Is(",") && !toks[p + 1].Is("from")) p++;
            if (p < stop && toks[p].Kind == TokKind.Ident && toks[p].Text != "from")
            {
                f.Imports[toks[p].Text] = new Import(target, "default", false);
                p++;
                if (p < stop && toks[p].Is(",")) p++;
            }
            if (p + 2 < stop && toks[p].Is("*") && toks[p + 1].Is("as") && toks[p + 2].Kind == TokKind.Ident)
            {
                f.Imports[toks[p + 2].Text] = new Import(target, "", IsNamespace: true);
                p += 3;
            }
            if (p < stop && toks[p].Is("{"))
            {
                var close = TypeExprs.Matching(toks, p, stop);
                foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, p + 1, close, false))
                {
                    var (exported, local) = Binding(a, b);
                    if (exported != null) f.Imports[local!] = new Import(target, exported, false);
                }
            }
            return stop + 1;
        }

        /// <summary>"[type] name [as alias]" → (name, alias ?? name).</summary>
        private (string? Name, string? Local) Binding(int a, int b)
        {
            if (a < b && toks[a].Is("type") && a + 1 < b) a++;
            if (a >= b || toks[a].Kind is not (TokKind.Ident or TokKind.String)) return (null, null);
            var name = toks[a].Text;
            var local = a + 2 < b && toks[a + 1].Is("as") && toks[a + 2].Kind is TokKind.Ident or TokKind.String ? toks[a + 2].Text : name;
            return (name, local);
        }

        private int Export(int i, int end, string[] ns)
        {
            var j = i + 1;
            if (j >= end) return j;
            if (toks[j].Is("default"))
            {
                j++;
                var d = Declaration(j, end, ns, true, i);
                if (d > j) return d;
                if (j < end && toks[j].Kind == TokKind.Ident && (j + 1 >= end || toks[j + 1].Is(";") || toks[j + 1].Line > toks[j].Line))
                {
                    f.DefaultExport = toks[j].Text;
                    return j + 1;
                }
                return j;
            }
            if (toks[j].Is("type") && j + 1 < end && (toks[j + 1].Is("{") || toks[j + 1].Is("*"))) j++;
            if (toks[j].Is("*"))
            {
                var k = j + 1;
                string? alias = null;
                if (k + 1 < end && toks[k].Is("as") && toks[k + 1].Kind == TokKind.Ident) { alias = toks[k + 1].Text; k += 2; }
                if (k + 1 < end && toks[k].Is("from") && toks[k + 1].Kind == TokKind.String)
                {
                    var target = session.ResolveModule(f.Path, toks[k + 1].Text);
                    if (alias != null) f.NamespaceReExports[alias] = target;
                    else f.StarReExports.Add(target);
                    return k + 2;
                }
                return k;
            }
            if (toks[j].Is("{"))
            {
                var close = TypeExprs.Matching(toks, j, end);
                var after = close + 1;
                string? target = null;
                var from = after + 1 < end && toks[after].Is("from") && toks[after + 1].Kind == TokKind.String;
                if (from) target = session.ResolveModule(f.Path, toks[after + 1].Text);
                foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, j + 1, close, false))
                {
                    var (name, alias) = Binding(a, b);
                    if (name == null) continue;
                    if (from) f.ReExports[alias!] = (target, name);
                    else f.ExportAliases[alias!] = name;
                }
                return from ? after + 2 : Math.Min(end, after);
            }
            if (toks[j].Is("=") || toks[j].Is("as")) return j + 1;
            if (toks[j].Is("import")) return ImportStatement(j, end);
            var decl = Declaration(j, end, ns, false, i);
            return decl > j ? decl : j;
        }

        private int SkipDecorator(int i, int end)
        {
            var k = i + 1;
            if (k < end && toks[k].Kind == TokKind.Ident) k++;
            while (k + 1 < end && toks[k].Is(".") && toks[k + 1].Kind == TokKind.Ident) k += 2;
            if (k < end && toks[k].Is("<")) k = TypeExprs.Matching(toks, k, end) + 1;
            if (k < end && toks[k].Is("(")) k = TypeExprs.Matching(toks, k, end) + 1;
            return Math.Min(end, Math.Max(k, i + 1));
        }

        // ------------------------------------------------------------------ declarations

        private string? DocOf(int from, int to)
        {
            if (_pendingDoc != null) return _pendingDoc;
            for (var k = Math.Max(0, from); k <= to && k < toks.Count; k++)
                if (toks[k].Doc != null) return toks[k].Doc;
            return null;
        }

        private RawType NewType(string[] ns, string name, CodeTypeKind kind, Tok nameTok, string? doc)
        {
            var nsText = string.Join(".", new[] { f.Namespace }.Concat(ns).Where(s => s.Length > 0));
            var symbol = nsText.Length == 0 ? name : nsText + "." + name;
            var t = new RawType
            {
                Symbol = symbol, Name = name, Namespace = nsText, Kind = kind, File = f.Path, Line = nameTok.Line, Doc = doc,
                Scope = new Scope(f, ns),
            };
            f.Locals[ns.Length == 0 ? name : string.Join(".", ns) + "." + name] = symbol;
            types.Add(t);
            return t;
        }

        private int TypeParams(RawType t, int k, int end)
        {
            if (k >= end || !toks[k].Is("<")) return k;
            var close = TypeExprs.Matching(toks, k, end);
            var names = new List<string>();
            foreach (var (a, _) in TypeExprs.SplitTopLevel(toks, k + 1, close, true))
            {
                var p = a;
                while (p < close && toks[p].Kind == TokKind.Ident && toks[p].Text is "in" or "out" or "const" && p + 1 < close && toks[p + 1].Kind == TokKind.Ident) p++;
                if (p < close && toks[p].Kind == TokKind.Ident) names.Add(toks[p].Text);
            }
            t.TypeParams.UnionWith(names);
            if (names.Count > 0) t.DisplayName = $"{t.Name}<{string.Join(", ", names)}>";
            return Math.Min(end, close + 1);
        }

        /// <summary>Index of the '{' that opens a declaration body, skipping (), [] and &lt;&gt;; or end.</summary>
        private int BodyOpen(int k, int end)
        {
            var depth = 0;
            for (var p = k; p < end; p++)
            {
                var t = toks[p];
                if (t.Kind != TokKind.Punct) continue;
                if (depth == 0 && t.Text == "{") return p;
                if (depth == 0 && t.Text is ";" or "}") return end;
                if (t.Text is "(" or "[" or "<") depth++;
                else if (t.Text is ")" or "]" or ">") depth = Math.Max(0, depth - 1);
            }
            return end;
        }

        /// <summary>Types separated by commas in [k, end).</summary>
        private void HeritageList(List<TypeExpr> into, int k, int end)
        {
            var p = k;
            while (p < end)
            {
                var before = p;
                var t = TypeExprs.Parse(toks, ref p, end, python: false);
                if (t.Name.Length > 0) into.Add(t);
                if (p < end && toks[p].Is(",")) { p++; continue; }
                if (p < end && toks[p].Is("(")) p = TypeExprs.Matching(toks, p, end) + 1;   // mixin calls
                if (p == before) p++;
                while (p < end && !toks[p].Is(",")) p++;
                p++;
            }
        }

        private void Heritage(RawType t, int k, int open)
        {
            for (var p = k; p < open; p++)
            {
                if (!(toks[p].Is("extends") || toks[p].Is("implements"))) continue;
                var stop = p + 1;
                while (stop < open && !toks[stop].Is("extends") && !toks[stop].Is("implements")) stop++;
                HeritageList(toks[p].Is("extends") ? t.Extends : t.Implements, p + 1, stop);
                p = stop - 1;
            }
        }

        private int Class(int j, int end, string[] ns, bool isAbstract, bool isDefault, int declStart)
        {
            var k = j + 1;
            Tok nameTok;
            string name;
            if (k < end && toks[k].Kind == TokKind.Ident && toks[k].Text is not ("extends" or "implements"))
            {
                nameTok = toks[k];
                name = nameTok.Text;
                k++;
            }
            else if (isDefault)
            {
                nameTok = toks[j];
                var file = f.Path[(f.Path.LastIndexOf('/') + 1)..];
                name = file.Split('.')[0];
            }
            else return j + 1;   // a class expression
            var doc = DocOf(declStart, j);
            var t = NewType(ns, name, CodeTypeKind.Class, nameTok, doc);
            t.IsAbstract = isAbstract;
            if (isDefault) f.DefaultExport = name;
            k = TypeParams(t, k, end);
            var open = BodyOpen(k, end);
            Heritage(t, k, open);
            if (open >= end) { issues.Add(nameTok.Line, $"class {name} has no body"); return k; }
            var close = TypeExprs.Matching(toks, open, end);
            if (close >= end) issues.Add(nameTok.Line, $"class {name} is not closed");
            ClassBody(t, open + 1, close);
            return Math.Min(end, close + 1);
        }

        private int Interface(int j, int end, string[] ns, bool isDefault, int declStart)
        {
            var nameTok = toks[j + 1];
            var t = NewType(ns, nameTok.Text, CodeTypeKind.Interface, nameTok, DocOf(declStart, j));
            if (isDefault) f.DefaultExport = nameTok.Text;
            var k = TypeParams(t, j + 2, end);
            var open = BodyOpen(k, end);
            Heritage(t, k, open);
            if (open >= end) { issues.Add(nameTok.Line, $"interface {nameTok.Text} has no body"); return k; }
            var close = TypeExprs.Matching(toks, open, end);
            if (close >= end) issues.Add(nameTok.Line, $"interface {nameTok.Text} is not closed");
            ObjectTypeMembers(t, open + 1, close);
            return Math.Min(end, close + 1);
        }

        private int Enum(int j, int end, string[] ns, int declStart)
        {
            var nameTok = toks[j + 1];
            var t = NewType(ns, nameTok.Text, CodeTypeKind.Enum, nameTok, DocOf(declStart, j));
            var open = j + 2;
            if (open >= end || !toks[open].Is("{")) { issues.Add(nameTok.Line, $"enum {nameTok.Text} has no body"); return open; }
            var close = TypeExprs.Matching(toks, open, end);
            foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, open + 1, close, false))
            {
                if (a >= b || toks[a].Kind is not (TokKind.Ident or TokKind.String)) continue;
                t.Members.Add(new RawMember { Name = toks[a].Text, Kind = CodeMemberKind.EnumValue, Doc = toks[a].Doc, Line = toks[a].Line });
            }
            return Math.Min(end, close + 1);
        }

        private int TypeAlias(int j, int end, string[] ns, int declStart)
        {
            var nameTok = toks[j + 1];
            var k = j + 2;
            var probe = new RawType { Symbol = "", Name = nameTok.Text, Namespace = "", File = f.Path };
            k = TypeParams(probe, k, end);
            if (k >= end || !toks[k].Is("=")) return k;
            var valueEnd = TypeSpanEnd(k + 1, end, stopAtBrace: false);
            // An object type, possibly intersected with named types: A & { … }.
            var parts = new List<(int A, int B)>();
            var depth = 0;
            var start = k + 1;
            if (start < valueEnd && toks[start].Is("&")) start++;
            for (var p = start; p < valueEnd; p++)
            {
                var tk = toks[p];
                if (tk.Kind != TokKind.Punct) continue;
                if (tk.Text is "(" or "[" or "{" or "<") depth++;
                else if (tk.Text is ")" or "]" or "}" or ">") depth--;
                else if (depth == 0 && tk.Text == "|") return valueEnd;   // a union is not an object type
                else if (depth == 0 && tk.Text == "&") { parts.Add((start, p)); start = p + 1; }
            }
            parts.Add((start, valueEnd));
            var literals = parts.Where(x => x.A < x.B && toks[x.A].Is("{")).ToList();
            if (literals.Count == 0) return valueEnd;
            var t = NewType(ns, nameTok.Text, CodeTypeKind.Interface, nameTok, DocOf(declStart, j));
            t.TypeParams.UnionWith(probe.TypeParams);
            t.DisplayName = probe.DisplayName;
            foreach (var (a, b) in parts)
            {
                if (a >= b) continue;
                if (toks[a].Is("{")) ObjectTypeMembers(t, a + 1, TypeExprs.Matching(toks, a, b));
                else
                {
                    var p = a;
                    var te = TypeExprs.Parse(toks, ref p, b, python: false);
                    if (te.Name.Length > 0) t.Extends.Add(te);
                }
            }
            return valueEnd;
        }

        // ------------------------------------------------------------------ members

        private static string Visibility(HashSet<string> mods, string name) =>
            mods.Contains("private") || name.StartsWith('#') ? "private" : mods.Contains("protected") ? "protected" : "public";

        private bool IsModifierAt(int i, int end)
        {
            if (i + 1 >= end) return false;
            var n = toks[i + 1];
            if (n.Kind is TokKind.Ident or TokKind.String or TokKind.Number) return n.Line == toks[i].Line || toks[i].Text is not ("get" or "set");
            return n.Is("[") || n.Is("*") || (n.Is("{") && toks[i].Text == "static");
        }

        private void ClassBody(RawType t, int from, int end)
        {
            var overloaded = new HashSet<string>(StringComparer.Ordinal);
            var accessors = new HashSet<string>(StringComparer.Ordinal);
            var i = from;
            while (i < end)
            {
                ct.ThrowIfCancellationRequested();
                var start = i;
                if (toks[i].Is(";") || toks[i].Is(",")) { i++; continue; }
                string? doc = null;
                while (i < end && toks[i].Is("@")) { doc ??= toks[i].Doc; i = SkipDecorator(i, end); }
                var mods = new HashSet<string>(StringComparer.Ordinal);
                while (i < end && toks[i].Kind == TokKind.Ident && MemberModifiers.Contains(toks[i].Text) && IsModifierAt(i, end))
                {
                    doc ??= toks[i].Doc;
                    mods.Add(toks[i].Text);
                    i++;
                }
                if (i >= end) break;
                if (toks[i].Is("{"))
                {
                    // static { … } initialization block
                    var c = TypeExprs.Matching(toks, i, end);
                    t.BodyRefs.AddRange(TypeExprs.NamesIn(toks, i + 1, c));
                    i = Math.Min(end, c + 1);
                    continue;
                }
                if (toks[i].Is("*")) i++;
                if (i >= end) break;
                var nameTok = toks[i];
                doc ??= nameTok.Doc;
                if (nameTok.Is("["))
                {
                    // Index signature or computed name: skipped.
                    i = MemberEnd(i, end);
                    continue;
                }
                if (nameTok.Kind is not (TokKind.Ident or TokKind.String or TokKind.Number))
                {
                    issues.Add(nameTok.Line, $"unexpected '{nameTok.Text}' in class {t.Name}");
                    i = MemberEnd(i, end);
                    if (i <= start) i = start + 1;
                    continue;
                }
                var name = nameTok.Text;
                i++;
                var optional = false;
                if (i < end && toks[i].Is("?")) { optional = true; i++; }
                if (i < end && toks[i].Is("!")) i++;
                if (i < end && (toks[i].Is("(") || toks[i].Is("<")))
                {
                    if (toks[i].Is("<")) i = TypeExprs.Matching(toks, i, end) + 1;
                    if (i >= end || !toks[i].Is("(")) { issues.Add(nameTok.Line, $"method {name}"); i = MemberEnd(i, end); continue; }
                    var pclose = TypeExprs.Matching(toks, i, end);
                    var pars = Params(t, i + 1, pclose);
                    i = Math.Min(end, pclose + 1);
                    TypeExpr? ret = null;
                    var retText = "";
                    if (i < end && toks[i].Is(":"))
                    {
                        var re = TypeSpanEnd(i + 1, end, stopAtBrace: true);
                        var p = i + 1;
                        ret = TypeExprs.Parse(toks, ref p, re, python: false);
                        retText = TypeExprs.Text(toks, i + 1, re);
                        i = re;
                    }
                    var hasBody = false;
                    if (i < end && toks[i].Is("{"))
                    {
                        var bclose = TypeExprs.Matching(toks, i, end);
                        t.BodyRefs.AddRange(TypeExprs.NamesIn(toks, i + 1, bclose));
                        i = Math.Min(end, bclose + 1);
                        hasBody = true;
                    }
                    if (name == "constructor")
                    {
                        foreach (var (param, propMods, line) in pars)
                        {
                            if (propMods == null) { if (param.Type != null) t.BodyRefs.AddRange(param.Type.Names()); continue; }
                            t.Members.Add(new RawMember
                            {
                                Name = param.Name, Kind = CodeMemberKind.Property, Type = param.Type, TypeText = param.TypeText,
                                Visibility = Visibility(propMods, param.Name), Line = line, IsOptional = false,
                            });
                        }
                        continue;
                    }
                    if (mods.Contains("get") || mods.Contains("set"))
                    {
                        if (!accessors.Add(name)) continue;
                        var isGet = mods.Contains("get");
                        t.Members.Add(new RawMember
                        {
                            Name = name, Kind = CodeMemberKind.Property, Type = isGet ? ret : pars.FirstOrDefault().Param?.Type,
                            TypeText = isGet ? retText : pars.FirstOrDefault().Param?.TypeText ?? "", Visibility = Visibility(mods, name),
                            IsStatic = mods.Contains("static"), IsAbstract = mods.Contains("abstract"), Doc = doc, Line = nameTok.Line,
                        });
                        continue;
                    }
                    // Overload signatures (no body) are the methods; the implementation that follows them is not.
                    if (!hasBody && !mods.Contains("abstract")) overloaded.Add(name);
                    else if (hasBody && overloaded.Contains(name)) continue;
                    t.Members.Add(new RawMember
                    {
                        Name = name, Kind = CodeMemberKind.Method, Type = ret, TypeText = retText, Visibility = Visibility(mods, name),
                        Params = pars.Select(x => x.Param).ToList(), IsStatic = mods.Contains("static"), IsAbstract = mods.Contains("abstract"),
                        Doc = doc, Line = nameTok.Line,
                    });
                    continue;
                }
                // A property: [: Type] [= initializer]
                TypeExpr? type = null;
                var typeText = "";
                if (i < end && toks[i].Is(":"))
                {
                    var te = TypeSpanEnd(i + 1, end, stopAtBrace: false);
                    var p = i + 1;
                    type = TypeExprs.Parse(toks, ref p, te, python: false);
                    typeText = TypeExprs.Text(toks, i + 1, te);
                    i = te;
                }
                if (i < end && toks[i].Is("="))
                {
                    var ie = InitEnd(i + 1, end);
                    t.BodyRefs.AddRange(TypeExprs.NamesIn(toks, i + 1, ie));
                    if (type == null && i + 2 < ie && toks[i + 1].Is("new") && toks[i + 2].Kind == TokKind.Ident)
                    {
                        // Inferred from "= new Battery(…)".
                        var p = i + 2;
                        var ne = p;
                        while (ne < ie && !toks[ne].Is("(")) ne++;
                        type = TypeExprs.Parse(toks, ref p, ne, python: false);
                        typeText = TypeExprs.Text(toks, i + 2, ne);
                    }
                    i = ie;
                }
                if (i < end && (toks[i].Is(";") || toks[i].Is(","))) i++;
                t.Members.Add(new RawMember
                {
                    Name = name, Kind = CodeMemberKind.Property, Type = type, TypeText = typeText, Visibility = Visibility(mods, name),
                    IsStatic = mods.Contains("static"), IsAbstract = mods.Contains("abstract"), IsOptional = optional, Doc = doc, Line = nameTok.Line,
                });
                if (i <= start) i = start + 1;
            }
        }

        /// <summary>Members of an interface body or object type literal.</summary>
        private void ObjectTypeMembers(RawType t, int from, int end)
        {
            var i = from;
            var seenAccessors = new HashSet<string>(StringComparer.Ordinal);
            while (i < end)
            {
                ct.ThrowIfCancellationRequested();
                var start = i;
                if (toks[i].Is(";") || toks[i].Is(",")) { i++; continue; }
                string? doc = toks[i].Doc;
                var mods = new HashSet<string>(StringComparer.Ordinal);
                while (i < end && toks[i].Kind == TokKind.Ident && toks[i].Text is "readonly" or "get" or "set" && IsModifierAt(i, end))
                {
                    mods.Add(toks[i].Text);
                    i++;
                }
                if (i >= end) break;
                var nameTok = toks[i];
                if (nameTok.Is("[") || nameTok.Is("(") || nameTok.Is("<") || (nameTok.Is("new") && i + 1 < end && (toks[i + 1].Is("(") || toks[i + 1].Is("<"))) || nameTok.Is("-") || nameTok.Is("+"))
                {
                    i = MemberEnd(i, end);   // index, call and construct signatures, mapped types
                    if (i <= start) i = start + 1;
                    continue;
                }
                if (nameTok.Kind is not (TokKind.Ident or TokKind.String or TokKind.Number))
                {
                    issues.Add(nameTok.Line, $"unexpected '{nameTok.Text}' in {t.Name}");
                    i = MemberEnd(i, end);
                    if (i <= start) i = start + 1;
                    continue;
                }
                var name = nameTok.Text;
                i++;
                var optional = false;
                if (i < end && toks[i].Is("?")) { optional = true; i++; }
                if (i < end && (toks[i].Is("(") || toks[i].Is("<")))
                {
                    if (toks[i].Is("<")) i = TypeExprs.Matching(toks, i, end) + 1;
                    if (i >= end || !toks[i].Is("(")) { i = MemberEnd(i, end); continue; }
                    var pclose = TypeExprs.Matching(toks, i, end);
                    var pars = Params(t, i + 1, pclose);
                    i = Math.Min(end, pclose + 1);
                    TypeExpr? ret = null;
                    var retText = "";
                    if (i < end && toks[i].Is(":"))
                    {
                        var re = TypeSpanEnd(i + 1, end, stopAtBrace: false);
                        var p = i + 1;
                        ret = TypeExprs.Parse(toks, ref p, re, python: false);
                        retText = TypeExprs.Text(toks, i + 1, re);
                        i = re;
                    }
                    if (mods.Contains("get") || mods.Contains("set"))
                    {
                        if (seenAccessors.Add(name))
                            t.Members.Add(new RawMember
                            {
                                Name = name, Kind = CodeMemberKind.Property, Type = mods.Contains("get") ? ret : pars.FirstOrDefault().Param?.Type,
                                TypeText = mods.Contains("get") ? retText : pars.FirstOrDefault().Param?.TypeText ?? "", Doc = doc, Line = nameTok.Line,
                            });
                    }
                    else
                        t.Members.Add(new RawMember
                        {
                            Name = name, Kind = CodeMemberKind.Method, Type = ret, TypeText = retText, Params = pars.Select(x => x.Param).ToList(),
                            IsAbstract = true, Doc = doc, Line = nameTok.Line,
                        });
                }
                else
                {
                    TypeExpr? type = null;
                    var typeText = "";
                    if (i < end && toks[i].Is(":"))
                    {
                        var te = TypeSpanEnd(i + 1, end, stopAtBrace: false);
                        var p = i + 1;
                        type = TypeExprs.Parse(toks, ref p, te, python: false);
                        typeText = TypeExprs.Text(toks, i + 1, te);
                        i = te;
                    }
                    else if (i < end && !toks[i].Is(";") && !toks[i].Is(",") && toks[i].Line == nameTok.Line)
                    {
                        issues.Add(nameTok.Line, $"member {name} of {t.Name}");
                        i = MemberEnd(i, end);
                    }
                    t.Members.Add(new RawMember
                    {
                        Name = name, Kind = CodeMemberKind.Property, Type = type, TypeText = typeText, IsOptional = optional, Doc = doc, Line = nameTok.Line,
                    });
                }
                if (i < end && (toks[i].Is(";") || toks[i].Is(","))) i++;
                if (i <= start) i = start + 1;
            }
        }

        /// <summary>Parameters in [from, end); constructor parameter properties carry their modifiers.</summary>
        private List<(RawParam Param, HashSet<string>? PropertyMods, int Line)> Params(RawType t, int from, int end)
        {
            var list = new List<(RawParam, HashSet<string>?, int)>();
            var index = 0;
            foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, from, end, angles: true))
            {
                index++;
                var k = a;
                while (k < b && toks[k].Is("@")) k = SkipDecorator(k, b);
                HashSet<string>? mods = null;
                while (k + 1 < b && toks[k].Kind == TokKind.Ident && toks[k].Text is "public" or "private" or "protected" or "readonly" or "override"
                       && toks[k + 1].Kind == TokKind.Ident)
                {
                    (mods ??= new HashSet<string>(StringComparer.Ordinal)).Add(toks[k].Text);
                    k++;
                }
                if (k < b && toks[k].Is("...")) k++;
                if (k >= b) continue;
                string name;
                var line = toks[k].Line;
                if (toks[k].Kind == TokKind.Ident) { name = toks[k].Text; k++; }
                else if (toks[k].Is("{") || toks[k].Is("["))
                {
                    var c = TypeExprs.Matching(toks, k, b);
                    name = $"arg{index}";
                    k = Math.Min(b, c + 1);
                }
                else continue;
                if (name == "this") continue;
                if (k < b && toks[k].Is("?")) k++;
                TypeExpr? type = null;
                var text = "";
                if (k < b && toks[k].Is(":"))
                {
                    var te = TypeSpanEnd(k + 1, b, stopAtBrace: false);
                    var p = k + 1;
                    type = TypeExprs.Parse(toks, ref p, te, python: false);
                    text = TypeExprs.Text(toks, k + 1, te);
                    k = te;
                }
                if (k < b && toks[k].Is("=")) t.BodyRefs.AddRange(TypeExprs.NamesIn(toks, k + 1, b));
                list.Add((new RawParam(name, type, text), mods, line));
            }
            return list;
        }

        // ------------------------------------------------------------------ spans

        private bool TypeComplete(Tok prev) =>
            prev.Kind is TokKind.Number or TokKind.String || (prev.Kind == TokKind.Ident && !NotTypeEnd.Contains(prev.Text))
            || prev.Is(")") || prev.Is("]") || prev.Is("}") || prev.Is(">");

        /// <summary>End of a type that starts at <paramref name="from"/>: a ';', ',', '=', closing bracket, a body '{'
        /// (with <paramref name="stopAtBrace"/>) or a line break after a complete type.</summary>
        private int TypeSpanEnd(int from, int end, bool stopAtBrace)
        {
            var depth = 0;
            for (var k = from; k < end; k++)
            {
                var t = toks[k];
                if (depth == 0)
                {
                    if (t.Is(";") || t.Is(",") || t.Is("=") || t.Is(")") || t.Is("]") || t.Is("}") || t.Is(">")) return k;
                    if (k > from && TypeComplete(toks[k - 1]))
                    {
                        if (stopAtBrace && t.Is("{")) return k;
                        if (t.Line > toks[k - 1].Line && !TypeContinuation.Contains(t.Text)) return k;
                    }
                }
                if (t.Kind != TokKind.Punct) continue;
                if (t.Text is "(" or "[" or "{" or "<") depth++;
                else if (t.Text is ")" or "]" or "}" or ">") { depth--; if (depth < 0) return k; }
            }
            return end;
        }

        private static bool ValueComplete(Tok prev) =>
            prev.Kind is TokKind.Number or TokKind.String || (prev.Kind == TokKind.Ident && !ValueKeywords.Contains(prev.Text))
            || prev.Is(")") || prev.Is("]") || prev.Is("}");

        /// <summary>End of a property initializer: ';' or the closing '}' at depth 0, or a line break after a complete value.</summary>
        private int InitEnd(int from, int end)
        {
            var depth = 0;
            for (var k = from; k < end; k++)
            {
                var t = toks[k];
                if (depth == 0)
                {
                    if (t.Is(";") || t.Is(")") || t.Is("]") || t.Is("}")) return k;   // no ",": "new Map<K, V>()"
                    if (k > from && t.Line > toks[k - 1].Line && ValueComplete(toks[k - 1])
                        && (t.Kind is TokKind.Ident or TokKind.String or TokKind.Number && !ValueKeywords.Contains(t.Text) || t.Is("@") || t.Is("[") || t.Is("#") || t.Is("*")))
                        return k;
                }
                if (t.Kind != TokKind.Punct) continue;
                if (t.Text is "(" or "[" or "{") depth++;
                else if (t.Text is ")" or "]" or "}") depth--;
            }
            return end;
        }

        /// <summary>Skips one member whose shape is not understood: to the next ';' (or a line break) at depth 0.</summary>
        private int MemberEnd(int i, int end)
        {
            var depth = 0;
            for (var k = i; k < end; k++)
            {
                var t = toks[k];
                if (depth == 0 && k > i)
                {
                    if (t.Is(";") || t.Is(",")) return k + 1;
                    if (t.Is("}")) return k;
                    if (t.Line > toks[k - 1].Line && (TypeComplete(toks[k - 1])) && !TypeContinuation.Contains(t.Text) && !t.Is("{")) return k;
                }
                if (t.Kind != TokKind.Punct) continue;
                if (t.Text is "(" or "[" or "{") depth++;
                else if (t.Text is ")" or "]" or "}") { depth--; if (depth < 0) return k; }
            }
            return end;
        }
    }
}
