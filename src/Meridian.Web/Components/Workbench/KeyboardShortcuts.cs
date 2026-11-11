namespace Meridian.Web.Components.Workbench;

/// <summary>
/// The keyboard and mouse reference shown by Help ▸ Keyboard &amp; mouse. It mirrors the user guide's
/// <c>docs-site/docs/keyboard-shortcuts.md</c> (a test keeps the two in step) and the handlers in <c>meridian.js</c>,
/// <c>settings.js</c> and <c>DiagramController.Key</c>.
/// </summary>
public static class KeyboardShortcuts
{
    public sealed record Section(string Title, (string Keys, string Action)[] Rows);

    public static readonly Section[] All =
    [
        new("Global",
        [
            ("⌘K", "command palette: commands, diagrams and elements by name"),
            ("⌘S", "commit a version"),
            ("⌘O", "projects"),
            ("⌘F", "find (focus the model explorer search)"),
            ("⇧⌘F", "find and replace across the model"),
            ("⌘P", "print / PDF of the active diagram"),
            ("⌘,", "settings"),
            ("⌘Z", "undo"),
            ("⇧⌘Z or ⌘Y", "redo"),
            ("F7", "validate the model"),
        ]),
        new("Canvas",
        [
            ("Esc", "select tool; clear the selection; cancel an edit"),
            ("Shift while placing", "keep the palette tool for several shapes"),
            ("Double-click empty canvas", "quick-add a shape by searching for its kind"),
            ("Double-click a shape", "rename it"),
            ("Double-click a path", "add a bend point"),
            ("Start typing", "replace the label of the selected shape or path"),
            ("Enter or F2", "edit the label of the selected shape (F2 also edits a selected path)"),
            ("Shift+Enter", "new line in a comment or free shape"),
            ("⌘A", "select all"),
            ("⌘C, ⌘V", "copy, paste"),
            ("⌘D", "duplicate"),
            ("Arrow keys", "nudge by one grid step"),
            ("Shift+arrow keys", "nudge by five grid steps"),
            ("Backspace", "remove from the diagram (keep in the model)"),
            ("Delete or ⌘Backspace", "delete from the model"),
        ]),
        new("Mouse",
        [
            ("Drag on empty canvas", "select with a rectangle"),
            ("Shift-click or ⌘-click", "add to or remove from the selection"),
            ("Drag a hover arrow", "connect to another shape"),
            ("Click a hover arrow", "add a connected shape"),
            ("⌘ + wheel", "zoom"),
            ("Middle-button drag, Space + drag or Alt + drag", "pan"),
            ("Right-click", "context menu"),
            ("Right-click a diagram tab", "Export ▸, close, close others"),
        ]),
        new("Model explorer",
        [
            ("Drag onto a diagram", "show the element there"),
            ("Drag onto another item", "move the element into it"),
            ("Double-click a diagram", "open it"),
        ]),
        new("Menus",
        [
            ("↑ / ↓", "move between menu items"),
            ("→ or Enter on an item with ▸", "open its submenu (Export ▸, New element ▸)"),
            ("←", "back from a submenu"),
            ("Escape", "close the submenu, then the menu"),
        ]),
        new("Text editors",
        [
            ("⌘Enter", "apply SysML v2 text to the model; run a script"),
        ]),
    ];
}
