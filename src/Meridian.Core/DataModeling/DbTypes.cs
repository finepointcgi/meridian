using System.Text.RegularExpressions;

namespace Meridian.Core.DataModeling;

/// <summary>A data type split into its base name and length / precision / scale ("numeric(10,2)" → numeric, 10, 2).</summary>
public readonly record struct SqlTypeSpec(string Base, int? Length, int? Precision, int? Scale, string Raw)
{
    public bool HasSize => Length != null || Precision != null;
}

/// <summary>Data type helpers: parsing, comparison across dialect spellings and per-dialect conversion for DDL.</summary>
public static partial class DbTypes
{
    [GeneratedRegex(@"^(?<pre>[^(]*)(?:\((?<args>[^)]*)\))?(?<post>.*)$", RegexOptions.Singleline)]
    private static partial Regex TypePattern();

    [GeneratedRegex(@"^\s*(?<a>\d+|max)(?:\s+\w+)?\s*(?:,\s*(?<b>-?\d+)\s*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SizePattern();

    /// <summary>
    /// Splits a type: a single size is a length for character / binary types, a precision otherwise
    /// ("timestamp(3) with time zone" → timestamp with time zone, precision 3; "varchar2(30 byte)" → varchar2, length 30).
    /// </summary>
    public static SqlTypeSpec Parse(string? type)
    {
        var raw = (type ?? "").Trim();
        var m = TypePattern().Match(raw);
        var pre = m.Groups["pre"].Value.Trim();
        var post = m.Groups["post"].Value.Trim();
        var b = Regex.Replace(post.Length == 0 ? pre : post.StartsWith('[') ? pre + post : pre + " " + post, @"\s+", " ").Trim();
        var size = m.Groups["args"].Success ? SizePattern().Match(m.Groups["args"].Value) : null;
        if (m.Groups["args"].Success && size is not { Success: true }) return new SqlTypeSpec(raw, null, null, null, raw);   // enum('a','b') and the like
        int? a = size is { Success: true } && int.TryParse(size.Groups["a"].Value, out var x) ? x : size is { Success: true } && size.Groups["a"].Value.Equals("max", StringComparison.OrdinalIgnoreCase) ? -1 : null;
        int? s = size is { Success: true } && size.Groups["b"].Success && int.TryParse(size.Groups["b"].Value, out var y) ? y : null;
        var lower = b.ToLowerInvariant();
        var isChar = lower.Contains("char") || lower.Contains("binary") || lower.Contains("bit") || lower is "raw" or "varbinary";
        return isChar ? new SqlTypeSpec(b, a, null, null, raw) : new SqlTypeSpec(b, null, a, s, raw);
    }

    /// <summary>Rebuilds a type from its parts.</summary>
    public static string Format(string baseType, int? length, int? precision, int? scale)
    {
        var size = length is { } l ? (l < 0 ? "max" : l.ToString()) : precision is { } p ? (scale is { } s ? $"{p},{s}" : p.ToString()) : null;
        return size == null ? baseType : $"{baseType}({size})";
    }

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["int"] = "integer", ["int4"] = "integer", ["serial"] = "integer", ["serial4"] = "integer", ["mediumint"] = "integer",
        ["int8"] = "bigint", ["bigserial"] = "bigint", ["serial8"] = "bigint",
        ["int2"] = "smallint", ["smallserial"] = "smallint", ["serial2"] = "smallint",
        ["bool"] = "boolean", ["bit"] = "boolean",
        ["character varying"] = "varchar", ["nvarchar"] = "varchar", ["varchar2"] = "varchar", ["nvarchar2"] = "varchar",
        ["character"] = "char", ["nchar"] = "char", ["bpchar"] = "char",
        ["decimal"] = "numeric", ["number"] = "numeric", ["dec"] = "numeric",
        ["float8"] = "double precision", ["double"] = "double precision", ["float"] = "double precision",
        ["float4"] = "real",
        ["timestamp without time zone"] = "timestamp", ["datetime"] = "timestamp", ["datetime2"] = "timestamp",
        ["timestamp with time zone"] = "timestamptz", ["datetimeoffset"] = "timestamptz",
        ["time without time zone"] = "time", ["time with time zone"] = "timetz",
        ["ntext"] = "text", ["clob"] = "text", ["longtext"] = "text", ["mediumtext"] = "text", ["nclob"] = "text",
        ["blob"] = "bytea", ["varbinary"] = "bytea", ["longblob"] = "bytea", ["image"] = "bytea",
        ["uniqueidentifier"] = "uuid",
    };

    /// <summary>A canonical spelling for comparisons: lower case, synonyms folded ("character varying(20)" = "varchar(20)").</summary>
    public static string Normalize(string? type)
    {
        var spec = Parse(type);
        var b = Regex.Replace(spec.Base.ToLowerInvariant(), @"\s+", " ").Trim();
        if (Synonyms.TryGetValue(b, out var syn)) b = syn;
        // Sizes that don't change meaning: int(11) in MySQL, varchar without length.
        if (b is "integer" or "bigint" or "smallint" or "boolean" or "text" or "bytea") return b;
        return Format(b, spec.Length, spec.Precision, spec.Scale);
    }

    /// <summary>Do two types hold the same values (a serial key and an integer reference are compatible)?</summary>
    public static bool Compatible(string? a, string? b)
    {
        var x = Normalize(a); var y = Normalize(b);
        if (x == y) return true;
        static string Family(string t) => Parse(t).Base;
        return Family(x) == Family(y) && Family(x) is "varchar" or "char" or "numeric";
    }

    /// <summary>Is <paramref name="type"/> an auto-numbering pseudo type (serial…)?</summary>
    public static bool IsSerial(string? type) => (type ?? "").Trim().ToLowerInvariant() is "serial" or "bigserial" or "smallserial" or "serial4" or "serial8" or "serial2";

    /// <summary>The type to write in DDL for <paramref name="dialect"/> (common spellings converted; unknown types kept).</summary>
    public static string ForDialect(string? type, string dialect)
    {
        var raw = string.IsNullOrWhiteSpace(type) ? "text" : type.Trim();
        var spec = Parse(raw);
        var b = spec.Base.ToLowerInvariant();
        string Sized(string name) => Format(name, spec.Length, spec.Precision, spec.Scale);
        switch (dialect)
        {
            case "sqlserver":
                return b switch
                {
                    "text" or "clob" or "longtext" => "nvarchar(max)",
                    "varchar" or "character varying" or "varchar2" => Sized("nvarchar"),
                    "boolean" or "bool" => "bit",
                    "timestamp" or "timestamp without time zone" => "datetime2",
                    "timestamptz" or "timestamp with time zone" => "datetimeoffset",
                    "bytea" or "blob" => "varbinary(max)",
                    "uuid" => "uniqueidentifier",
                    "double precision" or "float8" => "float",
                    "serial" => "int", "bigserial" => "bigint", "integer" => "int",
                    "jsonb" or "json" => "nvarchar(max)",
                    _ => raw,
                };
            case "mysql":
                return b switch
                {
                    "boolean" or "bool" => "tinyint(1)",
                    "timestamptz" or "timestamp with time zone" => "timestamp",
                    "bytea" => "longblob",
                    "uuid" => "char(36)",
                    "serial" => "int", "bigserial" => "bigint", "integer" => "int",
                    "jsonb" => "json",
                    "character varying" or "varchar2" or "nvarchar" => Sized("varchar"),
                    "double precision" or "float8" => "double",
                    _ => raw,
                };
            case "sqlite":
                return b switch
                {
                    "serial" or "bigserial" or "int" or "int4" or "bigint" or "smallint" => "integer",
                    "boolean" or "bool" => "integer",
                    "uuid" or "jsonb" or "json" => "text",
                    "bytea" => "blob",
                    _ => raw,
                };
            case "oracle":
                return b switch
                {
                    "text" or "clob" or "longtext" => "clob",
                    "varchar" or "character varying" or "nvarchar" => Sized("varchar2"),
                    "integer" or "int" or "serial" => "number(10)",
                    "bigint" or "bigserial" => "number(19)",
                    "smallint" => "number(5)",
                    "boolean" or "bool" => "number(1)",
                    "numeric" or "decimal" => Sized("number"),
                    "timestamptz" or "timestamp with time zone" => "timestamp with time zone",
                    "bytea" or "blob" => "blob",
                    "uuid" => "raw(16)",
                    "jsonb" or "json" => "clob",
                    "double precision" or "float8" => "binary_double",
                    _ => raw,
                };
            default:
                return b switch
                {
                    "nvarchar" or "varchar2" or "nvarchar2" => Sized("varchar"),
                    "datetime" or "datetime2" => "timestamp",
                    "datetimeoffset" => "timestamptz",
                    "bit" => "boolean",
                    "uniqueidentifier" => "uuid",
                    "tinyint" => "smallint",
                    "longtext" or "mediumtext" or "clob" or "ntext" => "text",
                    "blob" or "longblob" or "varbinary" => "bytea",
                    "number" => spec.Precision is { } p && spec.Scale is null or 0 ? (p <= 4 ? "smallint" : p <= 9 ? "integer" : p <= 18 ? "bigint" : Sized("numeric")) : Sized("numeric"),
                    _ => raw,
                };
        }
    }
}
