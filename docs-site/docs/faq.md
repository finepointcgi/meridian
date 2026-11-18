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
Yes, with its history. In the local workspace choose **Export ▸ Download project with history (.zip)** in the project
menu, then sign in to the server and choose **Open a file as a new project…** with that `.zip`. The new project on the
server has every version, comment, review and baseline, and you are its owner; share it from there. (**Download
.meridian file** takes only the current model.) See
[Moving a project with its history](version-control.md#moving-a-project-with-its-history).

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

## Reporting problems

**How do I report a bug or a crash?**
Use **Help ▸ Report a problem** (also in the command palette, ⌘K). It opens a new issue on GitHub in your browser with the
Meridian version and your operating system filled in. You review and edit everything there, and nothing is submitted until you
press **Submit new issue**. For a Cameo / MagicDraw import that lost something, choose the **Cameo import problem** form and
paste the report from the **Import check** dialog's **Download report** button: it counts what came across and what did not,
without your model's content.

**Does Meridian send crash reports or usage data?**
No. Meridian has no telemetry and never sends anything by itself. When the desktop app crashes or hits an unhandled error it writes a crash
file on your computer. On the next start it shows *Meridian ran into an unexpected error last time* with **Report this problem**, which
opens the same prefilled GitHub issue for you to review. **×** dismisses it; **Don't ask again** turns the prompt off (turn it
back on under **Settings ▸ Desktop ▸ Offer to report crashes**).

**What is in a crash file?**
Crash files are JSON files in the `crashes` folder of your data folder (**Settings ▸ Desktop ▸ Show crash files**). Each one
holds:

- the Meridian version, operating system and .NET runtime, and whether it was the desktop app or a server;
- where the error was caught (the app, a background task, or the workbench connection) and the error's type, message and stack trace;
- the last 60 lines of Meridian's own application log (warnings, errors and desktop-app messages), not the Output panel.

Before anything is written, Meridian removes: paths under your home folder (shown as `~/…`, keeping only the file
extension), your user and computer names, e-mail addresses, web addresses' query strings and credentials, access tokens,
and quoted text in messages, which is where element, diagram and project names usually appear. Crash files never contain
your model. The newest 20 are kept.

A team server doesn't write crash files: it logs unhandled errors, redacted the same way, to its normal log.
