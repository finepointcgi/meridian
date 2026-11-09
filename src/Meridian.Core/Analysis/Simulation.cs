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

public sealed class ActivitySimulator(ModelRepository model, Element activity) : Simulator(model, activity)
{
    private readonly Queue<Element> _tokens = new();
    private readonly Dictionary<Element, HashSet<Relationship>> _joinArrivals = new(ReferenceEqualityComparer.Instance);
    private Element? _last;

    public override IReadOnlyCollection<Element> Active => _tokens.Concat(_last is null ? [] : [_last]).Distinct().ToList();

    private IEnumerable<Relationship> Outgoing(Element n) =>
        Model.Outgoing(n).Where(r => r.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow);

    private IEnumerable<Relationship> Incoming(Element n) =>
        Model.Incoming(n).Where(r => r.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow);

    public override void Start()
    {
        _tokens.Clear();
        _joinArrivals.Clear();
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

    public override bool Step()
    {
        if (Finished || _tokens.Count == 0) { if (!Finished) { Finished = true; Log.Add("■ No more tokens — activity complete"); } return false; }
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
            case ElementKind.Action:
                Log.Add($"● {Label(node)}");
                Exec(node.Get(FieldKeys.Body), Label(node));
                break;
            default:
                Log.Add($"○ {Label(node)}");
                break;
        }
        foreach (var f in outgoing.Where(f => string.IsNullOrEmpty(f.Get(FieldKeys.Guard)) || Guard(f))) Offer(f);
        if (outgoing.Count == 0 && node.Kind != ElementKind.ActivityFinalNode) Log.Add($"  (flow ends at {Label(node)})");
        return true;
    }
}

public sealed class StateMachineSimulator(ModelRepository model, Element machine) : Simulator(model, machine)
{
    private Element? _current;

    public Element? Current => _current;
    public override IReadOnlyCollection<Element> Active => _current is null ? [] : _current.AncestorsAndSelf().TakeWhile(a => !ReferenceEquals(a, Behavior)).ToList();

    private IEnumerable<Relationship> TransitionsFrom(Element vertex) =>
        vertex.AncestorsAndSelf().TakeWhile(a => !ReferenceEquals(a, Behavior))
              .SelectMany(v => Model.Outgoing(v, ElementKind.Transition));

    /// <summary>Named transitions (events) that are enabled right now.</summary>
    public IReadOnlyList<Relationship> EnabledEvents =>
        _current is null ? [] : TransitionsFrom(_current).Where(t => !string.IsNullOrEmpty(t.Name) && Guard(t)).ToList();

    public override void Start()
    {
        Finished = false;
        Steps = 0;
        Log.Add($"▶ State machine '{Behavior.Name}' started");
        var initial = Behavior.OwnedElements.FirstOrDefault(e => e.Kind == ElementKind.InitialState);
        if (initial is null) { Log.Add("  ⚠ no initial pseudostate"); Finished = true; return; }
        var first = Model.Outgoing(initial, ElementKind.Transition).FirstOrDefault();
        if (first is null) { Log.Add("  ⚠ initial pseudostate has no transition"); Finished = true; return; }
        Exec(first.Get(FieldKeys.Effect), "effect");
        Enter(first.Target);
    }

    private void Enter(Element state)
    {
        _current = state;
        if (state.Kind == ElementKind.FinalState) { Log.Add("■ Final state reached"); Finished = true; return; }
        Log.Add($"→ {Label(state)}");
        Exec(state.Get(FieldKeys.Entry), "entry");
        Exec(state.Get(FieldKeys.DoActivity), "do");
        // Composite state: descend through its own initial pseudostate.
        var initial = state.OwnedElements.FirstOrDefault(e => e.Kind == ElementKind.InitialState);
        if (initial != null && Model.Outgoing(initial, ElementKind.Transition).FirstOrDefault() is { } t)
        {
            Exec(t.Get(FieldKeys.Effect), "effect");
            Enter(t.Target);
        }
    }

    private void Fire(Relationship t)
    {
        Steps++;
        // Exit from the current state up to (not including) the transition's source's owner.
        for (var s = _current; s != null && !ReferenceEquals(s, t.Source.Owner) && !ReferenceEquals(s, Behavior); s = s.Owner)
            Exec(s.Get(FieldKeys.Exit), $"exit {Label(s)}");
        Log.Add($"  ⇢ {(string.IsNullOrEmpty(t.Name) ? "completion" : t.Name)}{(t.Get(FieldKeys.Guard) is { Length: > 0 } g ? $" [{g}]" : "")}");
        Exec(t.Get(FieldKeys.Effect), "effect");
        Enter(t.Target);
    }

    /// <summary>Fires the first enabled completion (event-less) transition.</summary>
    public override bool Step()
    {
        if (Finished || _current is null) return false;
        var t = TransitionsFrom(_current).FirstOrDefault(x => string.IsNullOrEmpty(x.Name) && Guard(x));
        if (t is null) { Log.Add($"  waiting in {Label(_current)} — send an event or change a variable"); return false; }
        Fire(t);
        return true;
    }

    public bool Send(string eventName)
    {
        if (Finished || _current is null) return false;
        var t = TransitionsFrom(_current).FirstOrDefault(x => x.Name == eventName && Guard(x));
        if (t is null) { Log.Add($"  event '{eventName}' ignored in {Label(_current)}"); return false; }
        Fire(t);
        return true;
    }
}
