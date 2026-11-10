using Meridian.Core.Model;

namespace Meridian.Web.Services;

/// <summary>
/// Per-tab state of the phone and tablet shell: which full-screen view is showing, the canvas touch mode, the
/// tablet drawers and the element bottom sheet. Desktop layouts ignore it; the components that read it re-render
/// on <see cref="Changed"/>.
/// </summary>
public sealed class MobileUiState
{
    /// <summary>Below this width the phone shell (top bar + bottom navigation) replaces the desktop layout.</summary>
    public const int PhoneMaxWidth = 900;

    /// <summary>From <see cref="PhoneMaxWidth"/> up to this width the desktop layout keeps its canvas but side panels become drawers.</summary>
    public const int TabletMaxWidth = 1280;

    public enum View { Model, Diagram, Requirements, Reviews, Comments }

    public enum Layout { Desktop, Tablet, Phone }

    public Layout Current { get; private set; } = Layout.Desktop;
    public bool IsPhone => Current == Layout.Phone;
    public bool IsTablet => Current == Layout.Tablet;

    /// <summary>The phone view; the diagram view keeps the real canvas, the others cover it.</summary>
    public View ActiveView { get; private set; } = View.Diagram;

    /// <summary>Touch editing on the canvas. Off (view only) by default so stray drags don't move shapes.</summary>
    public bool EditMode { get; private set; }

    public bool LeftDrawer { get; private set; }
    public bool RightDrawer { get; private set; }

    /// <summary>The element shown in the bottom sheet (null: closed).</summary>
    public Element? SheetElement { get; private set; }
    public bool SheetExpanded { get; private set; }

    /// <summary>The element whose long-press action menu is open (null: closed).</summary>
    public Element? MenuElement { get; private set; }

    /// <summary>The package (or other container) the phone model list is drilled into; null means the model root.</summary>
    public Element? ModelContainer { get; private set; }

    /// <summary>An element the model list should highlight ("Show in tree").</summary>
    public Element? ModelHighlight { get; private set; }

    public event Action? Changed;

    public static Layout LayoutFor(double width) =>
        width < PhoneMaxWidth ? Layout.Phone : width < TabletMaxWidth ? Layout.Tablet : Layout.Desktop;

    /// <summary>Reported by the browser on load and whenever the viewport crosses a breakpoint.</summary>
    public void SetViewport(double width)
    {
        var layout = LayoutFor(width);
        if (layout == Current) return;
        Current = layout;
        if (layout != Layout.Tablet) { LeftDrawer = false; RightDrawer = false; }
        Notify();
    }

    public void Show(View view)
    {
        ActiveView = view;
        MenuElement = null;
        SheetElement = null;
        Notify();
    }

    public void SetEditMode(bool edit)
    {
        if (EditMode == edit) return;
        EditMode = edit;
        Notify();
    }

    public void ToggleDrawer(bool left)
    {
        if (left) { LeftDrawer = !LeftDrawer; RightDrawer = false; }
        else { RightDrawer = !RightDrawer; LeftDrawer = false; }
        Notify();
    }

    public void CloseDrawers()
    {
        if (!LeftDrawer && !RightDrawer) return;
        LeftDrawer = RightDrawer = false;
        Notify();
    }

    public void OpenSheet(Element e, bool expanded = false)
    {
        SheetElement = e;
        SheetExpanded = expanded;
        MenuElement = null;
        Notify();
    }

    public void ToggleSheetExpanded()
    {
        SheetExpanded = !SheetExpanded;
        Notify();
    }

    public void CloseSheet()
    {
        if (SheetElement is null) return;
        SheetElement = null;
        SheetExpanded = false;
        Notify();
    }

    public void OpenMenu(Element e)
    {
        MenuElement = e;
        Notify();
    }

    public void CloseMenu()
    {
        if (MenuElement is null) return;
        MenuElement = null;
        Notify();
    }

    /// <summary>Drills the phone model list into a container (null: the root).</summary>
    public void DrillTo(Element? container, Element? highlight = null)
    {
        ModelContainer = container;
        ModelHighlight = highlight;
        Notify();
    }

    /// <summary>"Show in tree": the model view, drilled into the element's owner with the element highlighted.</summary>
    public void ShowInTree(Element e)
    {
        ModelContainer = e.Owner;
        ModelHighlight = e;
        ActiveView = View.Model;
        SheetElement = null;
        MenuElement = null;
        if (IsTablet) { LeftDrawer = true; RightDrawer = false; }
        Notify();
    }

    public void Notify() => Changed?.Invoke();
}
