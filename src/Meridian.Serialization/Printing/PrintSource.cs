using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Meridian.Core.Model;

namespace Meridian.Serialization.Printing;

/// <summary>
/// A diagram drawn once for printing: the SVG body (<see cref="Inner"/>, in diagram units) and the part of the diagram that
/// is printed (<see cref="Content"/>). Every page is the same body behind a different viewBox, so text and lines stay vector.
/// </summary>
public sealed partial record PrintSource(string Inner, Bounds Content, string RootAttributes)
{
    /// <summary>Characters held (for the batch cache's budget).</summary>
    public long Size => Inner.Length;

    /// <summary>A standalone SVG showing <paramref name="viewBox"/> (diagram units) at the given size (any unit; points in PDFs).</summary>
    public string Svg(Bounds viewBox, double width, double height) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\"{RootAttributes} viewBox=\"{F(viewBox.X)} {F(viewBox.Y)} {F(viewBox.Width)} {F(viewBox.Height)}\" width=\"{F(width)}\" height=\"{F(height)}\" preserveAspectRatio=\"xMidYMid meet\">{Inner}</svg>";

    /// <summary>The printed region on its own, at its natural size.</summary>
    public string Svg() => Svg(Content, Content.Width, Content.Height);

    internal static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Draws <paramref name="d"/> for printing with <paramref name="setup"/>'s frame, legend and region. The model should be
    /// the user's own (redacted) view: hidden elements are already nameless placeholders there, and nothing else is read.
    /// </summary>
    /// <param name="theme">Colours when the setup does not force light (the canvas's own).</param>
    /// <param name="view">The canvas's visible area (diagram units), for <see cref="PrintRegion.CurrentView"/>.</param>
    /// <param name="selection">Ids of the selected shapes, for <see cref="PrintRegion.Selection"/>.</param>
    public static PrintSource Create(ModelRepository model, Diagram d, PageSetup setup, SvgTheme? theme = null, Bounds? view = null,
                                     IReadOnlyCollection<string>? selection = null)
    {
        var printed = PrintView(d, setup, selection);
        var svg = SvgRenderer.Render(model, printed, theme: setup.ForceLight ? SvgTheme.Light : theme ?? SvgTheme.Light, frame: setup.Frame);
        var (rootAttrs, box, inner) = Split(svg);
        if (setup.Region == PrintRegion.CurrentView && view is { Width: > 0, Height: > 0 } v && Intersect(box, v) is { } shown)
        {
            // Only what the canvas shows: clip the body to the view, so partial pages don't show what lies beyond it.
            var clip = ClipId(d.Id);
            inner = $"<defs><clipPath id=\"{clip}\"><rect x=\"{F(shown.X)}\" y=\"{F(shown.Y)}\" width=\"{F(shown.Width)}\" height=\"{F(shown.Height)}\"/></clipPath></defs>" +
                    $"<g clip-path=\"url(#{clip})\">{inner}</g>";
            box = shown;
        }
        return new PrintSource(inner, box, rootAttrs);
    }

    /// <summary>The diagram as printed: only the selected shapes (and what they contain) for a selection, with the legend on request.</summary>
    internal static Diagram PrintView(Diagram d, PageSetup setup, IReadOnlyCollection<string>? selection)
    {
        var onlySelection = setup.Region == PrintRegion.Selection && selection is { Count: > 0 };
        var legend = setup.Legend && d.Get(FieldKeys.Legend) != "true";
        if (!onlySelection && !legend) return d;
        var view = new Diagram(d.DiagramType, d.Id) { Owner = d.Owner };
        foreach (var kv in d.Fields) view.SetRaw(kv.Key, kv.Value);
        if (legend) view.SetRaw(FieldKeys.Legend, "true");
        if (onlySelection)
        {
            var keep = new HashSet<DiagramNode>(ReferenceEqualityComparer.Instance);
            foreach (var n in d.Nodes.Where(n => selection!.Contains(n.Id)))
            {
                keep.Add(n);
                foreach (var c in d.DescendantsOf(n)) keep.Add(c);
            }
            view.NodeList.AddRange(d.Nodes.Where(keep.Contains));
            view.EdgeList.AddRange(d.Edges.Where(e => keep.Contains(e.Source) && keep.Contains(e.Target)));
        }
        else
        {
            view.NodeList.AddRange(d.Nodes);
            view.EdgeList.AddRange(d.Edges);
        }
        return view;
    }

    /// <summary>Root attributes (other than size and viewBox), the viewBox, and the body of a rendered SVG.</summary>
    internal static (string RootAttributes, Bounds ViewBox, string Inner) Split(string svg)
    {
        var start = svg.IndexOf("<svg", StringComparison.Ordinal);
        var close = svg.IndexOf('>', start);
        var end = svg.LastIndexOf("</svg>", StringComparison.Ordinal);
        var root = svg[(start + 4)..close];
        var box = ViewBoxPattern().Match(root) is { Success: true } m
            ? new Bounds(D(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value))
            : new Bounds(0, 0, 400, 300);
        var attrs = new StringBuilder();
        foreach (Match a in AttributePattern().Matches(root))
            if (a.Groups[1].Value is not ("xmlns" or "viewBox" or "width" or "height" or "preserveAspectRatio"))
                attrs.Append(' ').Append(a.Value);
        return (attrs.ToString(), box, svg[(close + 1)..end]);
    }

    private static double D(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static Bounds? Intersect(Bounds a, Bounds b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var r = Math.Min(a.Right, b.Right);
        var btm = Math.Min(a.Bottom, b.Bottom);
        return r - x > 1 && btm - y > 1 ? new Bounds(x, y, r - x, btm - y) : null;
    }

    internal static string ClipId(string diagramId) => "mprint-clip-" + new string(diagramId.Where(char.IsAsciiLetterOrDigit).Take(40).ToArray());

    [GeneratedRegex(@"viewBox=""(-?[\d.eE+-]+)[ ,]+(-?[\d.eE+-]+)[ ,]+(-?[\d.eE+-]+)[ ,]+(-?[\d.eE+-]+)""")]
    private static partial Regex ViewBoxPattern();

    [GeneratedRegex(@"([\w:-]+)=""[^""]*""")]
    private static partial Regex AttributePattern();
}

/// <summary>
/// Print sources of a batch, kept while they fit a character budget and drawn again when evicted: a batch of large
/// diagrams never holds all of them at once.
/// </summary>
public sealed class PrintSourceCache(long budget = 48_000_000)
{
    private readonly Dictionary<string, PrintSource> _items = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _order = new();
    private readonly object _gate = new();
    private long _size;

    /// <summary>How many sources were drawn (misses).</summary>
    public int Renders { get; private set; }

    public PrintSource Get(string key, Func<PrintSource> create)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var hit))
            {
                _order.Remove(key);
                _order.AddLast(key);
                return hit;
            }
        }
        var made = create();
        lock (_gate)
        {
            Renders++;
            if (_items.TryAdd(key, made))
            {
                _order.AddLast(key);
                _size += made.Size;
                while (_size > budget && _order.Count > 1 && _order.First!.Value != key)
                {
                    var old = _order.First.Value;
                    _order.RemoveFirst();
                    _size -= _items[old].Size;
                    _items.Remove(old);
                }
            }
            return made;
        }
    }
}
