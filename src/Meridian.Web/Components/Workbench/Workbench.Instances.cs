using Meridian.Core.Instances;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Web.Services;
using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// Instance specifications (Cameo-style): "Create instance of…" on blocks (tree, canvas, ⌘K), the instance table tab,
/// object diagrams, and the choice of association for a link when several fit.
/// </summary>
public partial class Workbench
{
    /// <summary>Opens (or brings forward) the instance table of a classifier.</summary>
    public void OpenInstanceTable(Element classifier) => Ws.OpenView(ViewTabKind.Instances, classifier);

    /// <summary>
    /// Builds an instance of <paramref name="classifier"/> with its whole part tree in one undo step and selects it. From a
    /// shape on a block definition or object diagram the new instance tree is also shown beside that shape.
    /// </summary>
    public Element? CreateInstanceOf(Element classifier, DiagramController? on = null, DiagramNode? beside = null)
    {
        if (Ws.ReadOnly) return null;
        try
        {
            Element instance;
            using (Ws.Model.History.Transaction($"Create instance of {classifier.DisplayName}"))
            {
                instance = InstanceModel.CreateInstanceTree(Ws.Model, classifier);
                if (on is { ReadOnly: false } c && beside != null && c.Diagram.DiagramType is DiagramType.Object or DiagramType.BlockDefinition)
                {
                    var b = beside.Bounds;
                    InstanceDiagrams.ShowTree(Ws.Model, c.Diagram, instance, b.Right + 80, b.Y);
                }
            }
            Ws.Select(instance);
            var nested = instance.Descendants().Count(e => e.Kind == ElementKind.InstanceSpecification);
            Ws.SetStatus($"Created {InstanceModel.Label(Ws.Model, instance)}" + (nested > 0 ? $" with {Plural.Of(nested, "part instance")}" : ""));
            return instance;
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); return null; }
    }

    /// <summary>Opens the object diagram showing the instance (creating one with its tree when there is none).</summary>
    public void ShowOnObjectDiagram(Element instance)
    {
        try
        {
            var existing = InstanceDiagrams.DiagramShowing(Ws.Model, instance);
            var d = existing ?? (Ws.ReadOnly ? null : InstanceDiagrams.ObjectDiagramFor(Ws.Model, instance));
            if (d == null) { Ws.SetStatus("No object diagram shows this instance"); return; }
            Ws.Open(d);
            Ws.Select(instance);
        }
        catch (InvalidOperationException ex) { Ws.SetStatus(ex.Message); }
    }

    /// <summary>Asks which association a new link is an instance of (several fit its instances' classifiers).</summary>
    private async Task AskLinkAssociationAsync(Relationship link, IReadOnlyList<Relationship> choices)
    {
        await InvokeAsync(async () =>
        {
            var parameters = new DialogParameters<ChoiceDialog>
            {
                { x => x.Message, $"Several associations connect the classifiers of {link.Source.DisplayName} and {link.Target.DisplayName}. Which one is this link an instance of?" },
                { x => x.Choices, choices.Select(a => (a.Id, AssociationLabel(a))).ToList() },
            };
            var dialog = await Dialogs.ShowAsync<ChoiceDialog>("Link type", parameters);
            if (await dialog.Result is { Canceled: false, Data: string id } && Ws.Model.Contains(link) && Ws.Model.Find(id) is Relationship a)
            {
                Ws.Model.Set(link, InstanceFields.Association, a.Id);
                Ws.SetStatus($"Link typed by {AssociationLabel(a)}");
            }
            else Ws.SetStatus("Link left untyped: pick its association in the inspector");
        });
    }

    internal static string AssociationLabel(Relationship a) =>
        $"{(a.Name.Length > 0 ? a.Name + " " : "")}«{Metamodel.Style(a.Kind).Keyword ?? Metamodel.DisplayName(a.Kind).ToLowerInvariant()}» {a.Source.DisplayName} → {a.Target.DisplayName}";

    /// <summary>Context-menu entries for a classifier (create instance, instance table) or an instance (object diagram, table, duplicate).</summary>
    private List<MenuEntry> InstanceMenuEntries(Element e, DiagramController? on = null, DiagramNode? node = null)
    {
        var m = new List<MenuEntry>();
        if (InstanceModel.CanClassify(e.Kind) && e.Kind is not (ElementKind.Enumeration or ElementKind.Actor))
        {
            if (!Ws.ReadOnly && Ws.CanEdit(e)) m.Add(MenuEntry.Do($"Create instance of {e.DisplayName}", () => CreateInstanceOf(e, on, node)));
            m.Add(MenuEntry.Do("Instance table", () => OpenInstanceTable(e)));
        }
        else if (e.Kind == ElementKind.InstanceSpecification)
        {
            if (on == null || on.Diagram.DiagramType != DiagramType.Object) m.Add(MenuEntry.Do("Show on object diagram", () => ShowOnObjectDiagram(e)));
            if (InstanceModel.MainClassifier(Ws.Model, e) is { } cls) m.Add(MenuEntry.Do($"Instance table of {cls.DisplayName}", () => OpenInstanceTable(cls)));
            if (!Ws.ReadOnly && e.Owner != null) m.Add(MenuEntry.Do("Duplicate instance", () => Ws.Select(InstanceModel.Duplicate(Ws.Model, e))));
            if (on is { ReadOnly: false } c && node != null && InstanceModel.NestedInstances(e).Any(x => !c.Diagram.Shows(x)))
                m.Add(MenuEntry.Do("Show part instances", () => InstanceDiagrams.ShowTree(Ws.Model, c.Diagram, e, node.Bounds.X, node.Bounds.Y)));
        }
        return m;
    }

    private IEnumerable<CommandPalette.Command> InstancePaletteCommands()
    {
        CommandPalette.Command C(string title, string icon, Action a) => new(title, "Command", icon, () => { a(); return Task.CompletedTask; });
        var list = new List<CommandPalette.Command>();
        if (Ws.Selected is { Kind: ElementKind.InstanceSpecification } inst)
            list.Add(C($"Show {inst.DisplayName} on an object diagram", Icons.Material.Outlined.DataObject, () => ShowOnObjectDiagram(inst)));
        foreach (var b in Ws.Model.AllElements.Where(e => e.Kind is ElementKind.Block or ElementKind.Class or ElementKind.Component && !Ws.Model.IsLibraryElement(e))
                     .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            var block = b;
            if (!Ws.ReadOnly) list.Add(C($"Create instance of {block.DisplayName}", Icons.Material.Outlined.DataObject, () => CreateInstanceOf(block)));
            list.Add(C($"Instance table: {block.DisplayName}", Icons.Material.Outlined.TableChart, () => OpenInstanceTable(block)));
        }
        return list;
    }
}
