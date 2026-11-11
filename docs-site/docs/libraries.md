# Used projects (shared libraries)

Reuse a package of another project — a parts catalogue, a requirements standard, shared interfaces — without copying it.
Like Cameo's *used projects* and Enterprise Architect's shared packages, the package appears **read-only** in your project,
pinned to a version, a branch or a baseline, and you decide when to take its updates.

## Use a package of another project

1. Open **Project ▸ Used projects…** (also **Share ▸ Used projects…**, or ⌘K *Used projects*).
2. Pick the **project**, what to **pin to** — a committed **version**, a **branch head** (follows the branch as it changes) or
   a **baseline** — and the **package**. Only projects and packages you can read are offered.
3. Optionally pick the package of your project to **show it in** (the model root by default), then **Use package**.

The library appears in the model explorer below its mount point, with a badge naming its pin (for example `v3`). Everyone
working on the project sees it — as far as they may read the source project: packages hidden from someone there stay
hidden here.

## Work with library elements

- **Type parts** by library blocks and value types: the inspector's *Type* list includes them.
- **Link** to library requirements and elements: satisfy, trace, allocate, generalize… The links belong to your project.
- **Show** library elements on your diagrams: drag them from the model explorer. They carry a small library marker.
- Library elements **can't be changed** here. The inspector says where they come from and offers **Open in source project**.
- Library diagrams open read-only.

### Open in source project

**Open in source project** opens the library's project *at your pin*, with the element selected:

- pinned to a **version** or a **baseline**: a read-only view of that stored version, labelled **Viewing version 3 (pinned)**
  (or *Viewing baseline PDR — version 5 (pinned)*). Nothing can be edited there, and edits others make to the project meanwhile
  don't change what you see. **Go to latest** opens the project's current main line;
- pinned to a **branch**: that branch, live — editable if you are an editor of the source project.

Packages hidden from you in the source project stay hidden in this view too.

Library content is never saved into your project: only your references to it are. When you **export** XMI, SysML v2 text or
the HTML report, Meridian asks whether to include the library content or only reference it.

## Updates

Meridian checks for newer versions, branch heads and baselines when the project opens. You can also check from ⌘K
(*Check for library updates*) or the ribbon's **Model views ▸ Used projects** button. Available updates show as a badge in the title bar.

**Update used project…** opens a review of what changed between your pin and the new one — added, changed and removed
elements with their details, and the library's diagrams with the changes highlighted — and lists the elements of your project
that use what changed. **Update** moves the pin:

- your elements that refer to changed library elements become **suspect** (shown in the inspector, the traceability table and
  validation) until you mark them reviewed;
- references to library elements that were **removed** stay, as **missing references**: a dashed shape on diagrams and a
  validation error (LIB-001), never silently dropped;
- the pin belongs to the whole project, so **every branch** gets the suspect marks — also branches nobody has open, and
  projects that use your project (they load its libraries too): there the marks arrive as a version committed by the system
  (*Library update: “Parts” v3 → v4 …*), so they are waiting when someone opens it;
- your open diagram **tabs stay open** (except diagrams the update removed), with the same active tab, zoom, scroll position and
  selection;
- it is **one undo step** — ⌘Z puts the old pin back and clears the marks it set, everywhere.

### Library changed upstream

When a project you edit uses a library pinned to a version, and someone commits a new version of the library's project that
changes the package you use, you get a notification (the bell): *“Parts” changed upstream: v5 is available for “Vehicle”
(pinned to v4).* **Review update** opens the project and the update review. You only hear about libraries you can read, at
most once until you have read the notification; turn it off in **Settings ▸ Editor ▸ Library changed upstream**.

## On a phone

The **Model** list shows used projects like everything else, with the pin as a badge (for example `v3`) and *read-only* on
their content; an element's sheet says which used project it comes from and offers **Open source**. When updates are available
a library icon with a count appears in the top bar: it opens the update review as a full-screen sheet, with **Update** at the
bottom.

## Manage usages

The **Used projects** dialog lists each usage with its pin, where it is shown, who added it, any available update and **which
of your elements refer to it**. Change a pin or mount point, or remove a usage (Meridian warns when elements still refer to it).
Libraries can use other libraries; Meridian loads them too, skips cycles, and when the same library is reached at two
different pins it uses the first and tells you.

## Coming from Cameo

When you open a Cameo `.mdzip` that uses other Cameo projects (modules), and those projects already exist in Meridian (same
Cameo project id or the same name), Meridian offers to **use them instead of copying** their content. Copying stays the
default. Meridian reads the used projects from the `.mdzip`'s project metadata — local files and Teamwork Cloud projects
alike — and the import report lists each one as found (only referenced), copied (its shared packages were in the file) or
linked (used from a Meridian project), with the standard SysML and UML profiles listed apart.

Technical details: [docs/LIBRARIES.md](https://github.com/finepointcgi/meridian/blob/main/docs/LIBRARIES.md).
