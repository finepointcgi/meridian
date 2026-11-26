using System.Text.RegularExpressions;

namespace Meridian.Core.CodeSync;

/// <summary>
/// Plain-words phrasing of method bodies for behaviour diagrams, without AI: a condition as a question
/// ("user != null" → "Is user set?", "items.Count == 0" → "Is items empty?", "IsReadOnly" → "Is read only?"), a call
/// as a step ("await db.LoadProjectAsync(id)" → "Load project from db"), a loop header as "More items?". The phrasing
/// is a best effort over the code as written: anything it does not recognise is still shown in words (identifiers split,
/// operators spelled out), never as an empty "decision". Deterministic: the same code always reads the same.
/// </summary>
public static partial class CodePhrases
{
    /// <summary>The longest phrase returned (the rest is cut with "…").</summary>
    public const int MaxLength = 60;

    // ================================================================ conditions

    /// <summary>A yes/no question for a condition: "Is user set?", "Does order have items?", "Is count more than 3?".</summary>
    public static string Question(string? condition)
    {
        var clause = Clause(condition);
        if (clause.Length == 0) return "Condition holds?";
        return Cap(Ask(clause)) + "?";
    }

    /// <summary>The condition as a statement in words, lower case: "user is set", "items is empty", "not read only".</summary>
    public static string Clause(string? condition)
    {
        var c = Strip(condition);
        if (c.Length == 0) return "";
        return Clip(ClauseOf(c, 0));
    }

    private static string ClauseOf(string c, int depth)
    {
        c = Strip(c);
        if (depth > 6 || c.Length == 0) return Noun(c);
        if (SplitTop(c, "||") is { Count: > 1 } ors) return string.Join(" or ", ors.Select(o => ClauseOf(o, depth + 1)));
        if (SplitTop(c, "&&") is { Count: > 1 } ands) return string.Join(" and ", ands.Select(a => ClauseOf(a, depth + 1)));
        if (c.StartsWith('!') && !c.StartsWith("!=")) return Negate(ClauseOf(c[1..], depth + 1));

        if (PropertyPattern().Match(c) is { Success: true } pp)
        {
            // "x is { }" is a null check; "x is { Created: false, … }" a shape check.
            var empty = pp.Groups["body"].Value.Trim().Length == 0;
            var not = pp.Groups["not"].Success;
            return empty ? $"{Noun(pp.Groups["x"].Value)} is {(not ? "missing" : "set")}" : $"{Noun(pp.Groups["x"].Value)} {(not ? "does not match" : "matches")}";
        }
        if (IsNullCheck().Match(c) is { Success: true } nm)
            return $"{Noun(nm.Groups["x"].Value)} is {(nm.Groups["op"].Value is "!=" or "is not" ? "set" : "missing")}";
        if (EmptyCheck().Match(c) is { Success: true } em)
            return $"{Noun(em.Groups["x"].Value)} is {(em.Groups["op"].Value is "==" or "<=" ? "empty" : "not empty")}";
        if (StringEmpty().Match(c) is { Success: true } se) return $"{Noun(se.Groups["x"].Value)} is empty";
        if (AnyCall().Match(c) is { Success: true } any)
            return any.Groups["args"].Value.Length == 0 ? $"{Noun(any.Groups["x"].Value)} has any" : $"any {Noun(any.Groups["x"].Value)} match";
        if (TypeTest().Match(c) is { Success: true } tt)
            return $"{Noun(tt.Groups["x"].Value)} is {(tt.Groups["not"].Success ? "not " : "")}{Article(CodeNames.Lower(CodeNames.Humanize(tt.Groups["t"].Value)))}";
        if (Comparison().Match(c) is { Success: true } cm)
        {
            var (a, b) = (Noun(cm.Groups["a"].Value), Value(cm.Groups["b"].Value));
            return cm.Groups["op"].Value switch
            {
                "==" => $"{a} is {b}",
                "!=" => $"{a} is not {b}",
                ">" => $"{a} is more than {b}",
                ">=" => $"{a} is at least {b}",
                "<" => $"{a} is less than {b}",
                _ => $"{a} is at most {b}",
            };
        }
        if (Call().Match(c) is { Success: true } call) return CallClause(call.Groups["r"].Value, call.Groups["m"].Value);
        return BoolMember(c);
    }

    /// <summary>"x.IsReady" → "x is ready", "HasErrors" → "has errors", "user.CanEdit" → "user can edit", "done" → "done".</summary>
    private static string BoolMember(string c)
    {
        var (owner, member) = SplitMember(c);
        var words = CodeNames.Words(member).ToList();
        if (words.Count > 1 && words[0] is "Is" or "Has" or "Can" or "Should" or "Was" or "Are" or "Does" or "Did" or "Needs" or "Allows")
        {
            var rest = CodeNames.Lower(CodeNames.Humanize(string.Concat(words.Skip(1))));
            var verb = words[0].ToLowerInvariant();
            return owner.Length > 0 ? $"{Noun(owner)} {verb} {rest}" : $"{verb} {rest}";
        }
        return Noun(c);
    }

    private static string CallClause(string receiver, string method)
    {
        var words = CodeNames.Words(StripAsync(method)).ToList();
        if (words.Count == 0) return Noun(method);
        if (words[0] is "Try" && words.Count > 1)
        {
            // TryGetValue / TryFind → "value is found in cache"; TryParse → "parse works".
            var found = words[1] is "Get" or "Find" or "Lookup" or "Resolve" && words.Count > 2;
            var rest = CodeNames.Lower(CodeNames.Humanize(string.Concat(words.Skip(found ? 2 : 1))));
            if (found) return receiver.Length > 0 ? $"{rest} is found in {Noun(receiver)}" : $"{rest} is found";
            return receiver.Length > 0 ? $"{rest} on {Noun(receiver)} works" : $"{rest} works";
        }
        var phrase = CodeNames.Lower(CodeNames.Humanize(string.Concat(words)));
        if (words[0] is "Is" or "Has" or "Can" or "Contains" or "Exists" or "Starts" or "Ends" or "Equals" or "Matches")
            return receiver.Length > 0 ? $"{Noun(receiver)} {phrase}" : phrase;
        return receiver.Length > 0 ? $"{phrase} on {Noun(receiver)} succeeds" : $"{phrase} succeeds";
    }

    /// <summary>"x is set" → "x is not set"; anything else → "not (…)".</summary>
    private static string Negate(string clause)
    {
        if (clause.Contains(" is not ", StringComparison.Ordinal)) return clause.Replace(" is not ", " is ", StringComparison.Ordinal);
        if (clause.Contains(" is ", StringComparison.Ordinal) && !clause.Contains(" and ") && !clause.Contains(" or "))
        {
            var i = clause.IndexOf(" is ", StringComparison.Ordinal);
            return clause[..i] + " is not " + clause[(i + 4)..];
        }
        if (clause.StartsWith("is ", StringComparison.Ordinal)) return "is not " + clause[3..];
        if (clause.StartsWith("has ", StringComparison.Ordinal)) return "has no " + clause[4..];
        if (clause.StartsWith("can ", StringComparison.Ordinal)) return "cannot " + clause[4..];
        if (clause.Contains(" has ", StringComparison.Ordinal) && !clause.Contains(" and ") && !clause.Contains(" or "))
            return clause.Replace(" has any", " has none").Replace(" has ", " has no ");
        return "not " + (clause.Contains(' ') ? $"({clause})" : clause);
    }

    /// <summary>Turns a clause into a question: "user is set" → "is user set", "order has items" → "does order have items".</summary>
    private static string Ask(string clause)
    {
        if (clause.Contains(" and ") || clause.Contains(" or ")) return clause;
        if (clause.StartsWith("is ") || clause.StartsWith("can ") || clause.StartsWith("should ") || clause.StartsWith("was ") || clause.StartsWith("are "))
            return clause;
        if (clause.StartsWith("has ")) return "has " + clause[4..];
        var i = clause.IndexOf(" is ", StringComparison.Ordinal);
        if (i > 0) return "is " + clause[..i] + " " + clause[(i + 4)..];
        i = clause.IndexOf(" can ", StringComparison.Ordinal);
        if (i > 0) return "can " + clause[..i] + " " + clause[(i + 5)..];
        i = clause.IndexOf(" has ", StringComparison.Ordinal);
        if (i > 0) return "does " + clause[..i] + " have " + clause[(i + 5)..];
        return clause;
    }

    // ================================================================ loops and switches

    /// <summary>The question a loop asks each round: "More items?" (foreach), the condition's question (while / for).</summary>
    public static string LoopQuestion(CodeLoopKind? kind, string? header)
    {
        var h = Strip(header);
        if (kind == CodeLoopKind.ForEach)
        {
            var i = h.IndexOf(" in ", StringComparison.Ordinal);
            var source = i >= 0 ? h[(i + 4)..] : h;
            return Clip($"More {Noun(source)}?");
        }
        if (kind == CodeLoopKind.For)
        {
            var parts = h.Split(';');
            return parts.Length >= 2 && parts[1].Trim().Length > 0 ? Question(parts[1]) : "Keep going?";
        }
        return h.Length == 0 || h == "true" ? "Keep going?" : Question(h);
    }

    /// <summary>"What is mode?" for a switch on "mode" / "state.Mode".</summary>
    public static string SwitchQuestion(string? subject) => Clip($"What is {Noun(Strip(subject))}?");

    /// <summary>A case label in words: "Mode.Safe" → "safe", "case > 3" → "more than 3", "default" → "otherwise".</summary>
    public static string CaseLabel(string? label)
    {
        var l = Strip(label);
        if (l.Length == 0 || l == "default" || l == "_") return "otherwise";
        if (l.StartsWith("case ", StringComparison.Ordinal)) l = l[5..].Trim();
        var parts = SplitTop(l, ",");
        return Clip(string.Join(" or ", parts.Select(p => Value(p.Trim()))));
    }

    // ================================================================ steps

    /// <summary>A call as a step: "Load project from db", "Add to items", "Validate".</summary>
    public static string Step(string? method, string? receiver)
    {
        // Query and conversion calls gather what they are called on: "items.ToList()" → "Collect items".
        if (method is not null && QueryCalls.Contains(method) && receiver is { Length: > 0 } && receiver is not ("this" or "self"))
            return Clip("Collect " + Noun(receiver));
        var words = CodeNames.Words(StripAsync(method ?? "")).ToList();
        if (words.Count == 0) return "Do something";
        var verb = words[0];
        var rest = words.Count > 1 ? CodeNames.Lower(CodeNames.Humanize(string.Concat(words.Skip(1)))) : "";
        var r = receiver is null or "" or "this" or "self" or "base" or "super" ? "" : Noun(receiver);
        var head = rest.Length > 0 ? CodeNames.Humanize(verb) + " " + rest : CodeNames.Humanize(verb);
        if (r.Length == 0) return Clip(head);
        var preposition = verb switch
        {
            "Get" or "Find" or "Load" or "Read" or "Fetch" or "Query" or "Download" or "Receive" or "Pop" or "Dequeue" or "Take" or "Remove" or "Delete" => "from",
            "Save" or "Write" or "Send" or "Post" or "Upload" or "Store" or "Push" or "Enqueue" or "Add" or "Append" or "Insert" or "Publish" or "Emit" => "to",
            _ => "on",
        };
        if (rest.Length == 0 && preposition == "on") return Clip($"{head} {r}");
        return Clip($"{head} {preposition} {r}");
    }

    /// <summary>"Create order" for "new Order(…)" / "Order" created.</summary>
    public static string Create(string? type) => Clip("Create " + CodeNames.Lower(CodeNames.Humanize(Strip(type).Split('(')[0].Replace("new ", ""))));

    /// <summary>"Set total" for "total = …", "Set total, count" for several.</summary>
    public static string Set(IEnumerable<string?> targets)
    {
        var names = targets.Select(t => Noun(Strip(t).Split('=')[0].Trim().Replace("var ", ""))).Where(n => n.Length > 0).Distinct().ToList();
        if (names.Count == 0) return "Update values";
        return Clip("Set " + string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" (+{names.Count - 3})" : ""));
    }

    /// <summary>"Fail: invalid operation" for a thrown InvalidOperationException.</summary>
    public static string Fail(string? exception)
    {
        var t = Strip(exception).Replace("new ", "").Split('(')[0].Trim();
        if (t.EndsWith("Exception", StringComparison.Ordinal) && t.Length > 9) t = t[..^9];
        if (t.EndsWith("Error", StringComparison.Ordinal) && t.Length > 5) t = t[..^5];
        var words = CodeNames.Lower(CodeNames.Humanize(t));
        return Clip(words.Length == 0 ? "Fail" : $"Fail: {words}");
    }

    /// <summary>"Return result" for "return result", "Return" for a bare return.</summary>
    public static string Return(string? value)
    {
        var v = Strip(value);
        if (v.Length == 0) return "Done";
        if (v is "true" or "false" or "null") return $"Return {v}";
        return Clip("Return " + Noun(v));
    }

    // ================================================================ words

    /// <summary>An expression as a noun phrase: "_project.Name" → "project name", "this.items" → "items", "GetUser()" → "user".</summary>
    public static string Noun(string? expression)
    {
        var e = Strip(expression);
        if (e.Length == 0) return "";
        // Clipped or lambda-laden text: what comes before the first call that does not close, or the first query call.
        if (FirstQuery().Match(e) is { Success: true } q) e = e[..q.Index];
        if (e.Count(ch => ch == '(') != e.Count(ch => ch == ')') && e.IndexOf('(') is > 0 and var open) e = e[..open];
        e = e.Replace("await ", "").Replace("this.", "").Replace("self.", "").Replace("?.", ".").Replace("!.", ".").TrimEnd('!');
        if (e.StartsWith('"') || e.StartsWith('\'') || IsNumber(e)) return e;
        // Calls: the method's words without the verb "Get".
        e = ArgList().Replace(e, "");
        var segments = e.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => s.TrimStart('_', '@', '$')).Where(s => s.Length > 0).ToList();
        if (segments.Count == 0) return Clip(e);
        // Query and conversion calls at the end do not change what it is about: "_batteries.Where(…)" → "batteries".
        while (segments.Count > 1 && QueryCalls.Contains(segments[^1])) segments.RemoveAt(segments.Count - 1);
        // A one- or two-letter local in front ("b.Level") says nothing: "level".
        if (segments.Count > 1 && segments[0].Length <= 2 && segments[0].All(char.IsLower)) segments.RemoveAt(0);
        // The last two segments say enough: "order.Lines.Count" → "lines count".
        var keep = segments.Skip(Math.Max(0, segments.Count - 2)).Select(s =>
        {
            var w = CodeNames.Words(s).ToList();
            if (w.Count > 1 && w[0] is "Get") w.RemoveAt(0);
            return CodeNames.Lower(CodeNames.Humanize(string.Concat(w)));
        });
        return Clip(string.Join(" ", keep).Trim());
    }

    /// <summary>A compared value: enum members and constants in words ("Mode.Safe" → "safe"), literals as written.</summary>
    private static string Value(string v)
    {
        v = Strip(v);
        if (v is "null") return "missing";
        if (v.StartsWith('"') || v.StartsWith('\'') || IsNumber(v) || v is "true" or "false") return v;
        var dot = v.LastIndexOf('.');
        if (dot > 0 && char.IsUpper(v[0]) && !v.Contains('(')) return CodeNames.Lower(CodeNames.Humanize(v[(dot + 1)..]));
        return Noun(v);
    }

    private static readonly HashSet<string> QueryCalls = new(StringComparer.Ordinal)
    {
        "Where", "Select", "SelectMany", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "ToList", "ToArray", "ToHashSet",
        "ToDictionary", "AsEnumerable", "Distinct", "Skip", "Take", "Reverse", "Cast", "OfType", "Concat", "Append", "Prepend", "ToListAsync",
        "ToArrayAsync", "Values", "Keys", "ConfigureAwait", "Result",
    };

    private static string Article(string noun) => noun.Length == 0 ? noun : ("aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "an " : "a ") + noun;

    private static bool IsNumber(string s) => s.Length > 0 && (char.IsDigit(s[0]) || s[0] == '-' && s.Length > 1 && char.IsDigit(s[1]));

    private static string StripAsync(string method) => method.EndsWith("Async", StringComparison.Ordinal) && method.Length > 5 ? method[..^5] : method;

    private static (string Owner, string Member) SplitMember(string c)
    {
        var e = c.Replace("this.", "").Replace("?.", ".");
        var dot = e.LastIndexOf('.');
        return dot > 0 && !e[..dot].Contains('(') ? (e[..dot], e[(dot + 1)..]) : ("", e);
    }

    /// <summary>Removes outer parentheses, "await", a trailing "…" from clipping and extra whitespace.</summary>
    private static string Strip(string? s)
    {
        var t = (s ?? "").Trim();
        if (t.EndsWith('…')) t = t[..^1].TrimEnd();
        while (t.Length > 1 && t[0] == '(' && Closing(t, 0) == t.Length - 1) t = t[1..^1].Trim();
        return t;
    }

    private static int Closing(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>Splits on <paramref name="op"/> outside parentheses, brackets and strings.</summary>
    private static List<string> SplitTop(string s, string op)
    {
        var parts = new List<string>();
        var depth = 0;
        var inString = false;
        var start = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"') inString = !inString;
            if (inString) continue;
            if (c is '(' or '[' or '{' or '<') depth++;
            else if (c is ')' or ']' or '}' or '>' && depth > 0 && !(c == '>' && i > 0 && s[i - 1] is '=' or '-')) depth--;
            else if (depth == 0 && string.CompareOrdinal(s, i, op, 0, op.Length) == 0)
            {
                parts.Add(s[start..i].Trim());
                i += op.Length - 1;
                start = i + 1;
            }
        }
        parts.Add(s[start..].Trim());
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Clip(string s)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.Length <= MaxLength ? s : s[..(MaxLength - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"^(?<x>[^{}]+?)\s+is\s+(?<not>not\s+)?\{(?<body>.*)\}(\s+\w+)?$")]
    private static partial Regex PropertyPattern();

    [GeneratedRegex(@"\.(Where|Select|SelectMany|OrderBy|OrderByDescending|Any|All|First|FirstOrDefault|Distinct|Skip|Take|OfType|Cast)\(")]
    private static partial Regex FirstQuery();

    [GeneratedRegex(@"^(?<x>.+?)\s*(?<op>==|!=|is not|is)\s*null$")]
    private static partial Regex IsNullCheck();

    [GeneratedRegex(@"^(?<x>.+?)\.(Count|Length)(\(\))?\s*(?<op>==|<=|>|!=|>=)\s*(0|1)$")]
    private static partial Regex EmptyCheck();

    [GeneratedRegex(@"^(string|String)\.IsNullOrEmpty\((?<x>.+)\)$|^(string|String)\.IsNullOrWhiteSpace\((?<x>.+)\)$")]
    private static partial Regex StringEmpty();

    [GeneratedRegex(@"^(?<x>.+?)\.Any\((?<args>.*)\)$")]
    private static partial Regex AnyCall();

    [GeneratedRegex(@"^(?<x>[\w\.\[\]]+?)\s+is\s+(?<not>not\s+)?(?<t>[A-Z]\w*)(\s+\w+)?$")]
    private static partial Regex TypeTest();

    [GeneratedRegex(@"^(?<a>[^=!<>]+?)\s*(?<op>==|!=|>=|<=|>|<)\s*(?<b>[^=<>]+)$")]
    private static partial Regex Comparison();

    [GeneratedRegex(@"^(?:(?<r>[\w\.]+)\.)?(?<m>[A-Za-z_]\w*)\((?<args>.*)\)$")]
    private static partial Regex Call();

    [GeneratedRegex(@"\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)")]
    private static partial Regex ArgList();
}
