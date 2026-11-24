using System.Diagnostics;
using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Benchmarks;

/// <summary>
/// Times the work the diagram canvas does on every render, for each diagram of a real .meridian file:
/// the SVG body, the minimap's routes, the content bounds and the shapes' measured bounds.
/// Usage: dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --diagrams path/to/project.meridian [top N]
/// </summary>
public static class DiagramRenderBench
{
    public static void Run(string path, int top)
    {
        var load = Stopwatch.StartNew();
        var m = ProjectFile.Load(path);
        Console.WriteLine($"{Path.GetFileName(path)}: {m.Count:N0} elements, {m.Diagrams.Count():N0} diagrams (load {load.Elapsed.TotalMilliseconds:0} ms)");
        var ws = new Workspace();
        ws.UseLocalModel(m);

        var rows = new List<(string Name, int Nodes, int Edges, double Body, double Mini, double Bounds, double Content, double Frame)>();
        foreach (var d in m.Diagrams)
        {
            ws.Open(d);
            var c = ws.Active!;
            c.ViewWidth = 1600; c.ViewHeight = 900;
            var body = Best(() => { var sb = new StringBuilder(); SvgRenderer.RenderBody(sb, m, d, c.BoundsOf, c.WaypointsFor, null, c.Viewport); return sb.Length; });
            var mini = Best(() => EdgeRouter.RouteAll(DiagramExtras.VisibleView(d), c.MemoBounds()).Count);
            var bounds = Best(() => { var s = 0.0; foreach (var n in d.Nodes) s += c.BoundsOf(n).Width; return (int)s; });
            var content = Best(() => (int)c.ContentBounds().Width);
            // One canvas render as DiagramView does it today: body, the minimap's routes, content bounds twice.
            var frame = body + mini + 2 * content;
            rows.Add((d.Name, d.Nodes.Count, d.Edges.Count, body, mini, bounds, content, frame));
        }

        Console.WriteLine();
        Console.WriteLine($"{"Diagram",-44} {"nodes",5} {"edges",5} {"body ms",8} {"mini ms",8} {"bounds",8} {"content",8} {"render",8}");
        Console.WriteLine(new string('-', 102));
        foreach (var r in rows.OrderByDescending(r => r.Frame).Take(top))
            Console.WriteLine($"{Trunc(r.Name, 44),-44} {r.Nodes,5} {r.Edges,5} {r.Body,8:0.00} {r.Mini,8:0.00} {r.Bounds,8:0.00} {r.Content,8:0.00} {r.Frame,8:0.00}");
        Console.WriteLine();
        Console.WriteLine($"All {rows.Count} diagrams: render total {rows.Sum(r => r.Frame):0.0} ms, mean {rows.Average(r => r.Frame):0.00} ms, worst {rows.Max(r => r.Frame):0.00} ms");
    }

    private static double Best(Func<int> work, int repeat = 5)
    {
        work();
        var best = double.MaxValue;
        for (var i = 0; i < repeat; i++)
        {
            var t = Stopwatch.StartNew();
            work();
            best = Math.Min(best, t.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
}
