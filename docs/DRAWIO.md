# draw.io import and export, folders of diagrams

Meridian reads [draw.io / diagrams.net](https://www.drawio.com/) files and writes diagrams back out as draw.io files.
It can also import a whole folder of Mermaid and draw.io files in one go. Everything runs in C# on the server (or in the
desktop app); nothing is sent anywhere.

## Where to find it

| Entry point | What it does |
| --- | --- |
| Project menu → Import → **Folder of diagrams (Mermaid, PlantUML, draw.io)…** (also ⌘K) | Pick a folder. Meridian reads its Mermaid (`.mmd`, `.mermaid`, Markdown with ```` ```mermaid ```` blocks) and draw.io files, shows what each will become, then imports them as one undo step. |
| Drop a folder, or `.drawio` files, on the canvas | Imports straight into the diagram's package (no dialog). |
| Project menu → **Import into this project…** with draw.io files, or a `.zip` of a folder of diagrams | Opens the same preview dialog. |
| Project menu → **Open a file as a new project…** with a draw.io file | A new project holding one diagram per page. The desktop app opens `.drawio` files from its file dialog, the start screen and the command line. |
| Project menu → Export → **Export diagram as draw.io**, **Export all diagrams as draw.io**; a diagram tab's menu → Export; right-click the canvas | A `.drawio` file with one page per diagram. Open it in draw.io (desktop, web, or the VS Code extension). |

Accepted files: `.drawio`, `.dio`, `.drawio.xml`, and pictures with the diagram embedded: `.drawio.svg` and `.drawio.png`
(draw.io's "Include a copy of my diagram" export). Gliffy files (`.gliffy`) are read as draw.io pages too; see
[GLIFFY.md](GLIFFY.md). Compressed and uncompressed pages both work; every page of a file is
imported.

## What a page becomes

Meridian looks at the shapes on each page:

| The page has | It becomes | Notes |
| --- | --- | --- |
| UML lifelines (`umlLifeline`) | Sequence diagram | Messages in top-to-bottom order; dashed = reply, open arrow = asynchronous, filled = synchronous. UML frames become combined fragments (`alt`, `loop`, `opt`…), with a dashed line inside separating operands. Notes attach to the nearest lifeline. |
| Actors (`umlActor`) and ellipses | Use case diagram | Lines labelled «include» / «extend» become include / extend; a hollow triangle a generalization; other lines associations. A rectangle around the use cases (the system boundary) is kept as an outline. |
| Boxes whose first line is «requirement» | Requirement diagram | `id: …` and the text lines become the requirement's id and text; lines labelled «satisfy», «verify», «derive», «refine», «copy» become those relationships, a circle-plus end becomes containment. |
| UML class boxes (the "Class" shape, an HTML box with compartments, an ER "Table") | Block definition diagram | Rows such as `+ name: Type [0..*]` and `+ op(a: Int): Bool` become properties and operations; «interface» / «enumeration» / «valueType» set the kind, italic names make the block abstract. A hollow triangle is a generalization (dashed: realization), a filled diamond composition with a part property, a hollow diamond aggregation, a dashed arrow a dependency; small labels near an end such as `1` or `0..*` become multiplicities. Folder shapes around classes become packages. |
| Start / end states (`startState`, `endState`) and states with event labels | State machine diagram | `event [guard] / effect` labels become triggers, guards and effects; `entry / …`, `do / …`, `exit / …` lines inside a state become its behaviours. Rounded containers holding states become composite states. |
| Start / end states with decisions and guards | Activity diagram | Rounded boxes become actions, diamonds decisions, black bars forks / joins, cylinders data stores; labels on flows become guards. draw.io swimlanes become partitions. |
| Anything else | Whiteboard drawing | Every shape becomes a free shape (rectangle, rounded, ellipse, diamond, text, cylinder, hexagon, parallelogram, triangle, document, cloud, callout or picture) with its label, size, position and fill / line / text colours; lines keep their labels, bends and style (arrow, plain, dashed, dotted, two-way). |

Shapes keep the positions they had in draw.io (moved so the drawing starts at the top-left). Activities with swimlanes,
state machines with composite states and sequence diagrams are laid out again by Meridian.

Turn off **Recognise UML in draw.io pages** in the import dialog to bring every page in as a whiteboard drawing.

Limits and warnings (all listed in the Output panel after the import):

- Lines not attached to a shape at both ends are left out (Meridian connectors always join two shapes).
- Pictures are kept when they are embedded in the file; linked pictures (a URL) are left out. Library icons (AWS, Azure,
  network…) become rectangles with their label.
- On a UML page, shapes that are not part of the UML diagram are left out; import the page as a drawing to keep them.
- draw.io layers, groups and per-shape fonts are not kept.

## Export

Each diagram becomes a page with draw.io's own shapes: class boxes with their members, actors, use cases, lifelines with
messages at their heights, frames, notes, folders, start / end states, decisions and bars, and free shapes with their
colours. Every shape also carries a `meridian=<kind>` style entry that draw.io keeps and ignores, so a file exported from
Meridian, edited in draw.io and imported again comes back as the same kinds of diagram and element. Ports in class boxes
come back as ports. Internal block and parametric diagrams come back on the same block, with their parts ("name : Type"),
ports and parameters on their parts, connectors and binding connectors (with the parts their ends sit on) and item flows;
read back into the project they came from with **Match by name**, they land on the existing parts instead of copies.

## Folders of diagrams

**Folder of diagrams…** reads the folder and its subfolders (skipping `.git`, `node_modules`, `bin`, `obj`, `dist` and
`build`) for Mermaid and draw.io files. Markdown files without a ```` ```mermaid ```` block are ignored, so a `docs`
folder can be imported as it is.

- **Packages:** each folder becomes a package (the chosen folder is the top one); turn off *Create a package for each
  folder* to put every diagram straight into the target package.
- **Names:** a Mermaid diagram with a title keeps it; otherwise it is named after its file (`login.mmd` → *login*;
  several diagrams in one file → *login 1*, *login 2*). draw.io pages keep their page names; a single page left with
  draw.io's default name takes the file's name.
- **Shared elements:** a block, actor or requirement used in several files is created once.
- **Importing again:** every diagram remembers the file it came from (the `import.file` field). Importing the same
  folder again finds where it went last time and replaces those diagrams (with their activities, state machines,
  interactions and drawing shapes), matching blocks and other elements by name. Nothing else in the project is touched.
- Limits: 2,000 files, 32 MB per file, 128 MB in all.
