using System.Text.Json;
using System.Text.RegularExpressions;
using Bunit;
using Meridian.Web.Components.Workbench;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace Meridian.Web.Tests;

/// <summary>
/// Guards for the 0.7 ease-of-use pass (docs/UX-AUDIT-0.7.md): tour targets exist and sit on a known ribbon tab, the in-app
/// shortcut list matches the user guide, context menus nest long lists and close with Escape, and error messages are
/// written for people.
/// </summary>
public sealed class UxAuditTests : TestContext
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Meridian.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("Meridian.slnx not found above " + AppContext.BaseDirectory);
    }

    private static string AllMarkup() => string.Join("\n",
        Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Meridian.Web", "Components"), "*.razor", SearchOption.AllDirectories).Select(File.ReadAllText));

    // ---------------------------------------------------------------- tour

    [Fact]
    public void Every_Tour_Target_Exists_In_The_Markup()
    {
        var markup = AllMarkup();
        var missing = new List<string>();
        foreach (var chapter in TourChapters.All)
            foreach (var step in chapter.Steps.Where(s => s.Selector.Length > 0))
            {
                // A selector list ("a, b") needs one alternative to exist; each alternative needs all its parts.
                var ok = step.Selector.Split(',').Any(alternative =>
                {
                    foreach (Match m in Regex.Matches(alternative, @"\[data-tour=([\w-]+)\]"))
                        if (!markup.Contains($"data-tour=\"{m.Groups[1].Value}\"", StringComparison.Ordinal)) return false;
                    foreach (Match m in Regex.Matches(Regex.Replace(alternative, @"\[[^\]]*\]|:[\w-]+(\([^)]*\))?", ""), @"\.([\w-]+)"))
                        if (!Regex.IsMatch(markup, $@"[cC]lass=""[^""]*(?<![\w-]){Regex.Escape(m.Groups[1].Value)}(?![\w-])")) return false;
                    foreach (Match m in Regex.Matches(alternative, @"(?:^|[\s>])([a-z]+)(?=[.\s>]|$)"))
                        if (!markup.Contains("<" + m.Groups[1].Value, StringComparison.Ordinal)) return false;
                    return true;
                });
                if (!ok) missing.Add($"{chapter.Id}: {step.Title} → {step.Selector}");
            }
        Assert.True(missing.Count == 0, "Tour targets not found:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_Ribbon_Tour_Target_Knows_Its_Ribbon_Tab()
    {
        var ribbonTabs = new[] { "create", "diagram", "requirements", "trace", "review", "views", "simulate", "generate", "extend", "ai", "database", "code" };
        foreach (var step in TourChapters.All.SelectMany(c => c.Steps))
            foreach (Match m in Regex.Matches(step.Selector, @"data-tour=([\w-]+)"))
            {
                Assert.True(Workbench.TourTargetTabs.TryGetValue(m.Groups[1].Value, out var tab), $"{m.Groups[1].Value} has no ribbon tab");
                Assert.Contains(tab, ribbonTabs);
            }
        Assert.Equal("simulate", Workbench.TourTargetTab("[data-tour=verify]"));
        Assert.Equal("code", Workbench.TourTargetTab(".titlebar .chg-badge, [data-tour=code-group]"));
        Assert.Null(Workbench.TourTargetTab(".titlebar .project"));
    }

    [Fact]
    public void Whats_New_And_The_Tour_Cover_The_06_And_07_Features()
    {
        Assert.Equal("0.7", TourChapters.WhatsNewVersion);
        foreach (var id in new[] { "database", "libraries", "verify", "team", TourChapters.ComingFrom })
        {
            Assert.NotNull(TourChapters.Find(id));
            Assert.Contains(TourChapters.WhatsNew, w => w.ChapterId == id);
        }
        Assert.Equal(TourChapters.ComingFrom, TourChapters.All[1].Id);   // right after the basics
        // No tour text still points at the old project-menu Help section.
        Assert.DoesNotContain(TourChapters.All.SelectMany(c => c.Steps), s => s.Html.Contains("project menu: <b>Help", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- keyboard & mouse

    [Fact]
    public void Keyboard_Help_Matches_The_User_Guide()
    {
        var page = File.ReadAllText(Path.Combine(RepoRoot(), "docs-site", "docs", "keyboard-shortcuts.md"));
        var documented = Regex.Matches(page, @"^\| ([^|]+?) \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value.Trim()).ToHashSet();
        foreach (var section in KeyboardShortcuts.All.Where(s => s.Title != "Text editors"))
            foreach (var (keys, _) in section.Rows)
                Assert.True(documented.Contains(keys), $"“{keys}” ({section.Title}) is in Help ▸ Keyboard & mouse but not in keyboard-shortcuts.md");
        // The global shortcuts meridian.js and settings.js handle are all listed.
        var global = KeyboardShortcuts.All[0].Rows.Select(r => r.Keys).ToList();
        foreach (var k in new[] { "⌘K", "⌘S", "⌘O", "⌘F", "⌘,", "⌘Z", "⇧⌘Z or ⌘Y", "F7" }) Assert.Contains(k, global);
    }

    [Fact]
    public async Task Keyboard_Help_Dialog_Lists_Every_Section()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var provider = RenderComponent<MudBlazor.MudDialogProvider>();
        var dialogs = Services.GetRequiredService<MudBlazor.IDialogService>();
        await provider.InvokeAsync(() => dialogs.ShowAsync<KeyboardHelpDialog>("Keyboard & mouse"));
        provider.WaitForAssertion(() => Assert.Equal(KeyboardShortcuts.All.Length, provider.FindAll("table.kbd-table").Count));
        Assert.Contains("⌘K", provider.Markup);
    }

    // ---------------------------------------------------------------- context menu

    [Fact]
    public void Context_Menu_Nests_A_Submenu_That_Runs_Its_Entries()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var ran = "";
        var closed = false;
        var cut = RenderComponent<ContextMenu>(p => p
            .Add(x => x.Entries, new[]
            {
                MenuEntry.Sub("New element", [MenuEntry.Title("Structure"), MenuEntry.Do("Block", () => ran = "block")]),
                MenuEntry.Do("Rename…", () => ran = "rename", "F2"),
            })
            .Add(x => x.X, 100).Add(x => x.Y, 50)
            .Add(x => x.OnClose, () => closed = true));
        Assert.Single(cut.FindAll(".ctx-menu > .ctx-sub"));
        Assert.Equal("menu", cut.Find(".ctx-menu").GetAttribute("role"));
        var parent = cut.Find(".ctx-parent");
        Assert.Equal("false", parent.GetAttribute("aria-expanded"));
        parent.Click();
        Assert.Equal("true", cut.Find(".ctx-parent").GetAttribute("aria-expanded"));
        Assert.Contains("open", cut.Find(".ctx-sub").ClassList);
        cut.FindAll(".ctx-submenu .ctx-item").Single().Click();
        Assert.Equal("block", ran);
        Assert.True(closed);
        JSInterop.VerifyInvoke("meridian.fitMenu");
    }

    [Fact]
    public void Escape_Closes_The_Submenu_Then_The_Menu()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var closed = false;
        var cut = RenderComponent<ContextMenu>(p => p
            .Add(x => x.Entries, new[] { MenuEntry.Sub("New diagram", [MenuEntry.Do("Block Definition Diagram", () => { })]) })
            .Add(x => x.OnClose, () => closed = true));
        cut.Find(".ctx-parent").Click();
        cut.Find(".ctx-menu").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.DoesNotContain("open", cut.Find(".ctx-sub").ClassList);
        Assert.False(closed);
        cut.Find(".ctx-menu").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.True(closed);
    }

    // ---------------------------------------------------------------- error messages

    [Fact]
    public void Framework_Exceptions_Become_Plain_Language()
    {
        Assert.Equal("the file is not valid XML (near line 3)", FriendlyError.Describe(new System.Xml.XmlException("'<' is an unexpected token.", null, 3, 7)));
        Assert.Equal("the file is not a valid Meridian project (JSON)", FriendlyError.Describe(new JsonException("'x' is an invalid start of a value. Path: $ | LineNumber: 0")));
        Assert.Equal("the file is damaged or not in the expected format", FriendlyError.Describe(new InvalidDataException("End of Central Directory record could not be found.")));
        Assert.Equal("the browser blocked it", FriendlyError.Describe(new JSException("TypeError: undefined is not a function\n    at foo (bar.js:1:2)")));
        Assert.StartsWith("something in the model was not as expected", FriendlyError.Describe(new KeyNotFoundException("The given key 'x' was not present in the dictionary.")));
        // Domain messages are kept, first line only, without the full stop (the caller adds what to do next).
        Assert.Equal("The package is read-only for you", FriendlyError.Describe(new InvalidOperationException("The package is read-only for you.\n   at Meridian.Core…")));
        Assert.Equal(300, FriendlyError.Describe(new InvalidOperationException(new string('x', 900))).Length);
    }

    [Fact]
    public void Snackbar_And_Status_Messages_Do_Not_Show_Raw_Framework_Exceptions()
    {
        // Messages built from ex.Message of an exception type that carries technical text (XML, JSON, zip, JS, IO).
        var src = Path.Combine(RepoRoot(), "src", "Meridian.Web", "Components", "Workbench");
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(src, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].Contains("ex.Message", StringComparison.Ordinal)) continue;
                var window = string.Join("\n", lines.Skip(Math.Max(0, i - 4)).Take(5));
                if (Regex.IsMatch(window, @"catch \((?:Exception ex\) when \(.*\b(XmlException|JsonException|InvalidDataException|JSException|IOException)\b|JSException ex\))"))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}");
            }
        }
        Assert.True(offenders.Count == 0, "Use FriendlyError.Describe(ex) instead of ex.Message:\n" + string.Join("\n", offenders));
    }
}
