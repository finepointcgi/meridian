using System.Collections.Concurrent;
using Meridian.Core.Model;

namespace Meridian.Serialization.Reports;

/// <summary>A diagram rendered for a report: SVG text and its size in CSS pixels.</summary>
public sealed record DiagramImage(string Svg, double Width, double Height);

/// <summary>
/// Diagrams rendered for reports, by diagram id and model version (<see cref="ModelRepository.Revision"/>). A diagram that
/// several sections show (the SDD's structure, design-element and behavior sections), or that both passes of a streamed
/// report and every document of a split report need, is rendered once. Thread-safe.
/// </summary>
public sealed class DiagramImageCache
{
    private readonly ConcurrentDictionary<(string Id, long Version), Lazy<DiagramImage>> _images = new();
    private int _renders;

    /// <summary>How many diagrams were actually rendered (cache misses).</summary>
    public int Renders => Volatile.Read(ref _renders);

    public int Count => _images.Count;

    public DiagramImage Get(ModelRepository m, Diagram d) =>
        _images.GetOrAdd((d.Id, m.Revision), _ => new Lazy<DiagramImage>(() =>
        {
            Interlocked.Increment(ref _renders);
            var svg = SvgRenderer.Render(m, d);
            var (w, h) = DocResolver.SvgSize(svg);
            return new DiagramImage(svg, w, h);
        })).Value;
}
