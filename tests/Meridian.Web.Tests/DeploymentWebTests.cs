using Bunit;
using Meridian.Core.Deployment;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>Deployment diagrams in the editor: the palette, nesting an artifact in a node, automatic «deploy», the inspector and Deploy to….</summary>
public class DeploymentWebTests : TestContext
{
    public DeploymentWebTests()
    {
        Services.AddMudServices();
        Services.AddScoped<Workspace>();
        Services.AddSingleton<ImageCache>();
        // Sections of the inspector that talk to the server's integrations are not under test here.
        ComponentFactories.AddStub<LinkedIssuesSection>();
        ComponentFactories.AddStub<CommentsSection>();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private (ModelRepository M, Diagram D) Build()
    {
        var m = new ModelRepository("Shop");
        var pkg = m.Create(m.Root, ElementKind.Package, "Runtime");
        var d = m.CreateDiagram(pkg, DiagramType.Deployment, "Runtime");
        m.History.Clear();
        return (m, d);
    }

    private (Workspace Ws, DiagramController C) Open(ModelRepository m, Diagram d)
    {
        var ws = Services.GetRequiredService<Workspace>();
        ws.UseLocalModel(m);
        var c = ws.Open(d);
        c.SetZoom(1);
        return (ws, c);
    }

    private static DiagramNode Place(DiagramController c, PaletteItem item, Pt at)
    {
        var s = c.ToScreen(at);
        c.CreateAtScreen(item, s.X, s.Y);
        c.Editing = null;
        return c.SelectedNodes.Single();
    }

    private static void QuickConnect(DiagramController c, DiagramNode from, DiagramNode to)
    {
        var h = c.ToScreen(from.Bounds.Center);
        c.PointerMove(h.X, h.Y);
        var a = c.ToScreen(c.ArrowPoint(from, 1));
        c.PointerMove(a.X, a.Y);
        c.PointerDown(a.X, a.Y, 0, Mods.None, 1);
        var t = c.ToScreen(to.Bounds.Center);
        c.PointerMove(t.X, t.Y);
        c.PointerUp(Mods.None);
    }

    [Fact]
    public void Palette_Shows_Deployment_Shapes_And_Connectors_And_The_Canvas_Draws_Them()
    {
        var (m, d) = Build();
        var (_, c) = Open(m, d);
        Place(c, new PaletteItem(ElementKind.Node), new Pt(200, 120));
        RenderComponent<MudBlazor.MudPopoverProvider>();
        var cut = RenderComponent<DiagramView>(p => p.Add(x => x.C, c));
        string[] expected = ["Node", "Device", "Execution Environment", "Artifact", "Deployment Specification", "Deployment", "Manifestation", "Communication Path"];
        var markup = cut.Markup;
        foreach (var title in expected) Assert.Contains(title, markup);
        Assert.Contains("node-3d", markup);   // the new node is drawn as a 3D box
    }

    [Fact]
    public void An_Artifact_Created_Inside_A_Node_Is_Nested_And_Deployed_And_A_Line_From_An_Artifact_To_A_Node_Becomes_Deploy()
    {
        var (m, d) = Build();
        var (_, c) = Open(m, d);
        var server = Place(c, new PaletteItem(ElementKind.Device), new Pt(200, 100));
        var env = Place(c, new PaletteItem(ElementKind.ExecutionEnvironment), new Pt(server.Bounds.Center.X, server.Bounds.Y + 80));
        Assert.Equal(server.Id, env.ParentId);
        Assert.Same(server.Element, env.Element.Owner);   // execution environment nested in the device

        var inside = Place(c, new PaletteItem(ElementKind.Artifact), new Pt(c.BoundsOf(env).Center.X, c.BoundsOf(env).Y + 70));
        Assert.Equal(env.Id, inside.ParentId);
        Assert.NotSame(env.Element, inside.Element.Owner);   // owned by the package: deployment is a relationship
        Assert.Equal([env.Element], DeploymentModel.DeployedTo(m, inside.Element));
        var nested = m.Outgoing(inside.Element, ElementKind.Deployment).Single();
        if (d.EdgeFor(nested) is { } shown) Assert.DoesNotContain($"data-edge=\"{shown.Id}\"", Meridian.Serialization.SvgRenderer.Render(m, d));   // shown by nesting

        var node = Place(c, new PaletteItem(ElementKind.Node), new Pt(900, 100));
        var loose = Place(c, new PaletteItem(ElementKind.Artifact), new Pt(900, 500));
        QuickConnect(c, loose, node);
        var r = c.AutoConnected!.Relationship;
        Assert.Equal(ElementKind.Deployment, r.Kind);
        Assert.Same(loose.Element, r.Source);
        Assert.Same(node.Element, r.Target);

        var block = m.Create(m.Root, ElementKind.Block, "Orders");
        var blockNode = m.AddNode(d, block, new Bounds(1300, 500, 180, 110));
        QuickConnect(c, loose, blockNode);
        Assert.Equal(ElementKind.Manifestation, c.AutoConnected!.Relationship.Kind);
        QuickConnect(c, node, server);
        Assert.Equal(ElementKind.CommunicationPath, c.AutoConnected!.Relationship.Kind);
    }

    [Fact]
    public void Inspector_Shows_File_Name_Protocol_Deployments_And_Deploy_To()
    {
        var (m, d) = Build();
        var pkg = d.Owner!;
        var node = m.Create(pkg, ElementKind.Node, "AppServer");
        var other = m.Create(pkg, ElementKind.Node, "Backup");
        var jar = m.Create(pkg, ElementKind.Artifact, "orders.jar");
        m.Set(jar, DeploymentFields.FileName, "orders-service.jar");
        m.Connect(ElementKind.Deployment, jar, node);
        var path = m.Connect(ElementKind.CommunicationPath, node, other);
        m.Set(path, DeploymentFields.Protocol, "HTTPS");
        var (ws, _) = Open(m, d);

        ws.Select(jar);
        RenderComponent<MudBlazor.MudPopoverProvider>();
        RenderComponent<MudBlazor.MudDialogProvider>();
        var inspector = RenderComponent<Inspector>();
        Assert.Contains("File name", inspector.Markup);
        Assert.Contains("orders-service.jar", inspector.Markup);
        Assert.Contains("AppServer", inspector.Find("[data-testid=deploy-targets]").TextContent);
        Assert.NotNull(inspector.Find("[data-testid=deploy-to]"));

        // The section on a node lists what runs there and its communication paths.
        var section = RenderComponent<DeploymentInspectorSection>(p => p.Add(x => x.E, node));
        Assert.Contains("orders.jar", section.Find("[data-testid=deploy-hosted]").TextContent);
        Assert.Contains("HTTPS", section.Find("[data-testid=deploy-paths]").TextContent);

        ws.Select(path);
        var pathInspector = RenderComponent<Inspector>();
        Assert.Contains("Protocol", pathInspector.Markup);
        Assert.Contains("HTTPS", pathInspector.Markup);
    }

    [Fact]
    public void Deploy_To_Dialog_Deploys_The_Selection_And_Draws_The_Lines()
    {
        var (m, d) = Build();
        var pkg = d.Owner!;
        var node = m.Create(pkg, ElementKind.Node, "AppServer");
        var a = m.Create(pkg, ElementKind.Artifact, "a.jar");
        var b = m.Create(pkg, ElementKind.Artifact, "b.jar");
        m.AddNode(d, node, new Bounds(500, 40, 220, 130));
        m.AddNode(d, a, new Bounds(40, 40, 200, 76));
        m.AddNode(d, b, new Bounds(40, 200, 200, 76));
        m.History.Clear();
        var (ws, c) = Open(m, d);

        RenderComponent<MudBlazor.MudPopoverProvider>();
        var provider = RenderComponent<MudBlazor.MudDialogProvider>();
        var dialogs = Services.GetRequiredService<MudBlazor.IDialogService>();
        _ = provider.InvokeAsync(() => DeployDialog.ShowAsync(dialogs, ws, [a, b, node], c));
        provider.WaitForElement("[data-testid=deploy-target]");
        Assert.Contains("2 elements", provider.Find("[data-testid=deploy-hint]").TextContent);   // the node itself is not deployable
        provider.Find("[data-testid=deploy-target]").Click();
        provider.Find("[data-testid=deploy-ok]").Click();

        Assert.Equal([node], DeploymentModel.DeployedTo(m, a));
        Assert.Equal([node], DeploymentModel.DeployedTo(m, b));
        Assert.Equal(2, d.Edges.Count(e => e.Relationship.Kind == ElementKind.Deployment));
        m.History.Undo();
        Assert.Empty(m.OfKind(ElementKind.Deployment));
    }
}
