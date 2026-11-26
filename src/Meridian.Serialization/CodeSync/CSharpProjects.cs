using System.Xml;
using System.Xml.Linq;
using Meridian.Core.CodeSync;

namespace Meridian.Serialization.CodeSync;

/// <summary>
/// The C# projects (.csproj files) among the files of a read: each becomes a <see cref="CodeModule"/> (its assembly name,
/// folder, description, whether it is a test project and the projects it references). Project files are parsed as XML
/// without DTDs or external entities; one that does not parse is skipped with a warning.
/// </summary>
public static class CSharpProjects
{
    /// <summary>Package references that make a project a test project.</summary>
    private static readonly string[] TestPackages = ["Microsoft.NET.Test.Sdk", "xunit", "xunit.v3", "NUnit", "MSTest.TestFramework", "MSTest", "TUnit"];

    /// <summary>Largest project file read (bigger ones are skipped).</summary>
    public const int MaxProjectFileLength = 1_000_000;

    public static IReadOnlyList<CodeModule> Read(IReadOnlyList<CodeFile> files, List<string> warnings)
    {
        var parsed = new List<(string File, string Dir, string Name, string? Description, bool IsTest, List<string> Refs)>();
        foreach (var f in files.Where(f => f.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            var path = f.Path.Replace('\\', '/').TrimStart('/');
            if (CSharpCodeReader.IsGeneratedPath(path)) continue;
            var content = f.Content ?? "";
            if (content.Length > MaxProjectFileLength) { warnings.Add($"{path}: skipped, larger than {MaxProjectFileLength:N0} characters."); continue; }
            XDocument doc;
            try
            {
                using var reader = XmlReader.Create(new StringReader(content), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                doc = XDocument.Load(reader);
            }
            catch (XmlException e)
            {
                warnings.Add($"{path}: not read as a project ({e.Message}).");
                continue;
            }
            var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            var file = path[(path.LastIndexOf('/') + 1)..];
            string? Prop(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            var name = Prop("AssemblyName") is { } an && !an.Contains("$(") ? an : file[..^".csproj".Length];
            var packages = doc.Descendants().Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => (string?)e.Attribute("Include") ?? (string?)e.Attribute("Update") ?? "").ToList();
            var isTest = string.Equals(Prop("IsTestProject"), "true", StringComparison.OrdinalIgnoreCase)
                         || packages.Any(p => TestPackages.Contains(p, StringComparer.OrdinalIgnoreCase));
            var refs = doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => (string?)e.Attribute("Include") ?? "").Where(r => r.Length > 0)
                .Select(r => Resolve(dir, r.Replace('\\', '/'))).ToList();
            parsed.Add((path, dir, name, Prop("Description"), isTest, refs));
        }
        var byFile = parsed.ToDictionary(p => p.File, p => p.Name, StringComparer.OrdinalIgnoreCase);
        return parsed
            .Select(p => new CodeModule(p.Name, p.Dir)
            {
                File = p.File,
                Description = p.Description,
                IsTest = p.IsTest,
                References = p.Refs.Select(r => byFile.GetValueOrDefault(r)).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList(),
            })
            .GroupBy(m => m.Name, StringComparer.Ordinal).Select(g => g.First())   // two projects with one assembly name: the first
            .OrderBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>"src/Web" + "../Core/Core.csproj" → "src/Core/Core.csproj".</summary>
    private static string Resolve(string dir, string relative)
    {
        var parts = (dir.Length == 0 ? [] : dir.Split('/')).ToList();
        foreach (var seg in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg);
        }
        return string.Join("/", parts);
    }
}
