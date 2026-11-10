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

Library content is never saved into your project: only your references to it are. When you **export** XMI, SysML v2 text or
the HTML report, Meridian asks whether to include the library content or only reference it.

## Updates

Meridian checks for newer versions, branch heads and baselines when the project opens. You can also check from ⌘K
(*Check for library updates*) or the ribbon's **Libraries** button. Available updates show as a badge in the title bar.

**Update used project…** opens a review of what changed between your pin and the new one — added, changed and removed
elements with their details, and the library's diagrams with the changes highlighted — and lists the elements of your project
that use what changed. **Update** moves the pin:

- your elements that refer to changed library elements become **suspect** (shown in the inspector, the traceability table and
  validation) until you mark them reviewed;
- references to library elements that were **removed** stay, as **missing references**: a dashed shape on diagrams and a
  validation error (LIB-001), never silently dropped;
- it is **one undo step** — ⌘Z puts the old pin back.

## Manage usages

The **Used projects** dialog lists each usage with its pin, where it is shown, who added it, any available update and **which
of your elements refer to it**. Change a pin or mount point, or remove a usage (Meridian warns when elements still refer to it).
Libraries can use other libraries; Meridian loads them too, skips cycles, and when the same library is reached at two
different pins it uses the first and tells you.

## Coming from Cameo

When you open a Cameo `.mdzip` that uses other Cameo projects (modules), and those projects already exist in Meridian (same
Cameo project id or the same name), Meridian offers to **use them instead of copying** their content. Copying stays the
default.

Technical details: [docs/LIBRARIES.md](https://github.com/finepointcgi/meridian/blob/main/docs/LIBRARIES.md).
