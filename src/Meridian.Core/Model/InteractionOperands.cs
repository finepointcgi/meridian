using System.Globalization;
using Meridian.Core.Language;

namespace Meridian.Core.Model;

/// <summary>One operand of a combined fragment as drawn: the operand (null for a fragment that has none of its own) and its band.</summary>
public readonly record struct OperandBand(Element? Operand, int Index, double Top, double Bottom)
{
    public double Height => Bottom - Top;
}

/// <summary>
/// Interaction operands (<see cref="ElementKind.InteractionOperand"/>) of combined fragments.
/// <para>
/// The operand is a model element owned by its fragment, in order; its guard is the <see cref="FieldKeys.Guard"/> field. Where
/// an operand starts on a sequence diagram (its vertical extent) is view data: the <c>operand:&lt;id&gt;</c> style of the
/// fragment's shape holds the operand's top as an offset from the top of the fragment. The first operand always starts at the
/// top, and operands without a stored offset share the remaining height evenly. A fragment without operand elements is one
/// operand guarded by the fragment's own guard field (how files from before operands keep working).
/// </para>
/// </summary>
public static class InteractionOperands
{
    public const string StylePrefix = "operand:";

    /// <summary>Height of the operator tag in the top-left corner; no separator may go above it.</summary>
    public const double HeaderHeight = 24;

    /// <summary>The smallest band an operand keeps while a separator is dragged.</summary>
    public const double MinBand = 20;

    public static IReadOnlyList<Element> Of(Element fragment) =>
        fragment.OwnedElements.Where(e => e.Kind == ElementKind.InteractionOperand).ToList();

    /// <summary>The guards of a fragment's operands, in order (one entry, the fragment's guard, when it has no operands).</summary>
    public static IReadOnlyList<string> Guards(Element fragment)
    {
        var ops = Of(fragment);
        return ops.Count == 0 ? [fragment.Get(FieldKeys.Guard)] : ops.Select(o => o.Get(FieldKeys.Guard)).ToList();
    }

    /// <summary>The guard of the first operand (what is written next to the operator tag).</summary>
    public static string FirstGuard(Element fragment) => Of(fragment) is { Count: > 0 } ops ? ops[0].Get(FieldKeys.Guard) : fragment.Get(FieldKeys.Guard);

    public static string StyleKey(Element operand) => StylePrefix + operand.Id;

    /// <summary>The stored top of <paramref name="operand"/> relative to the fragment shape, or null.</summary>
    public static double? StoredOffset(DiagramNode fragmentNode, Element operand) =>
        fragmentNode.Style.TryGetValue(StyleKey(operand), out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;

    public static string Format(double offset) => Math.Round(offset, 1).ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The operand bands of a fragment shape drawn at <paramref name="b"/>, top to bottom (always at least one).</summary>
    public static IReadOnlyList<OperandBand> Layout(DiagramNode fragmentNode, Bounds b)
    {
        var ops = Of(fragmentNode.Element);
        if (ops.Count <= 1) return [new OperandBand(ops.FirstOrDefault(), 0, b.Y, b.Bottom)];
        // Tops: stored offsets where present (kept increasing), the rest spread evenly between their neighbours.
        var tops = new double?[ops.Count];
        tops[0] = 0;
        for (var i = 1; i < ops.Count; i++) tops[i] = StoredOffset(fragmentNode, ops[i]);
        var min = Math.Min(HeaderHeight, b.Height / ops.Count);
        for (var i = 1; i < ops.Count; i++)
            if (tops[i] is { } t && (t < (tops[i - 1] ?? 0) + MinBand / 2 || t > b.Height - MinBand / 2)) tops[i] = null;
        var list = new double[ops.Count];
        var k = 0;
        while (k < ops.Count)
        {
            if (tops[k] is { } known) { list[k] = k == 0 ? 0 : Math.Max(known, min); k++; continue; }
            var j = k;
            while (j < ops.Count && tops[j] == null) j++;
            var from = list[k - 1];
            var to = j < ops.Count ? tops[j]!.Value : b.Height;
            var step = (to - from) / (j - k + 1);
            for (var x = k; x < j; x++) list[x] = from + step * (x - k + 1);
            k = j;
        }
        var bands = new List<OperandBand>(ops.Count);
        for (var i = 0; i < ops.Count; i++)
        {
            var top = b.Y + list[i];
            var bottom = i + 1 < ops.Count ? b.Y + list[i + 1] : b.Bottom;
            bands.Add(new OperandBand(ops[i], i, top, Math.Max(top, bottom)));
        }
        return bands;
    }

    /// <summary>The operand band of a fragment shape that holds the height <paramref name="y"/> (clamped to the fragment).</summary>
    public static OperandBand At(DiagramNode fragmentNode, Bounds b, double y)
    {
        var bands = Layout(fragmentNode, b);
        foreach (var band in bands)
            if (y < band.Bottom) return band;
        return bands[^1];
    }

    // ================================================================== editing (each one undo step)

    /// <summary>
    /// Adds an operand at the end of <paramref name="fragment"/>. A fragment without operands first gets one for what it holds
    /// (its guard moves there). On every diagram showing the fragment the last operand is split in two, so the new operand
    /// takes its lower half. Returns the new operand.
    /// </summary>
    public static Element AddOperand(this ModelRepository m, Element fragment, string? guard = null)
    {
        if (fragment.Kind != ElementKind.CombinedFragment) throw new InvalidOperationException("Operands belong to combined fragments.");
        using (m.History.Transaction("Add operand"))
        {
            var views = m.Diagrams.SelectMany(d => d.Nodes.Where(n => ReferenceEquals(n.Element, fragment)).Select(n => (d, n))).ToList();
            var before = views.Select(v => Layout(v.n, v.n.Bounds)).ToList();
            if (Of(fragment).Count == 0)
            {
                var first = m.Create(fragment, ElementKind.InteractionOperand);
                m.Set(first, FieldKeys.Guard, fragment.Get(FieldKeys.Guard));
                m.Set(fragment, FieldKeys.Guard, null);
            }
            var op = m.Create(fragment, ElementKind.InteractionOperand);
            m.Set(op, FieldKeys.Guard, string.IsNullOrWhiteSpace(guard) ? DefaultGuard(fragment) : guard.Trim());
            for (var i = 0; i < views.Count; i++)
            {
                var (d, n) = views[i];
                // Pin the existing separators where they are drawn now, then split the last band.
                foreach (var band in before[i].Skip(1))
                    if (band.Operand != null) m.SetNodeStyle(d, n, StyleKey(band.Operand), Format(band.Top - n.Bounds.Y));
                var last = before[i][^1];
                m.SetNodeStyle(d, n, StyleKey(op), Format((last.Top + last.Bottom) / 2 - n.Bounds.Y));
            }
            return op;
        }
    }

    /// <summary>What a new operand is guarded by: "else" for alt, nothing for par and the others.</summary>
    public static string? DefaultGuard(Element fragment) => fragment.Get(FieldKeys.Operator) is "" or "alt" ? "else" : null;

    /// <summary>Deletes an operand and its separator offsets.</summary>
    public static void RemoveOperand(this ModelRepository m, Element operand)
    {
        if (operand.Kind != ElementKind.InteractionOperand || operand.Owner is not { } fragment) return;
        using (m.History.Transaction("Remove operand"))
        {
            foreach (var d in m.Diagrams)
                foreach (var n in d.Nodes.Where(n => ReferenceEquals(n.Element, fragment)).ToList())
                    m.SetNodeStyle(d, n, StyleKey(operand), null);
            m.Delete(operand);
        }
    }

    /// <summary>
    /// Moves the separator above operand <paramref name="index"/> (≥ 1) of a fragment shape to <paramref name="y"/> (diagram
    /// coordinates), kept between its neighbours. Returns the offset stored.
    /// </summary>
    public static double MoveSeparator(this ModelRepository m, Diagram d, DiagramNode fragmentNode, int index, double y)
    {
        var bands = Layout(fragmentNode, fragmentNode.Bounds);
        if (index < 1 || index >= bands.Count || bands[index].Operand is not { } op) throw new InvalidOperationException("No separator there.");
        var offset = ClampSeparator(bands, fragmentNode.Bounds, index, y) - fragmentNode.Bounds.Y;
        using (m.History.Transaction("Move operand separator"))
        {
            // Pin the other separators too, so moving one never shifts the evenly spread ones.
            foreach (var band in bands.Skip(1))
                if (band.Operand != null && band.Index != index) m.SetNodeStyle(d, fragmentNode, StyleKey(band.Operand), Format(band.Top - fragmentNode.Bounds.Y));
            m.SetNodeStyle(d, fragmentNode, StyleKey(op), Format(offset));
        }
        return offset;
    }

    /// <summary><paramref name="y"/> kept between the separators around separator <paramref name="index"/>.</summary>
    public static double ClampSeparator(IReadOnlyList<OperandBand> bands, Bounds b, int index, double y)
    {
        var lo = (index == 1 ? b.Y + HeaderHeight : bands[index - 1].Top) + MinBand / 2;
        var hi = (index + 1 < bands.Count ? bands[index + 1].Top : b.Bottom) - MinBand / 2;
        return hi < lo ? (lo + hi) / 2 : Math.Clamp(y, lo, hi);
    }
}
