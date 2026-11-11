# FAQ

## General

**Where is my data stored?**
In the desktop app's local workspace, in a SQLite database in your user data folder (`~/Library/Application
Support/Meridian`, `%APPDATA%\Meridian` or `~/.config/Meridian`). On a team server, in its PostgreSQL database (or a
SQLite file when the server is configured that way).

**Do I need an account?**
Not for the desktop app's local workspace. A team server needs an account, created by registration, by an
administrator or through single sign-on.

**Can I move a project from my computer to a team server?**
Yes. **Download .meridian file** from the project menu in the local workspace, then **Open a file as a new project** on the
server. The file holds the whole model; version history, comments and reviews stay in the original workspace.

**Which browsers work?**
The web app is a Blazor Server application and works in current versions of Chrome, Edge, Firefox and Safari. The
desktop app brings its own web view (WebView2 on Windows, WebKit on macOS and Linux).

**Is there a dark mode?**
Yes: the moon / sun icon in the title bar.

## Modeling

**Why did my shape disappear from only one diagram?**
**Backspace** removes a shape from the diagram but keeps the element in the model. Drag it back from the model
explorer. **Delete** removes the element from the model and every diagram.

**Why can't I connect these two shapes?**
Meridian enforces the SysML/UML rules for relationship ends, for example *satisfy* must go from a design element to a
requirement and *include* connects two use cases. The status bar says why a connection was refused.

**Why can't I create an internal block diagram?**
An IBD shows the inside of a block, so select the block (or something inside it) first.

**Why are my requirement's links marked as suspect?**
The requirement's name or text changed after the links were created or last reviewed. Check the other end of each
link, then clear the suspect markers in the traceability table. See [Suspect links](requirements.md#suspect-links).

**How do I change the requirement id prefix?**
**Requirements → Exchange → Renumber requirements…** renumbers everything in document order with a new prefix; new
requirements then use it.

**How big can a model be?**
The model explorer only builds the branches you expand, and very large diagrams draw only what is near the visible
area, so models with tens of thousands of elements stay responsive.

## Collaboration and history

**Is there a save button?**
Edits are saved as you make them. **Commit** (⌘S) creates a named version you can compare with, restore, branch from
or tag as a baseline.

**How do I undo a merge?**
Open **Version history**, select the version before the merge and **Restore** it. Restoring first commits the current
state, so nothing is lost.

**Two people edited the same element on different branches. What happens?**
The merge dialog shows a conflict and lets you choose either side or type a custom value. See
[Merging](version-control.md#merging).

## Scripting and plugins

**I can't find the script console on our server.**
It is off on servers unless an administrator sets `Meridian:Scripting:Enabled=true`, and even then only project owners
can run scripts.

**I installed a plugin but it does not show up.**
Plugins load when Meridian starts, so restart it. On a server, plugins load only from `Meridian:Plugins:Path`.
**Generate ▸ Plugins ▸ Loaded plugins…** shows load errors.

## Exchange

**Can I open Cameo `.mdzip` files?**
No. Export XMI from Cameo and import that. See [Coming from Cameo / EA](migration.md).

**Can I open EA `.eap` files?**
Not directly. Convert them to `.qea` in EA 16+ or export XMI.

**Does Meridian support SysML v2?**
Meridian reads and writes SysML v2 textual notation for the core of the language and edits it live against the model;
a team server also offers a read-only subset of the Systems Modeling API. The model itself is SysML 1.x / UML. See
[SysML v2 text](sysml-v2.md).

## Desktop app

**macOS says the app cannot be opened.**
Builds that are not notarized need right-click → **Open** the first time.

**How do I turn off update checks?**
Turn off *Check for updates* on the start screen, or set `MERIDIAN_NO_UPDATE_CHECK=1` for managed installs.
