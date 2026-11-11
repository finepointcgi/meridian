using Meridian.Core.CodeSync;
using Meridian.Serialization.CodeSync.Parsing;

namespace Meridian.Serialization.CodeSync;

/// <summary>
/// Base of the readers with a hand-written, tolerant parser (TypeScript, Java, Python). A language supplies a
/// <see cref="ParseSession"/> that turns each file into raw types and resolves names; this class applies the shared
/// limits (as the C# reader), merges, resolves member / base / parameter types against the scanned types, computes usage
/// dependencies and returns a deterministic <see cref="CodeModel"/>: types sorted by symbol, members in declaration
/// order. Reading never throws on bad input; a file that cannot be parsed adds a warning.
/// </summary>
public abstract class HandParsedCodeReader : ICodeReader
{
    /// <summary>Stop collecting types beyond this many (a warning says so).</summary>
    public int MaxTypes { get; init; } = 50_000;

    /// <summary>Files longer than this (characters) are skipped with a warning.</summary>
    public int MaxFileLength { get; init; } = 2_000_000;

    /// <summary>Stop reading files once their total length exceeds this (characters); a warning says so.</summary>
    public long MaxTotalLength { get; init; } = 200_000_000;

    /// <summary>Uses recorded per (type, target) in <see cref="CodeType.DependencyLocations"/>; the rest are only counted.</summary>
    public int MaxDependencyUsesPerTarget { get; init; } = CodeDependencyUses.DefaultMaxPerTarget;

    public abstract string Language { get; }

    public abstract bool Handles(string path);

    internal abstract TypeVocabulary Vocabulary { get; }

    /// <summary>A fresh parse state for one <see cref="Read"/> (readers are shared singletons, so no state lives on them).</summary>
    internal abstract ParseSession CreateSession(IReadOnlyList<CodeFile> files);

    public CodeModel Read(IReadOnlyList<CodeFile> files, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var warnings = new List<string>();
        var selected = new List<CodeFile>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (f.Path is null || !Handles(f.Path)) continue;
            if (!seenPaths.Add(f.Path)) { warnings.Add($"{f.Path}: listed twice, the second copy was ignored."); continue; }
            var content = f.Content ?? "";
            if (content.Length > MaxFileLength) { warnings.Add($"{f.Path}: skipped, larger than {MaxFileLength:N0} characters."); continue; }
            total += content.Length;
            if (total > MaxTotalLength)
            {
                warnings.Add($"Stopped reading at {f.Path}: the sources exceed {MaxTotalLength:N0} characters in total.");
                break;
            }
            selected.Add(f with { Content = content });
        }

        var session = CreateSession(selected);
        var index = new Dictionary<string, RawType>(StringComparer.Ordinal);
        var capped = false;
        foreach (var f in selected)
        {
            ct.ThrowIfCancellationRequested();
            var parsed = new List<RawType>();
            var issues = new ParseIssues();
            try { session.Parse(f, parsed, issues, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A parser bug must not fail the sync: keep what was read and say so.
                issues.Add(0, ex.GetType().Name);
            }
            if (issues.Count > 0)
                warnings.Add($"{f.Path}: {issues.Count} part{(issues.Count == 1 ? "" : "s")} could not be parsed (first at line {issues.FirstLine}{(issues.FirstWhat is { Length: > 0 } w ? ": " + w : "")}); read what could be recovered.");
            foreach (var t in parsed)
            {
                if (string.IsNullOrEmpty(t.Symbol) || string.IsNullOrEmpty(t.Name)) continue;
                if (index.TryGetValue(t.Symbol, out var existing))
                {
                    if (existing.Kind == t.Kind)
                    {
                        // TypeScript declaration merging, or the same name declared twice: one type with all members.
                        // Uses in the other declaration keep pointing at its file.
                        var other = t.File == existing.File ? null : t.File;
                        foreach (var m in t.Members) m.File ??= other;
                        existing.Members.AddRange(t.Members);
                        existing.Extends.AddRange(t.Extends);
                        existing.Implements.AddRange(t.Implements);
                        existing.BodyRefs.AddRange(other is null ? t.BodyRefs : t.BodyRefs.Select(r => r with { File = r.File ?? other }));
                        existing.Doc ??= t.Doc;
                    }
                    else warnings.Add($"Type {t.Symbol} is declared more than once ({existing.File}:{existing.Line} and {t.File}:{t.Line}); the first declaration was used.");
                    continue;
                }
                if (index.Count >= MaxTypes)
                {
                    if (!capped) warnings.Add($"Only the first {MaxTypes:N0} types were read; the rest were skipped.");
                    capped = true;
                    continue;
                }
                index[t.Symbol] = t;
            }
        }

        session.Index = index;
        session.Finish();
        var types = new List<CodeType>(index.Count);
        foreach (var t in index.Values.OrderBy(t => t.Symbol, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            types.Add(Build(t, session, ct));
        }
        return new CodeModel(Language, types, warnings);
    }

    private CodeType Build(RawType t, ParseSession session, CancellationToken ct)
    {
        var cache = new Dictionary<string, string?>(StringComparer.Ordinal);
        var typeParams = new HashSet<string>(t.TypeParams, StringComparer.Ordinal);
        for (var outer = session.Outer(t); outer != null; outer = session.Outer(outer)) typeParams.UnionWith(outer.TypeParams);

        string? Resolve(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (cache.TryGetValue(name, out var hit)) return hit;
            var first = name.Contains('.') ? name[..name.IndexOf('.')] : name;
            string? s = null;
            if (!typeParams.Contains(first))
            {
                s = session.Resolve(t, name);
                if (s != null && !session.Index.ContainsKey(s)) s = null;
            }
            cache[name] = s;
            return s;
        }

        // A body reference "Clock.now" or "pkg.Battery.create" resolves through its longest type prefix.
        string? ResolveChain(string chain)
        {
            for (var name = chain; ;)
            {
                if (Resolve(name) is { } s) return s;
                var dot = name.LastIndexOf('.');
                if (dot <= 0) return null;
                name = name[..dot];
            }
        }

        // Where each scanned type is used: file, line and kind (capped per target below).
        var uses = new List<CodeDependencyUse>();
        void Note(string? target, string? file, int line, CodeDependencyKind how)
        {
            if (target is null || target == t.Symbol) return;
            uses.Add(new CodeDependencyUse(target, file ?? t.File, line > 0 ? line : t.Line, how));
        }

        var bases = new List<string>();
        var interfaces = new List<string>();
        foreach (var e in t.Extends.Concat(t.Implements))
            Note(Resolve(e.Name), null, e.Line, CodeDependencyKind.Inherit);
        foreach (var e in t.Extends)
            if (Resolve(e.Name) is { } s && s != t.Symbol)
            {
                // A class "extending" an interface (Python ABC / Protocol) realizes it.
                if (t.Kind != CodeTypeKind.Interface && session.Index[s].Kind == CodeTypeKind.Interface) interfaces.Add(s);
                else bases.Add(s);
            }
        foreach (var e in t.Implements)
            if (Resolve(e.Name) is { } s && s != t.Symbol)
            {
                if (t.Kind == CodeTypeKind.Interface) bases.Add(s);
                else interfaces.Add(s);
            }

        var vocab = Vocabulary;
        var members = new List<CodeMember>(t.Members.Count);
        var memberTypes = new HashSet<string>(StringComparer.Ordinal);
        var used = new List<string>();
        foreach (var m in t.Members)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(m.Name)) continue;
            if (m.Kind == CodeMemberKind.EnumValue)
            {
                members.Add(new CodeMember(m.Name, CodeMemberKind.EnumValue, t.Name, null, false, "public") { Doc = m.Doc, Line = m.Line });
                continue;
            }
            var info = TypeExprs.Element(m.Type, vocab, Resolve);
            if (m.Type != null)
            {
                used.AddRange(m.Type.Names());
                var how = m.Kind == CodeMemberKind.Method ? CodeDependencyKind.Return : CodeDependencyKind.Field;
                foreach (var (name, line) in m.Type.NamesAt(m.Line)) Note(Resolve(name), m.File, line, how);
            }
            var member = new CodeMember(m.Name, m.Kind, m.TypeText, info.Symbol, info.Many, m.Visibility)
            {
                IsAbstract = m.IsAbstract, IsStatic = m.IsStatic, IsOptional = m.IsOptional || info.Optional, Doc = m.Doc, Line = m.Line,
            };
            if (m.Kind == CodeMemberKind.Method)
            {
                var pars = new List<CodeParameter>(m.Params.Count);
                foreach (var p in m.Params)
                {
                    var pi = TypeExprs.Element(p.Type, vocab, Resolve);
                    if (p.Type != null)
                    {
                        used.AddRange(p.Type.Names());
                        foreach (var (name, line) in p.Type.NamesAt(m.Line)) Note(Resolve(name), m.File, line, CodeDependencyKind.Param);
                    }
                    pars.Add(new CodeParameter(p.Name, p.TypeText, pi.Symbol, pi.Many, p.Direction));
                }
                member = member with { Parameters = pars };
            }
            else if (info.Symbol != null) memberTypes.Add(info.Symbol);
            members.Add(member);
        }

        var excluded = new HashSet<string>(memberTypes, StringComparer.Ordinal) { t.Symbol };
        excluded.UnionWith(bases);
        excluded.UnionWith(interfaces);
        var deps = new SortedSet<string>(StringComparer.Ordinal);
        if (t.Kind != CodeTypeKind.Enum)
        {
            foreach (var name in used)
                if (Resolve(name) is { } s && !excluded.Contains(s)) deps.Add(s);
            foreach (var r in t.BodyRefs)
            {
                if (ResolveChain(r.Name) is not { } s) continue;
                if (!excluded.Contains(s)) deps.Add(s);
                // Calling the type itself (Python "Battery()") creates it; "Battery.create()" calls it.
                var how = r.Kind == CodeDependencyKind.Call && Resolve(r.Name) == s ? CodeDependencyKind.New : r.Kind;
                Note(s, r.File, r.Line, how);
            }
        }
        if (uses.Count > 0)
        {
            // The imports that bring used types into the file.
            var targets = uses.Select(u => u.Symbol).ToHashSet(StringComparer.Ordinal);
            foreach (var (name, line) in session.Imports(t))
                if (Resolve(name) is { } s && targets.Contains(s)) Note(s, null, line, CodeDependencyKind.Import);
        }
        var (locations, omitted) = CodeDependencyUses.Cap(uses, MaxDependencyUsesPerTarget);

        return new CodeType(t.Symbol, t.DisplayName ?? t.Name, t.Namespace, t.Kind, t.File, t.Line, t.Doc,
            bases.Distinct().ToList(), interfaces.Distinct().ToList(), members, deps.ToList())
        {
            IsAbstract = t.IsAbstract && t.Kind is CodeTypeKind.Class or CodeTypeKind.Record,
            IsStatic = t.IsStatic,
            DependencyLocations = locations,
            OmittedDependencyLocations = omitted,
        };
    }
}

/// <summary>Parse problems in one file: how many parts were skipped, and where the first one was.</summary>
internal sealed class ParseIssues
{
    public int Count { get; private set; }
    public int FirstLine { get; private set; }
    public string? FirstWhat { get; private set; }

    public void Add(int line, string? what = null)
    {
        if (Count == 0) { FirstLine = line; FirstWhat = what; }
        Count++;
    }
}

/// <summary>One read of a set of files by a hand-written parser.</summary>
internal abstract class ParseSession
{
    /// <summary>Every scanned type by symbol; set once all files are parsed.</summary>
    public IReadOnlyDictionary<string, RawType> Index { get; set; } = new Dictionary<string, RawType>();

    /// <summary>Adds the types declared in <paramref name="file"/> (outer types before their nested types).</summary>
    public abstract void Parse(CodeFile file, List<RawType> types, ParseIssues issues, CancellationToken ct);

    /// <summary>The symbol <paramref name="name"/> (as written, possibly dotted) means inside <paramref name="from"/>, or
    /// null. The result only counts when it is in <see cref="Index"/>.</summary>
    public abstract string? Resolve(RawType from, string name);

    /// <summary>Called once the index is complete (a language may refine kinds that depend on other types).</summary>
    public virtual void Finish() { }

    /// <summary>The names the file of <paramref name="from"/> imports (as bound in the file) and the line of each import.</summary>
    public virtual IEnumerable<(string Name, int Line)> Imports(RawType from) => [];

    /// <summary>The scanned type that lexically contains <paramref name="t"/>, if any.</summary>
    public RawType? Outer(RawType t)
    {
        var dot = t.Symbol.LastIndexOf('.');
        while (dot > 0)
        {
            var prefix = t.Symbol[..dot];
            if (prefix.Length <= t.Namespace.Length) return null;
            if (Index.TryGetValue(prefix, out var o)) return o;
            dot = prefix.LastIndexOf('.');
        }
        return null;
    }

    /// <summary>The symbol of <paramref name="from"/> and its outer types, innermost first.</summary>
    protected IEnumerable<string> Enclosing(RawType from)
    {
        for (var t = from; t != null; t = Outer(t)) yield return t.Symbol;
    }
}

/// <summary>A type as a hand-written parser found it, before names are resolved.</summary>
internal sealed class RawType
{
    public required string Symbol { get; init; }
    public required string Name { get; init; }
    /// <summary>Name with type parameters ("Box&lt;T&gt;"); null means <see cref="Name"/>.</summary>
    public string? DisplayName { get; set; }
    public required string Namespace { get; init; }
    public CodeTypeKind Kind { get; set; }
    public required string File { get; init; }
    public int Line { get; init; }
    public string? Doc { get; set; }
    public bool IsAbstract { get; set; }
    public bool IsStatic { get; set; }
    /// <summary>Base classes (or, for an interface, base interfaces) as written.</summary>
    public List<TypeExpr> Extends { get; } = [];
    /// <summary>Implemented interfaces as written.</summary>
    public List<TypeExpr> Implements { get; } = [];
    public List<RawMember> Members { get; } = [];
    /// <summary>Dotted names used in bodies and initializers (usage dependencies), with their lines.</summary>
    public List<NameRef> BodyRefs { get; } = [];
    public HashSet<string> TypeParams { get; } = new(StringComparer.Ordinal);
    /// <summary>Language-specific resolution context (imports, module …).</summary>
    public object? Scope { get; init; }
}

internal sealed class RawMember
{
    public required string Name { get; init; }
    public CodeMemberKind Kind { get; init; }
    public TypeExpr? Type { get; set; }
    public string TypeText { get; set; } = "";
    public string Visibility { get; init; } = "public";
    public List<RawParam> Params { get; init; } = [];
    public bool IsAbstract { get; init; }
    public bool IsStatic { get; init; }
    public bool IsOptional { get; init; }
    public string? Doc { get; init; }
    public int Line { get; init; }
    /// <summary>The file it is declared in when that is not the owning type's (declaration merging across files).</summary>
    public string? File { get; set; }
}

internal sealed record RawParam(string Name, TypeExpr? Type, string TypeText, CodeParameterDirection Direction = CodeParameterDirection.In);
