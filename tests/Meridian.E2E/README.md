# Meridian end-to-end browser tests

Real browser tests for the Meridian web app, written with [Microsoft.Playwright](https://playwright.dev/dotnet/) and xUnit.

Each test collection starts the real app **in-process** (`MeridianServer.CreateAsync`) on a free loopback port, with
SQLite in a fresh temp folder, and drives it with headless Chromium:

- **Server mode** (`ServerFixture`): three accounts are registered through the real sign-up form with passwords generated
  at runtime. The first one becomes the server administrator; the other two are normal users (used for sharing,
  reviews and live collaboration).
- **Local mode** (`LocalModeTests`): the desktop app's private server, signed in only through `/?launch=<token>`.

Every test fails if any page logs a `console.error` or throws an uncaught exception, or if the server logs an error.
After each test the last screen of every page is saved to `bin/<config>/net10.0/e2e-screenshots`
(or `$MERIDIAN_E2E_SCREENSHOTS`).

## Install the browser (once per machine / Playwright version)

```bash
tests/Meridian.E2E/install-browsers.sh              # macOS, Linux, WSL
tests/Meridian.E2E/install-browsers.sh --with-deps  # Linux CI: also installs Chromium's system libraries (needs sudo)
```

The script builds this project and runs the Playwright driver that ships inside the NuGet package
(`bin/<config>/net10.0/.playwright`), so neither PowerShell nor a global tool is needed. With PowerShell installed,
`pwsh tests/Meridian.E2E/bin/Debug/net10.0/playwright.ps1 install chromium` does the same. Browsers go to the standard
Playwright cache (`~/Library/Caches/ms-playwright` on macOS, `~/.cache/ms-playwright` on Linux, or `$PLAYWRIGHT_BROWSERS_PATH`).

## Run

```bash
dotnet test tests/Meridian.E2E
MERIDIAN_DOCS_SCREENSHOTS=1 dotnet test tests/Meridian.E2E --filter DocsScreenshotTests   # refresh docs-site/images
```

- Without the browser installed, every test is **skipped** with a message saying how to install it, so a plain
  `dotnet test Meridian.slnx` never fails on a machine without Chromium.
- `MERIDIAN_E2E=require` turns a missing browser into a failure (the CI `e2e` job sets it).
- `HEADED=1` shows the browser window while the tests run.

## What is covered

| Test class | Flows |
|---|---|
| `AuthTests` | sign-in (wrong then right password), sample project opens, duplicate registration, sign-out |
| `LocalModeTests` | launch-token sign-in, private workspace without the token |
| `DrawingTests` | shapes panel, rename by typing (also very fast typing), move, undo/redo, quick-connect arrow drag, fill colour, delete; whiteboard shapes and lines |
| `RequirementsTests` | new requirement, Inspector attributes, satisfy link from the traceability table, coverage, suspect link and clearing it |
| `TablesTests` | text query, open as table, inline cell edit, save the view |
| `BranchTests` | new branch, edit, switch to main, conflicting merge, resolve, verify |
| `ReviewTests` | share with a second user, request a review, live notification bell, approve, vote notification |
| `SysmlTextTests` | edit SysML v2 text, apply, tree updates, error reporting, undo |
| `TemplateAndExportTests` | every project template creates a project that opens cleanly; SVG and PNG downloads |
| `AdminTests` | `/admin` for the admin, forbidden for a normal user, admin API protection |
| `CollaborationTests` | two browsers on one project see each other's edits live |
| `PermissionsTests` | Alice makes a package read-only for Bob (his edit is refused), then hidden (tree, diagrams and REST API lose it at once) |
| `LibraryTests` | a parts library is used from the Vehicle sample, a part is typed by a library block, a new library version shows the update badge and review, and the part becomes suspect |
| `CameoImportTests` | a Cameo `.mdzip` whose sequence diagram has an alt fragment with two operands opens as a new project with the separator where Cameo drew it and both guards |
| `OperandTests` | Vehicle sample sequence: make the fragment an alt, add an operand from the context menu, guard it in the inspector, drag its separator, undo |
| `TourTests` | Help ▸ Tour lists the chapters, a chapter spotlights its target, ⌘K opens a chapter, and on a phone a chapter is a bottom sheet from the account menu |
| `UxTests` | every tour spotlight target is on screen, dialogs fit a 1280×800 window, the tree context menu nests long lists, fits the window and closes with Escape |
| `UxWalkthroughTests` | opt-in (`MERIDIAN_UX_WALKTHROUGH=1`): screenshots of every entry point at four sizes in both themes plus `audit.json`, in `$TMPDIR/meridian-ux` (see `docs/UX-AUDIT-0.7.md`) |
| `DocsScreenshotTests` | opt-in (`MERIDIAN_DOCS_SCREENSHOTS=1`): writes the user guide's screenshots to `docs-site/images` in the dark and the light theme |

Tests in the `server` collection share one server and run one at a time; each creates its own project, so they do not
depend on each other's data.
