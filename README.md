# Meridian

Model-based systems engineering that is as easy to draw with as a whiteboard. Meridian is SysML block,
internal block, requirement, activity, state machine, sequence, use case and parametric diagrams, all
linked to requirements and traceability. It also covers BPMN, ArchiMate, ER/database diagrams and free
drawing, with live multi-user editing. It is built with C#, Blazor and MudBlazor.

It runs as a **web app** (a team server backed by PostgreSQL) and as a **desktop app** for Windows, macOS
and Linux. The desktop app works offline on your computer or connects to a team server.

## Download

Get the latest build from [Releases](../../releases):

| Platform | File | Notes |
| --- | --- | --- |
| macOS (Apple silicon) | `Meridian-<ver>-macos-arm64.zip` | Unzip, then drag **Meridian.app** to Applications. Not notarized yet: right-click → Open the first time. |
| macOS (Intel) | `Meridian-<ver>-macos-x64.zip` | Same as above. |
| Windows 10/11 x64 | `Meridian-<ver>-windows-x64.zip` | Unzip and run `Meridian.exe`. Needs the WebView2 runtime, which Windows 11 already has. |
| Linux x64 | `Meridian-<ver>-linux-x64.tar.gz` | Needs WebKitGTK 4.1. Run `./Meridian` or `./install.sh`. |
| Server (web version) | `Meridian-Server-<ver>.zip` | Needs the ASP.NET Core 10 runtime. You can also use Docker (below). |

When it first starts, the desktop app asks where to work:

- **This computer**: a private offline workspace. Data lives in a SQLite file in your user data folder
  (`~/Library/Application Support/Meridian`, `%APPDATA%\Meridian` or `~/.config/Meridian`). There is
  no account to create. Version history and comments still work.
- **Team server**: enter the address of a Meridian server to sign in, share projects and edit together
  in real time.

You can change the choice later from your avatar menu (**Switch workspace or server…**).

## Features

- **Drawing as easy as draw.io**
  - A searchable shapes panel: click to add, drag to place.
  - Hover arrows that add connected shapes.
  - Alignment guides and resize handles.
  - Double-click empty canvas to add a shape; start typing to label one.
  - A format bar for fill, line and text colors and line width, with a format painter.
- **SysML and UML**
  - Block definition, internal block (ports, item flows), parametric (with an equation solver), activity
    (swimlanes, pins, call behavior), state machine (composite states), sequence (fragments), use case,
    package and class diagrams.
  - Profiles and stereotypes, including UAF.
- **Requirements**
  - A requirements table, traceability matrix and impact analysis.
  - Satisfy, verify, refine, derive and trace links, plus allocation.
- **Other languages**: BPMN 2.0, ArchiMate 3, ER/database diagrams (DDL in and out for PostgreSQL, MySQL,
  SQL Server and SQLite), C# reverse engineering, and a free-drawing whiteboard.
- **Simulation**: step through activities and state machines.
- **Exchange**
  - XMI 2.5.1 (with diagrams), SysML v2 text, ReqIF, BPMN XML and ArchiMate exchange files.
  - Enterprise Architect `.qea` import.
  - A read-only SysML v2 REST API.
- **Reports**: Word and PDF from templates, plus SVG export.
- **Collaboration** (server)
  - Accounts and project roles (owner, editor, viewer).
  - Live co-editing with presence.
  - Version history with diff and restore, and comments.

## Run the server

With Docker (PostgreSQL included):

```bash
docker compose --profile app up -d --build    # → http://localhost:8180
```

From source (needs the .NET 10 SDK):

```bash
docker compose up -d db                                   # PostgreSQL on 127.0.0.1:5433
cp src/Meridian.Web/appsettings.Development.example.json src/Meridian.Web/appsettings.Development.json
dotnet run --project src/Meridian.Web --urls http://localhost:5180
```

In development, the users listed under `DevSeed` are created when the server starts. Change their passwords
in your local `appsettings.Development.json`, which git ignores.

To run with a single SQLite file instead of PostgreSQL, set `Meridian__Storage=sqlite` and
`Meridian__DataPath=<folder>`.

## Build the desktop apps

```bash
build/package.sh 0.1.0                 # all platforms → artifacts/
build/package.sh 0.1.0 osx-arm64       # just one
```

Pushing a tag such as `v0.2.0` runs the release workflow, which builds every platform and publishes a
GitHub release.

## Project layout

| Path | Contents |
| --- | --- |
| `src/Meridian.Core` | Model repository, metamodel and notation, layout, analysis, simulation, validation |
| `src/Meridian.Serialization` | Project files, SVG rendering, and all import/export formats |
| `src/Meridian.Data` | Storage: `PostgresStore` (server) and `SqliteStore` (desktop local mode) |
| `src/Meridian.Web` | Blazor Server app (MudBlazor UI, live collaboration, SysML v2 API) |
| `src/Meridian.Desktop` | Photino desktop host: start screen, in-process local server, native save dialogs |
| `tests/` | xUnit and bUnit tests |
| `build/` | Packaging script, icons and platform files |
