using System.Text;

namespace Meridian.Core.CodeSync;

/// <summary>
/// Readable names for elements made from code, without AI: identifiers split into words in sentence case
/// ("CodeSyncRunner" → "Code sync runner", "IGitHost" → "Git host", "parse_http_request" → "Parse HTTP request"), with
/// the affixes that only matter to a compiler dropped (an interface's "I", "Impl", "Base" on abstract classes stays).
/// Known acronyms keep their capitals ("XmlReader" → "XML reader", "HttpClient" → "HTTP client"). The code symbol stays on
/// the element, so a readable name never changes how re-syncs match.
/// </summary>
public static class CodeNames
{
    /// <summary>Words kept in capitals wherever they appear.</summary>
    private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "API", "UI", "ID", "IO", "HTTP", "HTTPS", "URL", "URI", "SQL", "JSON", "XML", "XMI", "HTML", "CSS", "CSV", "PDF", "SVG", "PNG",
        "DB", "AI", "MCP", "SDK", "CLI", "OS", "TCP", "UDP", "IP", "DNS", "JWT", "OAuth", "SSO", "SAML", "LDAP", "FMU", "FMI", "BDD",
        "IBD", "SysML", "UML", "BPMN", "UUID", "GUID", "CPU", "GPU", "RAM", "USB", "PR", "CI", "CD", "QA", "UTC", "AWS", "GCP", "VM",
    };

    /// <summary>
    /// "CodeSyncRunner" → ["Code", "Sync", "Runner"], "HTTPServer2" → ["HTTP", "Server", "2"], "snake_case" → ["snake", "case"].
    /// </summary>
    public static IReadOnlyList<string> Words(string identifier)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(identifier)) return words;
        var cur = new StringBuilder();
        void Flush()
        {
            if (cur.Length > 0) words.Add(cur.ToString());
            cur.Clear();
        }
        for (var i = 0; i < identifier.Length; i++)
        {
            var c = identifier[i];
            if (!char.IsLetterOrDigit(c)) { Flush(); continue; }
            if (cur.Length > 0)
            {
                var prev = identifier[i - 1];
                var next = i + 1 < identifier.Length ? identifier[i + 1] : '\0';
                var boundary =
                    char.IsUpper(c) && char.IsLower(prev)                                   // codeSync
                    || char.IsUpper(c) && char.IsUpper(prev) && char.IsLower(next)          // HTTPServer → HTTP | Server
                    || char.IsDigit(c) && char.IsLetter(prev) && cur.Length > 1             // Version2 (but N0, V2 stay one word)
                    || char.IsUpper(c) && char.IsDigit(prev) && char.IsLower(next);         // V2Importer → V2 | Importer
                if (boundary) Flush();
            }
            cur.Append(c);
        }
        Flush();
        return words;
    }

    /// <summary>
    /// A readable name for a code identifier: words in sentence case, acronyms kept, generic arguments ("Box&lt;T&gt;",
    /// "Box`1") left out. <paramref name="isInterface"/> drops a leading "I" before a capital ("IGitHost" → "Git host").
    /// "Impl" / "Implementation" at the end are dropped. An identifier that yields no words is returned as it is.
    /// </summary>
    public static string Humanize(string identifier, bool isInterface = false)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return identifier ?? "";
        var core = identifier;
        var cut = core.IndexOfAny(['<', '`']);
        if (cut > 0) core = core[..cut];
        if (isInterface && core.Length > 2 && core[0] == 'I' && char.IsUpper(core[1]) && char.IsLower(core[2])) core = core[1..];
        var words = Words(core).ToList();
        if (words.Count > 1 && words[^1] is "Impl" or "Implementation") words.RemoveAt(words.Count - 1);
        if (words.Count == 0) return identifier;
        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (i > 0) sb.Append(' ');
            if (Acronyms.TryGetValue(w, out var acronym)) sb.Append(acronym);
            else if (w.Length > 1 && w.All(char.IsUpper)) sb.Append(w);   // an all-caps word is an acronym we do not know
            else if (i == 0) sb.Append(char.ToUpperInvariant(w[0])).Append(w.AsSpan(1).ToString().ToLowerInvariant());
            else sb.Append(w.ToLowerInvariant());
        }
        return sb.ToString();
    }

    /// <summary>A namespace segment or project name ("Meridian.Core.CodeSync" → "Code sync" for the last segment).</summary>
    public static string HumanizeLastSegment(string dotted)
    {
        if (string.IsNullOrEmpty(dotted)) return dotted ?? "";
        var dot = dotted.LastIndexOf('.');
        return Humanize(dot >= 0 ? dotted[(dot + 1)..] : dotted);
    }

    /// <summary>"Code sync runner" for "CodeSyncRunner", in lower case for use inside a sentence ("the code sync runner").</summary>
    public static string Lower(string readable) =>
        string.Join(' ', readable.Split(' ').Select(w => w.Length > 1 && w.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? w : w.ToLowerInvariant()));
}
