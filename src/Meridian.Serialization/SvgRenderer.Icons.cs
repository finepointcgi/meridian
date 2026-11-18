using System.Text;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>Cloud and network icons (<see cref="IconLibrary"/>): icon shapes, group frames and the icon of a deployment node.</summary>
public static partial class SvgRenderer
{
    /// <summary>
    /// An icon shape: the line icon at the top, the name under it and the optional provider text under that. Group icons
    /// (region, virtual network, subnet, container cluster) are a frame with the icon and the name in the top-left corner.
    /// </summary>
    private static void RenderIconShape(StringBuilder sb, DiagramNode n, Bounds b, string text, IconDef icon, SvgTheme t)
    {
        var color = StyleColor(n, "stroke") ?? t.Ink;
        var ink = StyleColor(n, "text") ?? t.Text;
        var provider = n.Element.Get(IconLibrary.ProviderField).Trim();
        sb.Append($"<g class=\"icon-shape\" data-icon=\"{E(icon.Key)}\">");
        if (icon.Container)
        {
            var frame = StyleColor(n, "stroke") ?? t.Muted;
            var fill = StyleColor(n, "fill");
            var dash = icon.Key is "region" or "zone" or "subnet" ? " stroke-dasharray=\"7,4\"" : "";
            sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"6\" fill=\"{fill ?? t.FrameFill}\" fill-opacity=\"{(fill != null ? "1" : "0.55")}\" stroke=\"{frame}\" stroke-width=\"1.4\"{dash}/>");
            const double s = 20;
            sb.Append(IconLibrary.Markup(icon, b.X + 8, b.Y + 8, s, color, 1.7));
            var left = b.X + 8 + s + 7;
            var room = Math.Max(20, b.Right - left - 8);
            Text(sb, left, b.Y + 22, Trunc(text.Length > 0 ? text : icon.Name, room), true, "start", color: ink);
            if (provider.Length > 0) Text(sb, left, b.Y + 36, Trunc(provider, room, 6.2), false, "start", size: 10.5, color: StyleColor(n, "text") ?? t.Muted);
        }
        else
        {
            // Transparent hit area so the whole box can be picked, then the icon centred in the space above the labels.
            sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" fill=\"transparent\"/>");
            if (StyleColor(n, "fill") is { } bg)
                sb.Append($"<rect x=\"{F(b.X)}\" y=\"{F(b.Y)}\" width=\"{F(b.Width)}\" height=\"{F(b.Height)}\" rx=\"8\" fill=\"{bg}\"/>");
            var labels = (text.Length > 0 ? 1 : 0) + (provider.Length > 0 ? 1 : 0);
            var size = Math.Max(12, Math.Min(b.Width - 8, b.Height - 6 - (labels > 0 ? 32 : 0)));   // same size with or without the provider line
            var top = b.Y + 3;
            sb.Append(IconLibrary.Markup(icon, b.Center.X - size / 2, top, size, color));
            var y = top + size + 14;
            var width = Math.Max(b.Width + 30, 60);
            if (text.Length > 0) { Text(sb, b.Center.X, y, Trunc(text, width), true, "middle", size: 12, color: ink); y += 15; }
            if (provider.Length > 0) Text(sb, b.Center.X, y, Trunc(provider, width, 6.2), false, "middle", size: 10.5, color: StyleColor(n, "text") ?? t.Muted);
        }
        sb.Append("</g>");
    }

    /// <summary>The icon a deployment node, device or execution environment has chosen, in its top-left corner.</summary>
    private static void RenderNodeIcon(StringBuilder sb, Element e, Bounds b, SvgTheme t)
    {
        if (e.Kind == ElementKind.Shape || IconLibrary.Of(e) is not { } icon || b.Width < 60 || b.Height < 30) return;
        sb.Append(IconLibrary.Markup(icon, b.X + 6, b.Y + 7, 18, t.Ink, 1.7));
    }
}
