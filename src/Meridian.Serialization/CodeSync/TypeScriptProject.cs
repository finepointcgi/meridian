using System.Text.Json;
using System.Text.RegularExpressions;
using Meridian.Core.CodeSync;

namespace Meridian.Serialization.CodeSync;

/// <summary>
/// How a TypeScript snapshot resolves module specifiers, the way <c>tsc</c> does as far as the snapshot allows:
/// <list type="bullet">
/// <item>the nearest <c>tsconfig.json</c> (else <c>jsconfig.json</c>) of a file, with its <c>extends</c> chain (relative paths,
/// arrays, and package names of workspace packages), <c>compilerOptions.baseUrl</c>, <c>paths</c> (with <c>*</c>
/// wildcards) and <c>rootDirs</c>; <c>${configDir}</c> is the directory of the config the file uses;</item>
/// <item>monorepo workspaces (<c>package.json</c> <c>workspaces</c>, <c>pnpm-workspace.yaml</c>): an import of a workspace
/// package's name resolves to its source through <c>exports</c> (subpaths, wildcards, conditions), <c>types</c>,
/// <c>typings</c>, <c>source</c>, <c>module</c> or <c>main</c>; built paths (<c>dist/</c>, <c>build/</c>, <c>lib/</c>,
/// <c>out/</c>) map back to <c>src/</c> when only the source is in the snapshot.</item>
/// </list>
/// Everything else (packages in <c>node_modules</c>, which is never scanned) is external: <see cref="Resolve"/> returns null.
/// Never throws on malformed configuration; a config that cannot be read is ignored (and reported by the reader).
/// </summary>
internal sealed class TypeScriptProject
{
    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 64 };

    private readonly HashSet<string> _sources;
    private readonly Dictionary<string, string> _configs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonElement?> _json = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Config?> _byDir = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Path, string Leaf), Config> _loaded = new();
    private readonly Dictionary<string, Package> _packages = new(StringComparer.Ordinal);

    /// <summary>The effective compiler options of one config file (after <c>extends</c>), with repository-relative paths.</summary>
    internal sealed class Config
    {
        public string? BaseUrl { get; set; }
        /// <summary>Patterns in declaration order, each with its substitutions (relative to <see cref="PathsBase"/>).</summary>
        public List<(string Pattern, List<string> Targets)> Paths { get; set; } = [];
        /// <summary>Directory the <c>paths</c> substitutions are relative to when there is no baseUrl.</summary>
        public string PathsDir { get; set; } = "";
        public List<string> RootDirs { get; set; } = [];

        public string PathsBase => BaseUrl ?? PathsDir;
    }

    private sealed record Package(string Name, string Dir, JsonElement Json);

    public TypeScriptProject(IEnumerable<CodeFile> files)
    {
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in files)
        {
            if (IsConfigFile(f.Path)) _configs[f.Path] = f.Content ?? "";
            else sources.Add(f.Path);
        }
        _sources = sources;
        FindWorkspacePackages();
    }

    /// <summary>
    /// tsconfig / jsconfig files (<c>tsconfig.json</c>, <c>tsconfig.base.json</c>, <c>jsconfig.json</c> …), JSON files in a
    /// shared config package's folder (<c>packages/tsconfig/base.json</c>, <c>packages/typescript-config/nextjs.json</c>),
    /// <c>package.json</c> and <c>pnpm-workspace.yaml</c>.
    /// </summary>
    public static bool IsConfigFile(string path)
    {
        var p = path.Replace('\\', '/');
        var name = FileName(p);
        if (name is "package.json" or "pnpm-workspace.yaml" or "pnpm-workspace.yml") return true;
        if (!name.EndsWith(".json", StringComparison.Ordinal)) return false;
        if (name.StartsWith("tsconfig", StringComparison.Ordinal) || name.StartsWith("jsconfig", StringComparison.Ordinal)) return true;
        var dir = DirOf(p);
        return dir.Split('/').Any(s => s.Contains("tsconfig", StringComparison.Ordinal) || s.Contains("typescript-config", StringComparison.Ordinal)
                                        || s.Contains("ts-config", StringComparison.Ordinal));
    }

    /// <summary>Why a config file cannot be used ("invalid JSON …"), or null when it is fine.</summary>
    public string? Problem(string path)
    {
        if (!_configs.TryGetValue(path, out var text)) return null;
        if (path.EndsWith(".yaml", StringComparison.Ordinal) || path.EndsWith(".yml", StringComparison.Ordinal)) return null;
        try
        {
            using var _ = JsonDocument.Parse(text, JsonOptions);
            return null;
        }
        catch (JsonException ex) { return $"invalid JSON ({ex.Message.Split('.')[0]})"; }
    }

    // ------------------------------------------------------------------ module resolution

    /// <summary>The scanned source file <paramref name="spec"/> names from <paramref name="fromPath"/>, or null (external).</summary>
    public string? Resolve(string fromPath, string spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var dir = DirOf(fromPath);
        if (spec.StartsWith("./", StringComparison.Ordinal) || spec.StartsWith("../", StringComparison.Ordinal) || spec is "." or "..")
        {
            if (File(Combine(dir, spec)) is { } rel) return rel;
            // rootDirs: the folders are merged into one virtual folder, so "./x" may live under another root.
            if (ConfigFor(fromPath) is { RootDirs.Count: > 0 } rc)
                foreach (var root in rc.RootDirs)
                {
                    if (!IsUnder(dir, root)) continue;
                    var inside = dir.Length == root.Length ? "" : dir[(root.Length == 0 ? 0 : root.Length + 1)..];
                    foreach (var other in rc.RootDirs)
                        if (other != root && File(Combine(Combine(other, inside), spec)) is { } viaRoot) return viaRoot;
                }
            return null;
        }
        if (spec.StartsWith('/')) return File(Normalize(spec));

        var config = ConfigFor(fromPath);
        if (config is { Paths.Count: > 0 } && Match(config.Paths, spec) is { } m)
        {
            foreach (var target in m.Targets)
            {
                var star = target.IndexOf('*');
                var substituted = star < 0 ? target : target[..star] + m.Star + target[(star + 1)..];
                if (File(Combine(config.PathsBase, substituted)) is { } hit) return hit;
            }
        }
        if (config?.BaseUrl is { } baseUrl && File(Combine(baseUrl, spec)) is { } viaBase) return viaBase;
        if (PackageFile(spec) is { } viaPackage) return viaPackage;
        // Conventions without a config: "@/x" and "~/x" are src/x; "src/x" from the repository root.
        if (spec.StartsWith("@/", StringComparison.Ordinal) || spec.StartsWith("~/", StringComparison.Ordinal)) return File(Normalize("src/" + spec[2..]));
        return File(Normalize(spec));
    }

    /// <summary>"@acme/logging/sub" → ("@acme/logging", "sub"); "lodash" → ("lodash", "").</summary>
    public static (string Name, string Subpath) SplitPackage(string spec)
    {
        var parts = spec.Split('/');
        var n = spec.StartsWith('@') && parts.Length > 1 ? 2 : 1;
        return (string.Join("/", parts.Take(n)), string.Join("/", parts.Skip(n)));
    }

    /// <summary>The longest matching <c>paths</c> pattern: an exact one wins, then the longest prefix before <c>*</c>.</summary>
    private static (List<string> Targets, string Star)? Match(List<(string Pattern, List<string> Targets)> paths, string spec)
    {
        foreach (var (pattern, targets) in paths)
            if (!pattern.Contains('*') && pattern == spec) return (targets, "");
        (List<string> Targets, string Star)? best = null;
        var bestPrefix = -1;
        foreach (var (pattern, targets) in paths)
        {
            var star = pattern.IndexOf('*');
            if (star < 0) continue;
            var prefix = pattern[..star];
            var suffix = pattern[(star + 1)..];
            if (spec.Length < prefix.Length + suffix.Length || !spec.StartsWith(prefix, StringComparison.Ordinal) || !spec.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (prefix.Length <= bestPrefix) continue;
            bestPrefix = prefix.Length;
            best = (targets, spec[prefix.Length..(spec.Length - suffix.Length)]);
        }
        return best;
    }

    /// <summary>A module path as tsc tries it: the file itself, with a TypeScript extension, or the folder's index.</summary>
    private string? File(string p)
    {
        if (p.Length == 0) return _sources.Contains("index.ts") ? "index.ts" : _sources.Contains("index.tsx") ? "index.tsx" : null;
        var stem = p;
        foreach (var ext in new[] { ".d.ts", ".d.mts", ".d.cts", ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts" })
            if (stem.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { stem = stem[..^ext.Length]; break; }
        foreach (var c in new[] { p, stem + ".ts", stem + ".tsx", stem + ".d.ts", stem + ".mts", stem + ".cts", stem + "/index.ts", stem + "/index.tsx", stem + "/index.d.ts" })
            if (_sources.Contains(c)) return c;
        return null;
    }

    /// <summary>A built path ("dist/index.js", "lib/esm/x.d.ts") tried as is, then with the build folder mapped to src/.</summary>
    private string? SourceOf(string dir, string target)
    {
        var rel = Normalize(target);
        if (File(Combine(dir, rel)) is { } direct) return direct;
        var segs = rel.Split('/').ToList();
        var k = 0;
        while (k < segs.Count - 1 && segs[k] is "dist" or "build" or "lib" or "out" or "esm" or "cjs" or "types" or "es" or "umd") k++;
        if (k == 0) return null;
        var rest = string.Join("/", segs.Skip(k));
        return File(Combine(dir, "src/" + rest)) ?? File(Combine(dir, rest));
    }

    // ------------------------------------------------------------------ workspace packages

    private string? PackageFile(string spec)
    {
        var (name, sub) = SplitPackage(spec);
        if (!_packages.TryGetValue(name, out var pkg)) return null;
        var json = pkg.Json;
        if (json.TryGetProperty("exports", out var exports) && exports.ValueKind is JsonValueKind.String or JsonValueKind.Object or JsonValueKind.Array)
        {
            foreach (var target in ExportTargets(exports, sub.Length == 0 ? "." : "./" + sub))
                if (SourceOf(pkg.Dir, target) is { } hit) return hit;
        }
        if (sub.Length > 0) return SourceOf(pkg.Dir, sub);
        foreach (var field in new[] { "types", "typings", "source", "module", "main" })
            if (json.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String && SourceOf(pkg.Dir, v.GetString()!) is { } hit) return hit;
        return File(Combine(pkg.Dir, "src/index")) ?? File(Combine(pkg.Dir, "index"));
    }

    private static readonly string[] Conditions = ["types", "typescript", "source", "import", "module", "default", "require", "node", "browser", "development", "production"];

    /// <summary>The targets an <c>exports</c> field gives a subpath ("." or "./x"), most TypeScript-like condition first.</summary>
    internal static IEnumerable<string> ExportTargets(JsonElement exports, string subpath)
    {
        if (exports.ValueKind == JsonValueKind.Object && exports.EnumerateObject().Any(p => p.Name.StartsWith('.')))
        {
            if (exports.TryGetProperty(subpath, out var exact)) return Targets(exact, "");
            // "./*" or "./utils/*": the longest prefix wins.
            JsonElement? best = null;
            var star = "";
            var bestLen = -1;
            foreach (var p in exports.EnumerateObject())
            {
                var i = p.Name.IndexOf('*');
                if (i < 0) continue;
                var prefix = p.Name[..i];
                var suffix = p.Name[(i + 1)..];
                if (!subpath.StartsWith(prefix, StringComparison.Ordinal) || !subpath.EndsWith(suffix, StringComparison.Ordinal) || subpath.Length < prefix.Length + suffix.Length) continue;
                if (prefix.Length <= bestLen) continue;
                bestLen = prefix.Length;
                best = p.Value;
                star = subpath[prefix.Length..(subpath.Length - suffix.Length)];
            }
            return best is { } b ? Targets(b, star) : [];
        }
        return subpath == "." ? Targets(exports, "") : [];
    }

    private static IEnumerable<string> Targets(JsonElement e, string star, int depth = 0)
    {
        if (depth > 8) yield break;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                var s = e.GetString()!;
                yield return s.Replace("*", star, StringComparison.Ordinal);
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) foreach (var t in Targets(x, star, depth + 1)) yield return t;
                break;
            case JsonValueKind.Object:
                var props = e.EnumerateObject().ToList();
                foreach (var c in Conditions)
                    foreach (var p in props.Where(p => p.Name == c))
                        foreach (var t in Targets(p.Value, star, depth + 1)) yield return t;
                foreach (var p in props.Where(p => !Conditions.Contains(p.Name)))
                    foreach (var t in Targets(p.Value, star, depth + 1)) yield return t;
                break;
        }
    }

    private void FindWorkspacePackages()
    {
        // Workspace roots: package.json "workspaces" (array, or { packages: [...] }), pnpm-workspace.yaml "packages".
        var globs = new List<(string Root, string Glob, bool Negated)>();
        foreach (var (path, text) in _configs.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            var dir = DirOf(path);
            var name = FileName(path);
            IEnumerable<string> patterns = [];
            if (name == "package.json" && Json(path) is { ValueKind: JsonValueKind.Object } pj && pj.TryGetProperty("workspaces", out var ws))
            {
                var list = ws.ValueKind == JsonValueKind.Object && ws.TryGetProperty("packages", out var inner) ? inner : ws;
                if (list.ValueKind == JsonValueKind.Array)
                    patterns = list.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
            }
            else if (name is "pnpm-workspace.yaml" or "pnpm-workspace.yml") patterns = YamlList(text, "packages");
            foreach (var raw in patterns)
            {
                var p = raw.Trim();
                var negated = p.StartsWith('!');
                if (negated) p = p[1..];
                p = p.TrimStart('.', '/').TrimEnd('/');
                if (p.EndsWith("/package.json", StringComparison.Ordinal)) p = p[..^"/package.json".Length];
                if (p.Length > 0) globs.Add((dir, p, negated));
            }
        }
        if (globs.Count == 0) return;
        var compiled = globs.Select(g => (g.Root, Regex: GlobRegex(g.Glob), g.Negated)).ToList();
        foreach (var path in _configs.Keys.Where(p => FileName(p) == "package.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var dir = DirOf(path);
            var included = false;
            foreach (var (root, regex, negated) in compiled)
            {
                if (!IsUnder(dir, root) || dir == root) continue;
                var rel = root.Length == 0 ? dir : dir[(root.Length + 1)..];
                if (regex.IsMatch(rel)) included = !negated;
            }
            if (!included || Json(path) is not { ValueKind: JsonValueKind.Object } json) continue;
            if (json.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } pkgName)
                _packages.TryAdd(pkgName, new Package(pkgName, dir, json));
        }
    }

    private static Regex GlobRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**/" matches any number of folders (also none); a trailing "**" matches anything below.
                if (i + 2 < glob.Length && glob[i + 2] == '/') { sb.Append("(?:.*/)?"); i += 2; }
                else { sb.Append(".*"); i++; }
            }
            else if (c == '*') sb.Append("[^/]*");
            else if (c == '?') sb.Append("[^/]");
            else sb.Append(Regex.Escape(c.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>The items of a top-level list in a simple YAML file ("packages:\n  - 'apps/*'").</summary>
    internal static List<string> YamlList(string text, string key)
    {
        var items = new List<string>();
        var inList = false;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var hash = line.IndexOf(" #", StringComparison.Ordinal);
            if (line.TrimStart().StartsWith('#')) continue;
            if (hash >= 0) line = line[..hash];
            if (line.Trim().Length == 0) continue;
            if (!char.IsWhiteSpace(line[0]) && !line.StartsWith('-'))
            {
                inList = line.StartsWith(key + ":", StringComparison.Ordinal);
                var inline = inList ? line[(key.Length + 1)..].Trim() : "";
                if (inline.StartsWith('[') && inline.EndsWith(']'))
                {
                    items.AddRange(inline[1..^1].Split(',').Select(Unquote).Where(s => s.Length > 0));
                    inList = false;
                }
                continue;
            }
            var t = line.Trim();
            if (inList && t.StartsWith('-')) items.Add(Unquote(t[1..]));
        }
        return items;
    }

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && (s[0] == '\'' || s[0] == '"') && s[^1] == s[0] ? s[1..^1] : s;
    }

    // ------------------------------------------------------------------ tsconfig

    /// <summary>The config a source file uses: the nearest tsconfig.json, else jsconfig.json, up the folders.</summary>
    public Config? ConfigFor(string sourcePath)
    {
        var dir = DirOf(sourcePath);
        var visited = new List<string>();
        Config? found = null;
        while (true)
        {
            if (_byDir.TryGetValue(dir, out var cached)) { found = cached; break; }
            visited.Add(dir);
            var ts = Combine(dir, "tsconfig.json");
            var js = Combine(dir, "jsconfig.json");
            var path = _configs.ContainsKey(ts) ? ts : _configs.ContainsKey(js) ? js : null;
            if (path != null) { found = Load(path, dir, 0, []); break; }
            if (dir.Length == 0) break;
            dir = DirOf(dir);
        }
        foreach (var d in visited) _byDir[d] = found;
        return found;
    }

    /// <summary>A config with its <c>extends</c> chain applied. <paramref name="leafDir"/> is <c>${configDir}</c>.</summary>
    private Config Load(string path, string leafDir, int depth, HashSet<string> chain)
    {
        if (_loaded.TryGetValue((path, leafDir), out var done)) return done;
        var result = new Config();
        if (depth > 16 || !chain.Add(path) || Json(path) is not { ValueKind: JsonValueKind.Object } json) return result;
        var dir = DirOf(path);
        if (json.TryGetProperty("extends", out var ext))
        {
            var bases = ext.ValueKind == JsonValueKind.String ? [ext.GetString()!]
                : ext.ValueKind == JsonValueKind.Array ? ext.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
                : new List<string>();
            foreach (var b in bases)
                if (ExtendsPath(dir, b, leafDir) is { } basePath)
                {
                    var parent = Load(basePath, leafDir, depth + 1, chain);
                    if (parent.BaseUrl != null) result.BaseUrl = parent.BaseUrl;
                    if (parent.Paths.Count > 0) { result.Paths = parent.Paths; result.PathsDir = parent.PathsDir; }
                    if (parent.RootDirs.Count > 0) result.RootDirs = parent.RootDirs;
                }
        }
        if (json.TryGetProperty("compilerOptions", out var opts) && opts.ValueKind == JsonValueKind.Object)
        {
            if (opts.TryGetProperty("baseUrl", out var bu) && bu.ValueKind == JsonValueKind.String)
                result.BaseUrl = Combine(dir, ConfigDir(bu.GetString()!, leafDir));
            if (opts.TryGetProperty("paths", out var paths) && paths.ValueKind == JsonValueKind.Object)
            {
                result.Paths = paths.EnumerateObject()
                    .Select(p => (p.Name, p.Value.ValueKind == JsonValueKind.Array
                        ? p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => ConfigDir(x.GetString()!, leafDir)).ToList()
                        : []))
                    .ToList();
                result.PathsDir = dir;
            }
            if (opts.TryGetProperty("rootDirs", out var rd) && rd.ValueKind == JsonValueKind.Array)
                result.RootDirs = rd.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => Combine(dir, ConfigDir(x.GetString()!, leafDir))).ToList();
        }
        chain.Remove(path);
        _loaded[(path, leafDir)] = result;
        return result;
    }

    /// <summary>"${configDir}/src" → "/apps/web/src": the leaf config's folder, rooted at the repository (see <see cref="Combine"/>).</summary>
    private static string ConfigDir(string value, string leafDir)
    {
        const string token = "${configDir}";
        return value.Contains(token, StringComparison.Ordinal) ? value.Replace(token, "/" + leafDir, StringComparison.Ordinal) : value;
    }

    /// <summary>The config an <c>extends</c> entry names: a relative path (with or without .json), or a workspace package
    /// ("@acme/tsconfig" → its package.json "tsconfig" field or tsconfig.json; "@acme/tsconfig/base.json" → that file).</summary>
    private string? ExtendsPath(string dir, string spec, string leafDir)
    {
        spec = ConfigDir(spec, leafDir);
        string? Try(string p) => _configs.ContainsKey(p) ? p : _configs.ContainsKey(p + ".json") ? p + ".json" : null;
        if (spec.StartsWith('.') || spec.StartsWith('/')) return Try(spec.StartsWith('/') ? Normalize(spec) : Combine(dir, spec));
        var (name, sub) = SplitPackage(spec);
        if (!_packages.TryGetValue(name, out var pkg))
        {
            // A config package that is not a declared workspace but sits in the snapshot under its name's folder.
            pkg = _configs.Keys.Where(p => FileName(p) == "package.json")
                .Select(p => (p, Json(p)))
                .Where(x => x.Item2 is { ValueKind: JsonValueKind.Object } j && j.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() == name)
                .Select(x => new Package(name, DirOf(x.p), x.Item2!.Value)).FirstOrDefault();
            if (pkg is null) return null;
        }
        if (sub.Length > 0) return Try(Combine(pkg.Dir, sub));
        if (pkg.Json.TryGetProperty("tsconfig", out var field) && field.ValueKind == JsonValueKind.String && Try(Combine(pkg.Dir, field.GetString()!)) is { } viaField) return viaField;
        return Try(Combine(pkg.Dir, "tsconfig.json"));
    }

    private JsonElement? Json(string path)
    {
        if (_json.TryGetValue(path, out var cached)) return cached;
        JsonElement? value = null;
        if (_configs.TryGetValue(path, out var text) && path.EndsWith(".json", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(text, JsonOptions);
                value = doc.RootElement.Clone();
            }
            catch (JsonException) { value = null; }
        }
        _json[path] = value;
        return value;
    }

    // ------------------------------------------------------------------ paths

    private static string FileName(string path)
    {
        var p = path.Replace('\\', '/');
        return p[(p.LastIndexOf('/') + 1)..];
    }

    internal static string DirOf(string path)
    {
        var p = path.Replace('\\', '/').Trim('/');
        var slash = p.LastIndexOf('/');
        return slash < 0 ? "" : p[..slash];
    }

    private static bool IsUnder(string dir, string root) =>
        root.Length == 0 || dir == root || dir.StartsWith(root + "/", StringComparison.Ordinal);

    /// <summary><paramref name="rel"/> inside <paramref name="dir"/>; a path starting with '/' is from the repository root
    /// (a snapshot has no file system above it).</summary>
    private static string Combine(string dir, string rel) =>
        Normalize(dir.Length == 0 || rel.StartsWith('/') ? rel : dir + "/" + rel);

    internal static string Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var s in path.Replace('\\', '/').Split('/'))
        {
            if (s is "" or ".") continue;
            if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(s);
        }
        return string.Join("/", parts);
    }
}
