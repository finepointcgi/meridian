using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Core.Derivation;

/// <summary>Reads an interaction: its lifelines and its messages in the order they are drawn (or owned).</summary>
internal static class InteractionReader
{
    public static IReadOnlyList<Element> Lifelines(Element interaction) =>
        interaction.OwnedElements.Where(e => e.Kind == ElementKind.Lifeline).ToList();

    public static IReadOnlyList<Relationship> Messages(ModelRepository m, Element interaction)
    {
        var lifelines = Lifelines(interaction).ToHashSet();
        var all = lifelines.SelectMany(m.RelationshipsOf).Where(r => r.Kind == ElementKind.Message).Distinct().ToList();
        var seq = m.Diagrams.FirstOrDefault(d => d.DiagramType == DiagramType.Sequence && ReferenceEquals(d.Owner, interaction));
        var ownedOrder = all.Select((r, i) => (r, i)).ToDictionary(t => t.r, t => (double)t.i);
        double Y(Relationship r) => seq?.EdgeFor(r) is { } edge ? Notation.MessageY(edge) : 1e6 + ownedOrder[r];
        return all.OrderBy(Y).ThenBy(r => ownedOrder[r]).ToList();
    }

    public static string Digest(ModelRepository m, Element interaction, Element? target = null)
    {
        var sb = new StringBuilder().Append(interaction.Name).Append('\n');
        foreach (var l in Lifelines(interaction)) sb.Append("L ").Append(l.Id).Append(' ').Append(l.Name).Append(' ').Append(l.TypeRef).Append('\n');
        foreach (var r in Messages(m, interaction))
            sb.Append("M ").Append(r.Source.Id).Append('>').Append(r.Target.Id).Append(' ').Append(r.Name).Append(' ').Append(r.Get(FieldKeys.MessageSort))
              .Append(' ').Append(r.Get(FieldKeys.Signature)).Append('\n');
        if (target != null)
        {
            sb.Append("T ").Append(target.Id).Append('\n');
            // The state machine reads the alternatives too: which operand each message is in.
            foreach (var alt in ToStateMachine.FromSequence(m, interaction, target).Alts)
                foreach (var (guard, keys) in alt.Branches)
                    sb.Append("O ").Append(alt.Key).Append(" [").Append(guard).Append("] ").Append(string.Join(",", keys.Order(StringComparer.Ordinal))).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>
/// Sequence → BDD + IBD: lifelines become blocks (existing ones reused) and parts of a context block; messages become
/// operations on the receiving block (asynchronous ones «reception»s) traced from the message; parts that exchange messages
/// get ports and a connector, drawn on the context block's IBD.
/// </summary>
internal static class SequenceToStructure
{
    private static bool CanHaveFeatures(ElementKind k) => k is ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.InterfaceBlock or ElementKind.Actor;
    private static bool CanHavePorts(ElementKind k) => k is ElementKind.Block or ElementKind.Class or ElementKind.Component or ElementKind.InterfaceBlock;

    public static void Build(ModelRepository m, PlanBuilder b, DerivationOptions o)
    {
        var ia = b.Plan.Source;
        var package = AutoDiagrammer.NearestPackage(ia) ?? m.Root;
        var lifelines = InteractionReader.Lifelines(ia);
        if (lifelines.Count == 0) { b.Warn($"“{ia.Name}” has no lifelines: nothing to derive."); return; }
        var messages = InteractionReader.Messages(m, ia);

        var ctx = b.Element("context", ElementKind.Block, package, $"{ia.Name} context", new PlannedShape("bdd"),
            (FieldKeys.Documentation, $"The parts that take part in the interaction {ia.DisplayName}."));
        var ctxRef = ItemRef.To(ctx.Key);

        // A block per lifeline: its type when it has one, a block of the same name, or a new block.
        var blockOf = new Dictionary<Element, ItemRef>();
        var kindOf = new Dictionary<Element, ElementKind>();
        foreach (var ll in lifelines)
        {
            if (m.ResolveType(ll) is { } t && CanHaveFeatures(t.Kind))
            {
                blockOf[ll] = t;
                kindOf[ll] = t.Kind;
                b.Show(t, new PlannedShape("bdd"));
                continue;
            }
            var name = ll.Name.Length > 0 ? char.ToUpperInvariant(ll.Name[0]) + ll.Name[1..] : "Participant";
            if (m.OfKind(ElementKind.Block).FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same)
            {
                blockOf[ll] = same;
                kindOf[ll] = same.Kind;
                b.Show(same, new PlannedShape("bdd"));
                continue;
            }
            var key = $"block:{name.ToLowerInvariant()}";
            if (b.Plan[key] == null)
                b.Element(key, ElementKind.Block, package, name, new PlannedShape("bdd"), (FieldKeys.Documentation, $"Created for the lifeline “{ll.DisplayName}” of {ia.DisplayName}."));
            blockOf[ll] = ItemRef.To(key);
            kindOf[ll] = ElementKind.Block;
            b.Warn($"The lifeline “{ll.DisplayName}” has no type; a block “{name}” is proposed for it (the lifeline itself is left as it is).");
        }

        // Parts of the context block, and composition to each block.
        var partNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ll in lifelines)
        {
            var name = ll.Name.Length > 0 ? char.ToLowerInvariant(ll.Name[0]) + ll.Name[1..] : "part";
            var unique = name;
            for (var i = 2; !partNames.Add(unique); i++) unique = name + i;
            var part = b.Element($"part:{ll.Id}", ElementKind.Property, ctxRef, unique, new PlannedShape("ibd"), (FieldKeys.Multiplicity, "1"));
            PlanBuilder.SetRef(part, FieldKeys.Type, blockOf[ll]);
            if (kindOf[ll] != ElementKind.Actor && b.Plan[$"composition:{blockOf[ll]}"] == null)
                b.Rel($"composition:{blockOf[ll]}", ElementKind.Composition, ctxRef, blockOf[ll], null, (FieldKeys.Multiplicity, "1"));
        }

        // Operations (receptions for asynchronous messages) on the receiver, traced from each message.
        foreach (var msg in messages)
        {
            if (msg.Get(FieldKeys.MessageSort) == "reply" || msg.Name.Length == 0 || !blockOf.TryGetValue(msg.Target, out var receiver)) continue;
            var async = msg.Get(FieldKeys.MessageSort) == "asynchCall";
            var name = DeriveText.Identifier(msg.Name);
            ItemRef op;
            if (receiver.Element is { } re && re.OwnedElements.FirstOrDefault(x => x.Kind == ElementKind.Operation && x.Name == name && !DeriveFields.IsDerived(x)) is { } existing)
                op = existing;
            else
            {
                var key = $"operation:{receiver}:{name}";
                if (b.Plan[key] == null)
                {
                    var item = b.Element(key, ElementKind.Operation, receiver, name, null,
                        (FieldKeys.Stereotypes, async ? "reception" : ""), (FieldKeys.Signature, msg.Get(FieldKeys.Signature)));
                    if (async) item.Note = "asynchronous: a «reception» (Meridian has no separate Signal element)";
                }
                op = ItemRef.To(key);
            }
            b.Rel($"trace:{msg.Id}", ElementKind.Trace, msg, op);
        }

        // Ports and a connector between parts that exchange messages.
        var pairs = messages.Where(x => !ReferenceEquals(x.Source, x.Target) && blockOf.ContainsKey(x.Source) && blockOf.ContainsKey(x.Target))
                            .Select(x => string.CompareOrdinal(x.Source.Id, x.Target.Id) < 0 ? (A: x.Source, B: x.Target) : (A: x.Target, B: x.Source))
                            .Distinct().ToList();
        foreach (var (a, c) in pairs)
        {
            ItemRef End(Element self, Element other, out bool isPort)
            {
                isPort = CanHavePorts(kindOf[self]);
                if (!isPort) return ItemRef.To($"part:{self.Id}");
                var portName = (other.Name.Length > 0 ? char.ToLowerInvariant(other.Name[0]) + other.Name[1..] : "peer") + "Port";
                if (blockOf[self].Element is { } be && be.OwnedElements.FirstOrDefault(x => x.Kind == ElementKind.Port && x.Name == portName) is { } p) return p;
                var key = $"port:{self.Id}:{other.Id}";
                if (b.Plan[key] == null) b.Element(key, ElementKind.Port, blockOf[self], portName, null, (FieldKeys.Multiplicity, "1"));
                return ItemRef.To(key);
            }
            var ea = End(a, c, out var aPort);
            var ec = End(c, a, out var cPort);
            var conn = b.Rel($"connector:{a.Id}:{c.Id}", ElementKind.Connector, ea, ec);
            if (aPort) conn.FieldRefs[FieldKeys.SourcePart] = $"part:{a.Id}";
            if (cPort) conn.FieldRefs[FieldKeys.TargetPart] = $"part:{c.Id}";
        }

        b.Diagram("bdd", DiagramType.BlockDefinition, package, $"{ia.Name} structure", autoLayout: true,
            $"The blocks that take part in {ia.DisplayName}, the operations its messages call, and the context block that composes them.");
        b.Diagram("ibd", DiagramType.InternalBlock, ctxRef, $"{ia.Name} context", autoLayout: true,
            $"How the parts of {ia.DisplayName} are connected: one connector per pair that exchanges messages.");
    }
}

