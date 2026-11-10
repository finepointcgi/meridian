using System.Collections.Concurrent;
using Meridian.Core.Model;

namespace Meridian.Core.Language;

/// <summary>How a connector without manual waypoints is drawn.</summary>
public enum EdgeRouting
{
    /// <summary>The look diagrams had before routing styles existed (kept for existing diagrams).</summary>
    Legacy,
    /// <summary>A straight line between the two shapes (whiteboards).</summary>
    Straight,
    /// <summary>Rectilinear path that avoids other shapes (draw.io orthogonal, Cameo rectilinear).</summary>
    Orthogonal,
    /// <summary>The orthogonal route smoothed into a curve.</summary>
    Curved,
}

public enum Side { Top, Right, Bottom, Left }

/// <summary>
/// Obstacle-avoiding orthogonal connector routing.
/// <para>
/// Each connector leaves its source and enters its target through the sides that face each other; connectors sharing a
/// side are spread along it. Between the two stubs an A* search runs over a sparse grid made of the obstacle
/// boundary lines (inflated by <see cref="Margin"/>) inside a region around the two shapes, minimising length plus a
/// penalty per bend. Results are cached per diagram: a connector is re-routed only when its ports moved or a shape
/// that changed lies in its search region, so dragging a shape re-routes just the affected connectors.
/// </para>
/// </summary>
public static class EdgeRouter
{
    /// <summary>Diagram field: default routing for its connectors ("straight", "orthogonal", "curved"; empty = legacy look).</summary>
    public const string DiagramKey = "edgeRouting";

    /// <summary>Connector style key: per-connector override of the diagram's routing.</summary>
    public const string EdgeKey = "routing";

    /// <summary>Diagram field: "true" draws a small hop where a connector crosses one drawn before it.</summary>
    public const string LineJumpsKey = "lineJumps";

    /// <summary>Clearance kept between a routed path and the shapes it passes.</summary>
    public const double Margin = 14;

    /// <summary>Length of the straight stub leaving / entering a shape (room for arrow heads and diamonds).</summary>
    public const double Stub = 22;

    /// <summary>Cost of one bend, in units of path length.</summary>
    public const double BendPenalty = 40;

    public static EdgeRouting Parse(string? s) => s switch
    {
        "straight" => EdgeRouting.Straight,
        "orthogonal" => EdgeRouting.Orthogonal,
        "curved" => EdgeRouting.Curved,
        _ => EdgeRouting.Legacy,   // "classic" or empty
    };

    public static string? Format(EdgeRouting r) => r switch
    {
        EdgeRouting.Straight => "straight",
        EdgeRouting.Orthogonal => "orthogonal",
        EdgeRouting.Curved => "curved",
        _ => null,
    };

    public static string Display(EdgeRouting r) => r switch
    {
        EdgeRouting.Straight => "Straight",
        EdgeRouting.Orthogonal => "Orthogonal (routed)",
        EdgeRouting.Curved => "Curved",
        _ => "Classic",
    };

    /// <summary>Routing given to new diagrams: straight lines on whiteboards and communication diagrams, routed paths elsewhere.</summary>
    public static EdgeRouting DefaultFor(DiagramType t) => t switch
    {
        DiagramType.Whiteboard or DiagramType.Communication => EdgeRouting.Straight,
        _ => EdgeRouting.Orthogonal,
    };

    /// <summary>The routing of a connector: its own override, else the diagram's default, else the legacy look.</summary>
    public static EdgeRouting StyleOf(Diagram d, DiagramEdge e) =>
        e.Style.TryGetValue(EdgeKey, out var s) && s.Length > 0 ? Parse(s) : Parse(d.Get(DiagramKey));

    public static bool LineJumps(Diagram d) => d.Get(LineJumpsKey) == "true";

    /// <summary>Messages (sequence time order) and self loops keep their dedicated notation.</summary>
    public static bool Routable(DiagramEdge e) => e.Relationship.Kind != ElementKind.Message && !ReferenceEquals(e.Source, e.Target);

    /// <summary>
    /// The path of a connector under its routing style, or null when it uses the legacy look.
    /// Manual waypoints win over routing (curved connectors are smoothed through them).
    /// </summary>
    public static IReadOnlyList<Pt>? TryRoute(Diagram d, DiagramEdge e, Func<DiagramNode, Bounds> boundsOf, IReadOnlyList<Pt>? waypoints = null)
    {
        var style = StyleOf(d, e);
        if (style == EdgeRouting.Legacy || !Routable(e)) return null;
        var wps = waypoints ?? e.Waypoints;
        if (wps.Count > 0)
        {
            var manual = Notation.Route(e, boundsOf, wps);
            return style == EdgeRouting.Curved ? Smooth(manual) : manual;
        }
        if (style == EdgeRouting.Straight) return Straight(e, boundsOf);
        var state = StateFor(d);
        lock (state)
        {
            state.Refresh(d, boundsOf);
            return state.PathOf(e) ?? Notation.Orthogonal(boundsOf(e.Source), boundsOf(e.Target));
        }
    }

    /// <summary>Paths of every connector of a diagram, computed with one refresh of the routing cache (renderers, hit testing).</summary>
    public static Dictionary<DiagramEdge, IReadOnlyList<Pt>> RouteAll(Diagram d, Func<DiagramNode, Bounds> boundsOf, Func<DiagramEdge, IReadOnlyList<Pt>?>? waypoints = null)
    {
        var result = new Dictionary<DiagramEdge, IReadOnlyList<Pt>>(d.Edges.Count, ReferenceEqualityComparer.Instance);
        var auto = new List<DiagramEdge>();
        foreach (var e in d.Edges)
        {
            var wps = waypoints?.Invoke(e);
            if (DataModeling.DbNotation.ColumnRoute(d, e, boundsOf, wps) is { } columns) { result[e] = columns; continue; }
            if (StyleOf(d, e) is EdgeRouting.Orthogonal or EdgeRouting.Curved && Routable(e) && (wps ?? e.Waypoints).Count == 0) auto.Add(e);
            else result[e] = Notation.RouteIn(d, e, boundsOf, wps);
        }
        if (auto.Count > 0)
        {
            var state = StateFor(d);
            lock (state)
            {
                state.Refresh(d, boundsOf);
                foreach (var e in auto) result[e] = state.PathOf(e) ?? Notation.Orthogonal(boundsOf(e.Source), boundsOf(e.Target));
            }
        }
        return result;
    }

    /// <summary>How many connectors of <paramref name="d"/> were (re)computed by the last refresh. For tests and diagnostics.</summary>
    public static int LastRerouteCount(Diagram d) => States.TryGetValue(d.Id, out var s) ? s.LastRerouted : 0;

    /// <summary>Drops the cached routes of a diagram.</summary>
    public static void Invalidate(Diagram d) => States.TryRemove(d.Id, out _);

    public static IReadOnlyList<Pt> Straight(DiagramEdge e, Func<DiagramNode, Bounds> boundsOf)
    {
        var sb = boundsOf(e.Source); var tb = boundsOf(e.Target);
        return [Notation.Clip(sb, Notation.ShapeOf(e.Source.Element), tb.Center), Notation.Clip(tb, Notation.ShapeOf(e.Target.Element), sb.Center)];
    }

    // ================================================================ cache

    private static readonly ConcurrentDictionary<string, RouterState> States = new(StringComparer.Ordinal);

    private static RouterState StateFor(Diagram d)
    {
        if (States.Count > 256 && !States.ContainsKey(d.Id)) States.Clear();
        return States.GetOrAdd(d.Id, _ => new RouterState());
    }

    private static bool IsObstacle(DiagramNode n) =>
        !Notation.IsBorderShape(n.Element) && Notation.ShapeOf(n.Element) is not (NodeShape.Swimlane or NodeShape.Pool or NodeShape.Fragment or NodeShape.Lifeline);

    private sealed record Entry(Pt P0, Side S0, Pt P1, Side S1, Bounds Src, Bounds Tgt, Bounds Region, IReadOnlyList<Pt> Path)
    {
        public IReadOnlyList<Pt>? Curve;
    }

    private sealed class RouterState
    {
        private Dictionary<DiagramNode, Bounds> _bounds = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<DiagramEdge, Entry> _entries = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<DiagramEdge, bool> _curved = new(ReferenceEqualityComparer.Instance);
        public int LastRerouted;

        public IReadOnlyList<Pt>? PathOf(DiagramEdge e)
        {
            if (!_entries.TryGetValue(e, out var en)) return null;
            if (!_curved.GetValueOrDefault(e)) return en.Path;
            return en.Curve ??= Smooth(en.Path);
        }

        public void Refresh(Diagram d, Func<DiagramNode, Bounds> boundsOf)
        {
            // 1. What moved since the last refresh (old and new rectangles of shapes that changed, appeared or vanished).
            var cur = new Dictionary<DiagramNode, Bounds>(d.Nodes.Count, ReferenceEqualityComparer.Instance);
            var changed = new List<Bounds>();
            foreach (var n in d.Nodes)
            {
                var b = boundsOf(n);
                cur[n] = b;
                if (!_bounds.TryGetValue(n, out var old)) changed.Add(b);
                else if (old != b) { changed.Add(old); changed.Add(b); }
            }
            foreach (var (n, old) in _bounds)
                if (!cur.ContainsKey(n)) changed.Add(old);
            _bounds = cur;

            // 2. Ports for every routed connector (they depend on each other through spreading).
            var routed = new List<DiagramEdge>();
            foreach (var e in d.Edges)
            {
                if (e.Waypoints.Count > 0 || !Routable(e) || !cur.ContainsKey(e.Source) || !cur.ContainsKey(e.Target)) continue;
                var style = StyleOf(d, e);
                if (style is not (EdgeRouting.Orthogonal or EdgeRouting.Curved)) continue;
                routed.Add(e);
                _curved[e] = style == EdgeRouting.Curved;
            }
            var ports = AssignPorts(routed, cur);

            // 3. Re-route only connectors whose ports moved or whose search region saw a change.
            List<(DiagramNode Node, Bounds B)>? candidates = null;
            var live = new HashSet<DiagramEdge>(ReferenceEqualityComparer.Instance);
            LastRerouted = 0;
            foreach (var e in routed)
            {
                live.Add(e);
                var (p0, s0, p1, s1) = ports[e];
                var sb = cur[e.Source]; var tb = cur[e.Target];
                if (_entries.TryGetValue(e, out var en) && en.P0 == p0 && en.P1 == p1 && en.S0 == s0 && en.S1 == s1 && en.Src == sb && en.Tgt == tb
                    && !changed.Any(c => Overlaps(c, en.Region)))
                    continue;
                candidates ??= d.Nodes.Where(IsObstacle).Select(n => (n, cur[n])).ToList();
                _entries[e] = Compute(d, e, p0, s0, p1, s1, sb, tb, candidates);
                LastRerouted++;
            }
            foreach (var e in _entries.Keys.Where(k => !live.Contains(k)).ToList()) { _entries.Remove(e); _curved.Remove(e); }
        }
    }

    private static bool Overlaps(Bounds a, Bounds b) => a.X <= b.Right && b.X <= a.Right && a.Y <= b.Bottom && b.Y <= a.Bottom;

    // ================================================================ ports

    private static Pt Normal(Side s) => s switch { Side.Top => new(0, -1), Side.Right => new(1, 0), Side.Bottom => new(0, 1), _ => new(-1, 0) };

    /// <summary>The sides through which a connector leaves <paramref name="s"/> and enters <paramref name="t"/>.</summary>
    public static (Side Source, Side Target) ChooseSides(Bounds s, Bounds t)
    {
        var vGap = Math.Max(t.Y - s.Bottom, s.Y - t.Bottom);
        var hGap = Math.Max(t.X - s.Right, s.X - t.Right);
        var sc = s.Center; var tc = t.Center;
        if (vGap >= hGap)
            return tc.Y >= sc.Y ? (Side.Bottom, Side.Top) : (Side.Top, Side.Bottom);
        return tc.X >= sc.X ? (Side.Right, Side.Left) : (Side.Left, Side.Right);
    }

    private static bool Spreads(DiagramNode n) => Notation.ShapeOf(n.Element) is NodeShape.Rectangle or NodeShape.Rounded or NodeShape.Folder or NodeShape.Note
        or NodeShape.Swimlane or NodeShape.Pool or NodeShape.Fragment or NodeShape.Timeline or NodeShape.InteractionUse or NodeShape.InlineInteraction;

    private static Dictionary<DiagramEdge, (Pt P0, Side S0, Pt P1, Side S1)> AssignPorts(List<DiagramEdge> edges, Dictionary<DiagramNode, Bounds> b)
    {
        var sides = new Dictionary<DiagramEdge, (Side S, Side T)>(ReferenceEqualityComparer.Instance);
        var groups = new Dictionary<(DiagramNode, Side), List<(DiagramEdge E, bool IsSource, double Key)>>();
        foreach (var e in edges)
        {
            var sb = b[e.Source]; var tb = b[e.Target];
            var (ss, ts) = ChooseSides(sb, tb);
            sides[e] = (ss, ts);
            Add(e.Source, ss, e, true, ss is Side.Top or Side.Bottom ? tb.Center.X : tb.Center.Y);
            Add(e.Target, ts, e, false, ts is Side.Top or Side.Bottom ? sb.Center.X : sb.Center.Y);
        }

        var at = new Dictionary<(DiagramEdge, bool), Pt>();
        var alone = new HashSet<(DiagramEdge, bool)>();
        foreach (var ((node, side), list) in groups)
        {
            list.Sort((x, y) => x.Key != y.Key ? x.Key.CompareTo(y.Key) : string.CompareOrdinal(x.E.Id, y.E.Id) is var c && c != 0 ? c : x.IsSource.CompareTo(y.IsSource));
            var nb = b[node];
            var spread = Spreads(node);
            for (var i = 0; i < list.Count; i++)
            {
                var frac = spread ? (i + 1.0) / (list.Count + 1) : 0.5;
                at[(list[i].E, list[i].IsSource)] = PointOnSide(nb, side, frac);
                if (list.Count == 1 && spread) alone.Add((list[i].E, list[i].IsSource));
            }
        }

        var result = new Dictionary<DiagramEdge, (Pt, Side, Pt, Side)>(ReferenceEqualityComparer.Instance);
        foreach (var e in edges)
        {
            var (ss, ts) = sides[e];
            var p0 = at[(e, true)]; var p1 = at[(e, false)];
            // A lone connector between shapes that overlap across the gap goes straight through the overlap's middle.
            if (alone.Contains((e, true)) && alone.Contains((e, false)))
            {
                var sb = b[e.Source]; var tb = b[e.Target];
                if (ss is Side.Top or Side.Bottom)
                {
                    double lo = Math.Max(sb.X, tb.X) + 8, hi = Math.Min(sb.Right, tb.Right) - 8;
                    if (hi - lo >= 0) { var x = Math.Round((lo + hi) / 2); p0 = p0 with { X = x }; p1 = p1 with { X = x }; }
                }
                else
                {
                    double lo = Math.Max(sb.Y, tb.Y) + 8, hi = Math.Min(sb.Bottom, tb.Bottom) - 8;
                    if (hi - lo >= 0) { var y = Math.Round((lo + hi) / 2); p0 = p0 with { Y = y }; p1 = p1 with { Y = y }; }
                }
            }
            result[e] = (p0, ss, p1, ts);
        }
        return result;

        void Add(DiagramNode n, Side s, DiagramEdge e, bool isSource, double key)
        {
            if (!groups.TryGetValue((n, s), out var l)) groups[(n, s)] = l = new();
            l.Add((e, isSource, key));
        }
    }

    private static Pt PointOnSide(Bounds b, Side s, double frac)
    {
        const double inset = 12;
        double Along(double start, double len) => len <= 2 * inset ? start + len / 2 : start + Math.Clamp(frac * len, inset, len - inset);
        return s switch
        {
            Side.Top => new(Math.Round(Along(b.X, b.Width), 1), b.Y),
            Side.Bottom => new(Math.Round(Along(b.X, b.Width), 1), b.Bottom),
            Side.Left => new(b.X, Math.Round(Along(b.Y, b.Height), 1)),
            _ => new(b.Right, Math.Round(Along(b.Y, b.Height), 1)),
        };
    }

    // ================================================================ search

    private static Entry Compute(Diagram d, DiagramEdge e, Pt p0, Side s0, Pt p1, Side s1, Bounds sb, Bounds tb, List<(DiagramNode Node, Bounds B)> candidates)
    {
        var n0 = Normal(s0); var n1 = Normal(s1);
        // Shapes closer than two stubs apart: the stubs meet half way instead of crossing.
        var stub = Stub;
        var gap = (p1.X - p0.X) * n0.X + (p1.Y - p0.Y) * n0.Y;
        if (Math.Abs(n0.X + n1.X) < 0.01 && Math.Abs(n0.Y + n1.Y) < 0.01 && gap > 0 && gap < 2 * Stub) stub = gap / 2;
        var a = new Pt(p0.X + n0.X * stub, p0.Y + n0.Y * stub);
        var z = new Pt(p1.X + n1.X * stub, p1.Y + n1.Y * stub);

        var exclude = new HashSet<DiagramNode>(ReferenceEqualityComparer.Instance);
        for (var p = d.ParentOf(e.Source); p != null && exclude.Add(p); p = d.ParentOf(p)) { }
        for (var p = d.ParentOf(e.Target); p != null && exclude.Add(p); p = d.ParentOf(p)) { }

        var core = Union(Union(sb, tb), Bounds.FromPoints(a, z));
        foreach (var pad in new[] { 100.0, 400.0, double.PositiveInfinity })
        {
            var region = double.IsPositiveInfinity(pad)
                ? Inflate(candidates.Select(c => c.B).Aggregate(core, Union), 3 * Margin)
                : Inflate(core, pad);
            var obstacles = new List<Bounds>();
            foreach (var (n, b) in candidates)
            {
                if (exclude.Contains(n)) continue;
                var ib = Inflate(b, Margin);
                if (!Overlaps(ib, region) || StrictlyInside(a, ib) || StrictlyInside(z, ib)) continue;
                obstacles.Add(ib);
            }
            var mid = Search(a, n0, z, new Pt(-n1.X, -n1.Y), obstacles, region);
            if (mid == null) continue;
            var path = new List<Pt>(mid.Count + 2) { p0 };
            path.AddRange(mid);
            path.Add(p1);
            return new Entry(p0, s0, p1, s1, sb, tb, region, Simplify(path));
        }
        // Boxed in: fall back to the simple three-segment route (re-routed on any change).
        return new Entry(p0, s0, p1, s1, sb, tb, new Bounds(-1e9, -1e9, 2e9, 2e9), Notation.Orthogonal(sb, tb));
    }

    private static Bounds Union(Bounds a, Bounds b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y);
        return new Bounds(x, y, Math.Max(a.Right, b.Right) - x, Math.Max(a.Bottom, b.Bottom) - y);
    }

    private static Bounds Inflate(Bounds b, double m) => new(b.X - m, b.Y - m, b.Width + 2 * m, b.Height + 2 * m);

    private static bool StrictlyInside(Pt p, Bounds b) => p.X > b.X + 0.01 && p.X < b.Right - 0.01 && p.Y > b.Y + 0.01 && p.Y < b.Bottom - 0.01;

    private static readonly (int Dx, int Dy)[] Dirs = [(1, 0), (0, 1), (-1, 0), (0, -1)];

    private static int DirOf(Pt v) => v.X > 0.5 ? 0 : v.Y > 0.5 ? 1 : v.X < -0.5 ? 2 : 3;

    /// <summary>
    /// A* over the sparse grid of obstacle boundary lines. Starts at <paramref name="a"/> heading <paramref name="startDir"/>
    /// and must reach <paramref name="z"/> heading <paramref name="endDir"/>. Returns the bend points, or null.
    /// </summary>
    private static List<Pt>? Search(Pt a, Pt startDir, Pt z, Pt endDir, List<Bounds> obstacles, Bounds region)
    {
        var xsList = new List<double> { region.X, region.Right, a.X, z.X, (a.X + z.X) / 2 };
        var ysList = new List<double> { region.Y, region.Bottom, a.Y, z.Y, (a.Y + z.Y) / 2 };
        foreach (var o in obstacles)
        {
            if (o.X > region.X && o.X < region.Right) xsList.Add(o.X);
            if (o.Right > region.X && o.Right < region.Right) xsList.Add(o.Right);
            if (o.Y > region.Y && o.Y < region.Bottom) ysList.Add(o.Y);
            if (o.Bottom > region.Y && o.Bottom < region.Bottom) ysList.Add(o.Bottom);
        }
        var xs = Lines(xsList); var ys = Lines(ysList);
        int nx = xs.Length, ny = ys.Length;
        var ia = Index(xs, a.X); var ja = Index(ys, a.Y);
        var iz = Index(xs, z.X); var jz = Index(ys, z.Y);
        if (ia < 0 || ja < 0 || iz < 0 || jz < 0) return null;

        var inside = new bool[nx * ny];
        var hBlocked = new bool[nx * ny];   // segment (i,j)-(i+1,j)
        var vBlocked = new bool[nx * ny];   // segment (i,j)-(i,j+1)
        const double eps = 0.01;
        foreach (var o in obstacles)
        {
            // Lines strictly inside the obstacle, and gaps between lines whose open interval overlaps it.
            int i0 = Lower(xs, o.X + eps), i1 = Upper(xs, o.Right - eps);
            int j0 = Lower(ys, o.Y + eps), j1 = Upper(ys, o.Bottom - eps);
            int g0 = Math.Max(0, i0 - 1), g1 = Math.Min(nx - 2, i1);   // x gaps overlapping (o.X, o.Right)
            int h0 = Math.Max(0, j0 - 1), h1 = Math.Min(ny - 2, j1);
            for (var j = j0; j <= j1; j++)
            {
                for (var i = i0; i <= i1; i++) inside[j * nx + i] = true;
                for (var i = g0; i <= g1; i++)
                    if (xs[i + 1] > o.X + eps && xs[i] < o.Right - eps) hBlocked[j * nx + i] = true;
            }
            for (var i = i0; i <= i1; i++)
                for (var j = h0; j <= h1; j++)
                    if (ys[j + 1] > o.Y + eps && ys[j] < o.Bottom - eps) vBlocked[j * nx + i] = true;
        }
        if (inside[ja * nx + ia] || inside[jz * nx + iz]) return null;

        var midX = (a.X + z.X) / 2; var midY = (a.Y + z.Y) / 2;
        var d0 = DirOf(startDir); var dEnd = DirOf(endDir);
        var total = nx * ny * 4;
        var g = new double[total + 1];
        Array.Fill(g, double.PositiveInfinity);
        var parent = new int[total + 1];
        var queue = new PriorityQueue<int, double>();
        var start = (ja * nx + ia) * 4 + d0;
        g[start] = 0;
        parent[start] = -1;
        queue.Enqueue(start, H(ia, ja));
        var goal = total;

        while (queue.TryDequeue(out var s, out var f))
        {
            if (s == goal) break;
            var v = s >> 2; var dir = s & 3;
            var gs = g[s];
            if (f > gs + H(v % nx, v / nx) + 1e-6) continue;   // stale entry
            int i = v % nx, j = v / nx;
            if (i == iz && j == jz && dir != (dEnd + 2) % 4)
            {
                var cost = gs + (dir == dEnd ? 0 : BendPenalty);
                if (cost < g[goal]) { g[goal] = cost; parent[goal] = s; queue.Enqueue(goal, cost); }
            }
            for (var nd = 0; nd < 4; nd++)
            {
                if (nd == (dir + 2) % 4) continue;   // no U-turns
                var (dx, dy) = Dirs[nd];
                int ni = i + dx, nj = j + dy;
                if (ni < 0 || nj < 0 || ni >= nx || nj >= ny) continue;
                var blocked = dx != 0 ? hBlocked[j * nx + Math.Min(i, ni)] : vBlocked[Math.Min(j, nj) * nx + i];
                if (blocked || inside[nj * nx + ni]) continue;
                var len = dx != 0 ? Math.Abs(xs[ni] - xs[i]) : Math.Abs(ys[nj] - ys[j]);
                // A slight pull towards the middle channel between the two shapes keeps Z-routes centred.
                var cost = gs + len * (1 + 0.001 * (dx != 0 ? Math.Abs(ys[j] - midY) : Math.Abs(xs[i] - midX))) + (nd == dir ? 0 : BendPenalty);
                var ns = (nj * nx + ni) * 4 + nd;
                if (cost < g[ns] - 1e-9)
                {
                    g[ns] = cost;
                    parent[ns] = s;
                    queue.Enqueue(ns, cost + H(ni, nj));
                }
            }
        }
        if (double.IsPositiveInfinity(g[goal])) return null;

        var pts = new List<Pt>();
        for (var s = parent[goal]; s >= 0; s = parent[s])
        {
            var v = s >> 2;
            pts.Add(new Pt(xs[v % nx], ys[v / nx]));
            if (s == start) break;
        }
        pts.Reverse();
        return pts;

        double H(int i, int j)
        {
            double x = xs[i], y = ys[j];
            var h = Math.Abs(x - z.X) + Math.Abs(y - z.Y);
            return Math.Abs(x - z.X) > 0.01 && Math.Abs(y - z.Y) > 0.01 ? h + BendPenalty : h;
        }
    }

    private static double[] Lines(List<double> values)
    {
        values.Sort();
        var result = new List<double>(values.Count);
        foreach (var v in values)
            if (result.Count == 0 || v - result[^1] > 0.5) result.Add(v);
        return result.ToArray();
    }

    private static int Index(double[] lines, double v)
    {
        var lo = Lower(lines, v - 0.5);
        return lo < lines.Length && Math.Abs(lines[lo] - v) <= 0.5 ? lo : -1;
    }

    /// <summary>First index with lines[i] &gt;= v.</summary>
    private static int Lower(double[] lines, double v)
    {
        int lo = 0, hi = lines.Length;
        while (lo < hi) { var m = (lo + hi) / 2; if (lines[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    /// <summary>Last index with lines[i] &lt;= v.</summary>
    private static int Upper(double[] lines, double v)
    {
        int lo = 0, hi = lines.Length;
        while (lo < hi) { var m = (lo + hi) / 2; if (lines[m] <= v) lo = m + 1; else hi = m; }
        return lo - 1;
    }

    /// <summary>Removes repeated and collinear points.</summary>
    public static IReadOnlyList<Pt> Simplify(IReadOnlyList<Pt> pts)
    {
        var r = new List<Pt>(pts.Count);
        foreach (var p in pts)
        {
            if (r.Count > 0 && Math.Abs(r[^1].X - p.X) < 0.01 && Math.Abs(r[^1].Y - p.Y) < 0.01) continue;
            if (r.Count >= 2)
            {
                var a = r[^2]; var b = r[^1];
                var collinear = Math.Abs(a.X - b.X) < 0.01 && Math.Abs(b.X - p.X) < 0.01 || Math.Abs(a.Y - b.Y) < 0.01 && Math.Abs(b.Y - p.Y) < 0.01;
                if (collinear) r[^1] = p;
                else r.Add(p);
            }
            else r.Add(p);
        }
        return r;
    }

    // ================================================================ curves and line jumps

    /// <summary>A smooth curve following a polyline (Chaikin corner cutting), keeping both end points and end directions.</summary>
    public static IReadOnlyList<Pt> Smooth(IReadOnlyList<Pt> pts, int iterations = 3)
    {
        if (pts.Count < 3) return pts;
        IReadOnlyList<Pt> cur = pts;
        for (var k = 0; k < iterations; k++)
        {
            var next = new List<Pt>(cur.Count * 2) { cur[0] };
            for (var i = 0; i < cur.Count - 1; i++)
            {
                Pt p = cur[i], q = cur[i + 1];
                if (i > 0) next.Add(new Pt(0.75 * p.X + 0.25 * q.X, 0.75 * p.Y + 0.25 * q.Y));
                if (i < cur.Count - 2) next.Add(new Pt(0.25 * p.X + 0.75 * q.X, 0.25 * p.Y + 0.75 * q.Y));
            }
            next.Add(cur[^1]);
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// Where a path crosses earlier axis-aligned segments (for line jumps): for each segment index of <paramref name="pts"/>,
    /// the crossing points in path order. Only crossings away from segment ends count.
    /// </summary>
    public static List<(int Segment, Pt At)> Hops(IReadOnlyList<Pt> pts, IReadOnlyList<(Pt A, Pt B)> earlier, double radius = 5)
    {
        var hops = new List<(int, Pt)>();
        for (var s = 1; s < pts.Count; s++)
        {
            Pt a = pts[s - 1], b = pts[s];
            var horizontal = Math.Abs(a.Y - b.Y) < 0.01;
            var vertical = Math.Abs(a.X - b.X) < 0.01;
            if (!horizontal && !vertical) continue;
            var found = new List<Pt>();
            foreach (var (c, dd) in earlier)
            {
                if (horizontal && Math.Abs(c.X - dd.X) < 0.01)
                {
                    double x = c.X, y0 = Math.Min(c.Y, dd.Y), y1 = Math.Max(c.Y, dd.Y);
                    if (a.Y > y0 + 1 && a.Y < y1 - 1 && x > Math.Min(a.X, b.X) + radius + 2 && x < Math.Max(a.X, b.X) - radius - 2) found.Add(new Pt(x, a.Y));
                }
                else if (vertical && Math.Abs(c.Y - dd.Y) < 0.01)
                {
                    double y = c.Y, x0 = Math.Min(c.X, dd.X), x1 = Math.Max(c.X, dd.X);
                    if (a.X > x0 + 1 && a.X < x1 - 1 && y > Math.Min(a.Y, b.Y) + radius + 2 && y < Math.Max(a.Y, b.Y) - radius - 2) found.Add(new Pt(a.X, y));
                }
            }
            var ordered = found.OrderBy(p => Notation.Dist(a, p)).ToList();
            Pt? last = null;
            foreach (var p in ordered)
            {
                if (last is { } l && Notation.Dist(l, p) < 2 * radius + 2) continue;
                hops.Add((s, p));
                last = p;
            }
        }
        return hops;
    }

    /// <summary>Axis-aligned segments of a path (inputs for <see cref="Hops"/>).</summary>
    public static IEnumerable<(Pt A, Pt B)> Segments(IReadOnlyList<Pt> pts)
    {
        for (var i = 1; i < pts.Count; i++)
            if (Math.Abs(pts[i - 1].X - pts[i].X) < 0.01 || Math.Abs(pts[i - 1].Y - pts[i].Y) < 0.01) yield return (pts[i - 1], pts[i]);
    }
}
