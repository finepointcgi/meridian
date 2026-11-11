using Meridian.Core.CodeSync;
using Meridian.Serialization.CodeSync.Parsing;

namespace Meridian.Serialization.CodeSync;

/// <summary>
/// Reads Java (<c>.java</c>) with a hand-written, tolerant parser (no JVM): packages, classes, interfaces, enums, records
/// and annotation types (as interfaces), nested types, fields, methods with parameters (each overload separately),
/// <c>extends</c> / <c>implements</c> and generics. Names resolve like javac: nested types of the enclosing types,
/// single-type imports, the same package, on-demand (<c>.*</c>) imports, then fully qualified names. Types from libraries
/// are not resolved. Build output (<c>target/</c>, <c>build/</c>) and <c>module-info.java</c> / <c>package-info.java</c>
/// are skipped.
/// </summary>
public sealed class JavaCodeReader : HandParsedCodeReader
{
    public override string Language => "java";

    public override bool Handles(string path)
    {
        var p = "/" + (path ?? "").Replace('\\', '/').TrimStart('/');
        if (!p.EndsWith(".java", StringComparison.OrdinalIgnoreCase)) return false;
        if (p.Contains("/target/", StringComparison.Ordinal) || p.Contains("/build/generated", StringComparison.Ordinal)) return false;
        return !p.EndsWith("/module-info.java", StringComparison.Ordinal) && !p.EndsWith("/package-info.java", StringComparison.Ordinal);
    }

    private static readonly TypeVocabulary Vocab = new()
    {
        Collections = new(StringComparer.Ordinal)
        {
            "List", "ArrayList", "LinkedList", "Collection", "Set", "HashSet", "TreeSet", "LinkedHashSet", "SortedSet", "NavigableSet",
            "Iterable", "Iterator", "Queue", "Deque", "ArrayDeque", "PriorityQueue", "Stream", "Vector", "Stack", "CopyOnWriteArrayList",
            "CopyOnWriteArraySet", "BlockingQueue", "LinkedBlockingQueue", "ArrayBlockingQueue", "ConcurrentLinkedQueue", "EnumSet",
            "ImmutableList", "ImmutableSet", "SequencedCollection", "SequencedSet", "Flux",
        },
        Maps = new(StringComparer.Ordinal)
        {
            "Map", "HashMap", "TreeMap", "LinkedHashMap", "SortedMap", "NavigableMap", "ConcurrentMap", "ConcurrentHashMap",
            "ConcurrentSkipListMap", "EnumMap", "Hashtable", "WeakHashMap", "IdentityHashMap", "ImmutableMap", "SequencedMap",
        },
        Optionals = new(StringComparer.Ordinal) { "Optional" },
        Wrappers = new(StringComparer.Ordinal) { "AtomicReference", "CompletableFuture", "CompletionStage", "Future", "Supplier", "Mono", "WeakReference", "SoftReference" },
    };

    internal override TypeVocabulary Vocabulary => Vocab;

    internal override ParseSession CreateSession(IReadOnlyList<CodeFile> files) => new Session();

    private sealed class Scope
    {
        public string Package { get; set; } = "";
        public Dictionary<string, string> Single { get; } = new(StringComparer.Ordinal);
        public List<string> OnDemand { get; } = [];
        /// <summary>Line of each single-type import, by simple name.</summary>
        public Dictionary<string, int> ImportLines { get; } = new(StringComparer.Ordinal);
    }

    private sealed class Session : ParseSession
    {
        public override void Parse(CodeFile file, List<RawType> types, ParseIssues issues, CancellationToken ct) =>
            new Parser(file.Path, Lexer.CLike(file.Content, typeScript: false), types, issues, ct).File();

        public override string? Resolve(RawType from, string name)
        {
            if (from.Scope is not Scope scope) return null;
            var dot = name.IndexOf('.');
            var first = dot < 0 ? name : name[..dot];
            var rest = dot < 0 ? "" : name[dot..];
            if (Simple(from, scope, first) is { } head && Index.ContainsKey(head + rest)) return head + rest;
            return Index.ContainsKey(name) ? name : null;
        }

        public override IEnumerable<(string Name, int Line)> Imports(RawType from) =>
            from.Scope is Scope scope ? scope.ImportLines.Select(kv => (kv.Key, kv.Value)) : [];

        private string? Simple(RawType from, Scope scope, string name)
        {
            foreach (var outer in Enclosing(from))
                if (Index.ContainsKey(outer + "." + name)) return outer + "." + name;
            // A nested type inherited from a scanned base class is in scope too.
            foreach (var outer in Enclosing(from))
                if (Index.TryGetValue(outer, out var o))
                    foreach (var b in o.Extends.Concat(o.Implements))
                        if (b.Name.Length > 0 && b.Name != name && ResolveTop(o, (Scope)o.Scope!, b.Name) is { } bs && Index.ContainsKey(bs + "." + name))
                            return bs + "." + name;
            return ResolveTop(from, scope, name);
        }

        private string? ResolveTop(RawType from, Scope scope, string name)
        {
            var dot = name.IndexOf('.');
            var first = dot < 0 ? name : name[..dot];
            var rest = dot < 0 ? "" : name[dot..];
            if (scope.Single.TryGetValue(first, out var imported) && Index.ContainsKey(imported + rest)) return imported + rest;
            var local = scope.Package.Length == 0 ? name : scope.Package + "." + name;
            if (Index.ContainsKey(local)) return local;
            foreach (var pkg in scope.OnDemand)
                if (Index.ContainsKey(pkg + "." + name)) return pkg + "." + name;
            foreach (var outer in Enclosing(from))
                if (Index.ContainsKey(outer + "." + name)) return outer + "." + name;
            return Index.ContainsKey(name) ? name : null;
        }
    }

    private sealed class Parser(string path, List<Tok> toks, List<RawType> types, ParseIssues issues, CancellationToken ct)
    {
        private readonly Scope _scope = new();

        private static readonly HashSet<string> Modifiers = new(StringComparer.Ordinal)
        {
            "public", "protected", "private", "static", "abstract", "final", "sealed", "non", "default", "transient", "volatile",
            "synchronized", "native", "strictfp",
        };

        public void File()
        {
            var i = 0;
            var end = toks.Count;
            while (i < end)
            {
                ct.ThrowIfCancellationRequested();
                var t = toks[i];
                if (t.Is("package"))
                {
                    var (name, next) = Dotted(i + 1, end);
                    _scope.Package = name;
                    i = next;
                    continue;
                }
                if (t.Is("import"))
                {
                    var k = i + 1;
                    var isStatic = k < end && toks[k].Is("static");
                    if (isStatic) k++;
                    var (name, next) = Dotted(k, end);
                    var star = next < end && toks[next].Is(".") && next + 1 < end && toks[next + 1].Is("*");
                    if (star) next += 2;
                    if (name.Length > 0)
                    {
                        if (star) _scope.OnDemand.Add(name);
                        else if (!isStatic)
                        {
                            var simple = name[(name.LastIndexOf('.') + 1)..];
                            _scope.Single[simple] = name;
                            _scope.ImportLines[simple] = t.Line;
                        }
                        else if (name.Contains('.'))
                        {
                            // import static a.b.Outer.Inner: a nested type can be imported statically.
                            _scope.Single.TryAdd(name[(name.LastIndexOf('.') + 1)..], name);
                        }
                    }
                    i = next;
                    continue;
                }
                var start = i;
                var (mods, doc, after) = ModifiersAt(i, end);
                if (after < end && IsTypeKeyword(after, end))
                {
                    i = TypeDecl(after, end, null, mods, doc ?? DocFrom(start, after));
                    continue;
                }
                i = toks[i].Is("{") ? Math.Min(end, TypeExprs.Matching(toks, i, end) + 1) : Math.Max(after, i + 1);
                if (i <= start) i = start + 1;
            }
        }

        private (string Name, int Next) Dotted(int k, int end)
        {
            if (k >= end || toks[k].Kind != TokKind.Ident) return ("", k);
            var parts = new List<string> { toks[k].Text };
            k++;
            while (k + 1 < end && toks[k].Is(".") && toks[k + 1].Kind == TokKind.Ident) { parts.Add(toks[k + 1].Text); k += 2; }
            return (string.Join(".", parts), k);
        }

        private string? DocFrom(int from, int to)
        {
            for (var k = from; k <= to && k < toks.Count; k++)
                if (toks[k].Doc != null) return toks[k].Doc;
            return null;
        }

        private bool IsTypeKeyword(int k, int end)
        {
            var t = toks[k];
            if (t.Is("class") || t.Is("interface") || t.Is("enum")) return k + 1 < end && toks[k + 1].Kind == TokKind.Ident;
            if (t.Is("@") && k + 1 < end && toks[k + 1].Is("interface")) return true;
            // "record" is contextual: record Name( or record Name<
            return t.Is("record") && k + 2 < end && toks[k + 1].Kind == TokKind.Ident && (toks[k + 2].Is("(") || toks[k + 2].Is("<"));
        }

        /// <summary>Annotations and modifiers from <paramref name="i"/>: the modifier set, the first doc comment, and the index after.</summary>
        private (HashSet<string> Mods, string? Doc, int After) ModifiersAt(int i, int end)
        {
            var mods = new HashSet<string>(StringComparer.Ordinal);
            string? doc = null;
            while (i < end)
            {
                var t = toks[i];
                if (t.Is("@") && i + 1 < end && toks[i + 1].Kind == TokKind.Ident && !toks[i + 1].Is("interface"))
                {
                    doc ??= t.Doc;
                    i += 2;
                    while (i + 1 < end && toks[i].Is(".") && toks[i + 1].Kind == TokKind.Ident) i += 2;
                    if (i < end && toks[i].Is("(")) i = Math.Min(end, TypeExprs.Matching(toks, i, end) + 1);
                    continue;
                }
                if (t.Kind == TokKind.Ident && Modifiers.Contains(t.Text))
                {
                    // non-sealed is three tokens.
                    if (t.Text == "non")
                    {
                        if (i + 2 < end && toks[i + 1].Is("-") && toks[i + 2].Is("sealed")) { doc ??= t.Doc; i += 3; continue; }
                        break;
                    }
                    // "default" in an annotation method tail or a switch is handled by the callers; here it is a modifier.
                    doc ??= t.Doc;
                    mods.Add(t.Text);
                    i++;
                    continue;
                }
                break;
            }
            return (mods, doc, i);
        }

        private static string Visibility(HashSet<string> mods, bool inInterface) =>
            mods.Contains("public") ? "public" : mods.Contains("protected") ? "protected" : mods.Contains("private") ? "private" : inInterface ? "public" : "internal";

        private int TypeDecl(int k, int end, RawType? outer, HashSet<string> mods, string? doc)
        {
            var kw = toks[k];
            CodeTypeKind kind;
            var isAnnotation = false;
            if (kw.Is("@")) { kind = CodeTypeKind.Interface; isAnnotation = true; k++; }
            else kind = kw.Text switch
            {
                "interface" => CodeTypeKind.Interface,
                "enum" => CodeTypeKind.Enum,
                "record" => CodeTypeKind.Record,
                _ => CodeTypeKind.Class,
            };
            var nameTok = toks[k + 1];
            var name = nameTok.Text;
            var symbol = outer != null ? outer.Symbol + "." + name : _scope.Package.Length == 0 ? name : _scope.Package + "." + name;
            var t = new RawType
            {
                Symbol = symbol, Name = name, Namespace = _scope.Package, Kind = kind, File = path, Line = nameTok.Line, Doc = doc,
                IsAbstract = mods.Contains("abstract") && kind == CodeTypeKind.Class, IsStatic = mods.Contains("static") && outer != null,
                Scope = _scope,
            };
            types.Add(t);
            var p = k + 2;
            if (p < end && toks[p].Is("<"))
            {
                var close = TypeExprs.Matching(toks, p, end);
                var names = TypeExprs.SplitTopLevel(toks, p + 1, close, true)
                    .Select(x => FirstIdent(x.From, x.End)).OfType<string>().ToList();
                t.TypeParams.UnionWith(names);
                if (names.Count > 0) t.DisplayName = $"{name}<{string.Join(", ", names)}>";
                p = Math.Min(end, close + 1);
            }
            var components = new List<RawMember>();
            if (kind == CodeTypeKind.Record && p < end && toks[p].Is("("))
            {
                var close = TypeExprs.Matching(toks, p, end);
                foreach (var (par, line) in Params(t, p + 1, close))
                    components.Add(new RawMember { Name = par.Name, Kind = CodeMemberKind.Property, Type = par.Type, TypeText = par.TypeText, Visibility = "public", Line = line });
                p = Math.Min(end, close + 1);
            }
            var open = p;
            while (open < end && !toks[open].Is("{") && !toks[open].Is(";") && !toks[open].Is("}")) open++;
            for (var q = p; q < open; q++)
            {
                if (!(toks[q].Is("extends") || toks[q].Is("implements") || toks[q].Is("permits"))) continue;
                var stop = q + 1;
                while (stop < open && !toks[stop].Is("extends") && !toks[stop].Is("implements") && !toks[stop].Is("permits")) stop++;
                if (!toks[q].Is("permits"))
                {
                    var into = toks[q].Is("extends") ? t.Extends : t.Implements;
                    foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, q + 1, stop, angles: true))
                    {
                        var r = a;
                        var te = TypeExprs.Parse(toks, ref r, b, python: false);
                        if (te.Name.Length > 0) into.Add(te);
                    }
                }
                q = stop - 1;
            }
            if (open >= end || !toks[open].Is("{")) { issues.Add(nameTok.Line, $"{kw.Text} {name} has no body"); return Math.Max(open, k + 2); }
            var bodyEnd = TypeExprs.Matching(toks, open, end);
            if (bodyEnd >= end) issues.Add(nameTok.Line, $"{name} is not closed");
            t.Members.AddRange(components);
            if (isAnnotation) AnnotationBody(t, open + 1, bodyEnd);
            else Body(t, open + 1, bodyEnd);
            return Math.Min(end, bodyEnd + 1);
        }

        private string? FirstIdent(int a, int b)
        {
            for (var k = a; k < b; k++)
            {
                if (toks[k].Is("@")) { k++; continue; }
                if (toks[k].Kind == TokKind.Ident) return toks[k].Text;
            }
            return null;
        }

        private void AnnotationBody(RawType t, int from, int end)
        {
            // Annotation elements: Type name() [default value];
            var i = from;
            while (i < end)
            {
                var start = i;
                var (mods, doc, after) = ModifiersAt(i, end);
                i = after;
                if (i < end && IsTypeKeyword(i, end)) { i = TypeDecl(i, end, t, mods, doc); continue; }
                var semi = TypeExprs.NextAtDepth(toks, i, end, ";");
                var paren = TypeExprs.NextAtDepth(toks, i, semi, "(");
                if (paren < semi && paren > i && toks[paren - 1].Kind == TokKind.Ident)
                {
                    var p = i;
                    var type = TypeExprs.Parse(toks, ref p, paren - 1, python: false);
                    t.Members.Add(new RawMember
                    {
                        Name = toks[paren - 1].Text, Kind = CodeMemberKind.Method, Type = type, TypeText = TypeExprs.Text(toks, i, paren - 1),
                        Visibility = "public", IsAbstract = true, Doc = doc ?? toks[i < end ? i : start].Doc, Line = toks[paren - 1].Line,
                    });
                }
                i = Math.Max(semi + 1, start + 1);
            }
        }

        private void Body(RawType t, int from, int end)
        {
            var inInterface = t.Kind == CodeTypeKind.Interface;
            var i = from;
            if (t.Kind == CodeTypeKind.Enum) i = EnumConstants(t, i, end);
            while (i < end)
            {
                ct.ThrowIfCancellationRequested();
                var start = i;
                if (toks[i].Is(";")) { i++; continue; }
                var (mods, doc, after) = ModifiersAt(i, end);
                i = after;
                if (i >= end) break;
                if (toks[i].Is("{"))
                {
                    // (static) initializer block
                    var c = TypeExprs.Matching(toks, i, end);
                    t.BodyRefs.AddRange(TypeExprs.RefsIn(toks, i + 1, c));
                    i = Math.Min(end, c + 1);
                    continue;
                }
                if (IsTypeKeyword(i, end))
                {
                    i = TypeDecl(i, end, t, mods, doc ?? DocFrom(start, i));
                    continue;
                }
                var methodTypeParams = new HashSet<string>(StringComparer.Ordinal);
                if (toks[i].Is("<"))
                {
                    var c = TypeExprs.Matching(toks, i, end);
                    foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, i + 1, c, true))
                        if (FirstIdent(a, b) is { } tp) methodTypeParams.Add(tp);
                    i = Math.Min(end, c + 1);
                }
                if (i >= end) break;
                doc ??= DocFrom(start, i);
                // Constructor: Name( — or a compact record constructor: Name {
                if (toks[i].Kind == TokKind.Ident && toks[i].Text == t.Name && i + 1 < end && (toks[i + 1].Is("(") || toks[i + 1].Is("{")))
                {
                    var k = i + 1;
                    if (toks[k].Is("("))
                    {
                        var pc = TypeExprs.Matching(toks, k, end);
                        foreach (var (par, pline) in Params(t, k + 1, pc)) if (par.Type != null) t.BodyRefs.AddRange(par.Type.Uses(pline, CodeDependencyKind.Param));
                        k = Math.Min(end, pc + 1);
                    }
                    i = SkipThrowsAndBody(t, k, end);
                    continue;
                }
                var typeStart = i;
                var type = TypeExprs.Parse(toks, ref i, end, python: false);
                var typeText = TypeExprs.Text(toks, typeStart, i);
                if (i >= end || toks[i].Kind != TokKind.Ident || type.Name.Length == 0 && type.Refs.Count == 0)
                {
                    issues.Add(toks[Math.Min(i, end - 1)].Line, $"unexpected '{toks[Math.Min(i, end - 1)].Text}' in {t.Name}");
                    i = Recover(start, end);
                    continue;
                }
                var nameTok = toks[i];
                i++;
                var isStatic = mods.Contains("static");
                if (i < end && toks[i].Is("("))
                {
                    var pc = TypeExprs.Matching(toks, i, end);
                    var pars = Params(t, i + 1, pc).Select(x => x.Param).ToList();
                    if (methodTypeParams.Count > 0)
                    {
                        // A method's own type parameters are not types; keep them out of usage.
                        pars = pars.Select(x => x.Type != null && methodTypeParams.Contains(x.Type.Name) ? x with { Type = null } : x).ToList();
                        if (methodTypeParams.Contains(type.Name)) type = TypeExpr.Empty;
                    }
                    i = Math.Min(end, pc + 1);
                    while (i + 1 < end && toks[i].Is("[") && toks[i + 1].Is("]")) { type.Array++; i += 2; }
                    var bodyStart = i;
                    i = SkipThrowsAndBody(t, i, end);
                    var hasBody = false;
                    for (var q = bodyStart; q < i; q++) if (toks[q].Is("{")) { hasBody = true; break; }
                    t.Members.Add(new RawMember
                    {
                        Name = nameTok.Text, Kind = CodeMemberKind.Method, Type = type, TypeText = typeText,
                        Visibility = Visibility(mods, inInterface), Params = pars, IsStatic = isStatic,
                        IsAbstract = mods.Contains("abstract") || (inInterface && !hasBody && !isStatic), Doc = doc, Line = nameTok.Line,
                    });
                    continue;
                }
                // Fields: name [dims] [= init] {, name [dims] [= init]} ;
                var declarator = nameTok;
                while (true)
                {
                    var fieldType = type;
                    var dims = 0;
                    while (i + 1 < end && toks[i].Is("[") && toks[i + 1].Is("]")) { dims++; i += 2; }
                    if (dims > 0) fieldType = new TypeExpr { Name = type.Name, Args = type.Args, Refs = type.Refs, Array = type.Array + dims };
                    t.Members.Add(new RawMember
                    {
                        Name = declarator.Text, Kind = CodeMemberKind.Field, Type = fieldType, TypeText = typeText + string.Concat(Enumerable.Repeat("[]", dims)),
                        Visibility = Visibility(mods, inInterface), IsStatic = isStatic || inInterface, Doc = doc, Line = declarator.Line,
                    });
                    if (i < end && toks[i].Is("="))
                    {
                        var ie = InitializerEnd(i + 1, end);
                        t.BodyRefs.AddRange(TypeExprs.RefsIn(toks, i + 1, ie));
                        i = ie;
                    }
                    if (i + 1 < end && toks[i].Is(",") && toks[i + 1].Kind == TokKind.Ident)
                    {
                        declarator = toks[i + 1];
                        i += 2;
                        continue;
                    }
                    break;
                }
                if (i < end && toks[i].Is(";")) i++;
                else if (i < end)
                {
                    issues.Add(toks[i].Line, $"field {nameTok.Text} of {t.Name}");
                    i = Recover(i, end);
                }
                if (i <= start) i = start + 1;
            }
        }

        /// <summary>Enum constants up to the first ';' (or the end of the body); returns the index after them.</summary>
        private int EnumConstants(RawType t, int i, int end)
        {
            while (i < end)
            {
                var start = i;
                if (toks[i].Is(";")) return i + 1;
                if (toks[i].Is(",")) { i++; continue; }
                var (_, doc, after) = ModifiersAt(i, end);
                i = after;
                if (i >= end) break;
                if (toks[i].Kind != TokKind.Ident) { issues.Add(toks[i].Line, $"enum constant in {t.Name}"); return Recover(i, end); }
                var nameTok = toks[i];
                i++;
                if (i < end && toks[i].Is("("))
                {
                    var c = TypeExprs.Matching(toks, i, end);
                    t.BodyRefs.AddRange(TypeExprs.RefsIn(toks, i + 1, c));
                    i = Math.Min(end, c + 1);
                }
                if (i < end && toks[i].Is("{"))
                {
                    var c = TypeExprs.Matching(toks, i, end);
                    t.BodyRefs.AddRange(TypeExprs.RefsIn(toks, i + 1, c));
                    i = Math.Min(end, c + 1);
                }
                t.Members.Add(new RawMember { Name = nameTok.Text, Kind = CodeMemberKind.EnumValue, Doc = doc ?? nameTok.Doc, Line = nameTok.Line });
                if (i <= start) i = start + 1;
            }
            return i;
        }

        /// <summary>Skips "throws A, B", an annotation "default value" and the body '{…}' or ';'; collects body usage.</summary>
        private int SkipThrowsAndBody(RawType t, int i, int end)
        {
            while (i < end && !toks[i].Is("{") && !toks[i].Is(";") && !toks[i].Is("}"))
            {
                if (toks[i].Is("=") || toks[i].Is("(")) break;
                if (toks[i].Kind == TokKind.Ident && toks[i].Text is not ("throws" or "default")) t.BodyRefs.Add(new NameRef(toks[i].Text, toks[i].Line, CodeDependencyKind.Use));
                i++;
            }
            if (i < end && toks[i].Is("{"))
            {
                var c = TypeExprs.Matching(toks, i, end);
                t.BodyRefs.AddRange(TypeExprs.RefsIn(toks, i + 1, c));
                return Math.Min(end, c + 1);
            }
            if (i < end && toks[i].Is(";")) return i + 1;
            return Recover(i, end);
        }

        /// <summary>End of a field initializer: the ',' that starts the next declarator, or ';', at depth 0.</summary>
        private int InitializerEnd(int i, int end)
        {
            var k = i;
            while (true)
            {
                k = TypeExprs.NextAtDepth(toks, k, end, ",", ";");
                if (k >= end || toks[k].Is(";") || !toks[k].Is(",")) return k;
                // A ',' inside generics ("new HashMap<A, B>()") is not a declarator separator.
                if (k + 2 < end && toks[k + 1].Kind == TokKind.Ident && (toks[k + 2].Is("=") || toks[k + 2].Is(",") || toks[k + 2].Is(";") || toks[k + 2].Is("["))) return k;
                k++;
            }
        }

        /// <summary>After a member that could not be parsed: past the next ';' or balanced '{…}' at depth 0.</summary>
        private int Recover(int i, int end)
        {
            var depth = 0;
            for (var k = i; k < end; k++)
            {
                var t = toks[k];
                if (t.Is("(") || t.Is("[")) depth++;
                else if (t.Is(")") || t.Is("]")) depth = Math.Max(0, depth - 1);
                else if (depth == 0 && t.Is(";")) return k + 1;
                else if (depth == 0 && t.Is("{")) return Math.Min(end, TypeExprs.Matching(toks, k, end) + 1);
                else if (depth == 0 && t.Is("}")) return k + 1;
            }
            return end;
        }

        private List<(RawParam Param, int Line)> Params(RawType t, int from, int end)
        {
            var list = new List<(RawParam, int)>();
            foreach (var (a, b) in TypeExprs.SplitTopLevel(toks, from, end, angles: true))
            {
                var (_, _, k) = ModifiersAt(a, b);
                if (k >= b) continue;
                var typeStart = k;
                var type = TypeExprs.Parse(toks, ref k, b, python: false);
                var typeEnd = k;
                var text = TypeExprs.Text(toks, typeStart, typeEnd);
                if (k < b && toks[k].Is("...")) { type.Array++; text += "..."; k++; }
                if (k >= b || toks[k].Kind != TokKind.Ident) continue;
                var nameTok = toks[k];
                if (nameTok.Is("this")) continue;   // receiver parameter
                k++;
                while (k + 1 < b && toks[k].Is("[") && toks[k + 1].Is("]")) { type.Array++; text += "[]"; k += 2; }
                list.Add((new RawParam(nameTok.Text, type, text), nameTok.Line));
            }
            return list;
        }
    }
}
