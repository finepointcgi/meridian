using System.Globalization;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>What a <see cref="StateMachineEngine"/> reports to its host's log or trace.</summary>
public enum MachineEventKind { Enter, Exit, Transition, Info, Warning, Complete, Error }

/// <summary>
/// What the engine needs from the simulator that runs it: action execution and guard evaluation over the simulator's
/// variables, a log, and notifications when states are entered and exited (timers, timeline bars).
/// </summary>
public interface IStateMachineHost
{
    void Exec(string? statements, string what);
    bool Guard(Relationship transition);
    void Log(MachineEventKind kind, Element? element, string text);
    void Entered(Element state) { }
    void Exited(Element state) { }
}

/// <summary>A model error found while running (a choice with no enabled branch, a pseudostate chain that never ends).</summary>
public sealed class StateMachineError(string message) : Exception(message);

/// <summary>
/// One region of a composite state (or of the state machine itself). Meridian has no region element: regions are the groups
/// of a container's substates that transitions connect, with each initial pseudostate starting its own region and the
/// targets of one fork (or sources of one join) always in different regions. See <see cref="StateMachineEngine.Regions"/>.
/// </summary>
public sealed class MachineRegion
{
    public required Element Container { get; init; }
    public required int Index { get; init; }
    public required IReadOnlyList<Element> Members { get; init; }
    public Element? Initial => Members.FirstOrDefault(m => m.Kind == ElementKind.InitialState);
    public override string ToString() => $"{Container.Name}[{Index + 1}]: {string.Join(", ", Members.Select(m => m.Name))}";
}

/// <summary>
/// UML 2.5 state machine semantics shared by the stepping simulator (<see cref="StateMachineSimulator"/>) and the timed one
/// (<see cref="DiscreteEventSimulation"/>): an active <b>configuration</b> of states (several at once in orthogonal regions),
/// composite states with default entry, run-to-completion transitions with inner-first priority and conflict resolution, and
/// the pseudostates of <see cref="Pseudostates"/>:
/// <list type="bullet">
/// <item><b>choice</b>: guards are evaluated when the choice is reached (after the exits and effects before it); <c>[else]</c> is
/// taken when no other guard holds; none at all is a <see cref="StateMachineError"/>.</item>
/// <item><b>junction</b>: a static branch, chosen with the variables as they were before the transition fired; a transition
/// whose junction chain has no enabled branch is not enabled.</item>
/// <item><b>fork</b> enters every target (the orthogonal regions they are in), <b>join</b> waits until a transition has
/// arrived from each of its incoming transitions, then leaves.</item>
/// <item><b>shallow / deep history</b> restore the substates active when the composite was last exited (deep: the whole
/// nested configuration); without history (or if the region had finished) the history's own transition, else the default
/// entry, is used.</item>
/// <item><b>entry / exit points</b> enter a composite through the point's transitions, and leave it through the exit
/// point's transition after exiting everything inside.</item>
/// <item><b>terminate</b> ends the run at once, without exit behaviors.</item>
/// </list>
/// Guards and behaviors are evaluated by the host (with <see cref="Expressions"/>).
/// </summary>
public sealed class StateMachineEngine
{
    private readonly IStateMachineHost _host;
    private readonly List<Element> _active = new();
    private readonly HashSet<Element> _activeSet = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Element, (List<Element> Shallow, List<Element> Deep)> _history = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Element, HashSet<Relationship>> _joinArrivals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Element, List<MachineRegion>> _regions = new(ReferenceEqualityComparer.Instance);
    private const int MaxChain = 100;   // pseudostate segments in one compound transition

    public StateMachineEngine(ModelRepository model, Element machine, IStateMachineHost host)
    {
        Model = model;
        Machine = machine;
        _host = host;
    }

    public ModelRepository Model { get; }
    public Element Machine { get; }
    public bool Finished { get; private set; }
    public bool Terminated { get; private set; }
    /// <summary>Set when the run stopped on a model error (see <see cref="StateMachineError"/>).</summary>
    public string? Error { get; private set; }

    /// <summary>Active states, outermost first, in the order they were entered.</summary>
    public IReadOnlyList<Element> ActiveStates => _active.ToList();

    /// <summary>The innermost active states, one per active orthogonal region, in model order.</summary>
    public IReadOnlyList<Element> ActiveLeaves => _active.Where(s => !_active.Any(c => ReferenceEquals(c.Owner, s))).OrderBy(ModelOrder, StringComparer.Ordinal).ToList();

    private string ModelOrder(Element s)
    {
        var parts = new List<string>();
        for (var e = s; e.Owner is { } o && !ReferenceEquals(e, Machine); e = o)
            parts.Add(o.OwnedElements.ToList().IndexOf(e).ToString("D6", CultureInfo.InvariantCulture));
        parts.Reverse();
        return string.Join(".", parts);
    }

    /// <summary>Joins that have some, but not all, of their incoming transitions.</summary>
    public IReadOnlyList<Element> WaitingJoins => _joinArrivals.Keys.ToList();

    /// <summary>Everything to highlight: active states and waiting joins.</summary>
    public IReadOnlyList<Element> Active => _active.Concat(_joinArrivals.Keys).ToList();

    public bool IsActive(Element e) => _activeSet.Contains(e);

    /// <summary>The active leaves by path, e.g. "Operating.Heating, Operating.Monitoring".</summary>
    public string Configuration => ActiveLeaves.Count == 0 ? "(none)" : string.Join(", ", ActiveLeaves.Select(PathOf));

    /// <summary>The substates the last exit of <paramref name="composite"/> recorded for history (shallow: direct substates; deep: leaves).</summary>
    public IReadOnlyList<Element> RecordedHistory(Element composite, bool deep = false) =>
        _history.TryGetValue(composite, out var h) ? deep ? h.Deep : h.Shallow : [];

    private string PathOf(Element s)
    {
        var parts = new List<string>();
        for (var e = s; e != null && !ReferenceEquals(e, Machine); e = e.Owner) parts.Add(Label(e));
        parts.Reverse();
        return string.Join(".", parts);
    }

    public static string Label(Element e) => string.IsNullOrEmpty(e.Name)
        ? e.Kind == ElementKind.Pseudostate ? Pseudostates.Label(Pseudostates.KindOf(e)) : Metamodel.DisplayName(e.Kind)
        : e.Name;

    // ================================================================== structure

    private static bool IsRegionMember(Element e) =>
        e.Kind is ElementKind.State or ElementKind.FinalState or ElementKind.InitialState
        || e.Kind == ElementKind.Pseudostate && Pseudostates.KindOf(e) is Pseudostates.Choice or Pseudostates.Junction;

    private static string? Ps(Element e) => Pseudostates.KindOf(e);

    /// <summary>
    /// The regions of a composite state or state machine, inferred from its substates: transitions between substates (or
    /// their descendants and connection points) put them in the same region; each initial pseudostate starts a region of
    /// its own; the targets of one fork (and the sources of one join) are kept in different regions; a group of substates
    /// that no initial reaches joins the first region it may share. A simple state has no regions.
    /// </summary>
    public IReadOnlyList<MachineRegion> Regions(Element container)
    {
        if (_regions.TryGetValue(container, out var cached)) return cached;
        var members = container.OwnedElements.Where(IsRegionMember).ToList();
        var result = new List<MachineRegion>();
        _regions[container] = result;
        if (members.Count == 0) return result;
        var index = members.Select((m, i) => (m, i)).ToDictionary(x => x.m, x => x.i);   // elements compare by reference
        var parent = Enumerable.Range(0, members.Count).ToArray();
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        int? Map(Element v)
        {
            for (var x = v; x != null && !ReferenceEquals(x, Machine); x = x.Owner)
                if (ReferenceEquals(x.Owner, container)) return index.TryGetValue(x, out var i) ? i : null;
            return null;
        }
        var vertices = container.Descendants().Where(e => e.Kind is ElementKind.State or ElementKind.FinalState or ElementKind.InitialState or ElementKind.Pseudostate).ToList();
        var apart = new List<(int, int)>();
        foreach (var v in vertices)
        {
            var ps = Ps(v);
            foreach (var t in Model.Outgoing(v, ElementKind.Transition))
            {
                if (ps is Pseudostates.Fork or Pseudostates.Join || Ps(t.Target) is Pseudostates.Join || ps is Pseudostates.ShallowHistory or Pseudostates.DeepHistory) continue;
                if (Map(v) is { } a && Map(t.Target) is { } b && a != b) parent[Find(a)] = Find(b);
            }
        }
        // Forks and joins anywhere: their targets / sources inside this container belong to different regions.
        foreach (var fj in Model.AllElements.Where(e => e.Kind == ElementKind.Pseudostate && Ps(e) is Pseudostates.Fork or Pseudostates.Join))
        {
            var ends = (Ps(fj) == Pseudostates.Fork ? Model.Outgoing(fj, ElementKind.Transition).Select(t => t.Target) : Model.Incoming(fj, ElementKind.Transition).Select(t => t.Source))
                .Select(Map).OfType<int>().Distinct().ToList();
            for (var i = 0; i < ends.Count; i++)
                for (var j = i + 1; j < ends.Count; j++) apart.Add((ends[i], ends[j]));
        }
        var components = Enumerable.Range(0, members.Count).GroupBy(Find).Select(g => g.ToList()).OrderBy(g => g.Min()).ToList();
        int Comp(int member) => components.FindIndex(c => c.Contains(member));
        var apartComps = apart.Select(p => (Comp(p.Item1), Comp(p.Item2))).ToList();
        bool Apart(int c1, int c2) => apartComps.Any(p => p == (c1, c2) || p == (c2, c1));
        var groups = new List<List<int>>();   // regions as lists of component indexes
        for (var c = 0; c < components.Count; c++)
            if (components[c].Any(i => members[i].Kind == ElementKind.InitialState)) groups.Add([c]);
        for (var c = 0; c < components.Count; c++)
        {
            if (components[c].Any(i => members[i].Kind == ElementKind.InitialState)) continue;
            var host = groups.FirstOrDefault(g => g.All(o => !Apart(o, c)));
            if (host != null) host.Add(c); else groups.Add([c]);
        }
        var ordered = groups.Select(g => g.SelectMany(c => components[c]).OrderBy(i => i).ToList()).OrderBy(g => g[0]).ToList();
        for (var r = 0; r < ordered.Count; r++)
            result.Add(new MachineRegion { Container = container, Index = r, Members = ordered[r].Select(i => members[i]).ToList() });
        return result;
    }

    private MachineRegion? RegionOf(Element container, Element child) => Regions(container).FirstOrDefault(r => r.Members.Contains(child));

    /// <summary>Where a vertex sits for entering and exiting: entry / exit points and history on their composite's border.</summary>
    private static Element Pos(Element v) =>
        v.Kind == ElementKind.Pseudostate && Ps(v) is Pseudostates.EntryPoint or Pseudostates.ExitPoint or Pseudostates.ShallowHistory or Pseudostates.DeepHistory && v.Owner != null
            ? v.Owner! : v;

    private bool IsProperAncestor(Element a, Element e)
    {
        for (var x = e.Owner; x != null; x = x.Owner)
        {
            if (ReferenceEquals(x, a)) return true;
            if (ReferenceEquals(x, Machine)) return false;
        }
        return false;
    }

    private Element? ChildOf(Element container, Element e)
    {
        for (var x = e; x != null && !ReferenceEquals(x, Machine); x = x.Owner)
            if (ReferenceEquals(x.Owner, container)) return x;
        return null;
    }

    /// <summary>The innermost state (or the machine) properly containing <paramref name="a"/> and every one of <paramref name="others"/>.</summary>
    private Element Domain(Element a, params Element[] others)
    {
        for (var c = a.Owner; c != null; c = c.Owner)
        {
            if (ReferenceEquals(c, Machine)) return Machine;
            if (others.All(o => IsProperAncestor(c, o))) return c;
        }
        return Machine;
    }

    private Element ActiveDomain(Element d)
    {
        while (!ReferenceEquals(d, Machine) && !IsActive(d) && d.Owner != null) d = d.Owner;
        return d;
    }

    /// <summary>Where a transition's target makes the exit stop: entry / exit points and history count as their composite.</summary>
    private static Element Anchor(Element target) => Pos(target);

    private bool Completed(Element state)
    {
        if (!ReferenceEquals(state, Machine) && !IsActive(state)) return false;
        var regions = Regions(state);
        if (regions.Count == 0) return true;
        var anyFinal = false;
        foreach (var r in regions)
        {
            if (r.Members.Any(m => m.Kind == ElementKind.FinalState && IsActive(m))) { anyFinal = true; continue; }
            if (r.Members.Any(IsActive) || r.Initial != null) return false;
        }
        return anyFinal;
    }

    private int Depth(Element e)
    {
        var d = 0;
        for (var x = e.Owner; x != null && !ReferenceEquals(x, Machine); x = x.Owner) d++;
        return d;
    }

    // ================================================================== public operations

    public void Start()
    {
        _active.Clear(); _activeSet.Clear(); _history.Clear(); _joinArrivals.Clear();
        Finished = Terminated = false;
        Error = null;
        Guarded(() =>
        {
            var regions = Regions(Machine);
            if (regions.All(r => r.Initial is null)) { _host.Log(MachineEventKind.Warning, Machine, "  ⚠ no initial pseudostate"); Finished = true; return; }
            if (regions.Where(r => r.Initial != null).All(r => !Model.Outgoing(r.Initial!, ElementKind.Transition).Any()))
            {
                _host.Log(MachineEventKind.Warning, Machine, "  ⚠ initial pseudostate has no transition");
                Finished = true;
                return;
            }
            foreach (var r in regions) DefaultEnterRegion(Machine, r, 0);
        });
    }

    /// <summary>
    /// The transitions that would fire now for a trigger: from active states, matching <paramref name="trigger"/>, guard true,
    /// junction chain enabled; inner states first, without two transitions that exit the same state. With
    /// <paramref name="completion"/>, only from states that have completed (a simple state, or a composite whose regions
    /// all reached a final state).
    /// </summary>
    public IReadOnlyList<Relationship> Enabled(Func<Relationship, bool> trigger, bool completion = false) =>
        Finished ? [] : Select(trigger, completion).Select(c => c.T).ToList();

    /// <summary>Fires every selected transition for one event (orthogonal regions react together). Returns how many fired.</summary>
    public int Dispatch(Func<Relationship, bool> trigger, Func<Relationship, string> describe)
    {
        if (Finished) return 0;
        var n = 0;
        Guarded(() =>
        {
            foreach (var (t, path) in Select(trigger, false))
            {
                if (Finished) break;
                if (!IsActive(t.Source)) continue;
                n++;
                Fire(t, path, describe(t));
            }
        });
        return n;
    }

    /// <summary>Fires the first enabled transition for a trigger (completion transitions: one per step). False when none is enabled.</summary>
    public bool FireFirst(Func<Relationship, bool> trigger, bool completion, Func<Relationship, string> describe)
    {
        if (Finished) return false;
        var fired = false;
        Guarded(() =>
        {
            if (Select(trigger, completion).FirstOrDefault() is { T: { } t } c)
            {
                fired = true;
                Fire(t, c.Path, describe(t));
            }
        });
        return fired;
    }

    /// <summary>Fires one particular transition (a time event) if its source is still active and its guard holds.</summary>
    public bool TryFire(Relationship t, string why)
    {
        if (Finished || !IsActive(t.Source) || !GuardOk(t)) return false;
        Queue<Relationship>? path = null;
        if (Ps(t.Target) == Pseudostates.Junction && (path = ResolveJunctions(t)) is null) return false;
        Guarded(() => Fire(t, path, why));
        return true;
    }

    private void Guarded(Action a)
    {
        try { a(); }
        catch (StateMachineError ex)
        {
            Error = ex.Message;
            Finished = true;
            _host.Log(MachineEventKind.Error, null, "✖ " + ex.Message);
        }
    }

    // ================================================================== selection

    private bool GuardOk(Relationship t) => t.Get(FieldKeys.Guard).Trim().Length == 0 || _host.Guard(t);

    private static bool IsElse(Relationship t) => t.Get(FieldKeys.Guard).Trim().Trim('[', ']').Trim().Equals("else", StringComparison.OrdinalIgnoreCase);

    private List<(Relationship T, Queue<Relationship>? Path)> Select(Func<Relationship, bool> trigger, bool completion)
    {
        var candidates = new List<(Relationship T, Queue<Relationship>? Path, int Depth, int Order)>();
        var order = 0;
        foreach (var s in _active.ToList())
        {
            if (s.Kind != ElementKind.State) continue;
            if (completion && !Completed(s)) continue;
            foreach (var t in Model.Outgoing(s, ElementKind.Transition))
            {
                if (!trigger(t) || IsElse(t) || !GuardOk(t)) continue;
                Queue<Relationship>? path = null;
                if (Ps(t.Target) == Pseudostates.Junction && (path = ResolveJunctions(t)) is null) continue;
                candidates.Add((t, path, Depth(s), order++));
            }
        }
        var chosen = new List<(Relationship, Queue<Relationship>?)>();
        var exited = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        foreach (var c in candidates.OrderByDescending(c => c.Depth).ThenBy(c => c.Order))
        {
            var exits = ExitSet(c.T, c.Path);
            if (exits.Overlaps(exited)) continue;
            chosen.Add((c.T, c.Path));
            exited.UnionWith(exits);
        }
        return chosen;
    }

    /// <summary>The active states a transition would exit (for conflict resolution).</summary>
    private HashSet<Element> ExitSet(Relationship t, Queue<Relationship>? path)
    {
        var target = path is { Count: > 0 } ? path.Last().Target : t.Target;
        var set = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        if (Ps(target) == Pseudostates.Terminate) { set.UnionWith(_active); return set; }
        Element? root;
        if (Ps(target) == Pseudostates.Join) root = ChildOf(JoinScope(target), t.Source);
        else root = ChildOf(ActiveDomain(Domain(Pos(t.Source), Anchor(target))), t.Source);
        root ??= t.Source;
        set.Add(t.Source);
        foreach (var a in _active)
            if (ReferenceEquals(a, root) || IsProperAncestor(root, a)) set.Add(a);
        return set;
    }

    /// <summary>A junction chain chosen with the current variables (static conditional branch); null when a junction has no enabled branch.</summary>
    private Queue<Relationship>? ResolveJunctions(Relationship t)
    {
        var path = new Queue<Relationship>();
        var v = t.Target;
        while (Ps(v) == Pseudostates.Junction)
        {
            var next = Choose(v);
            if (next is null || path.Count > MaxChain) return null;
            path.Enqueue(next);
            v = next.Target;
        }
        return path;
    }

    private Relationship? Choose(Element pseudo)
    {
        var outs = Model.Outgoing(pseudo, ElementKind.Transition).ToList();
        return outs.FirstOrDefault(f => !IsElse(f) && GuardOk(f)) ?? outs.FirstOrDefault(IsElse);
    }

    private StateMachineError NoBranch(Element pseudo)
    {
        var outs = Model.Outgoing(pseudo, ElementKind.Transition).ToList();
        var guards = outs.Select(o => o.Get(FieldKeys.Guard).Trim()).Select(g => g.Length == 0 ? "(no guard)" : $"[{g}]").ToList();
        var what = $"{Pseudostates.Label(Ps(pseudo)).ToLowerInvariant()} '{Label(pseudo)}'";
        return new StateMachineError(outs.Count == 0
            ? $"The {what} has no outgoing transition"
            : $"At the {what} no outgoing guard is true and there is no [else] branch (guards: {string.Join(", ", guards)})");
    }

    // ================================================================== firing

    private void Fire(Relationship t, Queue<Relationship>? path, string why)
    {
        var guard = t.Get(FieldKeys.Guard) is { Length: > 0 } g ? $" [{g}]" : "";
        _host.Log(MachineEventKind.Transition, t, $"  ⇢ {why}{guard} → {Label(t.Target)}");
        Traverse(t, t.Source, path, 0);
    }

    private void Traverse(Relationship seg, Element sourcePos, Queue<Relationship>? path, int depth)
    {
        if (++depth > MaxChain) throw new StateMachineError($"The transition chain through '{Label(seg.Target)}' does not reach a state (a loop of pseudostates?)");
        var target = seg.Target;
        switch (Ps(target))
        {
            case Pseudostates.Terminate:
                _host.Exec(seg.Get(FieldKeys.Effect), "effect");
                Arrive(target, Machine, path, depth);
                return;
            case Pseudostates.Join:
                ArriveAtJoin(seg, sourcePos, target, path, depth);
                return;
        }
        var domain = ActiveDomain(Domain(sourcePos, Anchor(target)));
        ExitWithin(domain, sourcePos);
        _host.Exec(seg.Get(FieldKeys.Effect), "effect");
        Arrive(target, domain, path, depth);
    }

    /// <summary>Continues at <paramref name="target"/> once everything up to <paramref name="domain"/> has been exited.</summary>
    private void Arrive(Element target, Element domain, Queue<Relationship>? path, int depth)
    {
        if (Finished) return;
        switch (Ps(target))
        {
            case Pseudostates.Choice:
            {
                var next = Choose(target) ?? throw NoBranch(target);
                _host.Log(MachineEventKind.Info, target, $"  ◇ choice '{Label(target)}' → {Label(next.Target)}{(next.Get(FieldKeys.Guard) is { Length: > 0 } g ? $" [{g}]" : "")}");
                Traverse(next, target, path, depth);
                return;
            }
            case Pseudostates.Junction:
            {
                var next = path is { Count: > 0 } ? path.Dequeue() : Choose(target) ?? throw NoBranch(target);
                _host.Log(MachineEventKind.Info, target, $"  ● junction → {Label(next.Target)}{(next.Get(FieldKeys.Guard) is { Length: > 0 } g ? $" [{g}]" : "")}");
                Traverse(next, target, path, depth);
                return;
            }
            case Pseudostates.Fork:
            {
                var outs = Model.Outgoing(target, ElementKind.Transition).ToList();
                if (outs.Count == 0) throw NoBranch(target);
                _host.Log(MachineEventKind.Info, target, $"  ⑂ fork → {string.Join(", ", outs.Select(o => Label(o.Target)))}");
                var d = ActiveDomain(Domain(target, outs.Select(o => Pos(o.Target)).ToArray()));
                ExitWithin(d, target);
                foreach (var o in outs) _host.Exec(o.Get(FieldKeys.Effect), "effect");
                EnterInto(d, outs.Select(o => o.Target).ToList(), false, depth);
                return;
            }
            case Pseudostates.Terminate:
                Terminated = Finished = true;
                _host.Log(MachineEventKind.Complete, target, $"✕ terminated at '{Label(target)}': the state machine stops without exiting its states");
                return;
            case Pseudostates.Join:
                _host.Log(MachineEventKind.Warning, target, $"  ⚠ join '{Label(target)}' can only be reached by transitions from states");
                return;
            case Pseudostates.ExitPoint:
            {
                // The composite has been exited (the exit point's anchor is its composite); continue outside it.
                var next = Model.Outgoing(target, ElementKind.Transition).FirstOrDefault();
                if (next is null) { _host.Log(MachineEventKind.Warning, target, $"  ⚠ exit point '{Label(target)}' has no outgoing transition"); return; }
                _host.Log(MachineEventKind.Info, target, $"  ⊗ leaves {Label(target.Owner!)} through exit point '{Label(target)}'");
                Traverse(next, target.Owner!, path, depth);
                return;
            }
            default:
                EnterInto(domain, [target], false, depth);
                return;
        }
    }

    private void ArriveAtJoin(Relationship seg, Element sourcePos, Element join, Queue<Relationship>? path, int depth)
    {
        var scope = JoinScope(join);
        ExitWithin(ActiveDomain(scope), sourcePos);
        _host.Exec(seg.Get(FieldKeys.Effect), "effect");
        var incoming = Model.Incoming(join, ElementKind.Transition).ToList();
        if (!_joinArrivals.TryGetValue(join, out var set)) _joinArrivals[join] = set = new HashSet<Relationship>(ReferenceEqualityComparer.Instance);
        set.Add(seg);
        if (incoming.Any(i => !set.Contains(i)))
        {
            _host.Log(MachineEventKind.Info, join, $"  ▬ join '{Label(join)}' waits ({set.Count}/{incoming.Count})");
            return;
        }
        _joinArrivals.Remove(join);
        var next = Model.Outgoing(join, ElementKind.Transition).FirstOrDefault();
        if (next is null) { _host.Log(MachineEventKind.Warning, join, $"  ⚠ join '{Label(join)}' has no outgoing transition"); return; }
        _host.Log(MachineEventKind.Info, join, $"  ▬ join '{Label(join)}' complete ({incoming.Count}/{incoming.Count}) → {Label(next.Target)}");
        var pos = !ReferenceEquals(scope, Machine) && IsActive(scope) && !IsProperAncestor(scope, next.Target) ? scope : join;
        Traverse(next, pos, path, depth);
    }

    /// <summary>The composite whose orthogonal regions a join's sources are in.</summary>
    private Element JoinScope(Element join)
    {
        var sources = Model.Incoming(join, ElementKind.Transition).Select(t => Pos(t.Source)).ToList();
        if (sources.Count == 0) return join.Owner ?? Machine;
        return Domain(sources[0], sources.Skip(1).Append(sources[0]).ToArray());
    }

    // ================================================================== entering

    private void DefaultEnterRegion(Element container, MachineRegion r, int depth)
    {
        if (Finished) return;
        if (r.Initial is not { } initial)
        {
            if (r.Members.Any(m => m.Kind == ElementKind.State))
                _host.Log(MachineEventKind.Info, container, $"  (a region of {Label(container)} has no initial pseudostate and stays inactive)");
            return;
        }
        var t = Model.Outgoing(initial, ElementKind.Transition).FirstOrDefault();
        if (t is null) { _host.Log(MachineEventKind.Warning, initial, $"  ⚠ initial pseudostate in {Label(container)} has no transition"); return; }
        _host.Exec(t.Get(FieldKeys.Effect), "effect");
        if (t.Target.Kind == ElementKind.Pseudostate && Ps(t.Target) is not (Pseudostates.EntryPoint or Pseudostates.ShallowHistory or Pseudostates.DeepHistory))
            Arrive(t.Target, container, null, depth + 1);
        else EnterInto(container, [t.Target], false, depth + 1);
    }

    private void DefaultEnterEmptyRegions(Element container, int depth)
    {
        foreach (var r in Regions(container))
            if (!r.Members.Any(IsActive) && !WaitingIn(r)) DefaultEnterRegion(container, r, depth);
    }

    private bool WaitingIn(MachineRegion r) =>
        _joinArrivals.Values.SelectMany(s => s).Any(seg => r.Members.Any(m => ReferenceEquals(m, Pos(seg.Source)) || IsProperAncestor(m, seg.Source)));

    /// <summary>
    /// Enters <paramref name="targets"/> (vertices inside the active <paramref name="container"/>): the states on the way are
    /// entered without default entry for the regions the targets are in; with <paramref name="defaultOthers"/> (the container
    /// was just entered) its other regions get their default entry.
    /// </summary>
    private void EnterInto(Element container, IReadOnlyList<Element> targets, bool defaultOthers, int depth)
    {
        if (++depth > MaxChain) throw new StateMachineError($"Entering {Label(container)} does not end (a loop of transitions?)");
        foreach (var group in targets.GroupBy(t => ChildOf(container, Pos(t))))
        {
            if (Finished) return;
            if (group.Key is not { } child)
            {
                foreach (var t in group) _host.Log(MachineEventKind.Warning, t, $"  ⚠ cannot enter '{Label(t)}' from {Label(container)}");
                continue;
            }
            // A transition into a region that is still busy (into another orthogonal region): leave what is active there first.
            if (RegionOf(container, child) is { } region)
                foreach (var other in region.Members.Where(m => !ReferenceEquals(m, child) && IsActive(m)).ToList()) ExitState(other);
            var direct = group.FirstOrDefault(t => ReferenceEquals(Pos(t), child) || ReferenceEquals(t, child));
            var deeper = group.Where(t => !ReferenceEquals(Pos(t), child) && !ReferenceEquals(t, child)).ToList();
            if (direct != null && deeper.Count == 0)
            {
                ArriveAt(direct, container, depth);
                continue;
            }
            if (child.Kind != ElementKind.State) { ArriveAt(child, container, depth); continue; }
            var wasActive = IsActive(child);
            EnterState(child, false, depth);
            EnterInto(child, deeper, !wasActive, depth);
        }
        if (defaultOthers && !Finished) DefaultEnterEmptyRegions(container, depth);
    }

    /// <summary>Arrives at a direct child of <paramref name="container"/> (or a connection point / history of one).</summary>
    private void ArriveAt(Element v, Element container, int depth)
    {
        switch (v.Kind)
        {
            case ElementKind.State:
                EnterState(v, true, depth);
                return;
            case ElementKind.FinalState:
                EnterFinal(v);
                return;
            case ElementKind.Pseudostate:
                switch (Ps(v))
                {
                    case Pseudostates.EntryPoint: EnterViaEntryPoint(v, depth); return;
                    case Pseudostates.ShallowHistory or Pseudostates.DeepHistory: EnterViaHistory(v, depth); return;
                    default: Arrive(v, container, null, depth); return;
                }
            default:
                _host.Log(MachineEventKind.Warning, v, $"  ⚠ cannot enter {Metamodel.DisplayName(v.Kind)} '{Label(v)}'");
                return;
        }
    }

    private void EnterState(Element s, bool defaultChildren, int depth)
    {
        if (IsActive(s)) return;
        _active.Add(s);
        _activeSet.Add(s);
        _host.Log(MachineEventKind.Enter, s, $"→ {Label(s)}");
        _host.Exec(s.Get(FieldKeys.Entry), "entry");
        _host.Exec(s.Get(FieldKeys.DoActivity), "do");
        _host.Entered(s);
        if (defaultChildren) DefaultEnterEmptyRegions(s, depth);
    }

    private void EnterFinal(Element f)
    {
        if (IsActive(f)) return;
        _active.Add(f);
        _activeSet.Add(f);
        var container = f.Owner ?? Machine;
        if (ReferenceEquals(container, Machine))
        {
            if (Completed(Machine))
            {
                Finished = true;
                _host.Log(MachineEventKind.Complete, f, "■ Final state reached");
            }
            else _host.Log(MachineEventKind.Info, f, "  a region of the state machine finished");
            return;
        }
        _host.Entered(f);
        _host.Log(MachineEventKind.Enter, f, Completed(container) ? $"  {Label(container)} completed" : $"  a region of {Label(container)} finished");
    }

    private void EnterViaEntryPoint(Element ep, int depth)
    {
        var composite = ep.Owner!;
        EnterState(composite, false, depth);
        _host.Log(MachineEventKind.Info, ep, $"  ○ enters {Label(composite)} through entry point '{Label(ep)}'");
        foreach (var o in Model.Outgoing(ep, ElementKind.Transition).ToList())
        {
            if (Finished) return;
            _host.Exec(o.Get(FieldKeys.Effect), "effect");
            if (o.Target.Kind == ElementKind.Pseudostate && Ps(o.Target) is Pseudostates.Choice or Pseudostates.Junction or Pseudostates.Fork or Pseudostates.Terminate)
                Arrive(o.Target, composite, null, depth);
            else EnterInto(composite, [o.Target], false, depth);
        }
        if (!Finished) DefaultEnterEmptyRegions(composite, depth);
    }

    private void EnterViaHistory(Element h, int depth)
    {
        var composite = h.Owner!;
        var deep = Ps(h) == Pseudostates.DeepHistory;
        EnterState(composite, false, depth);
        if (_history.TryGetValue(composite, out var rec))
        {
            var restore = (deep ? rec.Deep : rec.Shallow).Where(s => s.Kind == ElementKind.State).ToList();
            if (restore.Count > 0)
            {
                _host.Log(MachineEventKind.Info, h, $"  ⟲ {(deep ? "deep" : "shallow")} history of {Label(composite)} restores {string.Join(", ", restore.Select(PathOf))}");
                EnterInto(composite, restore, true, depth);
                return;
            }
        }
        var dflt = Model.Outgoing(h, ElementKind.Transition).FirstOrDefault();
        _host.Log(MachineEventKind.Info, h, $"  ⟲ no history of {Label(composite)} yet: {(dflt != null ? $"history's default transition → {Label(dflt.Target)}" : "default entry")}");
        if (dflt != null)
        {
            _host.Exec(dflt.Get(FieldKeys.Effect), "effect");
            if (dflt.Target.Kind == ElementKind.Pseudostate && Ps(dflt.Target) is Pseudostates.Choice or Pseudostates.Junction or Pseudostates.Fork or Pseudostates.Terminate)
                Arrive(dflt.Target, composite, null, depth);
            else EnterInto(composite, [dflt.Target], false, depth);
        }
        if (!Finished) DefaultEnterEmptyRegions(composite, depth);
    }

    // ================================================================== exiting

    private void ExitWithin(Element domain, Element pos)
    {
        if (ChildOf(domain, pos) is { } child && IsActive(child)) ExitState(child);
    }

    private void ExitState(Element s)
    {
        if (!IsActive(s)) return;
        if (Regions(s).Count > 0)
        {
            // History: per region the active substate (shallow) and the innermost active states (deep).
            var shallow = _active.Where(a => ReferenceEquals(a.Owner, s)).ToList();
            var deep = _active.Where(a => IsProperAncestor(s, a) && !_active.Any(c => ReferenceEquals(c.Owner, a))).ToList();
            if (shallow.Count > 0)
            {
                // A region that had finished is not restored (it gets its default entry next time).
                _history[s] = (shallow.Where(x => x.Kind != ElementKind.FinalState).ToList(),
                    deep.Where(x => x.Kind != ElementKind.FinalState && !IsInFinishedRegion(s, x)).ToList());
            }
            foreach (var c in shallow.AsEnumerable().Reverse()) ExitState(c);
        }
        if (s.Kind == ElementKind.State)
        {
            _host.Exec(s.Get(FieldKeys.Exit), $"exit {Label(s)}");
            _host.Log(MachineEventKind.Exit, s, $"← {Label(s)}");
        }
        _active.Remove(s);
        _activeSet.Remove(s);
        _host.Exited(s);
        // Arrivals at joins from inside a state that is left no longer count.
        foreach (var j in _joinArrivals.Keys.ToList())
        {
            _joinArrivals[j].RemoveWhere(seg => ReferenceEquals(Pos(seg.Source), s) || IsProperAncestor(s, seg.Source));
            if (_joinArrivals[j].Count == 0) _joinArrivals.Remove(j);
        }
    }

    private bool IsInFinishedRegion(Element composite, Element leaf)
    {
        for (var x = leaf; x != null && !ReferenceEquals(x, composite); x = x.Owner)
            if (x.Owner is { } o && RegionOf(o, x) is { } r && r.Members.Any(m => m.Kind == ElementKind.FinalState && IsActive(m))) return true;
        return false;
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Label(Machine)}: {Configuration}");
}
