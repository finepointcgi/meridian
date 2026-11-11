using Meridian.Core.CodeSync;
using Meridian.Serialization.CodeSync.Parsing;

namespace Meridian.Serialization.CodeSync;

/// <summary>
/// Reads Python (<c>.py</c>, <c>.pyi</c>) with a hand-written, tolerant parser (no Python process). Modules become
/// packages: "src/sat/power/battery.py" with <c>__init__.py</c> in <c>sat</c> and <c>power</c> is the module
/// <c>sat.power.battery</c> (folders above the topmost <c>__init__.py</c> are dropped; without any <c>__init__.py</c> the
/// whole folder path is used). Classes, base classes, methods with parameters and type hints (<c>@overload</c>
/// signatures as overloads, <c>@property</c> as properties, <c>@staticmethod</c> / <c>@classmethod</c> as static), annotated
/// class attributes and dataclass fields, and <c>self.x = …</c> assignments in <c>__init__</c> are read. Classes based on
/// <c>Protocol</c> or <c>ABC</c> (or with <c>metaclass=ABCMeta</c>) are interfaces; <c>Enum</c> subclasses are enums.
/// Names resolve through enclosing classes, the module, imports (absolute, relative, aliased, star, and re-exports in
/// <c>__init__.py</c>). Virtual environments and caches are skipped.
/// </summary>
public sealed class PythonCodeReader : HandParsedCodeReader
{
    public override string Language => "python";

    public override bool Handles(string path)
    {
        var p = "/" + (path ?? "").Replace('\\', '/').TrimStart('/');
        if (!(p.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".pyi", StringComparison.OrdinalIgnoreCase))) return false;
        foreach (var skip in new[] { "/site-packages/", "/__pycache__/", "/.venv/", "/venv/", "/.tox/", "/node_modules/", "/.eggs/" })
            if (p.Contains(skip, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static readonly TypeVocabulary Vocab = new()
    {
        Collections = new(StringComparer.Ordinal)
        {
            "list", "List", "Sequence", "MutableSequence", "Iterable", "Iterator", "Collection", "set", "Set", "frozenset", "FrozenSet",
            "AbstractSet", "MutableSet", "tuple", "Tuple", "Deque", "deque", "Generator", "AsyncIterator", "AsyncIterable", "AsyncGenerator",
        },
        Maps = new(StringComparer.Ordinal) { "dict", "Dict", "Mapping", "MutableMapping", "DefaultDict", "defaultdict", "OrderedDict" },
        Optionals = new(StringComparer.Ordinal) { "Optional" },
        Wrappers = new(StringComparer.Ordinal) { "Final", "ClassVar", "Annotated", "Required", "NotRequired", "ReadOnly", "Awaitable" },
        NullNames = new(StringComparer.Ordinal) { "None" },
    };

    private static readonly HashSet<string> EnumBases = new(StringComparer.Ordinal) { "Enum", "IntEnum", "StrEnum", "Flag", "IntFlag", "ReprEnum" };

    internal override TypeVocabulary Vocabulary => Vocab;

    internal override ParseSession CreateSession(IReadOnlyList<CodeFile> files) => new Session(files);

    private sealed class Scope(string module, bool isPackage)
    {
        public string Module { get; } = module;
        public bool IsPackage { get; } = isPackage;
        /// <summary>Local name → qualified target ("Battery" → "sat.power.battery.Battery", "np" → "numpy").</summary>
        public Dictionary<string, string> Imports { get; } = new(StringComparer.Ordinal);
        public List<string> Stars { get; } = [];
        /// <summary>Line of each import, by local name.</summary>
        public Dictionary<string, int> ImportLines { get; } = new(StringComparer.Ordinal);
    }

    private sealed record PyLine(int Indent, List<Tok> Toks)
    {
        public int Line => Toks[0].Line;
        public bool StartsWith(string text) => Toks.Count > 0 && Toks[0].Is(text);
        public bool IsHeader => Toks.Count > 1 && Toks[^1].Is(":");
    }

    private sealed class Session : ParseSession
    {
        private readonly HashSet<string> _inits;
        private readonly Dictionary<string, Scope> _modules = new(StringComparer.Ordinal);

        public Session(IReadOnlyList<CodeFile> files)
        {
            _inits = files.Select(f => f.Path.Replace('\\', '/'))
                .Where(p => p.EndsWith("/__init__.py", StringComparison.Ordinal) || p.EndsWith("/__init__.pyi", StringComparison.Ordinal) || p is "__init__.py" or "__init__.pyi")
                .Select(p => p.Contains('/') ? p[..p.LastIndexOf('/')] : "")
                .ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>The dotted module of a file: folders from the topmost package (a folder with __init__.py) down.</summary>
        public string ModuleName(string path, out bool isPackage)
        {
            var p = path.Replace('\\', '/').Trim('/');
            var dirs = p.Contains('/') ? p[..p.LastIndexOf('/')].Split('/').ToList() : [];
            var file = p[(p.LastIndexOf('/') + 1)..];
            var stem = file.Contains('.') ? file[..file.IndexOf('.')] : file;
            isPackage = stem == "__init__";
            var k = dirs.Count;
            while (k > 0 && _inits.Contains(string.Join("/", dirs.Take(k)))) k--;
            var pkg = k < dirs.Count ? dirs.Skip(k).ToList() : dirs;
            if (!isPackage) pkg.Add(stem);
            return string.Join(".", pkg.Select(s => s.Replace('.', '_')));
        }

        public override void Parse(CodeFile file, List<RawType> types, ParseIssues issues, CancellationToken ct)
        {
            var module = ModuleName(file.Path, out var isPackage);
            var scope = new Scope(module, isPackage);
            _modules.TryAdd(module, scope);
            var toks = Lexer.Python(file.Content);
            var lines = new List<PyLine>();
            var current = new List<Tok>();
            foreach (var t in toks)
            {
                if (t.Kind == TokKind.Newline)
                {
                    if (current.Count > 0) lines.Add(new PyLine(Math.Max(0, current[0].Indent), current));
                    current = [];
                }
                else current.Add(t);
            }
            new Parser(file.Path, scope, lines, types, issues, ct).Module();
        }

        public override IEnumerable<(string Name, int Line)> Imports(RawType from) =>
            from.Scope is Scope scope ? scope.ImportLines.Select(kv => (kv.Key, kv.Value)) : [];

        public override string? Resolve(RawType from, string name)
        {
            if (from.Scope is not Scope scope) return null;
            foreach (var outer in Enclosing(from))
                if (Index.ContainsKey(outer + "." + name)) return outer + "." + name;
            if (Qualified(scope.Module.Length == 0 ? name : scope.Module + "." + name, 0) is { } local) return local;
            var dot = name.IndexOf('.');
            var first = dot < 0 ? name : name[..dot];
            var rest = dot < 0 ? "" : name[dot..];
            if (scope.Imports.TryGetValue(first, out var target) && Qualified(target + rest, 0) is { } imported) return imported;
            foreach (var star in scope.Stars)
                if (Qualified(star + "." + name, 0) is { } s) return s;
            return Qualified(name, 0);
        }

        /// <summary>A fully qualified name, following imports and re-exports of the modules it passes through.</summary>
        private string? Qualified(string q, int depth)
        {
            if (Index.ContainsKey(q)) return q;
            if (depth > 8) return null;
            for (var dot = q.LastIndexOf('.'); dot > 0; dot = q.LastIndexOf('.', dot - 1))
            {
                var module = q[..dot];
                if (!_modules.TryGetValue(module, out var m)) continue;
                var name = q[(dot + 1)..];
                var nd = name.IndexOf('.');
                var first = nd < 0 ? name : name[..nd];
                var rest = nd < 0 ? "" : name[nd..];
                if (m.Imports.TryGetValue(first, out var target) && target != q && Qualified(target + rest, depth + 1) is { } s) return s;
                foreach (var star in m.Stars)
                    if (Qualified(star + "." + name, depth + 1) is { } s2) return s2;
                return null;
            }
            return null;
        }
    }

    private sealed class Parser(string path, Scope scope, List<PyLine> lines, List<RawType> types, ParseIssues issues, CancellationToken ct)
    {
        public void Module()
        {
            foreach (var l in lines)
                if (l.StartsWith("import") || (l.StartsWith("from") && l.Toks.Any(t => t.Is("import")))) Import(l);
            Block(0, lines.Count, null);
        }

        private void Import(PyLine l)
        {
            var toks = l.Toks;
            if (toks[0].Is("import"))
            {
                foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, 1, toks.Count, false))
                {
                    var dotted = TypeExprs.NamesIn(toks, a, b).FirstOrDefault();
                    if (dotted == null) continue;
                    var asAt = Enumerable.Range(a, b - a).FirstOrDefault(k => toks[k].Is("as"), -1);
                    if (asAt >= 0 && asAt + 1 < b) { scope.Imports[toks[asAt + 1].Text] = dotted; scope.ImportLines[toks[asAt + 1].Text] = toks[a].Line; }
                    else { var first = dotted.Split('.')[0]; scope.Imports.TryAdd(first, first); }
                }
                return;
            }
            // from [.]*module import names
            var k = 1;
            var level = 0;
            while (k < toks.Count && (toks[k].Is(".") || toks[k].Is("...")))
            {
                level += toks[k].Text.Length;
                k++;
            }
            var module = "";
            if (k < toks.Count && toks[k].Kind == TokKind.Ident && !toks[k].Is("import"))
            {
                var segments = new List<string> { toks[k].Text };
                k++;
                while (k + 1 < toks.Count && toks[k].Is(".") && toks[k + 1].Kind == TokKind.Ident) { segments.Add(toks[k + 1].Text); k += 2; }
                module = string.Join(".", segments);
            }
            if (k >= toks.Count || !toks[k].Is("import")) return;
            k++;
            string baseName;
            if (level > 0)
            {
                var parts = scope.Module.Length == 0 ? new List<string>() : scope.Module.Split('.').ToList();
                if (!scope.IsPackage && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                for (var up = 1; up < level && parts.Count > 0; up++) parts.RemoveAt(parts.Count - 1);
                if (module.Length > 0) parts.Add(module);
                baseName = string.Join(".", parts);
            }
            else baseName = module;
            if (k < toks.Count && toks[k].Is("*")) { scope.Stars.Add(baseName); return; }
            var end = toks.Count;
            if (k < end && toks[k].Is("(")) { end = TypeExprs.Matching(toks, k, end); k++; }
            foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, k, end, false))
            {
                if (a >= b || toks[a].Kind != TokKind.Ident) continue;
                var name = toks[a].Text;
                var alias = a + 2 < b && toks[a + 1].Is("as") ? toks[a + 2].Text : name;
                scope.Imports[alias] = baseName.Length == 0 ? name : baseName + "." + name;
                scope.ImportLines[alias] = toks[a].Line;
            }
        }

        private int BodyEnd(int i, int end)
        {
            var e = i + 1;
            while (e < end && lines[e].Indent > lines[i].Indent) e++;
            return e;
        }

        /// <summary>Statements in [from, end) of a module (owner null) or a class body.</summary>
        private void Block(int from, int end, RawType? owner)
        {
            var decorators = new List<PyLine>();
            var overloaded = new HashSet<string>(StringComparer.Ordinal);
            var i = from;
            while (i < end)
            {
                ct.ThrowIfCancellationRequested();
                var l = lines[i];
                var bodyEnd = BodyEnd(i, end);
                var t0 = l.Toks[0];
                if (t0.Is("@")) { decorators.Add(l); i++; continue; }
                if (t0.Is("class")) Class(i, bodyEnd, owner);
                else if (t0.Is("def") || (t0.Is("async") && l.Toks.Count > 1 && l.Toks[1].Is("def")))
                {
                    if (owner != null) Def(i, bodyEnd, owner, decorators, overloaded);
                }
                else if (owner != null) ClassStatement(l, owner, i == from);
                if (l.IsHeader && !t0.Is("class") && !t0.Is("def") && !t0.Is("async") && bodyEnd > i + 1)
                    Block(i + 1, bodyEnd, owner);   // if / try / with … blocks hold declarations too
                decorators.Clear();
                i = bodyEnd;
            }
        }

        private static string Visibility(string name) =>
            name.StartsWith("__", StringComparison.Ordinal) && !name.EndsWith("__", StringComparison.Ordinal) ? "private"
            : name.StartsWith('_') ? "protected" : "public";

        private static bool IsDunder(string name) => name.Length > 4 && name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal);

        private static string? DocLine(PyLine l) =>
            l.Toks.Count == 1 && l.Toks[0].Kind == TokKind.String ? Lexer.CleanDocString(l.Toks[0].Text) : null;

        private void Class(int i, int bodyEnd, RawType? owner)
        {
            var l = lines[i];
            var toks = l.Toks;
            if (toks.Count < 2 || toks[1].Kind != TokKind.Ident) { issues.Add(l.Line, "class"); return; }
            var nameTok = toks[1];
            var name = nameTok.Text;
            var symbol = owner != null ? owner.Symbol + "." + name : scope.Module.Length == 0 ? name : scope.Module + "." + name;
            var t = new RawType { Symbol = symbol, Name = name, Namespace = scope.Module, Kind = CodeTypeKind.Class, File = path, Line = nameTok.Line, Scope = scope };
            types.Add(t);
            var k = 2;
            if (k < toks.Count && toks[k].Is("["))
            {
                var c = TypeExprs.Matching(toks, k, toks.Count);
                foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, k + 1, c, false))
                    if (a < b && toks[a].Kind == TokKind.Ident) t.TypeParams.Add(toks[a].Text);
                k = c + 1;
            }
            var isInterface = false;
            var isEnum = false;
            if (k < toks.Count && toks[k].Is("("))
            {
                var c = TypeExprs.Matching(toks, k, toks.Count);
                foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, k + 1, c, false))
                {
                    if (a >= b || toks[a].Is("*") || toks[a].Is("**")) continue;
                    if (a + 1 < b && toks[a].Kind == TokKind.Ident && toks[a + 1].Is("="))
                    {
                        if (toks[a].Text == "metaclass" && TypeExprs.NamesIn(toks, a + 2, b).Any(n => n == "ABCMeta" || n.EndsWith(".ABCMeta", StringComparison.Ordinal)))
                            isInterface = true;
                        continue;
                    }
                    var p = a;
                    var te = TypeExprs.Parse(toks, ref p, b, python: true);
                    if (te.Name.Length == 0) continue;
                    var last = te.LastSegment;
                    if (last is "Protocol" or "ABC") isInterface = true;
                    if (EnumBases.Contains(last)) isEnum = true;
                    if (last is "Generic" or "Protocol")
                    {
                        foreach (var arg in te.Args) if (arg.Name.Length > 0 && !arg.Name.Contains('.')) t.TypeParams.Add(arg.Name);
                        continue;
                    }
                    if (last is "object" or "ABC") continue;
                    t.Extends.Add(te);
                }
                k = c + 1;
            }
            if (k >= toks.Count || !toks[k].Is(":")) issues.Add(l.Line, $"class {name}");
            t.Kind = isEnum ? CodeTypeKind.Enum : isInterface ? CodeTypeKind.Interface : CodeTypeKind.Class;
            if (t.TypeParams.Count > 0) t.DisplayName = $"{name}[{string.Join(", ", t.TypeParams)}]";
            var bodyStart = i + 1;
            if (bodyStart < bodyEnd && DocLine(lines[bodyStart]) is { } doc) { t.Doc = doc; bodyStart++; }
            Block(bodyStart, bodyEnd, t);
            t.IsAbstract = t.Kind == CodeTypeKind.Class && t.Members.Any(m => m.IsAbstract);
        }

        private void ClassStatement(PyLine l, RawType t, bool first)
        {
            var toks = l.Toks;
            if (first && DocLine(l) != null) return;
            if (toks.Count >= 2 && toks[0].Kind == TokKind.Ident && toks[1].Is(":") && !l.IsHeader)
            {
                // name: Type [= value]
                var name = toks[0].Text;
                var eq = TypeExprs.NextAtDepth(toks, 2, toks.Count, "=");
                if (eq < toks.Count) t.BodyRefs.AddRange(TypeExprs.RefsIn(toks, eq + 1, toks.Count));
                if (t.Kind == CodeTypeKind.Enum || IsDunder(name)) return;
                var p = 2;
                var type = TypeExprs.Parse(toks, ref p, eq, python: true);
                var isStatic = type.LastSegment == "ClassVar";
                t.Members.Add(new RawMember
                {
                    Name = name, Kind = CodeMemberKind.Field, Type = type, TypeText = TypeExprs.Text(toks, 2, eq), Visibility = Visibility(name),
                    IsStatic = isStatic, Line = toks[0].Line,
                });
                return;
            }
            if (toks.Count >= 3 && toks[0].Kind == TokKind.Ident && toks[1].Is("=") && !toks[2].Is("="))
            {
                // name = value
                var name = toks[0].Text;
                t.BodyRefs.AddRange(TypeExprs.RefsIn(toks, 2, toks.Count));
                if (IsDunder(name) || t.Members.Any(m => m.Name == name)) return;
                if (t.Kind == CodeTypeKind.Enum)
                {
                    if (!name.StartsWith('_')) t.Members.Add(new RawMember { Name = name, Kind = CodeMemberKind.EnumValue, Line = toks[0].Line });
                    return;
                }
                var (type, text) = Infer(toks, 2, toks.Count, null);
                t.Members.Add(new RawMember
                {
                    Name = name, Kind = CodeMemberKind.Field, Type = type, TypeText = text, Visibility = Visibility(name), IsStatic = true, Line = toks[0].Line,
                });
                return;
            }
            t.BodyRefs.AddRange(TypeExprs.RefsIn(toks, 0, toks.Count));
        }

        /// <summary>The type of an assigned value: a parameter's annotation, a class call "Battery(…)", or a literal's type.</summary>
        private static (TypeExpr? Type, string Text) Infer(List<Tok> toks, int a, int b, Dictionary<string, RawParam>? pars)
        {
            if (a >= b) return (null, "");
            var t = toks[a];
            if (b - a == 1)
            {
                if (t.Kind == TokKind.String) return (new TypeExpr { Name = "str" }, "str");
                if (t.Kind == TokKind.Number) return (new TypeExpr { Name = t.Text.Contains('.') || t.Text.Contains('e') ? "float" : "int" }, t.Text.Contains('.') || t.Text.Contains('e') ? "float" : "int");
                if (t.Is("True") || t.Is("False")) return (new TypeExpr { Name = "bool" }, "bool");
                if (t.Kind == TokKind.Ident && pars != null && pars.TryGetValue(t.Text, out var p) && p.Type != null) return (p.Type, p.TypeText);
            }
            if (t.Is("[")) return (new TypeExpr { Name = "list" }, "list");
            if (t.Is("{")) return (new TypeExpr { Name = "dict" }, "dict");
            if (t.Kind == TokKind.Ident)
            {
                var dotted = TypeExprs.NamesIn(toks, a, b).First();
                var after = a + dotted.Split('.').Length * 2 - 1;
                var last = dotted[(dotted.LastIndexOf('.') + 1)..];
                if (after < b && toks[after].Is("(") && last.Length > 0 && char.IsUpper(last[0])) return (new TypeExpr { Name = dotted }, dotted);
                // "x or Battery()" / "x if x else Battery()" style defaults: the class called.
                if (pars != null && b - a >= 3 && toks[a + 1].Is("or") && pars.TryGetValue(t.Text, out var p) && p.Type != null) return (p.Type, p.TypeText);
            }
            return (null, "");
        }

        private void Def(int i, int bodyEnd, RawType t, List<PyLine> decorators, HashSet<string> overloaded)
        {
            var l = lines[i];
            var toks = l.Toks;
            var k = toks[0].Is("async") ? 1 : 0;
            if (k + 1 >= toks.Count || toks[k + 1].Kind != TokKind.Ident) { issues.Add(l.Line, "def"); return; }
            var nameTok = toks[k + 1];
            var name = nameTok.Text;
            k += 2;
            if (k < toks.Count && toks[k].Is("[")) k = TypeExprs.Matching(toks, k, toks.Count) + 1;
            if (k >= toks.Count || !toks[k].Is("(")) { issues.Add(l.Line, $"def {name}"); return; }
            var close = TypeExprs.Matching(toks, k, toks.Count);
            if (close >= toks.Count) issues.Add(l.Line, $"def {name}");

            var decoratorNames = decorators.Select(d => TypeExprs.NamesIn(d.Toks, 1, d.Toks.Count).FirstOrDefault() ?? "").ToList();
            bool Has(params string[] names) => decoratorNames.Any(d => names.Any(n => d == n || d.EndsWith("." + n, StringComparison.Ordinal)));
            var isStatic = Has("staticmethod");
            var isClassMethod = Has("classmethod");
            var isProperty = Has("property", "cached_property");
            var isAccessor = decoratorNames.Any(d => d.EndsWith(".setter", StringComparison.Ordinal) || d.EndsWith(".deleter", StringComparison.Ordinal) || d.EndsWith(".getter", StringComparison.Ordinal));
            var isAbstract = Has("abstractmethod", "abstractproperty");
            var isOverload = Has("overload");

            // Parameters (the first one is self / cls unless static).
            var pars = new List<RawParam>();
            var parts = TypeExprs.SplitTopLevel(toks, k + 1, Math.Min(close, toks.Count), false);
            var skipFirst = !isStatic;
            foreach (var (a, b) in parts)
            {
                if (a >= b) continue;
                var p = a;
                var star = 0;
                if (toks[p].Is("*")) { star = 1; p++; }
                else if (toks[p].Is("**")) { star = 2; p++; }
                if (p >= b || toks[p].Kind != TokKind.Ident) continue;   // "/" and bare "*" separators
                if (skipFirst) { skipFirst = false; if (star == 0) continue; }
                var pname = toks[p].Text;
                p++;
                TypeExpr? type = null;
                var text = "";
                var eq = TypeExprs.NextAtDepth(toks, p, b, "=");
                if (p < b && toks[p].Is(":"))
                {
                    var q = p + 1;
                    type = TypeExprs.Parse(toks, ref q, eq, python: true);
                    text = TypeExprs.Text(toks, p + 1, eq);
                    if (star > 0) type.Array++;
                }
                if (eq < b) t.BodyRefs.AddRange(TypeExprs.RefsIn(toks, eq + 1, b));
                pars.Add(new RawParam(star == 1 ? "*" + pname : star == 2 ? "**" + pname : pname, type, text));
            }
            TypeExpr? ret = null;
            var retText = "";
            var colon = TypeExprs.NextAtDepth(toks, close + 1, toks.Count, ":");
            if (close + 1 < toks.Count && toks[close + 1].Is("->"))
            {
                var q = close + 2;
                ret = TypeExprs.Parse(toks, ref q, colon, python: true);
                retText = TypeExprs.Text(toks, close + 2, colon);
            }

            var bodyStart = i + 1;
            string? doc = null;
            if (bodyStart < bodyEnd && DocLine(lines[bodyStart]) is { } d) { doc = d; bodyStart++; }
            // Single-line body after the colon ("def f(self): return Battery()").
            if (colon + 1 < toks.Count) t.BodyRefs.AddRange(TypeExprs.RefsIn(toks, colon + 1, toks.Count));
            for (var b = bodyStart; b < bodyEnd; b++)
                t.BodyRefs.AddRange(TypeExprs.RefsIn(lines[b].Toks, 0, lines[b].Toks.Count));

            if (name is "__init__" or "__post_init__")
            {
                var byName = pars.GroupBy(p => p.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                for (var b = bodyStart; b < bodyEnd; b++) SelfAssignment(lines[b], t, byName);
                return;
            }
            if (isAccessor || IsDunder(name)) return;
            if (isOverload) overloaded.Add(name);
            else if (overloaded.Contains(name)) return;   // the implementation behind @overload signatures
            if (isProperty)
            {
                if (t.Members.Any(m => m.Name == name)) return;
                t.Members.Add(new RawMember
                {
                    Name = name, Kind = CodeMemberKind.Property, Type = ret, TypeText = retText, Visibility = Visibility(name),
                    IsAbstract = isAbstract, Doc = doc, Line = nameTok.Line,
                });
                return;
            }
            t.Members.Add(new RawMember
            {
                Name = name, Kind = CodeMemberKind.Method, Type = ret, TypeText = retText, Visibility = Visibility(name), Params = pars,
                IsStatic = isStatic || isClassMethod, IsAbstract = isAbstract, Doc = doc, Line = nameTok.Line,
            });
        }

        /// <summary>"self.x: T = …" or "self.x = …" in __init__ declares an attribute (unless the class already has it).</summary>
        private static void SelfAssignment(PyLine l, RawType t, Dictionary<string, RawParam> pars)
        {
            var toks = l.Toks;
            if (toks.Count < 4 || !toks[0].Is("self") || !toks[1].Is(".") || toks[2].Kind != TokKind.Ident) return;
            var name = toks[2].Text;
            TypeExpr? type = null;
            var text = "";
            if (toks[3].Is(":"))
            {
                var eq = TypeExprs.NextAtDepth(toks, 4, toks.Count, "=");
                var p = 4;
                type = TypeExprs.Parse(toks, ref p, eq, python: true);
                text = TypeExprs.Text(toks, 4, eq);
            }
            else if (toks[3].Is("=") && !(toks.Count > 4 && toks[4].Is("=")))
            {
                // Chained "self.a = self.b = x" takes the value after the last '='.
                var last = 3;
                for (var k = 4; k < toks.Count; k++) if (toks[k].Is("=") && !toks[k - 1].Is("=") && !(k + 1 < toks.Count && toks[k + 1].Is("="))) last = k;
                (type, text) = Infer(toks, last + 1, toks.Count, pars);
            }
            else return;
            if (t.Members.Any(m => m.Name == name)) return;
            t.Members.Add(new RawMember
            {
                Name = name, Kind = CodeMemberKind.Field, Type = type, TypeText = text, Visibility = Visibility(name), Line = toks[2].Line,
            });
        }
    }
}
