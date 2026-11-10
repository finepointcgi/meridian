using MudBlazor;

namespace Meridian.Web.Components.Workbench;

/// <summary>
/// The guided tour's content: the first-run chapter ("The basics") and optional chapters picked from Help ▸ Tour, plus
/// the one-time "What's new" card. Steps point at a CSS selector; when nothing matches (a phone, a tablet's overflow menu,
/// a ribbon group that is hidden) the card is shown without a spotlight, so every chapter works on every layout.
/// </summary>
public static class TourChapters
{
    public sealed record Step(string Selector, string Icon, string Title, string Html);

    public sealed record Chapter(string Id, string Title, string Icon, string Blurb, Step[] Steps);

    public sealed record WhatsNewItem(string Title, string Text, string ChapterId);

    public const string Basics = "basics";

    /// <summary>The release the "What's new" card describes; <c>UserPreferences.WhatsNewSeen</c> stores the last one shown.</summary>
    public const string WhatsNewVersion = "0.5";

    public static readonly Chapter[] All =
    [
        new(Basics, "The basics", Icons.Material.Outlined.School, "Shapes, connecting, the Inspector and commits.",
        [
            new(".canvas .toolbox", Icons.Material.Outlined.Category, "Shapes panel",
                "Drag a shape onto the canvas, or click it to insert one. Search to find any SysML, BPMN, ArchiMate or drawing shape. " +
                "<b>Double-click</b> empty canvas to quick-add a shape where you clicked."),
            new(".diagram-host", Icons.Material.Outlined.East, "Connect with hover arrows",
                "Hover a shape and four blue arrows appear. <b>Click</b> one to add a connected shape next to it, or <b>drag</b> it onto another shape to connect the two. " +
                "Drop pictures, paste screenshots, add links and layers from the format bar."),
            new("aside.stack .card:first-child", Icons.Material.Outlined.Tune, "Inspector",
                "Everything about the selected element: name, type, properties, documentation, stereotypes, relationships and comments."),
            new("aside.stack .card:nth-child(2)", Icons.Material.Outlined.Assignment, "Requirements & traceability",
                "Requirements with their satisfy / verify coverage. The Traceability group in the toolbar opens the matrix, impact analysis and validation."),
            new(".titlebar .cmdk", Icons.Material.Outlined.Search, "Command palette ⌘K",
                "Press <b>⌘K</b> (Ctrl+K) to run any command, open a diagram or jump to an element by name."),
            new(".titlebar .btn-primary", Icons.Material.Outlined.Commit, "Commit versions",
                "Edits save continuously and sync live with collaborators. <b>Commit</b> (⌘S) records a named version you can compare and restore."),
            new("", Icons.Material.Outlined.RocketLaunch, "You're ready",
                "Start from a template with <b>New project</b>, or open a diagram and start drawing. More chapters are in the project menu: <b>Help ▸ Tour</b>."),
        ]),
        new("generate", "Generate diagrams", Icons.Material.Outlined.Dashboard, "Auto-diagram and Diagram my model.",
        [
            new("[data-tour=auto-diagram]", Icons.Material.Outlined.Dashboard, "Auto-diagram",
                "<b>Diagram ▸ Auto-diagram</b> draws a package overview, block, internal block, requirement and behaviour diagrams from what is already in the model. No AI needed. Tick the views you want."),
            new("[data-tour=assistant]", Icons.Material.Outlined.AutoAwesome, "Diagram my model",
                "With the AI assistant on, <b>Diagram my model</b> picks and names the views that tell the story. You accept or reject each diagram."),
            new("", Icons.Material.Outlined.AccountTree, "One package at a time",
                "Right-click a package and choose <b>Auto-diagram this package</b>, or press ⌘K and type <b>Diagram the whole model</b>."),
        ]),
        new("derive", "Create diagrams from diagrams", Icons.Material.Outlined.AccountTree, "Requirements → use case → activity → sequence → BDD / IBD.",
        [
            new("[data-tour=create-from]", Icons.Material.Outlined.AccountTree, "Create from…",
                "Select a use case, activity, sequence, block or requirements and choose <b>Create from…</b> to make the next view: " +
                "requirements → use cases → activity → sequence → BDD / IBD → state machine."),
            new(".diagram-host", Icons.Material.Outlined.Checklist, "Check the plan first",
                "Right-click works too. You see what will be created and untick what you don't want. It all goes in as one undo step."),
            new("aside.stack .card:first-child", Icons.Material.Outlined.Link, "The derivation chain",
                "The Inspector shows the <b>Derivation chain</b> with a <b>Create next…</b> button. When a source changes, the diagram shows <b>Source changed</b>; " +
                "<b>Update from source…</b> keeps your own edits."),
        ]),
        new("code", "Connect your code", Icons.Material.Outlined.Difference, "Code repositories, review branches and What changed.",
        [
            new(".titlebar .project", Icons.Material.Outlined.Extension, "Connect a repository",
                "Project menu ▸ <b>Integrations</b> ▸ <b>Code repositories</b> connects a GitHub, GitLab or Bitbucket repository (C#, TypeScript, Java or Python). Each push updates a <b>review branch</b>, never main."),
            new(".titlebar .chg-badge, [data-tour=code-group]", Icons.Material.Outlined.Difference, "What changed",
                "When code changes wait for review, <b>Code changes (N)</b> shows in the title bar. It opens <b>What changed</b>: what each push added, changed and removed."),
            new(".diagram-host", Icons.Material.Outlined.Highlight, "Accept or reject",
                "Diagrams outline added elements in green, changed ones in amber and removed ones as dashed ghosts. Tick changes, then <b>Accept selected</b> to merge them into main, or <b>Reject selected</b>."),
        ]),
        new("mermaid", "Import Mermaid", Icons.Material.Outlined.Schema, "Paste or drop Mermaid and get real model elements.",
        [
            new(".titlebar .project", Icons.Material.Outlined.Schema, "Paste Mermaid",
                "Project menu ▸ <b>Paste Mermaid…</b> turns flowcharts, class, state, sequence, requirement, ER and C4 diagrams into model elements with laid-out diagrams. You see a preview first."),
            new(".diagram-host", Icons.Material.Outlined.UploadFile, "Drop a file",
                "Drop a <b>.mmd</b> or Markdown file on the canvas, or open one as a new project. Ask any AI chat for “a Mermaid flowchart” and paste the answer."),
            new("", Icons.Material.Outlined.Download, "And back out",
                "Export ▸ <b>Export diagram as Mermaid</b> writes activity, state machine, sequence, block definition and requirement diagrams as Mermaid."),
        ]),
        new("ai", "AI assistant providers", Icons.Material.Outlined.AutoAwesome, "Claude, a local model or Claude Code.",
        [
            new("[data-tour=assistant]", Icons.Material.Outlined.AutoAwesome, "The assistant",
                "Ask about the open model, have it reviewed, or let it propose changes. You see every change as a diff and choose what to apply."),
            new(".titlebar .tb-settings", Icons.Material.Outlined.Settings, "Pick a provider",
                "Settings ▸ <b>AI assistant</b>: <b>Claude</b> with an Anthropic API key, a <b>local model</b> (Ollama, LM Studio or another OpenAI-compatible server), " +
                "or <b>Claude Code</b> with your own login in the desktop app."),
            new("", Icons.Material.Outlined.Shield, "Where your model goes",
                "With Claude or Claude Code, model content is sent to Anthropic. With a local model it stays on that server. The assistant asks before it sends anything the first time."),
        ]),
        new("multi", "Select many & link", Icons.Material.Outlined.SelectAll, "Multi-select, Link to… and automatic connector types.",
        [
            new(".diagram-host", Icons.Material.Outlined.SelectAll, "Select many",
                "<b>Shift-click</b> or <b>⌘-click</b> shapes and connectors, or drag a box around them. <b>⌘A</b> selects everything."),
            new("aside.stack .card:first-child", Icons.Material.Outlined.Tune, "Edit them together",
                "The Inspector shows <b>N selected</b>: fill, line and text colour, line style, stereotypes and shared fields. Each change is one undo step."),
            new(".diagram-host", Icons.Material.Outlined.AddLink, "Link to…",
                "Right-click the selection ▸ <b>Link to…</b> or <b>Trace to requirement…</b> makes one relationship per element. " +
                "Lines you draw pick their type for you (satisfy, verify, composition…); click the chip on a new line to switch."),
        ]),
        new("settings", "Settings & themes", Icons.Material.Outlined.Palette, "Light and dark, accent colour, density and more.",
        [
            new(".titlebar .tb-settings", Icons.Material.Outlined.Settings, "Settings",
                "<b>Settings</b> (⌘,) has the theme, accent colour, density, font size, grid, connector style, the AI assistant and your account. Search finds any setting."),
            new(".titlebar .tb-theme", Icons.Material.Outlined.DarkMode, "Light or dark",
                "One click switches between light and dark. In Settings, <b>System</b> follows your computer."),
            new("", Icons.Material.Outlined.Devices, "Follows you",
                "Your settings are saved with your account, so they follow you to other devices. Density and font size stay with each device."),
        ]),
    ];

    public static readonly WhatsNewItem[] WhatsNew =
    [
        new("Connect your code", "Pushes to GitHub, GitLab or Bitbucket update a review branch, and pull requests get a preview. Accept or reject in What changed.", "code"),
        new("Create diagrams from diagrams", "Use case → activity → sequence → BDD / IBD, with update from source.", "derive"),
        new("Import Mermaid", "Paste or drop Mermaid and get model elements and laid-out diagrams.", "mermaid"),
        new("More AI providers", "Run the assistant on a local model, or on Claude Code with your own login.", "ai"),
        new("Select many & link", "Edit many shapes at once, link them in one go, and lines pick their own type.", "multi"),
    ];

    public static Chapter? Find(string id) => All.FirstOrDefault(c => c.Id == id);
}
