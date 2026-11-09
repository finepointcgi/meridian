namespace Meridian.Web.Components.Workbench;

public sealed record MenuEntry(string Label, Func<Task>? Action = null, string? Shortcut = null, bool Separator = false, bool Header = false)
{
    public static MenuEntry Sep() => new("", Separator: true);
    public static MenuEntry Title(string text) => new(text, Header: true);
    public static MenuEntry Do(string label, Action action, string? shortcut = null) => new(label, () => { action(); return Task.CompletedTask; }, shortcut);
    public static MenuEntry Async(string label, Func<Task> action, string? shortcut = null) => new(label, action, shortcut);
}
