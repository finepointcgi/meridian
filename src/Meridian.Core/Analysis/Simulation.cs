using System.Globalization;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

/// <summary>
/// Executes activities (token flow) and state machines (event-driven) over a variable context seeded from the
/// owning block's value properties. Guards, action bodies, effects and entry/do/exit use <see cref="Expressions"/>.
/// </summary>
public abstract class Simulator
{
    protected Simulator(ModelRepository model, Element behavior)
    {
        Model = model;
        Behavior = behavior;
        // Context: value properties of the behavior's owning classifier (e.g. Engine.throttle).
        if (behavior.Owner is { } ctx && Metamodel.IsStructuralClassifier(ctx.Kind))
            foreach (var p in ctx.OwnedElements.Where(p => p.Kind == ElementKind.Property))
                if (double.TryParse(p.Get(FieldKeys.DefaultValue), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Variables[p.Name] = v;
    }

    public ModelRepository Model { get; }
    public Element Behavior { get; }
    public Dictionary<string, double> Variables { get; } = new(StringComparer.Ordinal);
    public List<string> Log { get; } = new();
    public bool Finished { get; protected set; }
    public int Steps { get; protected set; }

    /// <summary>Elements to highlight (current tokens / active states).</summary>
    public abstract IReadOnlyCollection<Element> Active { get; }

    public abstract void Start();
    public abstract bool Step();

    public int Run(int maxSteps = 500)
    {
        var n = 0;
        while (!Finished && n < maxSteps && Step()) n++;
        if (n >= maxSteps) Log.Add($"Stopped after {maxSteps} steps (possible infinite loop).");
        return n;
    }

    protected void Exec(string? statements, string what)
    {
        if (string.IsNullOrWhiteSpace(statements)) return;
        // Variables first assigned here start at 0 (so "count = count + 1" works without a declaration).
        foreach (var n in Expressions.AssignedNames(statements)) Variables.TryAdd(n, 0);
        try
        {
            var assigned = Expressions.Execute(statements, Variables);
            if (assigned.Count > 0) Log.Add($"  {what}: " + string.Join(", ", assigned.Select(a => $"{a} = {Variables[a]:G6}")));
        }
        catch (Expressions.ExpressionException ex) { Log.Add($"  ⚠ {what}: {ex.Message}"); }
    }

    protected bool Guard(Relationship r)
    {
        var g = r.Get(FieldKeys.Guard);
        try { return Expressions.IsTrue(g, Variables); }
        catch (Expressions.ExpressionException ex)
        {
            Log.Add($"  ⚠ guard [{g}]: {ex.Message} (treated as false)");
            return false;
        }
    }

    protected static string Label(Element e) => string.IsNullOrEmpty(e.Name) ? Metamodel.DisplayName(e.Kind) : e.Name;
}

/// <summary>
/// Token flow through an activity. Decisions take the first true guard (else <c>[else]</c>), a fork node with several
/// incoming flows joins them, a <b>central buffer</b> holds tokens and passes each one to a single outgoing flow (in turn),
/// a <b>data store</b> keeps its data and gives every outgoing flow a copy.
/// </summary>
public sealed class ActivitySimulator(ModelRepository model, Element activity) : Simulator(model, activity)
{
    private readonly Queue<Element> _tokens = new();
    private readonly Dictionary<Element, HashSet<Relationship>> _joinArrivals = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Element, int> _held = new(ReferenceEqualityComparer.Instance);   // central buffer / data store → tokens held
    private readonly Dictionary<Element, int> _next = new(ReferenceEqualityComparer.Instance);   // central buffer → next outgoing flow (round robin)
    private Element? _last;

    public override IReadOnlyCollection<Element> Active =>
        _tokens.Concat(_last is null ? [] : [_last]).Concat(_held.Where(h => h.Value > 0).Select(h => h.Key)).Distinct().ToList();

    /// <summary>Tokens a central buffer holds, or 1 when a data store holds data (0 otherwise).</summary>
    public int Held(Element node) => _held.GetValueOrDefault(node);

    private IEnumerable<Relationship> Outgoing(Element n) =>
        Model.Outgoing(n).Where(r => r.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow);

    private IEnumerable<Relationship> Incoming(Element n) =>
        Model.Incoming(n).Where(r => r.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow);

    public override void Start()
    {
        _tokens.Clear();
        _joinArrivals.Clear();
        _held.Clear();
        _next.Clear();
        Finished = false;
        Steps = 0;
        foreach (var i in Behavior.OwnedElements.Where(e => e.Kind == ElementKind.InitialNode)) _tokens.Enqueue(i);
        Log.Add($"▶ Activity '{Behavior.Name}' started");
        if (_tokens.Count == 0) { Log.Add("  ⚠ no initial node"); Finished = true; }
    }

    private void Offer(Relationship flow)
    {
        var target = flow.Target;
        var incoming = Incoming(target).ToList();
        if (target.Kind == ElementKind.ForkNode && incoming.Count > 1)
        {
            // Join: wait for a token on every incoming flow.
            if (!_joinArrivals.TryGetValue(target, out var set)) _joinArrivals[target] = set = new HashSet<Relationship>();
            set.Add(flow);
            if (set.Count < incoming.Count) { Log.Add($"  join waits ({set.Count}/{incoming.Count})"); return; }
            _joinArrivals.Remove(target);
        }
        _tokens.Enqueue(target);
    }

    private bool Enabled(Relationship f) => string.IsNullOrEmpty(f.Get(FieldKeys.Guard)) || Guard(f);

    /// <summary>
    /// A central buffer passes each token it holds to ONE outgoing flow whose guard holds (taking the flows in turn); tokens
    /// no flow accepts stay buffered and are offered again on later steps.
    /// </summary>
    private void ReleaseBuffer(Element buffer)
    {
        var outgoing = Outgoing(buffer).ToList();
        while (_held.GetValueOrDefault(buffer) > 0 && outgoing.Count > 0)
        {
            var start = _next.GetValueOrDefault(buffer);
            Relationship? chosen = null;
            for (var k = 0; k < outgoing.Count && chosen is null; k++)
            {
                var i = (start + k) % outgoing.Count;
                if (Enabled(outgoing[i])) { chosen = outgoing[i]; _next[buffer] = (i + 1) % outgoing.Count; }
            }
            if (chosen is null) return;
            _held[buffer]--;
            Log.Add($"  ▣ {Label(buffer)} passes a token to {Label(chosen.Target)} ({_held[buffer]} left)");
            Offer(chosen);
        }
    }

    private static bool IsBuffer(Element e) => e.Kind == ElementKind.ObjectNode && e.Get(FieldKeys.Subtype) == Subtypes.CentralBuffer;
    private static bool IsStore(Element e) => e.Kind == ElementKind.ObjectNode && e.Get(FieldKeys.Subtype) == Subtypes.DataStore;

    public override bool Step()
    {
        if (Finished) return false;
        // Buffered tokens are offered again (a guard may have become true since).
        foreach (var b in _held.Where(h => h.Value > 0 && IsBuffer(h.Key)).Select(h => h.Key).ToList()) ReleaseBuffer(b);
        if (_tokens.Count == 0)
        {
            Finished = true;
            Log.Add("■ No more tokens — activity complete");
            foreach (var (held, n) in _held.Where(h => h.Value > 0))
                Log.Add(IsStore(held) ? $"  (data store {Label(held)} keeps its data)" : $"  (central buffer {Label(held)} still holds {n} token{(n == 1 ? "" : "s")})");
            return false;
        }
        var node = _tokens.Dequeue();
        _last = node;
        Steps++;
        var outgoing = Outgoing(node).ToList();
        switch (node.Kind)
        {
            case ElementKind.ActivityFinalNode:
                Log.Add("■ Activity final reached");
                _tokens.Clear();
                Finished = true;
                return true;
            case ElementKind.DecisionNode:
            {
                var chosen = outgoing.FirstOrDefault(f => !f.Get(FieldKeys.Guard).Equals("else", StringComparison.OrdinalIgnoreCase) && Guard(f))
                             ?? outgoing.FirstOrDefault(f => f.Get(FieldKeys.Guard).Equals("else", StringComparison.OrdinalIgnoreCase));
                if (chosen is null) { Log.Add("  ⚠ decision: no guard is true — token stuck"); return true; }
                Log.Add($"◇ decision → {Label(chosen.Target)} [{chosen.Get(FieldKeys.Guard)}]");
                Offer(chosen);
                return true;
            }
            case ElementKind.ObjectNode when IsBuffer(node):
                _held[node] = _held.GetValueOrDefault(node) + 1;
                Log.Add($"▣ {Label(node)} buffers a token ({_held[node]} held)");
                ReleaseBuffer(node);
                return true;
            case ElementKind.ObjectNode when IsStore(node):
                // A data store keeps its data (a new token replaces the stored one) and gives every outgoing flow a copy.
                _held[node] = 1;
                Log.Add($"⛁ {Label(node)} stores the data");
                foreach (var f in outgoing.Where(Enabled)) { Log.Add($"  ⛁ {Label(node)} → copy to {Label(f.Target)}"); Offer(f); }
                return true;
            case ElementKind.Action:
                Log.Add($"● {Label(node)}");
                Exec(node.Get(FieldKeys.Body), Label(node));
                break;
            default:
                Log.Add($"○ {Label(node)}");
                break;
        }
        foreach (var f in outgoing.Where(Enabled)) Offer(f);
        if (outgoing.Count == 0 && node.Kind != ElementKind.ActivityFinalNode) Log.Add($"  (flow ends at {Label(node)})");
        return true;
    }
}

/// <summary>
/// Steps a state machine with <see cref="StateMachineEngine"/>: <see cref="Step"/> takes one completion (event-less)
/// transition, <see cref="Send"/> dispatches an event (a transition's name) to every active region. Choice guards are
/// evaluated as they are reached; a choice with no enabled branch stops the run with <see cref="Error"/>.
/// </summary>
public sealed class StateMachineSimulator : Simulator, IStateMachineHost
{
    private readonly StateMachineEngine _engine;

    public StateMachineSimulator(ModelRepository model, Element machine) : base(model, machine) => _engine = new StateMachineEngine(model, machine, this);

    public StateMachineEngine Engine => _engine;

    /// <summary>The first active innermost state (with orthogonal regions, see <see cref="ActiveLeaves"/>).</summary>
    public Element? Current => _engine.ActiveLeaves.FirstOrDefault();
    public IReadOnlyList<Element> ActiveLeaves => _engine.ActiveLeaves;
    /// <summary>The active innermost states by path, e.g. "Operating.Heating, Operating.Monitoring".</summary>
    public string Configuration => _engine.Configuration;
    public string? Error => _engine.Error;
    public bool Terminated => _engine.Terminated;
    public override IReadOnlyCollection<Element> Active => _engine.Active;

    /// <summary>Named transitions (events) that are enabled right now, one per name.</summary>
    public IReadOnlyList<Relationship> EnabledEvents =>
        Finished ? [] : _engine.Enabled(t => !string.IsNullOrEmpty(t.Name)).DistinctBy(t => t.Name).ToList();

    public override void Start()
    {
        Finished = false;
        Steps = 0;
        Log.Add($"▶ State machine '{Behavior.Name}' started");
        _engine.Start();
        Finished = _engine.Finished;
    }

    /// <summary>Fires the first enabled completion (event-less) transition.</summary>
    public override bool Step()
    {
        if (Finished) return false;
        if (!_engine.FireFirst(t => string.IsNullOrEmpty(t.Name), completion: true, _ => "completion"))
        {
            Finished = _engine.Finished;
            if (!Finished) Log.Add($"  waiting in {Configuration} — send an event or change a variable");
            return false;
        }
        Steps++;
        Finished = _engine.Finished;
        return true;
    }

    /// <summary>Dispatches an event to every active region. False when no transition took it.</summary>
    public bool Send(string eventName)
    {
        if (Finished) return false;
        var n = _engine.Dispatch(t => t.Name == eventName, t => t.Name);
        Finished = _engine.Finished;
        if (n == 0) { Log.Add($"  event '{eventName}' ignored in {Configuration}"); return false; }
        Steps++;
        return true;
    }

    void IStateMachineHost.Exec(string? statements, string what) => Exec(statements, what);
    bool IStateMachineHost.Guard(Relationship transition) => Guard(transition);
    void IStateMachineHost.Log(MachineEventKind kind, Element? element, string text) => Log.Add(text);
}
