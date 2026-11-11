using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Safety;

namespace Meridian.Serialization;

public static partial class SvgRenderer
{
    /// <summary>Height of the symbol hanging under a basic, undeveloped or house event's description box.</summary>
    private const double FaultSymbolH = 40;

    /// <summary>
    /// A fault tree event: a description box; basic events hang a circle under it, undeveloped events a diamond and
    /// house events a house. The probability is shown under leaves and computed for top / intermediate events.
    /// </summary>
    private static void RenderFaultEvent(StringBuilder sb, ModelRepository m, Element e, Bounds b, string fill, SvgTheme t)
    {
        var kind = SafetyFields.EventKind(e);
        var leaf = kind is SafetyFields.Basic or SafetyFields.Undeveloped or SafetyFields.House;
        var stroke = t.Stroke;
        var boxH = leaf ? Math.Max(24, b.Height - FaultSymbolH) : b.Height;
        var top = kind == SafetyFields.Top;
        sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(boxH)}\" rx=\"2\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"{(top ? "2" : "1.2")}\" filter=\"url(#msh)\"/>");
        var refName = m.Find(e.Get(SafetyFields.Ref)) is { } r ? r.Name : null;
        // The name gets the lines left after the "represents" and probability lines; the last one shown ends in "…".
        var footer = (refName != null ? 13 : 0) + (!leaf ? 13 : 0);
        var maxLines = Math.Max(1, (int)((boxH - footer - 6) / Line));
        var lines = Wrap(e.DisplayName, Math.Max(6, (int)((b.Width - 12) / CharWidth))).ToList();
        if (lines.Count > maxLines) { lines = lines.Take(maxLines).ToList(); lines[^1] = Trunc(lines[^1] + "…", b.Width - 12); }
        var ny = b.Y + (boxH - footer) / 2 - (lines.Count - 1) * Line / 2 + 4;
        foreach (var l in lines) { Text(sb, b.Center.X, ny, l, false, "middle", color: t.Text); ny += Line; }
        if (refName != null && boxH >= 34)
            Text(sb, b.Center.X, b.Y + boxH - (leaf ? 6 : 18), Trunc("⛓ " + refName, b.Width - 10), false, "middle", italic: true, size: 10, color: t.Muted);

        var p = leaf ? FaultTree.ProbabilityOf(m, e) : FaultTree.CachedProbability(m, e);
        var pText = p is { } pv ? "P = " + SafetyFields.Format(pv) : null;
        if (!leaf)
        {
            if (pText != null) Text(sb, b.Center.X, b.Bottom - 6, pText, true, "middle", size: 10.5, color: t.TraceTitle);
            return;
        }
        var cx = b.Center.X;
        var cy = b.Y + boxH + FaultSymbolH / 2;
        var rr = Math.Min(FaultSymbolH / 2 - 2, b.Width / 4);
        sb.Append($"<line x1=\"{F(cx)}\" y1=\"{F(b.Y + boxH)}\" x2=\"{F(cx)}\" y2=\"{F(cy - rr)}\" stroke=\"{stroke}\"/>");
        switch (kind)
        {
            case SafetyFields.Undeveloped:
                sb.Append($"<polygon points=\"{F(cx)},{F(cy - rr)} {F(cx + rr * 1.3)},{F(cy)} {F(cx)},{F(cy + rr)} {F(cx - rr * 1.3)},{F(cy)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.2\"/>");
                break;
            case SafetyFields.House:
                sb.Append($"<polygon points=\"{F(cx)},{F(cy - rr)} {F(cx + rr)},{F(cy - rr * 0.2)} {F(cx + rr)},{F(cy + rr)} {F(cx - rr)},{F(cy + rr)} {F(cx - rr)},{F(cy - rr * 0.2)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.2\"/>");
                Text(sb, cx, cy + rr * 0.55, e.Get(SafetyFields.HouseState) == "true" ? "1" : "0", true, "middle", size: 10, color: t.Text);
                pText = null;
                break;
            default:
                sb.Append($"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(rr)}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.2\"/>");
                break;
        }
        if (pText != null) Text(sb, cx + rr * 1.4 + 4, cy + 4, pText, false, "start", size: 10.5, color: t.TraceTitle);
        else if (kind != SafetyFields.House) Text(sb, cx + rr * 1.4 + 4, cy + 4, "P = ?", false, "start", italic: true, size: 10.5, color: t.Muted);
    }

    /// <summary>A fault tree gate: AND (flat bottom, round top), OR (curved shield) or vote k/n (shield with k/n inside); output at the top.</summary>
    private static void RenderFaultGate(StringBuilder sb, ModelRepository m, Element e, Bounds b, string fill, SvgTheme t)
    {
        var kind = SafetyFields.GateKind(e);
        var stroke = t.Stroke;
        double x = b.X, y = b.Y, w = b.Width, h = b.Height, r = b.Right, bot = b.Bottom, cx = b.Center.X;
        sb.Append($"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"transparent\"/>");
        string d = kind == SafetyFields.And
            ? $"M{F(x)},{F(bot)} V{F(y + h * 0.5)} A{F(w / 2)},{F(h * 0.5)} 0 0 1 {F(r)},{F(y + h * 0.5)} V{F(bot)} Z"
            : $"M{F(x)},{F(bot)} Q{F(cx)},{F(bot - h * 0.28)} {F(r)},{F(bot)} Q{F(r)},{F(y + h * 0.35)} {F(cx)},{F(y)} Q{F(x)},{F(y + h * 0.35)} {F(x)},{F(bot)} Z";
        sb.Append($"<path d=\"{d}\" fill=\"{fill}\" stroke=\"{stroke}\" stroke-width=\"1.4\" filter=\"url(#msh)\"/>");
        var label = kind switch { SafetyFields.Vote => FaultTree.GateSymbol(e, m), SafetyFields.And => "AND", _ => "OR" };
        Text(sb, cx, y + h * 0.62, label, true, "middle", size: kind == SafetyFields.Vote ? 11 : 9.5, color: t.Text);
        if (e.Name.Length > 0) Text(sb, r + 4, y + 12, e.Name, false, "start", size: 11, color: t.Text);
    }
}
