namespace Meridian.Core.CodeSync;

/// <summary>A method of the code model: its type, the member and its operation symbol.</summary>
public sealed record CodeMethod(CodeType Type, CodeMember Member, string Symbol)
{
    public bool IsVoid => Member.TypeName is "" or "void" or "None" or "Task" or "ValueTask" or "Promise<void>" or "null" or "undefined";
}

/// <summary>Lookups over a <see cref="CodeModel"/> that behaviour diagrams need: methods by operation symbol, calls
/// resolved to methods, implementations of interface methods, and entry points.</summary>
public sealed class CodeBehaviourIndex
{
    private readonly Dictionary<string, CodeType> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CodeMethod> _methods = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CodeType>> _implementors = new(StringComparer.Ordinal);

    public CodeBehaviourIndex(CodeModel code)
    {
        foreach (var t in code.Types)
        {
            if (string.IsNullOrEmpty(t.Symbol) || !_types.TryAdd(t.Symbol, t)) continue;
            foreach (var m in t.Members)
                if (m.Kind == CodeMemberKind.Method && !string.IsNullOrEmpty(m.Name))
                {
                    var symbol = CodeModelMapper.OperationSymbol(t.Symbol, m);
                    _methods.TryAdd(symbol, new CodeMethod(t, m, symbol));
                }
            foreach (var b in t.BaseTypes.Concat(t.Interfaces).Distinct(StringComparer.Ordinal))
            {
                if (!_implementors.TryGetValue(b, out var list)) _implementors[b] = list = [];
                list.Add(t);
            }
        }
    }

    public IEnumerable<CodeMethod> Methods => _methods.Values;

    public CodeType? Type(string? symbol) => symbol != null && _types.TryGetValue(symbol, out var t) ? t : null;

    public CodeMethod? Method(string? operationSymbol) => operationSymbol != null && _methods.TryGetValue(operationSymbol, out var m) ? m : null;

    /// <summary>The method a call goes to: the exact operation when the reader resolved it, else the receiver type's (or a
    /// base type's) method of that name, preferring one with as many parameters as the call has arguments.</summary>
    public CodeMethod? Resolve(CodeStatement call)
    {
        if (Method(call.Operation) is { } exact) return exact;
        if (string.IsNullOrEmpty(call.Method)) return null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var t = Type(call.ReceiverType); t != null && seen.Add(t.Symbol); t = Type(t.BaseTypes.FirstOrDefault()))
        {
            var named = t.Members.Where(m => m.Kind == CodeMemberKind.Method && m.Name == call.Method).ToList();
            if (named.Count == 0) continue;
            var pick = named.FirstOrDefault(m => m.Parameters.Count(p => p.Direction != CodeParameterDirection.Return) == call.ArgumentCount) ?? named[0];
            return Method(CodeModelMapper.OperationSymbol(t.Symbol, pick));
        }
        return null;
    }

    /// <summary>The body to follow for <paramref name="m"/>: its own, or (for an interface or abstract method) the only
    /// scanned implementation's.</summary>
    public CodeMethod? WithBody(CodeMethod m)
    {
        if (m.Member.Body != null) return m;
        if (!_implementors.TryGetValue(m.Type.Symbol, out var impls)) return null;
        var found = impls.Select(t => t.Members.FirstOrDefault(x => x.Kind == CodeMemberKind.Method && x.Name == m.Member.Name && x.Body != null
                                                                    && x.Parameters.Count == m.Member.Parameters.Count) is { } x ? new CodeMethod(t, x, CodeModelMapper.OperationSymbol(t.Symbol, x)) : null)
            .OfType<CodeMethod>().ToList();
        return found.Count == 1 ? found[0] : null;
    }

    // ================================================================ entry points

    private HashSet<string>? _calledInside;

    /// <summary>Types whose methods are called from another type of their own namespace.</summary>
    private HashSet<string> CalledInside()
    {
        if (_calledInside != null) return _calledInside;
        _calledInside = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in _methods.Values)
        {
            if (m.Member.Body is not { } body) continue;
            foreach (var s in CodeBodies.Walk(body.Statements))
                if (s.HasCall || s.Kind == CodeStatementKind.Call)
                    if (Type(s.ReceiverType) is { } callee && callee.Symbol != m.Type.Symbol && callee.Namespace == m.Type.Namespace)
                        _calledInside.Add(callee.Symbol);
        }
        return _calledInside;
    }

    private static readonly string[] TestAttributes = ["Fact", "Theory", "Test", "TestMethod", "TestCase", "SkippableFact", "DataTestMethod"];
    private static readonly string[] EndpointAttributes = ["HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "Route", "GetMapping", "PostMapping",
        "PutMapping", "DeleteMapping", "PatchMapping", "RequestMapping", "app.get", "app.post", "router.get", "router.post"];
    private static readonly string[] EntryTypeSuffixes = ["Controller", "Handler", "Endpoint", "Endpoints", "Consumer", "Worker", "HostedService", "Job"];
    private static readonly string[] HostBases = ["BackgroundService", "IHostedService", "ControllerBase", "Controller", "IRequestHandler", "INotificationHandler", "IConsumer"];

    /// <summary>A test class or test method (xUnit, NUnit, MSTest, JUnit, pytest naming).</summary>
    public static bool IsTest(CodeType t, CodeMember? m = null) =>
        m != null && (m.Attributes.Any(a => TestAttributes.Contains(a)) || m.Name.StartsWith("test_", StringComparison.Ordinal))
        || t.Members.Any(x => x.Attributes.Any(a => TestAttributes.Contains(a)))
        || t.Name.EndsWith("Tests", StringComparison.Ordinal) || t.Name.EndsWith("Test", StringComparison.Ordinal) && t.Name.Length > 4;

    /// <summary>Why <paramref name="m"/> is an entry point ("endpoint", "controller", "hosted service", "handler", "API of its
    /// namespace"); null when it is not one.</summary>
    public string? EntryReason(CodeMethod m)
    {
        var (t, mem) = (m.Type, m.Member);
        if (mem.Body is null || IsTest(t, mem)) return null;
        if (mem.Attributes.Any(a => EndpointAttributes.Contains(a))) return "endpoint";
        var calls = CodeBodies.Walk(mem.Body.Statements).Where(s => s.HasCall || s.Kind == CodeStatementKind.Call).ToList();
        if (calls.Any(c => c.Method is { } n && n.StartsWith("Map", StringComparison.Ordinal) && n.Length > 3 && n[3..] is "Get" or "Post" or "Put" or "Delete" or "Patch" or "Methods" or "Group" or "Fallback"))
            return "endpoint mapping";
        var hosted = t.ExternalBases.Any(b => b is "BackgroundService" or "IHostedService");
        if (hosted && mem.Name is "ExecuteAsync" or "StartAsync" or "StopAsync") return "hosted service";
        if (mem.Visibility != "public") return null;
        if (t.Attributes.Contains("ApiController") || t.ExternalBases.Any(b => HostBases.Contains(b))) return "controller or handler";
        if (EntryTypeSuffixes.Any(s => t.Name.EndsWith(s, StringComparison.Ordinal))) return "controller or handler";
        if (mem.Name is "Main" && mem.IsStatic) return "main";
        // The namespace's API: public methods of a type nothing else in its namespace calls, that call into other types.
        if (!CalledInside().Contains(t.Symbol) && calls.Any(c => c.ReceiverType is { } r && r != t.Symbol && Type(r) != null)) return "API of its namespace";
        return null;
    }
}
