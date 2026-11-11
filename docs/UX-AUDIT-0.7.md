# Ease-of-use audit, 0.7

An ease-of-use pass over the whole app after 0.4–0.7 added auto-diagram, AI diagramming, settings and themes, the tabbed
ribbon, more AI providers, multi-select, Mermaid, derivation, code sync, architecture checks, package permissions,
DOORS / Polarion, verification and variants, database modelling, used projects, the tour and desktop updates. The goal
was a coherent app that a new user, especially one coming from Cameo or Enterprise Architect, finds easy.

## How it was checked

- **Walkthrough**: `tests/Meridian.E2E/UxWalkthroughTests.cs`, opt-in with `MERIDIAN_UX_WALKTHROUGH=1`
  (`MERIDIAN_UX_ONLY=desktop-dark,…` limits it, `MERIDIAN_UX_DIR` picks the output folder, default `$TMPDIR/meridian-ux`).
  It opens every ribbon tab, the project and Help menus, ⌘K, every dialog, every dock tab (empty and filled), the
  database, code-changes, verification, permissions and derivation flows, at 1440×900, 1280×800, 1024×768 and 390×844
  in the dark and the light theme, plus a first-run session without the "tour seen" flags. For every screen it records
  in `audit.json`: dialogs outside the window, whether the dialog has the focus, sideways scrolling, and buttons without
  an accessible name. It also writes 2×2 contact sheets (`sheet-*.png`). 389 screenshots were reviewed.
- **Code review** of every `Snackbar.Add` / `SetStatus` / `_error =` with an exception message, every dialog's buttons,
  every `MudIconButton`, the keyboard handlers (`meridian.js`, `settings.js`, `DiagramController.Key`), the tour
  selectors and the docs-site table of contents.
- The web app was also started on its own (SQLite, temporary data folder) and checked over HTTP.

## Findings

Severity: **High** blocks or misleads a new user; **Medium** slows them down or looks broken; **Low** polish.

### Discoverability and menus

| # | Sev | Where | Finding | Fix |
|---|---|---|---|---|
| 1 | High | Project menu | 33 entries; at 900 px tall the **Help** section (Tour, Keyboard & mouse, What's new, About) was below the bottom of the window and could not be reached without knowing to scroll a popover. | Help moved to its own **Help (?)** menu in the title bar (Tour…, Coming from Cameo or EA, Keyboard & mouse, All commands ⌘K, What's new, About). Project and Help menus never grow past the window (`.menu-fit`). Settings stays on the gear and the account menu. |
| 2 | High | Model explorer context menu | A package's menu listed ~45 entries flat (every element kind, every diagram type incl. nine UAF views); **Rename, Delete and Permissions… ran off the bottom of the screen** (the E2E test even used `DispatchEvent` because of it). | Long lists become Cameo-style submenus **New element ▸** and **New diagram ▸**; short lists stay inline. The menu (and its submenus) is moved inside the window, takes the focus, closes with **Escape** (the old one ignored Escape), supports ↑ ↓ → ← Enter, and has `role=menu` / `menuitem`. |
| 3 | Medium | AI ribbon tab | One button (Assistant); Diagram my model, Diagram from text, Requirements from text, Suggest links and Review model were reachable only via ⌘K. | Five buttons added to the AI tab. |
| 4 | Medium | Database | The data dictionary (Excel, CSV, document) was only on ⌘K. | **Data dictionary** menu on the Database ribbon tab. |
| 5 | Medium | ⌘K | Cameo / EA users search for their own words. | ⌘K understands *dependency matrix*, *relationship matrix*, *requirement table*, *report wizard*, *use project*, *quick diagram layout*, *containment tree*, *model search*, *macros*, *Teamwork Cloud commit* and offers the Meridian equivalent. Duplicate "Take the guided tour" removed (it is "Tour: The basics"). |
| 6 | Low | Integrations | The menu label "Integrations (issue trackers, webhooks, code)" missed DOORS / Polarion and databases. | "Integrations…" with a grey hint "Jira, DOORS, Polarion, Git, databases" and a tooltip. |
| 7 | Low | Only on ⌘K (left as is) | Fit diagram frames to contents, show a variant on diagrams (also in the Variants tab), highlight code changes (also the Code tab), Generate SDD (also Generate ▸ Formats). | Recorded; all have a second entry point except *Fit frames* (rare). |

### Naming

| # | Sev | Finding | Fix |
|---|---|---|---|
| 8 | Medium | "Code changes" (badge) vs "What changed" (tab, ⌘K, tour, docs) vs "Changes" (ribbon) vs "CODE SYNC" (crumb). | **Code changes** everywhere (tab, ribbon, ⌘K, tour, README, docs). |
| 9 | Medium | "Used projects" (menu, docs) vs "Libraries" (ribbon button, tooltip). | **Used projects** everywhere; "shared libraries" only as an explanation. |
| 10 | Medium | "Subject areas" (dialogs, docs) vs "Groups" / "Add to group" (Database ribbon). | **Subject areas** / **Add to subject area**. |
| 11 | Low | "Custom table" (ribbon, new tab title) vs "Generic table" (⌘K, docs, Cameo). | **Generic table**. |
| 12 | Low | Integrations tab "Requirements tools" hid the product names users look for. | Tab renamed **DOORS & Polarion** (UI, docs, docs/INTEGRATIONS.md). |
| 13 | Low | About said "Meridian 0.1". | About shows the release (`TourChapters.WhatsNewVersion`, now 0.7) and the current feature list. |

### Onboarding

| # | Sev | Finding | Fix |
|---|---|---|---|
| 14 | High | What's new still described **0.5**; the tour had no chapter for 0.6/0.7 features (database, used projects, submodules, permissions, verification, DOORS / Polarion) and none for Cameo / EA users. | What's new is **0.7** (database, used projects, verification, permissions + DOORS / Polarion, more code languages, Coming from Cameo or EA). New chapters: **Coming from Cameo or EA** (second in the list), **Database modelling**, **Used projects**, **Verify requirements**, **Permissions & requirements tools**; *Connect your code* gains an architecture-checks step and mentions submodules and PR previews. 13 chapters. |
| 15 | Medium | Tour text pointed at "the Traceability group in the toolbar" and "project menu: Help ▸ Tour" after the tabbed ribbon. | Updated to ribbon tabs and the Help (?) menu. |
| 16 | Medium | The tour switched ribbon tabs for only three targets (hard-coded). | `Workbench.TourTargetTabs` maps every `data-tour` target to its tab (`verify`, `used-projects`, `database` added). Tests check every target exists and has a tab, and E2E checks every spotlight is on screen. |

### Error messages

| # | Sev | Finding | Fix |
|---|---|---|---|
| 17 | High | Raw framework exception text reached users: XML parser errors ("'<' is an unexpected token. Line 1, position 3"), JSON reader paths, zip errors, `KeyNotFoundException` ("The given key was not present in the dictionary"), JavaScript exceptions with stack lines, OpenXML package errors — in open / import, ReqIF, CSV / Excel, auto-diagram, image export / copy, report preview / generation, FMU import / attach, database import and migration baselines, link following. | `Services/FriendlyError.Describe` turns framework exceptions into a plain clause ("the file is not valid XML (near line 3)", "the browser blocked it") and keeps domain messages (first line, capped). Every such message now says what happened and what to do ("Nothing was changed.", "Meridian opens .meridian, XMI, …", "Try a smaller scale, or SVG."). A test fails when a `catch` of those types shows `ex.Message`. |
| 18 | Medium | The assistant-off text on a server listed configuration keys (`Meridian:AI:Provider=Local with Meridian:AI:Local:BaseUrl…`) to every user. | "The AI assistant is off on this server. Ask your administrator…", with the key names only as a pointer to the Administration page. |
| 19 | Low | "See docs/VERIFICATION.md", "see docs/PLUGINS.md": repository paths shown in the UI. | Point at the user guide's Verification / Plugins pages (new Verification page). |

### Dialogs and destructive actions

| # | Sev | Finding | Fix |
|---|---|---|---|
| 20 | High | Deleting an issue-tracker / webhook connection, a code repository, a DOORS / Polarion connection or a live database connection removed it **and its stored secret** at one click, with no confirmation and no undo. Removing a project member and deleting a comment were also unconfirmed. | `Confirm.AskAsync`: "Delete “X”? … This cannot be undone." with Cancel and a named action (Delete / Disconnect / Remove). |
| 21 | — | Button order and labels | Checked all 33 dialogs: Cancel / secondary on the left, the filled primary on the right, everywhere. "Close" (viewers) vs "Done" (editors) is consistent with what the dialog does. No change. |
| 22 | — | Size and focus | Measured every dialog at 1280×800, 1024×768 and 390×844: none leaves the window and every one takes the focus. E2E test added for 1280×800. |
| 23 | Medium | Keyboard & mouse was a paragraph in a message box that missed ⌘K, ⌘, ⌘A, ⌘C/⌘V, ⌘D, F2 and Shift+arrows, and called ⌘S "save". | A scrolling dialog with tables from `KeyboardShortcuts` (the same rows as `keyboard-shortcuts.md`, which gained ⌘,). A test keeps the two in step and checks the global shortcuts `meridian.js` / `settings.js` handle. |

### Accessibility

| # | Sev | Finding | Fix |
|---|---|---|---|
| 24 | High | 77 of 79 `MudIconButton`s had no accessible name; ~25 (edit / delete / refresh in Integrations, Projects, Databases, DOORS / Polarion, report template editor, tagged values…) had **no tooltip either**. | Every icon button has an `aria-label`; the ones without a tooltip also get a `title`. Icon-only format-bar buttons get `aria-label`s. Buttons with a visible caption keep it as their name. |
| 25 | Medium | Hard-coded text colours in the feature stylesheets failed WCAG AA in one theme: variant compare (≈3.6:1 on dark), export error (≈2.8:1 on light), SysML string highlight (4.25:1 on light). | Theme-specific colours / tokens; a new test scans `css/*.css` and `extras.css` for fixed text colours against both panels (the existing token test covers `app.css`). |
| 26 | Medium | Focus outlines, reduced motion | Nothing honoured `prefers-reduced-motion` (tour, menus, dialogs and ripples animate). | A global reduced-motion rule in `app.css`; context-menu items got a visible focus ring. |

### Layout, mobile, progress

| # | Sev | Finding | Fix / recommendation |
|---|---|---|---|
| 27 | — | Sideways scrolling | None at any size, theme or screen. |
| 28 | — | Phone | Database, verification, architecture, used projects, code changes and permissions are not on the phone shell (Model, Diagram, Requirements, Reviews, Comments); they are hidden gracefully, and Settings and the tour work as sheets. **0.7.1**: verification results and code changes are read-only lists in the account menu (decision 4). |
| 29 | Medium | Phone Diagram view | After switching from a desktop width to a phone width in the same session the Diagram view showed an empty grid until Fit; on a fresh phone load it is fine. **Recommendation**: zoom to fit when the phone shell first shows a diagram. |
| 30 | Medium | Verification dock | At 1280–1440 px the results table squeezes columns (ids and dates wrap). **Recommendation**: open results as a view tab next to the diagrams, like Code changes. **Done in 0.7.1** (decision 3); the rows also had a global `.row { display: flex }` applied, which is what squeezed the columns. |
| 31 | Low | Status bar | "1 diagrams", "0 relationships" plural forms. **Fixed in 0.7.1** with `Plural.Of` (decision 6). |
| 32 | Low | Snackbars | Success toasts (e.g. "Imported Database…") stay ~10 s over the ribbon. **Recommendation**: 4 s for success, keep errors until dismissed. |
| 33 | — | Long operations | Import, sync, verification (Verify disabled while running, progress in the tab), report generation (with *Cancel generation*) and library updates already show progress; AI runs can be stopped. No change. |
| 34 | Low | Empty canvas hint | With the dock open on a small window the "diagram is empty" hint overlaps the diagram frame label. Recorded. |

### Docs

| # | Sev | Finding | Fix |
|---|---|---|---|
| 35 | High | No user-guide page for verification or package permissions. | `docs-site/docs/verification.md`, `docs-site/docs/permissions.md`. |
| 36 | Medium | The table of contents was a flat list of 28 pages with *Coming from Cameo / EA* near the bottom. | Grouped: Start here (Getting started, **Coming from Cameo / EA**, Keyboard shortcuts, Settings, Samples, FAQ), Modelling, Requirements and analysis, Working as a team, Connecting other tools, Extending and running. |
| 37 | High | *Coming from Cameo / EA* said used projects, timing / communication / interaction overview diagrams and FMU co-simulation were "not available", and sent DOORS / Polarion users to ReqIF only. | Corrected; rows added for package permissions, Create Element ▸ / Create Diagram ▸, verification, architecture checks, Help ▸ Keyboard; a section on finding things by their Cameo / EA name in ⌘K. |
| 38 | Medium | README feature list stopped at 0.5. | 0.6 / 0.7 features listed (database, used projects, permissions, DOORS / Polarion, verification, code languages, architecture checks), with a "Coming from Cameo or EA?" pointer. Getting started, Settings (tour chapters), Database, Libraries and Code sync pages updated for the new names and the Help menu. |

## Naming decisions

| Concept | Use | Instead of | Why |
|---|---|---|---|
| Making a diagram from another (use case → activity…) | **Create from…** (ribbon), "Create *X* from *Y*" (menus) | Derive | "Derive" is SysML's deriveReqt; "Create from" says what happens. |
| Code-sync review | **Code changes** | What changed, Changes | Matches the title-bar badge users click. |
| Shared packages from other projects | **Used projects** | Libraries | Cameo's term; "shared libraries" only in explanations. |
| Groups of tables | **Subject areas** | Groups | EA / data-modelling term already used in dialogs and docs. |
| Configurable element table | **Generic table** | Custom table | Cameo's term. |
| DOORS Next / Polarion connections | **DOORS & Polarion** (Integrations tab) | Requirements tools | People look for the product names. |
| Help | **Help (?)** menu | Project menu ▸ Help | Where Cameo and EA users expect it; always on screen. |
| Tree context menu | **New element ▸**, **New diagram ▸** | flat lists | Cameo's Create Element / Create Diagram. |
| Integrations dialog | **Integrations…** | Integrations (issue trackers, webhooks, code)… | Short label, full list as a hint. |

## Ribbon and menu map

**Title bar**: project menu (click the project name) · branch picker · Code changes badge · used-project updates badge ·
⌘K search · presence · **Help (?)** · notifications · Share · Version history · Undo · Redo · Light / dark · Settings ·
**Commit** · account menu (Settings, Switch workspace, API tokens, Administration, Sign out).

**Project menu**: *Project* Projects… ⌘O, New project…, Rename model… · *Import* Open a file as a new project…, Import
into this project…, Paste Mermaid…, Database schema (DDL / .dacpac)…, Connect to a database…, Add UAF profile ·
*Versions & team* Commit version… ⌘S, Version history…, Branches & merge…, Baselines…, Used projects…, Share &
members…, Integrations… · **Export ▸** (submenu since 0.7.1) Download .meridian file, Export XMI 2.5.1, Export SysML v2
text, *Active diagram*: Export diagram as SVG / PNG…, Copy diagram as image, Export diagram as Mermaid, Copy diagram as
Mermaid.

**Diagram tab (right-click, 0.7.1)**: Export ▸ (SVG, PNG…, image, Mermaid), Select in containment tree, Close, Close others.

**Help (?)**: Tour…, Coming from Cameo or EA, Keyboard & mouse, **User guide** (0.7.1), All commands… ⌘K, What's new in
0.7, About Meridian.

**Ribbon tabs**

| Tab | Buttons |
|---|---|
| Create | Package, Block, Interface Block, Value Type, Requirement, Actor, Use Case |
| Diagram | BOARD, BDD, IBD, REQ, ACT, STM, SD, UC, PKG, More ▾ (Parametric, BPMN, ArchiMate, Database, Class, Timing, Interaction Overview, Communication), Arrange, Auto-diagram, Create from… |
| Requirements | Test case, Table, Exchange ▾ (ReqIF, CSV, Excel, re-import with preview, renumber) |
| Traceability | Matrix, Trace table, Impact, Coverage, Validate · *Review*: Request review, Reviews (the Review tab before 0.7.1) |
| Model views | Generic table, Query, Used projects |
| Simulate | Simulate, Parametrics, Variants, Verify (opens the Verification view tab) |
| Generate | Report, SysML v2, Edit text, XMI, C#, Formats ▾ (SDD, Word / PDF / HTML, HTML report, BPMN, Mermaid, ArchiMate, ReqIF, SQL DDL ×5) · *Extend*: Script, Plugins ▾ (the Extend tab before 0.7.1) |
| AI | Assistant, Diagram my model, Diagram from text, Requirements from text, Suggest links, Review model |
| Database (with a data model) | New table, Add to subject area, Subject areas ▾ (manage, filter, columns), DDL…, Migration…, Refresh…, Data dictionary ▾ |
| Code (with a repository) | Code changes, Highlight, Architecture |

**Dock**: Validation, Requirements, Matrix, Impact, Simulation, Parametrics, Trace table, Reviews, Script, Variants,
Verification ↗ (opens the view tab), SysML text, Assistant, Output. **View tabs** next to diagrams: generic tables,
queries, report templates, Code changes, Architecture, **Verification** (0.7.1).

**Phone account menu**: Projects…, **Verification results**, **Code changes** (read-only, 0.7.1), **User guide**, Light /
dark mode, Settings…, Tour…, Sign out.

**Balance**: 0.7.0 had Review (2) and Extend (2) as their own light tabs; 0.7.1 merged them (decision 1), so the strip has
eight tabs (ten with Database and Code). A ribbon tab id remembered from 0.7.0 (`review`, `extend`) opens the tab its
buttons moved to (`Workbench.MovedRibbonTabs`).

## Decisions (all done in 0.7.1)

All six recommendations were approved and built; `tests/Meridian.E2E/UxDecisionsTests.cs` and
`tests/Meridian.Web.Tests/UxDecisionsTests.cs` cover them.

1. **Review → Traceability, Extend → Generate.** Done. *Request review* and *Reviews* sit after a divider on the
   Traceability tab (reviews approve what the trace shows); *Script* and *Plugins ▾* sit after a divider on Generate
   (plugins mostly add exporters). `RibbonTabs()`, the `data-tab` groups (`PluginsMenu` renders buttons only), the tour
   text, `Ui.RibbonTabAsync` (old ids still accepted), docs and tests were updated. A remembered `review` / `extend` tab
   opens `trace` / `generate`.
2. **Export ▸ submenu.** Done, as a MudBlazor nested menu inside the project menu (the menu is a `MudMenu`; rewriting it
   on the canvas `ContextMenu` would have broken the parallel branches that add project-menu items). MudBlazor flips the
   submenu inside the window; `meridian.js` adds the keyboard for `kbd-menu` lists: ↑ / ↓ / Home / End, → or Enter opens
   the submenu at its first item, ← goes back, Escape closes the submenu first and then the menu. The diagram exports are
   also on the new diagram-tab context menu (`ContextMenu` with an Export ▸ submenu).
3. **Verification results as a view tab.** Done: `ViewTabKind.Verification`. *Simulate ▸ Verify*, ⌘K *Verification
   results* and the dock's *Verification ↗* entry open or bring forward one tab; `OpenDock(VerificationDock)` redirects
   to it. Only the hosting changed (`VerificationPanel` is unchanged apart from a guide link in its empty state). Checked
   at 1280×800 and 1440×900: the squeeze came from a global `.row { display: flex }` on the result rows; with real table
   rows, ids and dates stay on one line and, when the centre is under 760 px wide, the Variant column (also in each row's
   details) is dropped so nothing scrolls sideways.
4. **Phone: read-only verification results and code changes.** Done, in the account menu (the bottom bar keeps its five
   tabs): `MobileVerification` (status, requirement id and name, criterion, value vs bound, margin, time; filter chips;
   tap for the element sheet) and `MobileCodeChanges` (per review branch: added / changed / removed, element kind, path,
   with "accept or reject on a larger screen"). Checked at 390×844 without sideways scrolling.
5. **User guide inside the app.** Done. `build/package.sh` builds docs-site once with DocFX (pinned local tool) and copies
   it into every desktop package and the server zip as `guide/`; the Dockerfile builds it into `/app/guide`
   (`SKIP_GUIDE=1` leaves it out). `Hosting/UserGuide.cs` serves it at `/guide` (index.html for folders; a short "how to
   build it" page when absent; `Meridian:GuidePath` overrides the folder). **Access decision: anonymous.** The guide is the
   same public documentation as docs-site, the sign-in page links to it (so people without an account can read it),
   and the desktop app opens it in the system browser, which does not hold the window's sign-in. It is its own pipeline
   branch, so it never reaches app pages, APIs or project data, and it sits in front of the desktop launch-token gate.
   *Help ▸ User guide*, ⌘K *User guide* and the phone account menu open it (a new tab, or the system browser from the
   desktop app); "Learn more" goes to the matching page from import / ReqIF / requirements import errors (snackbar
   action), the empty project hint (Getting started, Coming from Cameo or EA), the verification empty state, the Plugins
   dialog and the Cameo / EA tour chapter. The docs build is not part of `dotnet test`.
6. **Plural forms.** Done: `Plural.Of(count, noun)` for the status bar (elements, relationships, diagrams), model health
   and validation (errors, warnings), import logs, Auto-diagram, requirement imports, the trace table, plugin counts, the
   used-project tooltip and the phone code-changes list. `Ui.CountsAsync` accepts the singular.

## Remaining feature gaps for Cameo / EA users

Ranked by how often a Cameo or EA user would miss them.

1. **Instance specifications / object diagrams and instance tables** — needed for configurations, test data and Cameo's simulation instance tables.
2. **Deployment diagrams (nodes, artifacts)** — common in EA software architecture work.
3. **Relation map** — Cameo's interactive graph of an element's relationships; Impact is only a list.
4. **Excel / CSV import with column mapping for any element kind** — only requirements round-trip today.
5. **OCL / scripted constraints in validation suites** — queries cover the common rules, not arbitrary OCL.
6. **Glossary / term management** — EA's project glossary and Cameo's glossary tables.
7. **Find and replace across the model** — rename many names or text fields at once.
8. **Diagram printing / multi-page PDF of a single diagram** with page tiling.
9. **Element-level history** ("who changed this element when") — today history is per project version.
10. **Project compare / merge of two unrelated projects** (not branches), like EA's model compare.
11. **Model-wide diagram templates / styles per stereotype** (Cameo symbol styles, EA shape scripts).
12. **Simulation UI mock-ups** (Cameo GUI prototyping) and sequence-diagram simulation.
13. **PlantUML import** — EA users bring PlantUML as often as Mermaid.
14. **Kanban / project management views** (EA) — tasks, resources and effort on elements.
