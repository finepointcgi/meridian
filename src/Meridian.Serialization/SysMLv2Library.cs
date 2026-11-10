using System.Text.RegularExpressions;

namespace Meridian.Serialization;

/// <summary>
/// The SysML v2 standard library as a read-only model for name resolution: ScalarValues, Base, Items, Parts, Ports, Actions,
/// States, Requirements, Cases, Views, Metadata, Collections, ISQ quantities and SI / US customary units, and so on. It is
/// parsed once from Meridian's own declarations (embedded <c>SysMLv2Library/*.sysml</c>), is never part of a user's project and
/// never changes. References such as <c>ScalarValues::Real</c>, <c>ISQ::mass</c> or <c>SI::kg</c> resolve against it.
/// </summary>
public sealed partial class SysMLv2Library
{
    public sealed record Entry(string QualifiedName, string Name, SysmlDecl Decl)
    {
        public bool IsPackage => Decl.Keyword == "package";

        /// <summary>A measurement unit: a usage typed by a <c>…Unit</c> definition (SI::kg, USCustomaryUnits::lb …).</summary>
        public bool IsUnit => !Decl.IsDef && Decl.Types.Count > 0 && Decl.Types[0].Simple.EndsWith("Unit", StringComparison.Ordinal);
    }

    private static readonly Lazy<SysMLv2Library> _standard = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The standard library (loaded on first use, shared, immutable).</summary>
    public static SysMLv2Library Standard => _standard.Value;

    private readonly Dictionary<string, Entry> _byQualified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Entry>> _bySimple = new(StringComparer.Ordinal);

    public IReadOnlyList<SysmlDocument> Documents { get; }

    /// <summary>Top-level library package names.</summary>
    public IReadOnlyList<string> Packages { get; }

    /// <summary>Problems found while loading the library (expected to be empty).</summary>
    public IReadOnlyList<SysmlDiagnostic> Diagnostics { get; }

    private SysMLv2Library(IReadOnlyList<SysmlDocument> docs)
    {
        Documents = docs;
        foreach (var d in docs)
            foreach (var top in d.Members.OfType<SysmlDecl>()) Index(top, "");
        Packages = docs.SelectMany(d => d.Members.OfType<SysmlDecl>()).Where(d => d.Keyword == "package" && d.Name != null).Select(d => d.Name!).ToList();
        Diagnostics = docs.SelectMany(d => d.Diagnostics).ToList();
    }

    private static SysMLv2Library Load()
    {
        var asm = typeof(SysMLv2Library).Assembly;
        var docs = new List<SysmlDocument>();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.EndsWith(".sysml", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            docs.Add(SysMLv2Parser.Parse(reader.ReadToEnd(), lint: false));
        }
        return new SysMLv2Library(docs);
    }

    private void Index(SysmlDecl d, string prefix)
    {
        foreach (var n in new[] { d.Name, d.ShortName }.OfType<string>().Distinct())
        {
            var q = prefix + n;
            var e = new Entry(q, n, d);
            _byQualified.TryAdd(q, e);
            if (!_bySimple.TryGetValue(n, out var l)) _bySimple[n] = l = new List<Entry>();
            l.Add(e);
        }
        if (d.Name == null) return;
        foreach (var c in d.Decls) Index(c, prefix + d.Name + "::");
    }

    /// <summary>Resolves a simple or qualified name; a simple name matches any library declaration of that name.</summary>
    public Entry? Find(IReadOnlyList<string> segments)
    {
        if (segments.Count == 0) return null;
        if (segments.Count == 1)
            return _bySimple.TryGetValue(segments[0], out var l) ? l.FirstOrDefault(e => e.IsPackage) ?? l[0] : null;
        return _byQualified.GetValueOrDefault(string.Join("::", segments));
    }

    public Entry? Find(string qualifiedName) => Find(qualifiedName.Split("::"));

    public bool Contains(IReadOnlyList<string> segments) => Find(segments) != null;

    /// <summary>A unit by simple name, short name (kg) or qualified name (SI::kg).</summary>
    public Entry? FindUnit(IReadOnlyList<string> segments)
    {
        if (segments.Count == 1)
            return _bySimple.TryGetValue(segments[0], out var l) ? l.FirstOrDefault(e => e.IsUnit) : null;
        return Find(segments) is { IsUnit: true } e ? e : null;
    }

    /// <summary>All units, by the names they can be written with.</summary>
    public IEnumerable<Entry> Units => _byQualified.Values.Where(e => e.IsUnit);

    // ================================================================== unit checks

    [GeneratedRegex(@"\[(?<body>[^\[\]\n;{}]*)\]")]
    private static partial Regex Bracket();

    [GeneratedRegex(@"'(?:[^'\\\n]|\\.)*'|[A-Za-z_][A-Za-z0-9_]*(?:::(?:[A-Za-z_][A-Za-z0-9_]*|'(?:[^'\\\n]|\\.)*'))*")]
    private static partial Regex UnitName();

    /// <summary>Warns about unknown units in quantity literals (<c>1450 [kg]</c>, <c>(a + b) [m/s]</c>): every name inside the
    /// brackets must be a library unit or a name the document declares.</summary>
    internal static void CheckUnits(SysmlDocument doc)
    {
        var text = Blank(doc.Text);
        HashSet<string>? declared = null;
        foreach (Match m in Bracket().Matches(text))
        {
            if (!AfterValue(text, m.Index)) continue;
            foreach (Match u in UnitName().Matches(m.Groups["body"].Value))
            {
                var segs = SplitName(u.Value);
                if (Standard.FindUnit(segs) != null) continue;
                declared ??= doc.AllDecls().SelectMany(d => new[] { d.Name, d.ShortName }).OfType<string>().ToHashSet(StringComparer.Ordinal);
                if (declared.Contains(segs[^1])) continue;
                var at = m.Groups["body"].Index + u.Index;
                doc.Diagnostics.Add(new SysmlDiagnostic(SysmlSeverity.Warning, $"Unknown unit '{u.Value}'", SpanOf(doc.Text, at, at + u.Length)));
            }
        }
    }

    /// <summary>True when the '[' at <paramref name="at"/> follows a number literal or a closing parenthesis (a quantity, not a multiplicity).</summary>
    private static bool AfterValue(string text, int at)
    {
        var k = at - 1;
        while (k >= 0 && text[k] is ' ' or '\t') k--;
        if (k < 0) return false;
        if (text[k] == ')') return true;
        if (!char.IsDigit(text[k])) return false;
        while (k >= 0 && (char.IsDigit(text[k]) || text[k] == '.' || text[k] is 'e' or 'E' && k > 0 && char.IsDigit(text[k - 1]))) k--;
        return k < 0 || !(char.IsLetter(text[k]) || text[k] == '_');
    }

    internal static IReadOnlyList<string> SplitName(string name) =>
        name.Split("::").Select(s => s.Length >= 2 && s[0] == '\'' && s[^1] == '\'' ? s[1..^1].Replace("\\'", "'") : s).ToList();

    /// <summary>The text with comments and string literals replaced by spaces (offsets preserved).</summary>
    private static string Blank(string s)
    {
        var c = s.ToCharArray();
        for (var i = 0; i < c.Length; i++)
        {
            if (c[i] == '/' && i + 1 < c.Length && c[i + 1] == '/')
            {
                while (i < c.Length && c[i] != '\n') c[i++] = ' ';
            }
            else if (c[i] == '/' && i + 1 < c.Length && c[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? c.Length : end + 2;
                for (; i < end; i++) if (c[i] != '\n') c[i] = ' ';
                i--;
            }
            else if (c[i] == '"')
            {
                c[i++] = ' ';
                while (i < c.Length && c[i] != '"' && c[i] != '\n') { if (c[i] == '\\' && i + 1 < c.Length) c[i++] = ' '; c[i++] = ' '; }
                if (i < c.Length && c[i] == '"') c[i] = ' ';
            }
            else if (c[i] == '\'')
            {   // quoted names are kept (units such as '°C'); skip over them so their content isn't scanned as code
                i++;
                while (i < c.Length && c[i] != '\'' && c[i] != '\n') { if (c[i] == '\\') i++; i++; }
            }
        }
        return new string(c);
    }

    internal static TextSpan SpanOf(string text, int start, int end)
    {
        var line = 1; var lineStart = 0;
        for (var i = 0; i < start && i < text.Length; i++)
            if (text[i] == '\n') { line++; lineStart = i + 1; }
        return new TextSpan(start, end, line, start - lineStart + 1);
    }
}
