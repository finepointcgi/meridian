using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.DataModeling;

namespace Meridian.Serialization.Database;

/// <summary>
/// CREATE / DROP statements for routines and triggers with their bodies, per dialect, for the DDL and migration writers.
/// Bodies are written as the source had them (they are not translated between dialects). Names come in already quoted
/// and qualified. SQL Server statements end with GO (CREATE OR ALTER must start a batch, so callers start one first);
/// MySQL bodies with semicolons are wrapped in DELIMITER $$; Oracle blocks end with a "/" line.
/// </summary>
public static partial class DbModuleSql
{
    [GeneratedRegex(@"^\s*(BEGIN\s+ATOMIC\b|RETURN\b)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlStandardBody();

    /// <summary>Whether a routine can be created in <paramref name="dialect"/> (its body is known and SQLite has no routines).</summary>
    public static bool CanCreate(DbRoutineDef r, string dialect) => r.Body is { Length: > 0 } && dialect != "sqlite";

    /// <summary>CREATE (or replace) of a routine with its body.</summary>
    public static string CreateRoutine(string name, DbRoutineDef r, string d, bool replace)
    {
        var kind = r.Kind == DbFields.Procedure ? "PROCEDURE" : "FUNCTION";
        var fn = r.Kind != DbFields.Procedure;
        var body = (r.Body ?? "").Trim();
        var opts = r.Options is { Length: > 0 } o ? " " + o.Trim() : "";
        var sb = new StringBuilder();
        switch (d)
        {
            case "postgresql":
            {
                var sqlBody = SqlStandardBody().IsMatch(body);
                var lang = r.Language is { Length: > 0 } l ? l : sqlBody ? "sql" : Regex.IsMatch(body, @"\bBEGIN\b", RegexOptions.IgnoreCase) ? "plpgsql" : "sql";
                sb.Append($"CREATE OR REPLACE {kind} {name}{Params(r.Signature, d, fn)}");
                if (fn && r.Returns is { Length: > 0 } ret) sb.Append($" RETURNS {ret}");
                sb.Append($" LANGUAGE {lang}{opts}\n");
                if (sqlBody) sb.Append(body.TrimEnd(';')).Append(";\n");
                else
                {
                    var tag = DollarTag(body);
                    sb.Append($"AS {tag}\n{body.Trim('\n')}\n{tag};\n");
                }
                break;
            }
            case "sqlserver":
                sb.Append($"CREATE OR ALTER {kind} {name}{Params(r.Signature, d, fn)}");
                if (fn && r.Returns is { Length: > 0 } sret) sb.Append($"\nRETURNS {sret}");
                sb.Append(opts.Length > 0 ? "\n" + opts.Trim() : "").Append("\nAS\n").Append(body).Append("\nGO\n");
                break;
            case "mysql":
            {
                if (replace) sb.Append($"DROP {kind} IF EXISTS {name};\n");
                var create = new StringBuilder($"CREATE {kind} {name}{Params(r.Signature, d, fn)}");
                if (fn && r.Returns is { Length: > 0 } mret) create.Append($" RETURNS {mret}");
                create.Append(opts).Append('\n').Append(body.TrimEnd().TrimEnd(';'));
                sb.Append(Delimited(create.ToString()));
                break;
            }
            case "oracle":
                sb.Append($"CREATE OR REPLACE {kind} {name}{Params(r.Signature, d, fn)}");
                if (fn && r.Returns is { Length: > 0 } oret) sb.Append($" RETURN {oret}");
                sb.Append(opts).Append(" IS\n").Append(EndWithSemicolon(body)).Append("\n/\n");
                break;
            default:
                sb.Append($"-- {DbReservedWords.DialectName(d)} has no stored routines: {kind.ToLowerInvariant()} {name} is not generated\n");
                break;
        }
        return sb.ToString();
    }

    /// <summary>DROP of a routine (PostgreSQL names the argument list so overloads stay apart).</summary>
    public static string DropRoutine(string name, DbRoutineDef r, string d)
    {
        var kind = r.Kind == DbFields.Procedure ? "PROCEDURE" : "FUNCTION";
        return d switch
        {
            "postgresql" => $"DROP {kind} IF EXISTS {name}{Params(r.Signature, d, true)};",
            "sqlserver" or "mysql" => $"DROP {kind} IF EXISTS {name};",
            "oracle" => $"DROP {kind} {name};",
            _ => $"-- {DbReservedWords.DialectName(d)} has no stored routines",
        };
    }

    /// <summary>Whether a trigger can be created in <paramref name="dialect"/> (its action is known and fits the dialect).</summary>
    public static string? CannotCreate(DbTriggerDef t, string d)
    {
        if (t.Action is not { Length: > 0 }) return "its action is not in the model";
        var events = Events(t.Events);
        if (d is "mysql" or "sqlite" && events.Count > 1) return $"{DbReservedWords.DialectName(d)} triggers fire on one event (split it per event)";
        if (d == "sqlserver" && t.Timing.Equals("BEFORE", StringComparison.OrdinalIgnoreCase)) return "SQL Server has no BEFORE triggers";
        if (d == "postgresql" && !Regex.IsMatch(t.Action, @"\bEXECUTE\s+(FUNCTION|PROCEDURE)\b", RegexOptions.IgnoreCase)) return "PostgreSQL triggers call a function (EXECUTE FUNCTION …)";
        return null;
    }

    /// <summary>CREATE (or replace) of a trigger. <paramref name="name"/> is qualified where the dialect qualifies trigger names.</summary>
    public static string CreateTrigger(string name, string table, DbTriggerDef t, string d, bool replace)
    {
        if (CannotCreate(t, d) is { } why) return $"-- Trigger {name} on {table}: not generated ({why})\n";
        var events = Events(t.Events);
        var timing = t.Timing.ToUpperInvariant();
        var action = t.Action!.Trim();
        var row = t.Level is { Length: > 0 } lv ? $" FOR EACH {lv.ToUpperInvariant()}" : "";
        var sb = new StringBuilder();
        switch (d)
        {
            case "postgresql":
                if (replace) sb.Append($"DROP TRIGGER IF EXISTS {name} ON {table};\n");
                sb.Append($"CREATE TRIGGER {name} {timing} {string.Join(" OR ", events)} ON {table}{row}\n    {action.TrimEnd(';')};\n");
                break;
            case "sqlserver":
                sb.Append($"CREATE OR ALTER TRIGGER {name} ON {table} {(timing == "INSTEAD OF" ? "INSTEAD OF" : "AFTER")} {string.Join(", ", events)}\nAS\n{action}\nGO\n");
                break;
            case "mysql":
                if (replace) sb.Append($"DROP TRIGGER IF EXISTS {name};\n");
                sb.Append(Delimited($"CREATE TRIGGER {name} {timing} {events[0]} ON {table} FOR EACH ROW\n{action.TrimEnd().TrimEnd(';')}"));
                break;
            case "oracle":
                sb.Append($"CREATE OR REPLACE TRIGGER {name} {timing} {string.Join(" OR ", events)} ON {table}{(row.Contains("ROW", StringComparison.Ordinal) ? row : "")}\n{EndWithSemicolon(action)}\n/\n");
                break;
            default:
                if (replace) sb.Append($"DROP TRIGGER IF EXISTS {name};\n");
                sb.Append($"CREATE TRIGGER {name} {timing} {events[0]} ON {table}{(row.Contains("ROW", StringComparison.Ordinal) ? row : "")}\n{action.TrimEnd().TrimEnd(';')};\n");
                break;
        }
        return sb.ToString();
    }

    public static string DropTrigger(string name, string table, string d) => d switch
    {
        "postgresql" => $"DROP TRIGGER IF EXISTS {name} ON {table};",
        "oracle" => $"DROP TRIGGER {name};",
        _ => $"DROP TRIGGER IF EXISTS {name};",
    };

    /// <summary>INSERT / UPDATE / DELETE … from "INSERT, UPDATE" or "INSERT OR UPDATE".</summary>
    public static List<string> Events(string events)
    {
        var list = Regex.Split(events ?? "", @"\s*,\s*|\s+OR\s+", RegexOptions.IgnoreCase).Select(e => e.Trim().ToUpperInvariant()).Where(e => e.Length > 0).ToList();
        return list.Count == 0 ? ["INSERT"] : list;
    }

    /// <summary>The parameter list as the dialect wants it: T-SQL procedures and parameterless Oracle routines go without parentheses.</summary>
    private static string Params(string signature, string d, bool function)
    {
        var sig = (signature ?? "").Trim();
        if (sig.Length == 0) sig = "()";
        if (!sig.StartsWith('(')) sig = $"({sig})";
        var inner = sig[1..^1].Trim();
        return d switch
        {
            "sqlserver" when !function => inner.Length == 0 ? "" : " " + inner,
            "oracle" when inner.Length == 0 => "",
            _ => sig,
        };
    }

    private static string DollarTag(string body)
    {
        var tag = "$body$";
        for (var i = 1; body.Contains(tag, StringComparison.Ordinal); i++) tag = $"$body{i}$";
        return tag;
    }

    /// <summary>MySQL: a body with semicolons needs another statement delimiter around it.</summary>
    private static string Delimited(string statement) =>
        statement.Contains(';') ? $"DELIMITER $$\n{statement}$$\nDELIMITER ;\n" : statement + ";\n";

    private static string EndWithSemicolon(string body)
    {
        var b = body.TrimEnd();
        return b.EndsWith(';') ? b : b + ";";
    }
}
