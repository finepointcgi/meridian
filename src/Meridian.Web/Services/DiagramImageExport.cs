using System.IO.Compression;
using System.Text;
using Meridian.Core.Model;
using Meridian.Serialization;
using Microsoft.JSInterop;

namespace Meridian.Web.Services;

/// <summary>Background of an exported picture.</summary>
public enum ExportBackground { Transparent, Light, Dark }

/// <summary>
/// Diagram pictures: SVG is rendered on the server by <see cref="SvgRenderer"/>; PNG is rasterised in the browser
/// (canvas, at 1x–3x), so it looks exactly like the SVG. Also copies a diagram to the clipboard and zips a package's diagrams.
/// </summary>
public static class DiagramImageExport
{
    public static string Svg(ModelRepository m, Diagram d, ExportBackground bg) =>
        SvgRenderer.Render(m, d, theme: bg == ExportBackground.Dark ? SvgTheme.Dark : SvgTheme.Light, transparent: bg == ExportBackground.Transparent);

    public static async Task<byte[]> PngAsync(IJSRuntime js, string svg, int scale) =>
        Convert.FromBase64String(await js.InvokeAsync<string>("meridian.svgToPng", TimeSpan.FromMinutes(3), svg, Math.Clamp(scale, 1, 4)));

    public static Task DownloadAsync(IJSRuntime js, string fileName, string mime, byte[] bytes) =>
        js.InvokeVoidAsync("meridian.download", fileName, mime, Convert.ToBase64String(bytes)).AsTask();

    /// <summary>Puts a diagram on the clipboard as a PNG. Returns null on success, else a message for the user.</summary>
    public static async Task<string?> CopyAsync(IJSRuntime js, ModelRepository m, Diagram d, int scale, ExportBackground bg) =>
        await js.InvokeAsync<string?>("meridian.copySvgAsPng", Svg(m, d, bg), Math.Clamp(scale, 1, 4));

    /// <summary>Exports one diagram as .png or .svg (downloaded, or saved through the desktop app's dialog).</summary>
    public static async Task ExportAsync(IJSRuntime js, ModelRepository m, Diagram d, bool png, int scale, ExportBackground bg)
    {
        var svg = Svg(m, d, bg);
        if (png) await DownloadAsync(js, SafeName(d.Name) + ".png", "image/png", await PngAsync(js, svg, scale));
        else await DownloadAsync(js, SafeName(d.Name) + ".svg", "image/svg+xml", Encoding.UTF8.GetBytes(svg));
    }

    /// <summary>Every diagram inside a package (or model), at any depth.</summary>
    public static IReadOnlyList<Diagram> DiagramsIn(Element container) =>
        container is Diagram d ? [d] : container.Descendants().OfType<Diagram>().ToList();

    /// <summary>A zip with one picture per diagram, in folders mirroring the containment tree.</summary>
    public static async Task<byte[]> ZipAsync(IJSRuntime js, ModelRepository m, Element container, bool png, int scale, ExportBackground bg, Action<int, int>? progress = null)
    {
        var diagrams = DiagramsIn(container);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < diagrams.Count; i++)
            {
                var d = diagrams[i];
                progress?.Invoke(i, diagrams.Count);
                var folder = string.Join("/", d.AncestorsAndSelf().Skip(1).TakeWhile(a => !ReferenceEquals(a, container)).Reverse().Select(a => SafeName(a.DisplayName)));
                var path = (folder.Length > 0 ? folder + "/" : "") + SafeName(d.Name);
                var name = path;
                for (var k = 2; !used.Add(name); k++) name = $"{path} ({k})";
                var svg = Svg(m, d, bg);
                var bytes = png ? await PngAsync(js, svg, scale) : Encoding.UTF8.GetBytes(svg);
                var entry = zip.CreateEntry(name + (png ? ".png" : ".svg"), png ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                await using var s = entry.Open();
                await s.WriteAsync(bytes);
            }
        }
        progress?.Invoke(diagrams.Count, diagrams.Count);
        return ms.ToArray();
    }

    public static string SafeName(string? s)
    {
        var cleaned = string.Join("_", (s ?? "").Split(Path.GetInvalidFileNameChars().Append('/').Append('\\').ToArray(), StringSplitOptions.RemoveEmptyEntries)).Trim().Trim('.');
        return cleaned.Length > 0 ? cleaned : "diagram";
    }
}
