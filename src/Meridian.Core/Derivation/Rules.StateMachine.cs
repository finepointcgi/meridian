using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>
/// Sequence (or activity) → state machine for one block. The block waits in a state; each event it receives is a transition.
/// A synchronous call the block answers with a reply is handled in a "Handling …" state and returns to the state it came from;
/// an event without a reply moves the block on to an "After …" state. What the block sends while it handles an event becomes
/// the transition's effect. Alternatives (alt / opt / break operands, or the branches of a decision in an activity) become
/// guarded transitions, through a choice pseudostate when the branches start with the same event.
/// </summary>
internal static class ToStateMachine
{
    // ------------------------------------------------------------------ what the block sees, in order

    /// <summary>One message as the block sees it: an event it receives, something it sends, or its reply.</summary>
    internal sealed record Seen(string Key, SeenKind Kind, string Name);

    internal enum SeenKind { Other, Event, Send, Reply }

    /// <summary>Alternative operands: each a guard and the keys of the messages it holds.</summary>
    internal sealed record Alternatives(string Key, IReadOnlyList<(string Guard, IReadOnlySet<string> Keys)> Branches)
    {
        public int Size => Branches.Sum(b => b.Keys.Count);
        public bool Holds(string key) => Branches.Any(b => b.Keys.Contains(key));
    }

    internal abstract record Step;
    internal sealed record EventStep(string Name, bool Replied, string Effect) : Step;
    internal sealed record AltStep(string Key, IReadOnlyList<(string Guard, IReadOnlyList<Step> Steps)> Branches) : Step;

    private static bool IsBlock(Element? lifeline, Element block) => lifeline?.TypeRef == block.Id;

    /// <summary>The messages of an interaction as <paramref name="block"/> sees them, and the alternatives drawn on its sequence diagram.</summary>
    internal static (List<Seen> Seen, List<Alternatives> Alts) FromSequence(ModelRepository m, Element interaction, Element block)
    {
        var messages = InteractionReader.Messages(m, interaction);
        var seen = messages.Select(r =>
        {
            var self = ReferenceEquals(r.Source, r.Target);
            var kind = self ? SeenKind.Other
                : IsBlock(r.Target, block) && r.Get(FieldKeys.MessageSort) != "reply" && r.Name.Length > 0 ? SeenKind.Event
                : IsBlock(r.Source, block) && r.Get(FieldKeys.MessageSort) == "reply" ? SeenKind.Reply
                : IsBlock(r.Source, block) && r.Name.Length > 0 ? SeenKind.Send
                : SeenKind.Other;
            return new Seen(r.Id, kind, DeriveText.Identifier(r.Name));
        }).ToList();
        var alts = new List<Alternatives>();
        var d = m.Diagrams.FirstOrDefault(x => x.DiagramType == DiagramType.Sequence && ReferenceEquals(x.Owner, interaction));
        if (d == null) return (seen, alts);
        var ys = messages.Select(r => (r.Id, Y: d.EdgeFor(r) is { } e ? Notation.MessageY(e) : double.NaN)).Where(t => !double.IsNaN(t.Y)).ToList();
        foreach (var f in d.Nodes.Where(n => n.Element.Kind == ElementKind.CombinedFragment && n.Element.Get(FieldKeys.Operator) is "" or "alt" or "opt" or "break"))
        {
            var bands = InteractionOperands.Layout(f, f.Bounds);
            var branches = bands.Select(band => (Guard: band.Operand?.Get(FieldKeys.Guard) ?? f.Element.Get(FieldKeys.Guard),
                Keys: (IReadOnlySet<string>)ys.Where(t => t.Y >= band.Top && t.Y < band.Bottom).Select(t => t.Id).ToHashSet(StringComparer.Ordinal))).ToList();
            // opt, break and a one-operand alt: the other way is to skip what they hold.
            if (branches.Count == 1) branches.Add(("else", new HashSet<string>()));
            if (branches.Any(x => x.Keys.Count > 0)) alts.Add(new Alternatives($"alt:{f.Element.Id}", branches));
        }
        return (seen, alts);
    }

    /// <summary>The hand-overs of an activity as <paramref name="block"/>'s swimlane sees them, and its decisions.</summary>
    internal static (List<Seen> Seen, List<Alternatives> Alts) FromActivity(ModelRepository m, Element activity, Element block)
    {
        var g = new ActivityGraph(m, activity);
        var messages = ActivityToSequence.Messages(m, g);
        var fragments = ActivityToSequence.PlanFragments(m, g, activity, messages);
        messages = ActivityToSequence.Regroup(messages, fragments);
        var seen = messages.Select(p => new Seen(p.Key,
            p.ToLane.Get(FieldKeys.Represents) == block.Id ? SeenKind.Event : p.FromLane.Get(FieldKeys.Represents) == block.Id ? SeenKind.Send : SeenKind.Other,
            DeriveText.Identifier(p.Action.Name))).ToList();
        var alts = fragments.Where(f => f.Operator == "alt")
            .Select(f => new Alternatives(f.Key.Replace("fragment:", "alt:"), f.Operands.Select(o => (o.Guard, (IReadOnlySet<string>)o.Messages.Select(p => p.Key).ToHashSet(StringComparer.Ordinal))).ToList()))
            .ToList();
        return (seen, alts);
    }

    /// <summary>The block's steps: its events (with replies and effects) and the alternatives that hold some of them.</summary>
    internal static List<Step> Steps(IReadOnlyList<Seen> seen, IReadOnlyList<Alternatives> alts)
    {
        var steps = new List<Step>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        EventStep? last = null;
        var effects = new List<string>();
        void Flush()
        {
            if (last == null) return;
            steps[^1] = last with { Effect = string.Join("; ", effects.Select(x => x + "()")) };
            effects.Clear();
        }
        for (var i = 0; i < seen.Count; i++)
        {
            var s = seen[i];
            if (used.Contains(s.Key)) continue;
            var alt = alts.Where(a => a.Holds(s.Key)).OrderByDescending(a => a.Size).FirstOrDefault();
            if (alt != null)
            {
                var inner = alts.Where(a => !ReferenceEquals(a, alt) && a.Branches.All(b => b.Keys.All(alt.Holds))).ToList();
                var branches = alt.Branches.Select(b => (b.Guard, (IReadOnlyList<Step>)Steps(seen.Where(x => b.Keys.Contains(x.Key)).ToList(), inner))).ToList();
                foreach (var b in alt.Branches) used.UnionWith(b.Keys);
                if (branches.All(b => !b.Item2.OfType<EventStep>().Any() && !b.Item2.OfType<AltStep>().Any())) continue;
                Flush();
                last = null;
                steps.Add(new AltStep(alt.Key, branches));
                continue;
            }
            switch (s.Kind)
            {
                case SeenKind.Event:
                    Flush();
                    last = new EventStep(s.Name, false, "");
                    steps.Add(last);
                    break;
                case SeenKind.Reply when last != null:
                    last = last with { Replied = true };
                    steps[^1] = last;
                    break;
                case SeenKind.Send when last != null:
                    effects.Add(s.Name);
                    break;
            }
        }
        Flush();
        return steps;
    }

    // ------------------------------------------------------------------ offers

    /// <summary>Blocks a state machine can be derived for from this source: lifeline types that receive messages, or swimlane blocks.</summary>
    public static IEnumerable<Element> Candidates(ModelRepository m, Element source) => source.Kind switch
    {
        ElementKind.Interaction => InterfaceTargets(InteractionReader.Messages(m, source).Where(r => !ReferenceEquals(r.Source, r.Target)).Select(r => m.ResolveType(r.Target))),
        ElementKind.Activity => InterfaceTargets(source.OwnedElements.Where(p => p.Kind == ElementKind.Partition).Select(p => m.Find(p.Get(FieldKeys.Represents)))),
        _ => [],
    };

    private static IEnumerable<Element> InterfaceTargets(IEnumerable<Element?> xs) =>
        xs.OfType<Element>().Where(t => t.Kind is ElementKind.Block or ElementKind.Class or ElementKind.Component).Distinct();

    // ------------------------------------------------------------------ planning

    /// <summary>Where the next transition starts: a state, or a choice (whose outgoing transitions carry no trigger), with a pending guard.</summary>
    private sealed record From(string Key, string Guard, bool Choice, string Home);

    public static void Build(ModelRepository m, PlanBuilder b, DerivationOptions o)
    {
        var source = b.Plan.Source;
        var block = b.Plan.Target!;
        var (seen, alts) = source.Kind == ElementKind.Interaction ? FromSequence(m, source, block) : FromActivity(m, source, block);
        var steps = Steps(seen, alts);
        if (!steps.Any())
        {
            b.Warn($"{block.DisplayName} receives nothing in {source.DisplayName}, so there are no events to derive states from.");
            return;
        }
        var stm = b.Element("statemachine", ElementKind.StateMachine, block, $"{block.Name} states", null,
            (FieldKeys.Documentation, $"Derived from {source.DisplayName}: the states {block.DisplayName} waits in between the events it receives."));
        var sm = ItemRef.To(stm.Key);
        b.Element("initial", ElementKind.InitialState, sm, null, new PlannedShape("diagram"));
        b.Element("state:0", ElementKind.State, sm, "Idle", new PlannedShape("diagram"));
        b.Rel("transition:initial", ElementKind.Transition, ItemRef.To("initial"), ItemRef.To("state:0"));

        var states = 0;
        var transitions = 0;
        var choices = 0;
        void Transition(From from, string to, string trigger, string effect)
        {
            var t = b.Rel($"transition:{++transitions}", ElementKind.Transition, ItemRef.To(from.Key), ItemRef.To(to), from.Choice ? null : trigger);
            if (from.Guard.Length > 0) t.Fields[FieldKeys.Guard] = from.Guard;
            if (effect.Length > 0) t.Fields[FieldKeys.Effect] = effect;
        }
        string State(string name, string? doActivity = null)
        {
            var key = $"state:{++states}";
            var s = b.Element(key, ElementKind.State, sm, name, new PlannedShape("diagram"));
            if (doActivity != null) s.Fields[FieldKeys.DoActivity] = doActivity;
            return key;
        }

        List<From> Run(IReadOnlyList<Step> list, List<From> current)
        {
            foreach (var step in list)
            {
                switch (step)
                {
                    case EventStep ev when ev.Replied:
                    {
                        // Handled and answered: the block returns to the state it was waiting in.
                        var next = new List<From>();
                        foreach (var home in current.GroupBy(c => c.Home))
                        {
                            var h = State($"Handling {ev.Name}", ev.Name);
                            foreach (var c in home) Transition(c, h, ev.Name, ev.Effect);
                            Transition(new From(h, "", false, h), home.Key, "", "");
                            next.Add(new From(home.Key, "", false, home.Key));
                        }
                        current = next;
                        break;
                    }
                    case EventStep ev:
                    {
                        var s = State($"After {ev.Name}");
                        foreach (var c in current) Transition(c, s, ev.Name, ev.Effect);
                        current = [new From(s, "", false, s)];
                        break;
                    }
                    case AltStep alt:
                    {
                        var firsts = alt.Branches.Select(x => x.Steps.FirstOrDefault() as EventStep).ToList();
                        var shared = firsts.All(f => f != null) && firsts.Select(f => f!.Name).Distinct().Count() == 1;
                        var merged = new List<From>();
                        if (shared)
                        {
                            // The same event leads into every branch: the guards are evaluated in a choice after it.
                            var key = $"choice:{++choices}";
                            b.Element(key, ElementKind.Pseudostate, sm, null, new PlannedShape("diagram"), (FieldKeys.Subtype, Pseudostates.Choice));
                            foreach (var c in current) Transition(c, key, firsts[0]!.Name, "");
                            var home = current.Select(c => c.Home).FirstOrDefault() ?? "state:0";
                            foreach (var (guard, branch) in alt.Branches)
                                merged.AddRange(Run(branch, [new From(key, guard, true, home)]));
                        }
                        else
                        {
                            foreach (var (guard, branch) in alt.Branches)
                            {
                                // Nothing for the block on this branch: it stays where it is (a choice still needs the guard).
                                if (branch.Count == 0) { merged.AddRange(current.Select(c => c.Choice ? c with { Guard = Join(c.Guard, guard) } : c)); continue; }
                                merged.AddRange(Run(branch, current.Select(c => c with { Guard = Join(c.Guard, guard) }).ToList()));
                            }
                        }
                        current = merged.Select(c => c with { Guard = c.Choice ? c.Guard : "" }).DistinctBy(c => (c.Key, c.Guard)).ToList();
                        break;
                    }
                }
            }
            return current;
        }

        var end = Run(steps, [new From("state:0", "", false, "state:0")]);
        // Back to Idle once the last event is dealt with.
        var back = 0;
        foreach (var c in end.Where(c => c.Key != "state:0").DistinctBy(c => c.Key))
            b.Rel(back++ == 0 ? "transition:back" : $"transition:back:{back}", ElementKind.Transition, ItemRef.To(c.Key), ItemRef.To("state:0"), null,
                c.Guard.Length > 0 ? [(FieldKeys.Guard, c.Guard)] : []);

        if (block.OwnedElements.Any(e => e.Kind == ElementKind.StateMachine && !DeriveFields.IsDerived(e)))
            b.Warn($"{block.DisplayName} already has a state machine; this adds another one.");
        b.Warn("States are inferred from the order of the events and the replies (approximate): merge states that are really the same and add the transitions the source does not show.");
        b.Diagram("diagram", DiagramType.StateMachine, sm, $"{block.Name} states", autoLayout: true,
            $"The states of {block.DisplayName} as inferred from {source.DisplayName}.");
    }

    private static string Join(string a, string c) => a.Length == 0 ? c : c.Length == 0 ? a : $"{a} and {c}";
}
