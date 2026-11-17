using System.Text;
using Meridian.Core.Model;
using Meridian.Serialization.DrawIo;
using Meridian.Serialization.Mermaid;

namespace Meridian.Serialization.DiagramFiles;

/// <summary>A diagram file from a folder: its path relative to the folder's parent ("docs/flows/login.mmd") and its bytes.</summary>
public sealed record DiagramSourceFile(string Path, byte[] Content);

public enum DiagramFileFormat { Mermaid, Markdown, DrawIo }

public sealed record DiagramFolderOptions
{
    /// <summary>Mirror the folders as packages (the chosen folder itself becomes the top package).</summary>
    public bool PackagePerFolder { get; init; } = true;

    public MermaidMatchMode Match { get; init; } = MermaidMatchMode.CreateNew;

    /// <summary>draw.io pages drawn with UML shapes become model elements; false keeps them drawings.</summary>
    public bool DrawIoModelElements { get; init; } = true;

    /// <summary>Diagrams made from the same file by an earlier import (into the same package) are replaced instead of added again.</summary>
    public bool ReplacePrevious { get; init; } = true;
}

/// <summary>One diagram a file will become.</summary>
public sealed record DiagramPreviewItem(string Name, string Becomes, string Counts, bool Supported);

/// <summary>What one file will become.</summary>
public sealed record DiagramFilePreview(string Path, DiagramFileFormat Format, IReadOnlyList<DiagramPreviewItem> Diagrams, IReadOnlyList<string> Warnings, bool ImportedBefore, string? Error)
{
    public int Supported => Diagrams.Count(d => d.Supported);
}

public sealed record DiagramFolderResult(IReadOnlyList<Diagram> Diagrams, int Files, int Replaced, int Elements, int Relationships, IReadOnlyList<string> Warnings)
{
    public ImportReport ToReport() => new(Elements, Relationships, Warnings);

    public string Summary => Diagrams.Count == 0
        ? "Nothing imported" + (Warnings.Count > 0 ? $" ({Warnings.Count} warnings)" : "")
        : $"{Plural(Diagrams.Count, "diagram")} from {Plural(Files, "file")}, {Plural(Elements, "element")}, {Plural(Relationships, "relationship")}"
          + (Replaced > 0 ? $", {Plural(Replaced, "earlier diagram")} replaced" : "")
          + (Warnings.Count > 0 ? $", {Plural(Warnings.Count, "warning")}" : "");

    private static string Plural(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
}

/// <summary>
/// Imports a set of diagram files (a folder of Mermaid .mmd / Markdown and draw.io files) into a package as one undo step.
/// Each diagram is stamped with the file it came from, so importing the folder again replaces those diagrams rather than
/// adding copies; elements shared between files (blocks, actors…) are made once. See docs/MERMAID.md and docs/DRAWIO.md.
/// </summary>
public static class DiagramFolderImport
{
    /// <summary>Field on imported diagrams (and the behaviours / shapes made for them): "folder/file.mmd#1".</summary>
    public const string SourceField = "import.file";

    public const long MaxFileBytes = 32L * 1024 * 1024;
    public const long MaxTotalBytes = 128L * 1024 * 1024;
    public const int MaxFiles = 2000;

    public static DiagramFileFormat? FormatOf(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (DrawIoFile.IsDrawIoFile(path)) return DiagramFileFormat.DrawIo;
        if (MermaidImporter.IsMarkdownFile(path)) return DiagramFileFormat.Markdown;
        if (MermaidImporter.IsMermaidFile(path)) return DiagramFileFormat.Mermaid;
        return null;
    }

    public static bool IsDiagramFile(string? path) => FormatOf(path) != null;

    /// <summary>Extensions picked up from a folder (also used by the browser to filter what it uploads).</summary>
    public static IReadOnlyList<string> Extensions => [.. MermaidImporter.Extensions, .. DrawIoFile.Extensions];

    private static string Normalise(string path) => path.Replace('\\', '/').Trim('/');

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('﻿');

    /// <summary>Reads every file without touching the model: what each will become. Markdown files without diagrams are left out.</summary>
    public static IReadOnlyList<DiagramFilePreview> Preview(IReadOnlyList<DiagramSourceFile> files, DiagramFolderOptions? options = null, ModelRepository? model = null, Element? target = null)
    {
        options ??= new DiagramFolderOptions();
        var before = model != null ? Stamped(model, target ?? model.Root) : new HashSet<string>(StringComparer.Ordinal);
        var list = new List<DiagramFilePreview>();
        foreach (var f in files.OrderBy(f => Normalise(f.Path), StringComparer.OrdinalIgnoreCase))
        {
            var path = Normalise(f.Path);
            if (FormatOf(path) is not { } format) continue;
            var importedBefore = before.Contains(path);
            try
            {
                if (format == DiagramFileFormat.DrawIo)
                {
                    var pages = DrawIoFile.Read(path, f.Content);
                    var items = DrawIoImporter.Preview(path, pages, new DrawIoImportOptions { ModelElements = options.DrawIoModelElements })
                        .Select(p => new DiagramPreviewItem(p.DiagramName, p.Becomes, p.Counts, p.Supported)).ToList();
                    list.Add(new DiagramFilePreview(path, format, items, [], importedBefore, null));
                }
                else
                {
                    var preview = MermaidImporter.Preview(Text(f.Content), format == DiagramFileFormat.Markdown);
                    if (format == DiagramFileFormat.Markdown && preview.Diagrams.Count == 0) continue;   // ordinary documentation
                    var stem = Path.GetFileNameWithoutExtension(path);
                    var items = preview.Diagrams.Select((d, i) => new DiagramPreviewItem(
                        d.Title.Length > 0 ? d.Title : preview.Diagrams.Count == 1 ? stem : $"{stem} {i + 1}", d.Becomes, d.Counts, d.Supported)).ToList();
                    list.Add(new DiagramFilePreview(path, format, items, preview.Warnings, importedBefore, null));
                }
            }
            catch (FormatException ex)
            {
                list.Add(new DiagramFilePreview(path, format, [], [], importedBefore, ex.Message));
            }
        }
        return list;
    }

    /// <summary>Source files ("folder/file.mmd") already imported under <paramref name="target"/>.</summary>
    public static HashSet<string> Stamped(ModelRepository m, Element target)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in m.Diagrams)
            if (d.Get(SourceField) is { Length: > 0 } s && (ReferenceEquals(target, m.Root) || target.IsAncestorOf(d)))
                set.Add(FileOf(s));
        return set;
    }

    private static string FileOf(string stamp) => stamp.LastIndexOf('#') is var i and > 0 ? stamp[..i] : stamp;

    /// <summary>
    /// The package an earlier import of any of these files went into (climbing out of the folder packages it made), so
    /// importing a folder again updates it in place; null when none of them was imported before.
    /// </summary>
    public static Element? PreviousTarget(ModelRepository m, IReadOnlyList<DiagramSourceFile> files)
    {
        var paths = files.Select(f => Normalise(f.Path)).ToHashSet(StringComparer.Ordinal);
        foreach (var d in m.Diagrams)
        {
            if (d.Get(SourceField) is not { Length: > 0 } s || !paths.Contains(FileOf(s))) continue;
            var dirs = FileOf(s).Split('/')[..^1];
            var p = d.AncestorsAndSelf().Skip(1).FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model);
            for (var i = dirs.Length - 1; i >= 0 && p is { Kind: ElementKind.Package } && p.Name == dirs[i]; i--) p = p.Owner;
            if (p is { Kind: ElementKind.Package or ElementKind.Model } && m.Contains(p)) return p;
        }
        return null;
    }

    /// <summary>Imports <paramref name="files"/> into <paramref name="target"/> as one undo step.</summary>
    public static DiagramFolderResult Import(ModelRepository m, Element target, IReadOnlyList<DiagramSourceFile> files, DiagramFolderOptions? options = null)
    {
        options ??= new DiagramFolderOptions();
        if (!m.Contains(target) || target.Kind is not (ElementKind.Package or ElementKind.Model)) target = m.Root;
        var warnings = new List<string>();
        var diagrams = new List<Diagram>();
        var elementsBefore = m.AllElements.Count(e => e is not Relationship && e is not Diagram);
        var relationshipsBefore = m.Relationships.Count();
        var replaced = 0;
        var fileCount = 0;
        var ordered = files.Select(f => f with { Path = Normalise(f.Path) }).Where(f => IsDiagramFile(f.Path))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (ordered.Count > MaxFiles)
        {
            warnings.Add($"more than {MaxFiles} diagram files; only the first {MaxFiles} were imported.");
            ordered = ordered.Take(MaxFiles).ToList();
        }
        using (m.History.Transaction("Import diagrams"))
        {
            var builder = new MermaidBuilder(m, target, new MermaidImportOptions { Match = options.Match }, warnings) { Source = "a diagram file" };
            var packages = new Dictionary<string, Element>(StringComparer.OrdinalIgnoreCase);
            Element PackageFor(string path)
            {
                if (!options.PackagePerFolder) return target;
                var dir = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                if (dir.Length == 0) return target;
                if (packages.TryGetValue(dir, out var p)) return p;
                var parentDir = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : "";
                var parent = parentDir.Length == 0 ? target : PackageFor(parentDir + "/x");
                var name = dir[(dir.LastIndexOf('/') + 1)..];
                // Folders always reuse the package of the same name, so importing a folder again lands in the same place.
                p = parent.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Package && c.Name == name) ?? m.Create(parent, ElementKind.Package, name);
                return packages[dir] = p;
            }

            foreach (var f in ordered)
            {
                if (f.Content.LongLength > MaxFileBytes) { warnings.Add($"{f.Path}: larger than {MaxFileBytes / (1024 * 1024)} MB; skipped."); continue; }
                var format = FormatOf(f.Path)!.Value;
                var pkg = PackageFor(f.Path);
                builder.Target = pkg;
                if (options.ReplacePrevious) replaced += RemovePrevious(m, target, f.Path);
                var fileWarnings = new List<string>();
                List<Diagram> made;
                try
                {
                    if (format == DiagramFileFormat.DrawIo)
                    {
                        var pages = DrawIoFile.Read(f.Path, f.Content);
                        made = DrawIoImporter.ImportPages(m, builder, f.Path, pages, new DrawIoImportOptions { ModelElements = options.DrawIoModelElements, Match = options.Match }, fileWarnings);
                    }
                    else
                    {
                        var graphs = MermaidImporter.Parse(Text(f.Content), format == DiagramFileFormat.Markdown, fileWarnings);
                        if (format == DiagramFileFormat.Markdown && graphs.Count == 0) continue;
                        made = new List<Diagram>();
                        var stem = Path.GetFileNameWithoutExtension(f.Path);
                        foreach (var g in graphs)
                        {
                            if (g.Kind != MermaidDiagramKind.Unsupported && builder.Build(g, stem) is { } d) made.Add(d);
                            fileWarnings.AddRange(g.Warnings);
                        }
                    }
                }
                catch (FormatException ex)
                {
                    warnings.Add($"{f.Path}: {ex.Message}");
                    continue;
                }
                warnings.AddRange(fileWarnings.Select(w => $"{f.Path}: {w}"));
                if (made.Count > 0) fileCount++;
                for (var i = 0; i < made.Count; i++) Stamp(m, made[i], pkg, made.Count == 1 ? f.Path : $"{f.Path}#{i + 1}");
                diagrams.AddRange(made);
            }
        }
        return new DiagramFolderResult(diagrams, fileCount, replaced,
            m.AllElements.Count(e => e is not Relationship && e is not Diagram) - elementsBefore,
            m.Relationships.Count() - relationshipsBefore, warnings);
    }

    /// <summary>Marks a diagram, the behaviour that owns it and the shapes, notes and lines drawn only for it with their source file.</summary>
    private static void Stamp(ModelRepository m, Diagram d, Element pkg, string source)
    {
        m.Set(d, SourceField, source);
        if (d.Owner is { Kind: ElementKind.Activity or ElementKind.StateMachine or ElementKind.Interaction } owner && !ReferenceEquals(owner, pkg))
            m.Set(owner, SourceField, source);
        foreach (var n in d.Nodes)
            if (n.Element.Kind is ElementKind.Shape or ElementKind.Comment && ReferenceEquals(n.Element.Owner, pkg))
                m.Set(n.Element, SourceField, source);
        foreach (var e in d.Edges)
            if (e.Relationship.Kind == ElementKind.Line) m.Set(e.Relationship, SourceField, source);
    }

    /// <summary>Deletes what an earlier import made from <paramref name="path"/> under <paramref name="target"/>. Returns the number of diagrams removed.</summary>
    private static int RemovePrevious(ModelRepository m, Element target, string path)
    {
        var old = m.AllElements.Where(e => e.Get(SourceField) is { Length: > 0 } s && FileOf(s) == path && (ReferenceEquals(target, m.Root) || target.IsAncestorOf(e))).ToList();
        if (old.Count == 0) return 0;
        var diagrams = old.Count(e => e is Diagram);
        // Owners first: deleting a behaviour takes its diagram with it.
        foreach (var e in old.OrderBy(e => e is Diagram ? 1 : 0))
            if (m.Contains(e)) m.Delete(e);
        return diagrams;
    }
}
