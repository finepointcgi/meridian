using System.Text;
using Meridian.Core.Deployment;
using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>Deployment notation: nodes, devices and execution environments as 3D boxes, artifacts with the document icon.</summary>
public static partial class SvgRenderer
{
    /// <summary>Depth of a node's 3D box, drawn above and to the right of the shape's bounds (the front face is the shape).</summary>
    public const double NodeDepth = 10;

    /// <summary>Draws a deployment shape; false for any other element (the caller draws it).</summary>
    private static bool RenderDeployment(StringBuilder sb, ModelRepository m, Diagram d, DiagramNode n, Bounds b, NodeContent c, string fill, string header, SvgTheme t)
    {
        var kind = n.Element.Kind;
        if (!DeploymentModel.IsNode(kind) && !DeploymentModel.IsArtifact(kind)) return false;
        // Untinted (no stereotype colour, no fill style): nodes take the behaviour blue, artifacts the value-type teal.
        if (header == t.Header(kind) && StyleColor(n, "fill") == null)
            header = t.Header(DeploymentModel.IsNode(kind) ? ElementKind.Activity : ElementKind.ValueType);
        // A shape with nested shapes shows only its title band; the nested nodes and artifacts fill the body.
        var content = ContainerLayout.IsContainer(d, n) ? c with { Compartments = [] } : c;
        if (DeploymentModel.IsNode(kind))
        {
            const double k = NodeDepth;
            sb.Append($"<path class=\"node-3d\" d=\"M{F(b.X)},{F(b.Y)} L{F(b.X + k)},{F(b.Y - k)} H{F(b.Right + k)} V{F(b.Bottom - k)} L{F(b.Right)},{F(b.Bottom)} V{F(b.Y)} Z\" fill=\"{header}\" stroke=\"{t.Stroke}\"/>");
            sb.Append($"<path d=\"M{F(b.Right)},{F(b.Y)} L{F(b.Right + k)},{F(b.Y - k)}\" fill=\"none\" stroke=\"{t.Stroke}\"/>");
            RenderCompartments(sb, b, content, fill, kind, t, header);
            return true;
        }
        RenderCompartments(sb, b, content, fill, kind, t, header);
        // The artifact icon: a page with a folded corner, top right.
        double x = b.Right - 20, y = b.Y + 7, w = 12, h = 15, f = 4;
        sb.Append($"<path class=\"artifact-icon\" d=\"M{F(x)},{F(y)} H{F(x + w - f)} L{F(x + w)},{F(y + f)} V{F(y + h)} H{F(x)} Z M{F(x + w - f)},{F(y)} V{F(y + f)} H{F(x + w)}\" fill=\"{t.NodeFill}\" stroke=\"{t.Muted}\" stroke-width=\"1\"/>");
        return true;
    }
}
