using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Safety;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>Safety &amp; reliability analysis (docs/SAFETY.md): FMEA worksheet, fault trees, ribbon tab and ⌘K commands.</summary>
public partial class Workbench
{
    /// <summary>Bottom-dock index of the safety panel (FMEA worksheet and fault tree analysis).</summary>
    public const int SafetyDock = 21;

    /// <summary>Which tab of the safety panel to show next (0 = FMEA, 1 = fault tree analysis).</summary>
    internal int SafetyTab { get; set; }

    /// <summary>Top event the fault tree tab analyses (null = the active fault tree diagram's top event).</summary>
    internal Element? SafetyTop { get; set; }

    public void OpenFmea()
    {
        SafetyTab = 0;
        OpenDock(SafetyDock);
        StateHasChanged();
    }

    /// <summary>Opens the fault tree analysis tab on <paramref name="top"/> (else the active fault tree, else the selection).</summary>
    public void AnalyzeFaultTree(Element? top = null)
    {
        top ??= Ws.Selected is { Kind: ElementKind.FaultTreeEvent } sel ? TopOf(sel) : null;
        top ??= Ws.Active?.Diagram is { DiagramType: DiagramType.FaultTree } d ? FaultTree.TopEvents(Ws.Model, d).FirstOrDefault() : null;
        top ??= FaultTree.TopEvents(Ws.Model).FirstOrDefault();
        SafetyTop = top;
        SafetyTab = 1;
        OpenDock(SafetyDock);
        if (top == null) Ws.SetStatus("No fault tree yet: create one with Safety ▸ Fault tree");
        StateHasChanged();
    }

    /// <summary>The topmost event above <paramref name="e"/> (itself when nothing is above it).</summary>
    private Element TopOf(Element e)
    {
        var seen = new HashSet<Element>(ReferenceEqualityComparer.Instance);
        var cur = e;
        while (seen.Add(cur) && FaultTree.Outputs(Ws.Model, cur).FirstOrDefault() is { } up) cur = up;
        return cur.Kind == ElementKind.FaultTreeEvent ? cur : e;
    }

    /// <summary>New failure mode on the selected block, part or function (or in the selection's package).</summary>
    public void NewFailureMode()
    {
        var context = Ws.Selected ?? Ws.Active?.Diagram.Owner ?? Ws.Model.Root;
        if (context is Diagram dg) context = dg.Owner ?? Ws.Model.Root;
        if (context is Relationship rel) context = rel.Owner ?? Ws.Model.Root;
        if (context.Kind == ElementKind.FailureMode) context = context.Owner ?? Ws.Model.Root;
        var owner = Ws.Model.DefaultOwnerFor(context, ElementKind.FailureMode);
        try
        {
            Element fm;
            using (Ws.Model.History.Transaction("Create Failure Mode"))
            {
                fm = Ws.Model.Create(owner, ElementKind.FailureMode);
                if (Ws.Active is { } c && Metamodel.PaletteNodes(c.Diagram.DiagramType).Contains(ElementKind.FailureMode)) c.Show(fm);
            }
            Ws.Select(fm);
            Ws.SetStatus($"Created failure mode in {owner.DisplayName} — rate it in Properties or the FMEA worksheet");
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    /// <summary>
    /// New fault tree diagram with a top event and an OR gate under it. With a failure mode selected the top event
    /// represents it; with a block selected the diagram goes in its package and the top event names the block.
    /// </summary>
    public void NewFaultTree()
    {
        if (Ws.ReadOnly) return;
        var sel = Ws.Selected;
        var context = sel ?? Ws.Active?.Diagram.Owner ?? Ws.Model.Root;
        if (context is Diagram dg) context = dg.Owner ?? Ws.Model.Root;
        var package = context.AncestorsAndSelf().FirstOrDefault(a => a.Kind is ElementKind.Package or ElementKind.Model) ?? Ws.Model.Root;
        try
        {
            Diagram d;
            using (Ws.Model.History.Transaction("New Fault Tree"))
            {
                var topName = sel switch
                {
                    { Kind: ElementKind.FailureMode } fm => fm.Get(SafetyFields.Effect) is { Length: > 0 } eff ? eff : fm.Name,
                    { Kind: ElementKind.Block or ElementKind.Property or ElementKind.Activity or ElementKind.Action } item => $"{item.Name} fails",
                    _ => "Top event",
                };
                d = Ws.Model.CreateDiagram(package, DiagramType.FaultTree, $"{topName} fta");
                var top = Ws.Model.Create(package, ElementKind.FaultTreeEvent, topName);
                Ws.Model.Set(top, FieldKeys.Subtype, SafetyFields.Top);
                if (sel is { Kind: ElementKind.FailureMode or ElementKind.Block or ElementKind.Property or ElementKind.Activity or ElementKind.Action })
                    Ws.Model.Set(top, SafetyFields.Ref, sel.Id);
                var gate = Ws.Model.Create(package, ElementKind.FaultTreeGate);
                Ws.Model.Set(gate, FieldKeys.Subtype, SafetyFields.Or);
                var (tw, th) = Notation.DefaultSizeOf(top);
                tw = Math.Clamp(topName.Length * 7.0 + 24, tw, 260);   // long effects get a wider box
                th = topName.Length * 7.0 > tw - 12 ? th + 16 : th;
                var (gw, gh) = Notation.DefaultSizeOf(gate);
                Ws.Model.AddNode(d, top, new Bounds(200, 60, tw, th));
                Ws.Model.AddNode(d, gate, new Bounds(200 + (tw - gw) / 2, 60 + th + 50, gw, gh));
                var link = Ws.Model.Connect(ElementKind.FaultTreeLink, top, gate, package);
                Ws.Model.AddEdge(d, link);
                Ws.Open(d);
            }
            Ws.Select(d, syncCanvas: false);
            Ws.SetStatus("Fault tree created: add events and gates from the palette, then Safety ▸ Analyze");
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    public Task ExportFmeaCsv() => DownloadText(BaseName + " FMEA.csv", "text/csv", Fmea.ToCsv(Ws.Model));

    private IEnumerable<CommandPalette.Command> SafetyPaletteCommands()
    {
        CommandPalette.Command C(string title, string icon, Action a) => new(title, "Command", icon, () => { a(); return Task.CompletedTask; });
        CommandPalette.Command CA(string title, string icon, Func<Task> a) => new(title, "Command", icon, a);
        yield return C("FMEA worksheet (failure modes, RPN, action priority)", Icons.Material.Outlined.ReportProblem, OpenFmea);
        yield return C("New failure mode on selection", Icons.Material.Outlined.ReportProblem, NewFailureMode);
        yield return C("New fault tree", Icons.Material.Outlined.AccountTree, NewFaultTree);
        yield return C("Analyze fault tree (minimal cut sets, top-event probability)", Icons.Material.Outlined.Calculate, () => AnalyzeFaultTree());
        yield return CA("Export FMEA as CSV", Icons.Material.Outlined.Download, ExportFmeaCsv);
        foreach (var top in FaultTree.TopEvents(Ws.Model))
        {
            var t = top;
            yield return C($"Analyze fault tree: {t.DisplayName}", Icons.Material.Outlined.Calculate, () => AnalyzeFaultTree(t));
        }
    }

    /// <summary>Tree context menu: new failure mode on items that can own one; analysis entries for safety elements.</summary>
    private IEnumerable<MenuEntry> SafetyTreeEntries(Element e)
    {
        if (Metamodel.CanOwn(e.Kind, ElementKind.FailureMode) && e.Kind is not (ElementKind.Package or ElementKind.Model))
            yield return MenuEntry.Do("New failure mode", () => { Ws.Select(e); NewFailureMode(); });
        if (e.Kind is ElementKind.FailureMode or ElementKind.Block or ElementKind.Property or ElementKind.Activity or ElementKind.Action)
            yield return MenuEntry.Do("New fault tree for this", () => { Ws.Select(e); NewFaultTree(); });
        if (e.Kind == ElementKind.FaultTreeEvent) yield return MenuEntry.Do("Analyze fault tree", () => AnalyzeFaultTree(TopOf(e)));
        if (e.Kind == ElementKind.FailureMode) yield return MenuEntry.Do("Show in FMEA worksheet", OpenFmea);
    }
}
