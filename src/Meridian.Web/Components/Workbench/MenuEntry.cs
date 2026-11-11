namespace Meridian.Web.Components.Workbench;

/// <summary>
/// One entry of a <see cref="ContextMenu"/>: a command, a separator, a heading, or a submenu (<see cref="Children"/>).
/// </summary>
public sealed record MenuEntry(string Label, Func<Task>? Action = null, string? Shortcut = null, bool Separator = false, bool Header = false)
{
    /// <summary>The entries of the submenu this entry opens (like Cameo's "Create Element ▸"), or null for a command.</summary>
    public IReadOnlyList<MenuEntry>? Children { get; init; }

    public static MenuEntry Sep() => new("", Separator: true);
    public static MenuEntry Title(string text) => new(text, Header: true);
    public static MenuEntry Do(string label, Action action, string? shortcut = null) => new(label, () => { action(); return Task.CompletedTask; }, shortcut);
    public static MenuEntry Async(string label, Func<Task> action, string? shortcut = null) => new(label, action, shortcut);
    public static MenuEntry Sub(string label, IReadOnlyList<MenuEntry> children) => new(label) { Children = children };
}
