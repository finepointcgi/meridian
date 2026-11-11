using System.Globalization;
using System.Text.RegularExpressions;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Analysis;

public enum TraceKind { Start, Complete, Enter, Exit, Transition, Signal, Accept, Wait, Info, Warning, Finish }

public sealed record TraceEntry(double Time, string Lane, Element? Element, TraceKind Kind, string Text)
{
    public override string ToString() => $"t={Time.ToString("0.###", CultureInfo.InvariantCulture),8}s  [{Lane}] {Text}";
}

/// <summary>A bar on the timeline: an action executing, an accept event waiting or a state being occupied. End is NaN while open.</summary>
public sealed record TimelineSpan(string Row, string Lane, Element Element, double Start, double End, string Kind)
{
    public bool Open => double.IsNaN(End);
}

/// <summary>A broadcast signal: who sent it and who consumed it (for the sequence-style arrows on the timeline).</summary>
public sealed record SignalMark(double Time, string Signal, string FromRow, IReadOnlyList<string> ToRows);

/// <summary>
/// Discrete-event simulation of one or more activities and state machines sharing a clock, a variable context and a
/// signal bus. Actions take their <see cref="ExtFieldKeys.Duration"/> to complete, send a signal on completion
/// (<see cref="ExtFieldKeys.SendSignal"/>) or wait for one (<see cref="ExtFieldKeys.AcceptEvent"/>). Transition names are
/// triggers: a signal name, <c>after(5s)</c> (time event), <c>when(x &gt; 3)</c> (change event) or empty (completion).
/// Guards and bodies use <see cref="Expressions"/> over the shared variables, seeded from the owning blocks' value properties.
/// Each event is processed to completion before the next one (run-to-completion).
/// </summary>
public sealed class DiscreteEventSimulation
{
    private readonly PriorityQueue<Action, (double Time, long Seq)> _queue = new();
    private long _seq;
    private readonly List<Execution> _execs = new();
    private readonly List<TimelineSpan> _spans = new();

    public DiscreteEventSimulation(ModelRepository model, IEnumerable<Element> behaviors)
    {
        Model = model;
        foreach (var b in behaviors.Distinct())
        {
            if (b.Owner is { } ctx && Metamodel.IsStructuralClassifier(ctx.Kind))
                foreach (var p in ctx.OwnedElements.Where(p => p.Kind == ElementKind.Property))
                    if (double.TryParse(p.Get(FieldKeys.DefaultValue), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) Variables.TryAdd(p.Name, v);
            var lane = LaneName(b);
            _execs.Add(b.Kind == ElementKind.StateMachine ? new MachineExec(this, b, lane) : new ActivityExec(this, b, lane));
        }
    }

    public ModelRepository Model { get; }
    public double Clock { get; private set; }
    public Dictionary<string, double> Variables { get; } = new(StringComparer.Ordinal);
    public List<TraceEntry> Trace { get; } = new();
    public IReadOnlyList<TimelineSpan> Spans => _spans;
    public List<SignalMark> Signals { get; } = new();
    public bool Finished { get; private set; }
    public int EventsProcessed { get; private set; }
    public int MaxEvents { get; set; } = 20000;
    public IEnumerable<Element> Behaviors => _execs.Select(e => e.Behavior);
    public IEnumerable<string> Lanes => _execs.Select(e => e.Lane);

    /// <summary>Time of the next scheduled event, or null when nothing is pending.</summary>
    public double? NextEventTime => _queue.TryPeek(out _, out var p) ? p.Time : null;

    /// <summary>Elements to highlight: executing actions, waiting accept events and active states.</summary>
    public IReadOnlyCollection<Element> Active => _execs.SelectMany(e => e.Active).Distinct().ToList();

    private string LaneName(Element b)
    {
        var name = string.IsNullOrEmpty(b.Name) ? Metamodel.DisplayName(b.Kind) : b.Name;
        var n = name; var i = 2;
        while (_execs.Any(e => e.Lane == n)) n = $"{name} ({i++})";
        return n;
    }

    // ------------------------------------------------------------------ scheduling

    internal void Schedule(double time, Action action) => _queue.Enqueue(action, (Math.Max(time, Clock), _seq++));

    public void Start()
    {
        Clock = 0;
        Finished = false;
        foreach (var e in _execs) e.Start();
        if (_execs.Count == 0) { Log(null, "", TraceKind.Warning, "Nothing to simulate"); Finished = true; }
    }

    /// <summary>Processes the next event (advancing the clock to it). False when the simulation has finished.</summary>
    public bool Step()
    {
        if (Finished) return false;
        if (!_queue.TryDequeue(out var action, out var p))
        {
            Finish("no more events");
            return false;
        }
        Clock = p.Time;
        action();
        EventsProcessed++;
        foreach (var e in _execs) e.AfterEvent();
        if (EventsProcessed >= MaxEvents) { Log(null, "", TraceKind.Warning, $"Stopped after {MaxEvents} events (possible endless loop)"); Finish("event limit"); }
        else if (_execs.All(e => e.Done)) Finish("all behaviors completed");
        return true;
    }

    /// <summary>Processes every event up to and including <paramref name="time"/>, then sets the clock to it.</summary>
    public void RunUntil(double time)
    {
        while (!Finished && NextEventTime is { } t && t <= time) Step();
        if (!Finished && time > Clock) Clock = time;
    }

    public int RunToCompletion(double maxTime = 1e9)
    {
        var n = 0;
        while (!Finished && NextEventTime is { } t && t <= maxTime && Step()) n++;
        if (!Finished && NextEventTime is null) Finish("no more events");
        if (!Finished && NextEventTime is { } next && next > maxTime) Log(null, "", TraceKind.Info, $"Paused at the time limit {maxTime:G6}s (next event at {next:G6}s)");
        return n;
    }

    private void Finish(string why)
    {
        if (Finished) return;
        Finished = true;
        foreach (var s in _spans.Where(s => s.Open).ToList()) Close(s.Element, s.Lane);
        Log(null, "", TraceKind.Finish, $"■ Simulation finished at t={Clock:G6}s ({why}), {EventsProcessed} events");
    }

    /// <summary>Broadcasts a signal from outside (the user / environment) at the current time.</summary>
    public void Inject(string signal)
    {
        if (string.IsNullOrWhiteSpace(signal)) return;
        if (Finished) { Finished = false; }
        Broadcast(signal.Trim(), "environment");
    }

    internal void Broadcast(string signal, string fromRow)
    {
        Log(null, fromRow, TraceKind.Signal, $"⚡ signal {signal}");
        Schedule(Clock, () =>
        {
            var receivers = new List<string>();
            foreach (var e in _execs.Where(e => !e.Done))
                if (e.Receive(signal) is { } row) receivers.Add(row);
            Signals.Add(new SignalMark(Clock, signal, fromRow, receivers));
            if (receivers.Count == 0) Log(null, fromRow, TraceKind.Info, $"  signal {signal} was not consumed");
        });
    }

    // ------------------------------------------------------------------ trace / timeline

    internal void Log(Element? e, string lane, TraceKind kind, string text) => Trace.Add(new TraceEntry(Clock, lane, e, kind, text));

    internal void Open(Element e, string lane, string row, string kind) => _spans.Add(new TimelineSpan(row, lane, e, Clock, double.NaN, kind));

    internal void Close(Element e, string lane)
    {
        var i = _spans.FindLastIndex(s => s.Open && ReferenceEquals(s.Element, e) && s.Lane == lane);
        if (i >= 0) _spans[i] = _spans[i] with { End = Clock };
    }

    internal void Exec(string? statements, string lane, string what)
    {
        if (string.IsNullOrWhiteSpace(statements)) return;
        foreach (var n in Expressions.AssignedNames(statements)) Variables.TryAdd(n, 0);
        try
        {
            var assigned = Expressions.Execute(statements, Variables);
            if (assigned.Count > 0) Log(null, lane, TraceKind.Info, $"  {what}: " + string.Join(", ", assigned.Select(a => $"{a} = {Variables[a]:G6}")));
        }
        catch (Expressions.ExpressionException ex) { Log(null, lane, TraceKind.Warning, $"  ⚠ {what}: {ex.Message}"); }
    }

    internal bool Guard(Relationship r, string lane)
    {
        var g = r.Get(FieldKeys.Guard);
        try { return Expressions.IsTrue(g, Variables); }
        catch (Expressions.ExpressionException ex)
        {
            Log(null, lane, TraceKind.Warning, $"  ⚠ guard [{g}]: {ex.Message} (treated as false)");
            return false;
        }
    }

    internal bool Condition(string expr, string lane)
    {
        try { return Expressions.IsTrue(expr, Variables); }
        catch (Expressions.ExpressionException ex)
        {
            Log(null, lane, TraceKind.Warning, $"  ⚠ when({expr}): {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ durations and triggers

    private static readonly Regex DurationRe = new(@"^\s*([0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?)\s*(ms|msec|s|sec|secs|second|seconds|m|min|mins|minute|minutes|h|hr|hrs|hour|hours)?\s*$", RegexOptions.IgnoreCase);

    /// <summary>Seconds for "5", "2.5s", "200ms", "1 min", "2h" or an expression over <paramref name="vars"/>. Null when unreadable.</summary>
    public static double? ParseDuration(string? text, IReadOnlyDictionary<string, double>? vars = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        var m = DurationRe.Match(text);
        if (m.Success)
        {
            var v = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            return m.Groups[2].Value.ToLowerInvariant() switch
            {
                "ms" or "msec" => v / 1000,
                "m" or "min" or "mins" or "minute" or "minutes" => v * 60,
                "h" or "hr" or "hrs" or "hour" or "hours" => v * 3600,
                _ => v,
            };
        }
        try { return Math.Max(0, Expressions.Evaluate(text, vars ?? new Dictionary<string, double>())); }
        catch (Expressions.ExpressionException) { return null; }
    }

    public enum TriggerKind { Completion, Signal, After, When }

    /// <summary>Reads a transition's trigger from its name: "after(5s)", "when(x &gt; 3)", a signal name, or empty (completion).</summary>
    public static (TriggerKind Kind, string Arg) ParseTrigger(string? name)
    {
        var n = (name ?? "").Trim();
        var cut = n.IndexOfAny(['[', '/']);
        if (cut >= 0) n = n[..cut].Trim();
        if (n.Length == 0) return (TriggerKind.Completion, "");
        var m = Regex.Match(n, @"^(after|when)\s*\(?\s*(.*?)\s*\)?$", RegexOptions.IgnoreCase);
        if (m.Success && m.Groups[2].Value.Length > 0)
            return (m.Groups[1].Value.Equals("after", StringComparison.OrdinalIgnoreCase) ? TriggerKind.After : TriggerKind.When, m.Groups[2].Value);
        return (TriggerKind.Signal, n);
    }

    // ------------------------------------------------------------------ executions

    private abstract class Execution(DiscreteEventSimulation sim, Element behavior, string lane)
    {
        protected readonly DiscreteEventSimulation Sim = sim;
        public Element Behavior { get; } = behavior;
        public string Lane { get; } = lane;
        public bool Done { get; protected set; }
        public abstract IEnumerable<Element> Active { get; }
        public abstract void Start();
        /// <summary>Delivers a signal; returns the timeline row that consumed it, or null.</summary>
        public abstract string? Receive(string signal);
        public virtual void AfterEvent() { }
        protected static string Label(Element e) => string.IsNullOrEmpty(e.Name) ? Metamodel.DisplayName(e.Kind) : e.Name;
    }

    private sealed class ActivityExec(DiscreteEventSimulation sim, Element activity, string lane) : Execution(sim, activity, lane)
    {
        private readonly HashSet<Element> _running = new(ReferenceEqualityComparer.Instance);
        private readonly List<Element> _waiting = new();
        private readonly Dictionary<Element, HashSet<Relationship>> _join = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Element, int> _held = new(ReferenceEqualityComparer.Instance);   // central buffers / data stores
        private readonly Dictionary<Element, int> _next = new(ReferenceEqualityComparer.Instance);   // central buffer round robin
        private int _inFlight;   // scheduled arrivals / completions not yet processed

        public override IEnumerable<Element> Active => _running.Concat(_waiting).Concat(_held.Where(h => h.Value > 0).Select(h => h.Key));

        private static bool IsBuffer(Element e) => e.Kind == ElementKind.ObjectNode && e.Get(FieldKeys.Subtype) == Subtypes.CentralBuffer;
        private static bool IsStore(Element e) => e.Kind == ElementKind.ObjectNode && e.Get(FieldKeys.Subtype) == Subtypes.DataStore;
        private bool Enabled(Relationship f) => string.IsNullOrEmpty(f.Get(FieldKeys.Guard)) || Sim.Guard(f, Lane);

        /// <summary>A central buffer passes each held token to ONE enabled outgoing flow, taking the flows in turn.</summary>
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
                Sim.Log(buffer, Lane, TraceKind.Info, $"  ▣ {Label(buffer)} passes a token to {Label(chosen.Target)} ({_held[buffer]} left)");
                Arrive(chosen.Target, chosen);
            }
        }

        private string Row(Element node) => $"{Lane} · {Label(node)}";

        private IEnumerable<Relationship> Outgoing(Element n) =>
            Sim.Model.Outgoing(n).Where(r => r.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow);
        private IEnumerable<Relationship> Incoming(Element n) =>
            Sim.Model.Incoming(n).Where(r => r.Kind is ElementKind.ControlFlow or ElementKind.ObjectFlow);

        public override void Start()
        {
            Sim.Log(Behavior, Lane, TraceKind.Start, $"▶ activity '{Label(Behavior)}' started");
            var initials = Behavior.OwnedElements.Where(e => e.Kind == ElementKind.InitialNode).ToList();
            if (initials.Count == 0) { Sim.Log(Behavior, Lane, TraceKind.Warning, "  ⚠ no initial node"); Done = true; return; }
            foreach (var i in initials) Arrive(i, null);
        }

        private void Arrive(Element node, Relationship? via)
        {
            _inFlight++;
            Sim.Schedule(Sim.Clock, () => { _inFlight--; if (!Done) Handle(node, via); });
        }

        private void Handle(Element node, Relationship? via)
        {
            if (node.Kind == ElementKind.ForkNode && via != null)
            {
                var incoming = Incoming(node).ToList();
                if (incoming.Count > 1)
                {
                    if (!_join.TryGetValue(node, out var set)) _join[node] = set = new HashSet<Relationship>();
                    set.Add(via);
                    if (set.Count < incoming.Count) { Sim.Log(node, Lane, TraceKind.Info, $"  join waits ({set.Count}/{incoming.Count})"); return; }
                    _join.Remove(node);
                }
            }
            switch (node.Kind)
            {
                case ElementKind.ActivityFinalNode:
                    Sim.Log(node, Lane, TraceKind.Complete, $"■ activity '{Label(Behavior)}' reached its final node");
                    Stop();
                    return;
                case ElementKind.DecisionNode:
                {
                    var outgoing = Outgoing(node).ToList();
                    var chosen = outgoing.FirstOrDefault(f => !f.Get(FieldKeys.Guard).Equals("else", StringComparison.OrdinalIgnoreCase) && Sim.Guard(f, Lane))
                                 ?? outgoing.FirstOrDefault(f => f.Get(FieldKeys.Guard).Equals("else", StringComparison.OrdinalIgnoreCase));
                    if (chosen is null) { Sim.Log(node, Lane, TraceKind.Warning, "  ⚠ decision: no guard is true — token stuck"); return; }
                    Sim.Log(node, Lane, TraceKind.Info, $"◇ decision → {Label(chosen.Target)} [{chosen.Get(FieldKeys.Guard)}]");
                    Arrive(chosen.Target, chosen);
                    return;
                }
                case ElementKind.Action:
                    if (node.Get(ExtFieldKeys.AcceptEvent) is { Length: > 0 } sig)
                    {
                        _waiting.Add(node);
                        Sim.Open(node, Lane, Row(node), "wait");
                        Sim.Log(node, Lane, TraceKind.Wait, $"⏸ {Label(node)} waits for signal {sig}");
                        return;
                    }
                    Begin(node);
                    return;
                case ElementKind.ObjectNode when IsBuffer(node):
                    _held[node] = _held.GetValueOrDefault(node) + 1;
                    Sim.Log(node, Lane, TraceKind.Info, $"▣ {Label(node)} buffers a token ({_held[node]} held)");
                    ReleaseBuffer(node);
                    return;
                case ElementKind.ObjectNode when IsStore(node):
                    _held[node] = 1;
                    Sim.Log(node, Lane, TraceKind.Info, $"⛁ {Label(node)} stores the data");
                    foreach (var f in Outgoing(node).Where(Enabled).ToList()) Arrive(f.Target, f);
                    return;
                default:
                    Continue(node);
                    return;
            }
        }

        private void Begin(Element action)
        {
            var d = ParseDuration(action.Get(ExtFieldKeys.Duration), Sim.Variables);
            if (d is null) { Sim.Log(action, Lane, TraceKind.Warning, $"  ⚠ cannot read duration '{action.Get(ExtFieldKeys.Duration)}' (using 0)"); d = 0; }
            _running.Add(action);
            Sim.Open(action, Lane, Row(action), "action");
            Sim.Log(action, Lane, TraceKind.Start, $"● {Label(action)} starts{(d > 0 ? $" ({d:G6}s)" : "")}");
            _inFlight++;
            Sim.Schedule(Sim.Clock + d.Value, () =>
            {
                _inFlight--;
                if (Done) return;
                _running.Remove(action);
                Sim.Exec(action.Get(FieldKeys.Body), Lane, Label(action));
                Sim.Close(action, Lane);
                Sim.Log(action, Lane, TraceKind.Complete, $"✓ {Label(action)} completes");
                if (action.Get(ExtFieldKeys.SendSignal) is { Length: > 0 } s) Sim.Broadcast(s, Row(action));
                Continue(action);
            });
        }

        private void Continue(Element node)
        {
            if (node.Kind is not ElementKind.Action) Sim.Log(node, Lane, TraceKind.Info, $"○ {Label(node)}");
            var outgoing = Outgoing(node).ToList();
            foreach (var f in outgoing.Where(f => string.IsNullOrEmpty(f.Get(FieldKeys.Guard)) || Sim.Guard(f, Lane))) Arrive(f.Target, f);
            if (outgoing.Count == 0) Sim.Log(node, Lane, TraceKind.Info, $"  (flow ends at {Label(node)})");
        }

        public override string? Receive(string signal)
        {
            var node = _waiting.FirstOrDefault(n => string.Equals(n.Get(ExtFieldKeys.AcceptEvent), signal, StringComparison.Ordinal));
            if (node is null) return null;
            _waiting.Remove(node);
            Sim.Close(node, Lane);
            Sim.Log(node, Lane, TraceKind.Accept, $"⚑ {Label(node)} accepts {signal}");
            Begin(node);
            return Row(node);
        }

        public override void AfterEvent()
        {
            if (Done) return;
            // Buffered tokens are offered again (a guard may have become true).
            foreach (var b in _held.Where(h => h.Value > 0 && IsBuffer(h.Key)).Select(h => h.Key).ToList()) ReleaseBuffer(b);
            if (_inFlight == 0 && _running.Count == 0 && _waiting.Count == 0)
            {
                Sim.Log(Behavior, Lane, TraceKind.Complete, $"■ activity '{Label(Behavior)}' has no more tokens");
                foreach (var (node, n) in _held.Where(h => h.Value > 0))
                    Sim.Log(node, Lane, TraceKind.Info, IsStore(node) ? $"  (data store {Label(node)} keeps its data)" : $"  (central buffer {Label(node)} still holds {n} token{(n == 1 ? "" : "s")})");
                Stop();
            }
        }

        private void Stop()
        {
            Done = true;
            foreach (var n in _running.Concat(_waiting)) Sim.Close(n, Lane);
            _running.Clear();
            _waiting.Clear();
        }
    }

    /// <summary>
    /// A state machine on the shared clock, run by <see cref="StateMachineEngine"/> (orthogonal regions, pseudostates, history).
    /// Each orthogonal region gets its own timeline row.
    /// </summary>
    private sealed class MachineExec : Execution, IStateMachineHost
    {
        private readonly StateMachineEngine _engine;
        private readonly Dictionary<Element, long> _entered = new(ReferenceEqualityComparer.Instance);
        private long _stamp;

        public MachineExec(DiscreteEventSimulation sim, Element machine, string lane) : base(sim, machine, lane) =>
            _engine = new StateMachineEngine(sim.Model, machine, this);

        public StateMachineEngine Engine => _engine;

        public override IEnumerable<Element> Active => Done ? [] : _engine.Active;

        private void Sync() => Done = _engine.Finished;

        public override void Start()
        {
            Sim.Log(Behavior, Lane, TraceKind.Start, $"▶ state machine '{Label(Behavior)}' started");
            Sim.Schedule(Sim.Clock, () => { _engine.Start(); Sync(); });
        }

        /// <summary>The timeline row of a state: one per orthogonal region of the innermost composite with several regions.</summary>
        private string Row(Element state)
        {
            for (var x = state; x.Owner is { } o; x = o)
            {
                var regions = _engine.Regions(o);
                if (regions.Count > 1)
                {
                    var i = regions.FirstOrDefault(r => r.Members.Contains(x))?.Index ?? 0;
                    return ReferenceEquals(o, Behavior) ? $"{Lane} [{i + 1}]" : $"{Lane} · {Label(o)} [{i + 1}]";
                }
                if (ReferenceEquals(o, Behavior)) break;
            }
            return Lane;
        }

        void IStateMachineHost.Entered(Element state)
        {
            if (state.Kind != ElementKind.State) return;
            _entered[state] = ++_stamp;
            if (state.Owner is { Kind: ElementKind.State } parent) Sim.Close(parent, Lane);   // the composite's bar gives way to its substates' bars
            Sim.Open(state, Lane, Row(state), "state");
            // Time events of every transition leaving the newly entered state.
            foreach (var t in Sim.Model.Outgoing(state, ElementKind.Transition))
            {
                var (kind, arg) = ParseTrigger(t.Name);
                if (kind != TriggerKind.After) continue;
                var d = ParseDuration(arg, Sim.Variables);
                if (d is null) { Sim.Log(t, Lane, TraceKind.Warning, $"  ⚠ cannot read time '{arg}'"); continue; }
                var stamp = _stamp;
                var transition = t;
                Sim.Schedule(Sim.Clock + d.Value, () =>
                {
                    if (Done || !_entered.TryGetValue(state, out var s) || s != stamp) return;   // left the state meanwhile
                    _engine.TryFire(transition, $"after({arg})");
                    Sync();
                });
            }
        }

        void IStateMachineHost.Exited(Element state)
        {
            Sim.Close(state, Lane);
            _entered.Remove(state);
        }

        void IStateMachineHost.Exec(string? statements, string what) => Sim.Exec(statements, Lane, what);

        bool IStateMachineHost.Guard(Relationship transition) => Sim.Guard(transition, Lane);

        void IStateMachineHost.Log(MachineEventKind kind, Element? element, string text)
        {
            if (kind == MachineEventKind.Complete && text.StartsWith("■ Final state", StringComparison.Ordinal))
                text = $"■ state machine '{Label(Behavior)}' reached its final state";
            Sim.Log(element ?? Behavior, Lane, kind switch
            {
                MachineEventKind.Enter => TraceKind.Enter,
                MachineEventKind.Exit => TraceKind.Exit,
                MachineEventKind.Transition => TraceKind.Transition,
                MachineEventKind.Complete => TraceKind.Complete,
                MachineEventKind.Warning or MachineEventKind.Error => TraceKind.Warning,
                _ => TraceKind.Info,
            }, text);
        }

        public override string? Receive(string signal)
        {
            if (Done) return null;
            var n = _engine.Dispatch(t => ParseTrigger(t.Name) is (TriggerKind.Signal, var s) && s == signal, _ => signal);
            Sync();
            if (n == 0) { Sim.Log(Behavior, Lane, TraceKind.Info, $"  {signal} ignored in {_engine.Configuration}"); return null; }
            return Lane;
        }

        /// <summary>Completion transitions and change events, re-checked after every event (bounded per event).</summary>
        public override void AfterEvent()
        {
            for (var guard = 0; guard < 100 && !Done; guard++)
            {
                var fired = _engine.FireFirst(t => ParseTrigger(t.Name).Kind == TriggerKind.Completion, completion: true, _ => "completion")
                            || _engine.FireFirst(t => ParseTrigger(t.Name) is (TriggerKind.When, var c) && Sim.Condition(c, Lane), completion: false,
                                t => $"when({ParseTrigger(t.Name).Arg})");
                Sync();
                if (!fired) return;
            }
        }
    }

    /// <summary>The engines of the state machines being simulated (for inspecting their configuration).</summary>
    public IReadOnlyList<StateMachineEngine> Machines => _execs.OfType<MachineExec>().Select(e => e.Engine).ToList();
}
