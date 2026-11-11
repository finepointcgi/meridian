using Meridian.Core.CodeSync;

namespace Meridian.Serialization.CodeSync.Parsing;

/// <summary>
/// Resolves the receivers of a parsed body to scanned types: <c>this</c> / <c>self</c> and plain calls of the type's own
/// methods, fields and properties of the type (also <c>this.x</c> / <c>self.x</c>), parameters, locals with a known
/// type, and type names (static calls; in Python, calling a class creates it). Calls in <see cref="CodeStatement.Calls"/>
/// that do not resolve are dropped.
/// </summary>
internal static class BodyResolver
{
    public static List<CodeStatement> Resolve(RawBody body, string self, HashSet<string> methods, Dictionary<string, string> fields,
        Dictionary<string, string> pars, Func<string, string?> resolveType, Func<string, string?> resolveChain)
    {
        string? Local(string name) => pars.TryGetValue(name, out var p) ? p
            : body.Locals.TryGetValue(name, out var l) ? resolveType(l)
            : fields.TryGetValue(name, out var f) ? f : null;

        (string? Type, bool Static, bool Create) Receiver(CodeStatement s)
        {
            var r = s.Receiver;
            if (string.IsNullOrEmpty(r))
            {
                if (s.Method is { } m && methods.Contains(m)) return (self, false, false);
                if (s.Method is { Length: > 0 } c && char.IsUpper(c[0]) && resolveType(c) is { } created) return (created, false, true);
                return (null, false, false);
            }
            if (r is "this" or "self" or "cls" or "super") return (self, false, false);
            foreach (var prefix in new[] { "this.", "self.", "cls." })
                if (r.StartsWith(prefix, StringComparison.Ordinal) && !r[prefix.Length..].Contains('.') && fields.TryGetValue(r[prefix.Length..], out var ft)) return (ft, false, false);
            if (!r.Contains('.') && !r.Contains('(') && Local(r) is { } lt) return (lt, false, false);
            if (!r.Contains('(') && resolveChain(r) is { } st) return (st, true, false);
            return (null, false, false);
        }

        CodeStatement Fix(CodeStatement s)
        {
            if (s.Kind == CodeStatementKind.Create && s.Method != null)
                s = s with { ReceiverType = resolveType(s.Method), Receiver = null };
            else if (s.Method != null && s.Kind != CodeStatementKind.Throw)
            {
                var (type, isStatic, create) = Receiver(s);
                s = create && s.Kind == CodeStatementKind.Call
                    ? s with { Kind = CodeStatementKind.Create, ReceiverType = type }
                    : create ? s with { ReceiverType = null }
                    : s with { ReceiverType = type, IsStatic = isStatic, Receiver = s.Receiver ?? (type == self ? "this" : null) };
            }
            return s with
            {
                Body = Map(s.Body), Else = Map(s.Else), Finally = Map(s.Finally),
                Branches = s.Branches.Count == 0 ? s.Branches : s.Branches.Select(b => b with { Body = Map(b.Body) }).ToList(),
                Calls = s.Calls.Count == 0 ? s.Calls : s.Calls.Select(Fix).Where(c => c.ReceiverType != null && c.Kind == CodeStatementKind.Call).ToList(),
            };
        }

        IReadOnlyList<CodeStatement> Map(IReadOnlyList<CodeStatement> list) => list.Count == 0 ? list : list.Select(Fix).ToList();
        return body.Statements.Select(Fix).ToList();
    }
}
