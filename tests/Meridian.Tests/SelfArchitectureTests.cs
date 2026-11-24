using Meridian.Core.CodeSync;
using Meridian.Core.CodeSync.Conformance;
using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

/// <summary>
/// Meridian's own architecture is a Meridian model (docs/architecture/Meridian.meridian, see docs/ARCHITECTURE.md). These
/// tests run Meridian's architecture checks on Meridian's own source, so a dependency the model does not allow fails the
/// build, and keep the diagram images in docs/architecture in step with the model.
/// <para>To refresh the images after editing the model in Meridian, run the tests with
/// <c>MERIDIAN_UPDATE_ARCHITECTURE_DOCS=1</c>.</para>
/// </summary>
public class SelfArchitectureTests(SelfArchitectureTests.SourceFixture source) : IClassFixture<SelfArchitectureTests.SourceFixture>
{
    public const string UpdateVariable = "MERIDIAN_UPDATE_ARCHITECTURE_DOCS";

    /// <summary>Reads src/ once for the class (Roslyn over the whole solution takes about a minute).</summary>
    public sealed class SourceFixture
    {
        public string Root { get; } = FindRoot();
        public string ModelPath => Path.Combine(Root, "docs", "architecture", "Meridian.meridian");
        public CodeModel Code { get; }

        public SourceFixture()
        {
            var src = Path.Combine(Root, "src");
            var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
                .Where(p => !CSharpCodeReader.IsGeneratedPath(p))
                .Select(p => new CodeFile(p, File.ReadAllText(Path.Combine(Root, p))))
                .ToList();
            // Bodies are only needed for behaviour diagrams; the checks use declarations and dependencies.
            Code = new CSharpCodeReader { ReadBodies = false }.Read(files);
        }

        private static string FindRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Meridian.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    private ModelRepository LoadModel() => ProjectFile.Load(source.ModelPath);

    private static IReadOnlyList<Element> Layers(ModelRepository m) =>
        m.AllElements.Where(e => e.Kind == ElementKind.Package && ArchitectureConformance.HasStereotype(e, ArchitectureConformance.LayerStereotype)).ToList();

    [Fact]
    public void Every_Assembly_Under_Src_Is_A_Layer_Of_The_Model()
    {
        var assemblies = Directory.GetDirectories(Path.Combine(source.Root, "src"))
            .Where(d => Directory.EnumerateFiles(d, "*.csproj").Any())
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        var layers = Layers(LoadModel())
            .Select(l => l.Get(FieldKeys.TagPrefix + ArchitectureConformance.NamespacesTag))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(assemblies.SequenceEqual(layers),
            $"Add a «layer» package for each new assembly to docs/architecture/Meridian.meridian (Assemblies package). " +
            $"Assemblies: {string.Join(", ", assemblies)}. Layers: {string.Join(", ", layers)}.");
    }

    [Fact]
    public void Model_Passes_Meridians_Own_Validation()
    {
        var issues = new Meridian.Core.Validation.ModelValidator().Validate(LoadModel());
        Assert.True(issues.Count == 0, string.Join("\n", issues.Select(i => $"  {i.Severity} {i.RuleId} {i.Element.QualifiedName}: {i.Message}")));
    }

    [Fact]
    public void Source_Conforms_To_The_Architecture_Model()
    {
        Assert.Empty(source.Code.Warnings);
        var result = ArchitectureConformance.Check(LoadModel(), source.Code);

        var errors = result.Violations.Where(v => v.Severity == ViolationSeverity.Error).ToList();
        Assert.True(errors.Count == 0,
            "src/ breaks the architecture in docs/architecture/Meridian.meridian (see docs/ARCHITECTURE.md):\n" +
            string.Join("\n", errors.Select(v => $"  [{v.RuleId}] {v.FirstLocation}: {v.Message} Fix: {v.FixHint}")));

        // Every «implemented» extension point still exists in code, so the checks keep biting after renames.
        var missing = result.Violations.Where(v => v.Rule == ConformanceRule.MissingInCode).Select(v => v.ElementName).ToList();
        Assert.True(missing.Count == 0, "«implemented» elements with no code type: " + string.Join(", ", missing));
        Assert.True(result.TypesMapped >= 20, $"Only {result.TypesMapped} code types map to the model; the name mapping may have broken.");
    }

    [Fact]
    public void Layering_Check_Catches_A_Dependency_The_Model_Does_Not_Allow()
    {
        // Meridian.Desktop really uses Meridian.Serialization; without the «allowed» edge that must be reported.
        var m = LoadModel();
        var edge = m.Relationships.Single(r => r.Kind == ElementKind.Dependency
            && ArchitectureConformance.HasStereotype(r, ArchitectureConformance.AllowedStereotype)
            && r.Source.Name == "Meridian.Desktop" && r.Target.Name == "Meridian.Serialization");
        m.Delete(edge);

        var result = ArchitectureConformance.Check(m, source.Code);
        Assert.Contains(result.Violations, v => v.Rule == ConformanceRule.Layering && v.CodeSymbol!.StartsWith("Meridian.Desktop.", StringComparison.Ordinal));
    }

    [Fact]
    public void Diagram_Images_Match_The_Model()
    {
        var m = LoadModel();
        var dir = Path.GetDirectoryName(source.ModelPath)!;
        var update = Environment.GetEnvironmentVariable(UpdateVariable) == "1";
        var stale = new List<string>();
        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in m.AllElements.OfType<Diagram>().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            foreach (var (suffix, theme) in new[] { ("", SvgTheme.Light), ("-dark", SvgTheme.Dark) })
            {
                var file = Slug(d.Name) + suffix + ".svg";
                expected.Add(file);
                var path = Path.Combine(dir, file);
                var svg = SvgRenderer.Render(m, d, theme: theme);
                if (update) File.WriteAllText(path, svg);
                else if (!File.Exists(path) || Normalize(File.ReadAllText(path)) != Normalize(svg)) stale.Add(file);
            }
        }
        var orphans = Directory.EnumerateFiles(dir, "*.svg").Select(Path.GetFileName).Where(f => !expected.Contains(f!)).ToList();
        Assert.True(stale.Count == 0 && orphans.Count == 0,
            $"docs/architecture images are out of date (stale: {string.Join(", ", stale)}; no diagram: {string.Join(", ", orphans)}). " +
            $"Run the tests with {UpdateVariable}=1 and commit the result.");
    }

    /// <summary>"Code sync and architecture check" → "code-sync-and-architecture-check".</summary>
    public static string Slug(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Trim('-');
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal);
}
