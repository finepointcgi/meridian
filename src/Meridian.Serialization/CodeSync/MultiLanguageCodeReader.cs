using Meridian.Core.CodeSync;

namespace Meridian.Serialization.CodeSync;

/// <summary>
/// The "auto" reader: every file goes to the first language reader that handles it (C#, TypeScript, Java, Python), each
/// language reads its own files, and the code models are merged (types sorted by symbol, warnings in language order).
/// Its <see cref="CodeModel.Language"/> is the language that was read, or the languages joined with '+'.
/// </summary>
public sealed class MultiLanguageCodeReader(IReadOnlyList<ICodeReader> readers) : ICodeReader
{
    public const string Auto = "auto";

    public MultiLanguageCodeReader() : this(CodeReaders.CreateAll()) { }

    public IReadOnlyList<ICodeReader> Readers { get; } = readers;

    public string Language => Auto;

    public bool Handles(string path) => Readers.Any(r => r.Handles(path));

    public CodeModel Read(IReadOnlyList<CodeFile> files, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        var groups = Readers.Select(r => (Reader: r, Files: new List<CodeFile>())).ToList();
        foreach (var f in files)
        {
            if (f?.Path is null) continue;
            var g = groups.FirstOrDefault(x => x.Reader.Handles(f.Path));
            g.Files?.Add(f);
        }
        var types = new List<CodeType>();
        var modules = new List<CodeModule>();
        var warnings = new List<string>();
        var languages = new List<string>();
        foreach (var (reader, list) in groups)
        {
            if (list.Count == 0) continue;
            ct.ThrowIfCancellationRequested();
            var model = reader.Read(list, ct);
            if (model.Types.Count == 0 && model.Warnings.Count == 0) continue;
            languages.Add(reader.Language);
            types.AddRange(model.Types);
            modules.AddRange(model.Modules);
            warnings.AddRange(model.Warnings);
        }
        var sorted = types.OrderBy(t => t.Symbol, StringComparer.Ordinal).ToList();
        return new CodeModel(languages.Count == 0 ? Auto : string.Join("+", languages), sorted, warnings)
        {
            Modules = modules.GroupBy(m => m.Name, StringComparer.Ordinal).Select(g => g.First()).OrderBy(m => m.Name, StringComparer.Ordinal).ToList(),
        };
    }
}

/// <summary>The code-sync readers by language name, and the file filter a language uses.</summary>
public static class CodeReaders
{
    /// <summary>Language names a repository connection may use.</summary>
    public static readonly IReadOnlyList<string> Languages = ["auto", "csharp", "typescript", "java", "python"];

    /// <summary>A new instance of every single-language reader, in the order "auto" tries them.</summary>
    public static IReadOnlyList<ICodeReader> CreateAll() =>
        [new CSharpCodeReader(), new TypeScriptCodeReader(), new JavaCodeReader(), new PythonCodeReader()];

    /// <summary>The reader for a language name ("auto" reads them all); null for an unknown name.</summary>
    public static ICodeReader? For(string? language) => (language ?? "").Trim().ToLowerInvariant() switch
    {
        "auto" or "" => new MultiLanguageCodeReader(),
        "csharp" or "c#" or "cs" => new CSharpCodeReader(),
        "typescript" or "ts" => new TypeScriptCodeReader(),
        "java" => new JavaCodeReader(),
        "python" or "py" => new PythonCodeReader(),
        _ => null,
    };

    /// <summary>The files a language reads (by extension and the reader's skip rules); nothing for an unknown language.</summary>
    public static Func<string, bool> Include(string? language) => For(language) is { } r ? r.Handles : _ => false;
}
