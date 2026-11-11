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

    /// <summary>The chapter for people who know Cameo / MagicDraw or Enterprise Architect (Help ▸ Coming from Cameo or EA).</summary>
    public const string ComingFrom = "coming-from";

    /// <summary>The release the "What's new" card describes; <c>UserPreferences.WhatsNewSeen</c> stores the last one shown.</summary>
    public const string WhatsNewVersion = "0.8";

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
                "Requirements with their satisfy / verify coverage. The <b>Traceability</b> tab of the ribbon opens the matrix, the traceability table, impact analysis and validation."),
            new(".titlebar .cmdk", Icons.Material.Outlined.Search, "Command palette ⌘K",
                "Press <b>⌘K</b> (Ctrl+K) to run any command, open a diagram or jump to an element by name."),
            new(".titlebar .btn-primary", Icons.Material.Outlined.Commit, "Commit versions",
                "Edits save continuously and sync live with collaborators. <b>Commit</b> (⌘S) records a named version you can compare and restore."),
            new("", Icons.Material.Outlined.RocketLaunch, "You're ready",
                "Start from a template with <b>New project</b>, or open a diagram and start drawing. More chapters are in the <b>Help</b> menu (<b>?</b> in the title bar) ▸ <b>Tour</b>."),
        ]),
        new(ComingFrom, "Coming from Cameo or EA", Icons.Material.Outlined.SwapHoriz, "Where the containment tree, specification window and menus went.",
        [
            new("nav.ribbon .rtabs", Icons.Material.Outlined.ViewWeek, "Menus are ribbon tabs",
                "The toolbars and menus you know are the ribbon tabs: <b>Create</b>, <b>Diagram</b>, <b>Requirements</b>, <b>Traceability</b>, <b>Simulate</b>, <b>Generate</b>… " +
                "Click the <b>project name</b> for open, import (<b>.mdzip</b>, <b>.qea</b>, XMI), versions, sharing, integrations and export."),
            new("main.workbench > section.card:first-child", Icons.Material.Outlined.AccountTree, "Containment tree = Model explorer",
                "Right-click an element for <b>New element ▸</b>, <b>New diagram ▸</b>, Rename, Delete and <b>Permissions…</b>. Drag elements onto a diagram to show them there."),
            new("aside.stack .card:first-child", Icons.Material.Outlined.Tune, "Specification window = Inspector",
                "Name, type, properties, stereotypes, tagged values, traceability and documentation of the selection, always open on the right."),
            new(".canvas .toolbox", Icons.Material.Outlined.Category, "Palette = Shapes panel",
                "Click or drag a shape in. The blue <b>hover arrows</b> on a shape work like EA's Quick Linker and Cameo's smart manipulator."),
            new(".titlebar .cmdk", Icons.Material.Outlined.Search, "Can't find a command?",
                "Press <b>⌘K</b> (Ctrl+K) and type what you want: “generic table”, “dependency matrix”, “used projects”, “report”…"),
            new("", Icons.Material.Outlined.Commit, "Saving works differently",
                "Edits save as you go and sync live. <b>Commit</b> is a Teamwork Cloud commit: a named version to compare or restore. " +
                "<b>Backspace</b> removes a shape from the diagram; <b>Delete</b> removes the element from the model. The user guide's <a data-guide href=\"/guide/docs/migration.html\"><b>Coming from Cameo / EA</b></a> page maps every window and menu."),
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
        new("code", "Connect your code", Icons.Material.Outlined.Difference, "Code repositories, review branches, code changes and architecture checks.",
        [
            new(".titlebar .project", Icons.Material.Outlined.Extension, "Connect a repository",
                "Project menu ▸ <b>Integrations</b> ▸ <b>Code repositories</b> connects a GitHub, GitLab or Bitbucket repository (C#, TypeScript, Java or Python, submodules included). " +
                "Each push updates a <b>review branch</b>, never main, and pull requests get a preview."),
            new(".titlebar .chg-badge, [data-tour=code-group]", Icons.Material.Outlined.Difference, "Code changes",
                "When code changes wait for review, <b>Code changes (N)</b> shows in the title bar and a <b>Code</b> tab joins the ribbon. It lists what each push added, changed and removed."),
            new(".diagram-host", Icons.Material.Outlined.Highlight, "Accept or reject",
                "Diagrams outline added elements in green, changed ones in amber and removed ones as dashed ghosts. Tick changes, then <b>Accept selected</b> to merge them into main, or <b>Reject selected</b>."),
            new("[data-tour=code-group]", Icons.Material.Outlined.Rule, "Architecture checks",
                "<b>Code ▸ Architecture</b> compares the synced code with the model: a dependency the model does not allow is listed with a link to the line, and drawn in red on the diagram."),
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
        new("database", "Database modelling", Icons.Material.Outlined.Storage, "Tables, keys, subject areas, DDL and migration scripts.",
        [
            new(".titlebar .project", Icons.Material.Outlined.Storage, "Bring in a schema",
                "Project menu ▸ <b>Database schema (DDL / .dacpac)…</b> reads PostgreSQL, SQL Server, MySQL, SQLite or Oracle DDL. <b>Connect to a database…</b> reads a live schema you can refresh later. " +
                "Or add a <b>Database diagram</b> from Diagram ▸ More and draw tables."),
            new("[data-tour=database]", Icons.Material.Outlined.TableChart, "The Database tab",
                "Once the project has a data model, the ribbon gets a <b>Database</b> tab: New table, Subject areas, <b>DDL…</b>, <b>Migration…</b> (ALTER scripts from a version, branch or baseline), <b>Refresh…</b> and <b>Data dictionary</b>."),
            new("", Icons.Material.Outlined.Workspaces, "Subject areas",
                "Group tables across schemas into colour-framed <b>subject areas</b>, filter a diagram to one, and give them permissions. <b>Data dictionary</b> exports every table and column to Excel or a document."),
        ]),
        new("libraries", "Used projects (shared libraries)", Icons.Material.Outlined.LibraryBooks, "Reuse packages of other projects, pinned and reviewed.",
        [
            new(".titlebar .project", Icons.Material.Outlined.LibraryBooks, "Use a project",
                "Project menu ▸ <b>Used projects…</b> adds a package of another project, read-only and pinned to a version, branch or baseline, like Cameo's used projects."),
            new("[data-tour=used-projects]", Icons.Material.Outlined.Sync, "Updates",
                "<b>Model views ▸ Used projects</b> checks for new versions. A badge in the title bar says when one is ready; you review the differences before updating, and links to changed elements become suspect."),
            new("main.workbench > section.card:first-child", Icons.Material.Outlined.OpenInNew, "In the model explorer",
                "Used elements carry a library badge. Type parts by them or link to them like any element; right-click ▸ <b>Open in source project</b> to change them there."),
        ]),
        new("verify", "Verify requirements", Icons.Material.Outlined.Rule, "Criteria, simulation-driven verification and variants.",
        [
            new("aside.stack .card:nth-child(2)", Icons.Material.Outlined.Assignment, "Give a requirement a criterion",
                "In the Requirements table or the Inspector, write a criterion such as <code>mass &lt;= 1500 kg</code> or <code>mc: p95(accelTime) &lt; 8 s</code>."),
            new("[data-tour=verify]", Icons.Material.Outlined.Rule, "Simulate ▸ Verify",
                "Evaluates every criterion with parametrics, Monte Carlo, timed simulation or co-simulation. The <b>Verification</b> tab lists pass, fail and stale results, and diagrams colour each requirement."),
            new("", Icons.Material.Outlined.Balance, "Variants",
                "<b>Simulate ▸ Variants</b> holds the feature model, configurations and trade studies. Show one variant on the diagrams (greyed out or hidden) from the Variants tab or ⌘K."),
        ]),
        new("team", "Permissions & requirements tools", Icons.Material.Outlined.Lock, "Package permissions, DOORS Next, Polarion, branches and reviews.",
        [
            new("main.workbench > section.card:first-child", Icons.Material.Outlined.Lock, "Package permissions",
                "Right-click a package ▸ <b>Permissions…</b> makes it read-only or hidden for chosen people, like Teamwork Cloud permissions. Project roles are in <b>Share &amp; members</b>."),
            new(".titlebar .project", Icons.Material.Outlined.SyncAlt, "DOORS Next and Polarion",
                "Project menu ▸ <b>Integrations</b> ▸ <b>DOORS &amp; Polarion</b> syncs requirements both ways and can publish satisfy, verify and trace links back. Jira and Azure DevOps issues are under <b>Issue trackers</b>."),
            new(".titlebar .project", Icons.Material.Outlined.CallSplit, "Branches, baselines and reviews",
                "<b>Branches &amp; merge…</b> and <b>Baselines…</b> are in the project menu; <b>Traceability ▸ Request review</b> asks colleagues to approve the selection."),
        ]),
        new("analysis", "Instances, deployment & more", Icons.Material.Outlined.Explore, "Object and deployment diagrams, relation map, find and replace, history, spreadsheets and printing.",
        [
            new("", Icons.Material.Outlined.DataObject, "Instances",
                "Right-click a block ▸ <b>Create instance of…</b> builds the whole part tree. The <b>instance table</b> edits slot values with units, and an <b>object diagram</b> shows the instances and their links. Verification can run against an instance."),
            new("", Icons.Material.Outlined.Dns, "Deployment diagrams",
                "Nodes, devices, execution environments and artifacts. Draw a line from an artifact to a node and it becomes «deploy»; <b>Deploy to…</b> works on a selection, databases included."),
            new("", Icons.Material.Outlined.Hub, "Relation map",
                "<b>Traceability ▸ Relation map</b> (or right-click an element) shows everything connected to it, up to six levels. Select nodes and create a real diagram from them."),
            new("", Icons.Material.Outlined.FindReplace, "Find and replace",
                "<b>⇧⌘F</b> searches names, documentation, requirement text and values across the model. Replace shows a preview and is one undo step."),
            new("", Icons.Material.Outlined.History, "Element history",
                "<b>History</b> in the inspector, or right-click ▸ <b>History…</b>, lists who changed an element and when, with a field-by-field diff. Restore one field in a click."),
            new("", Icons.Material.Outlined.TableView, "Spreadsheets",
                "<b>Import ▸ Spreadsheet (Excel / CSV)…</b> maps columns to any element kind, with presets for requirements and parts lists. Re-importing shows what changed."),
            new("", Icons.Material.Outlined.Print, "Printing",
                "<b>⌘P</b> prints the diagram across one or many pages with headers, footers and a classification marking, as a vector PDF."),
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
        new("Instances & object diagrams", "Create an instance of a block with its parts, edit slot values in an instance table, and verify against it.", "analysis"),
        new("Deployment diagrams", "Nodes, devices and artifacts; drawing a line from an artifact to a node deploys it.", "analysis"),
        new("Relation map, find & replace", "Explore everything connected to an element; search and replace across the whole model (⇧⌘F).", "analysis"),
        new("History, spreadsheets & printing", "Per-element history with diffs, Excel/CSV import for any element, and multi-page PDF printing (⌘P).", "analysis"),
        new("Editing together", "Several people can change the same element at once without locks; everyone ends up with the same model.", "team"),
        new("Coming from Cameo or EA?", "A short chapter on where the containment tree, specification window and menus went.", ComingFrom),
    ];

    public static Chapter? Find(string id) => All.FirstOrDefault(c => c.Id == id);
}
