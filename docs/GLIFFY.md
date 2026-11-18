# Gliffy import

Meridian reads [Gliffy](https://www.gliffy.com/) diagrams: the `.gliffy` files that Gliffy for Confluence and Gliffy
Online save (a JSON document). Each Gliffy page is turned into a draw.io page and then imported exactly like a draw.io
file, so everything in [DRAWIO.md](DRAWIO.md) applies: UML pages become model elements, other pages whiteboard
drawings. Everything runs in C# on the server (or in the desktop app); nothing is sent anywhere.

## Where to find it

Gliffy files work wherever draw.io files do:

| Entry point | What it does |
| --- | --- |
| Project menu → **Open a file as a new project…** with a `.gliffy` file | A new project named after the file, one diagram per page. The desktop app's file dialog and command line open `.gliffy` too. |
| Project menu → **Import into this project…** with `.gliffy` files | The diagram files dialog: what each page becomes, then **Import** (one undo step). |
| Drop `.gliffy` files, or a folder holding them, on the canvas | Imports straight into the diagram's package. |
| Project menu → Import → **Folder of diagrams…** | Picks up `.gliffy` files next to Mermaid and draw.io files; importing the folder again replaces what it made last time. |

Gliffy for Confluence keeps each diagram as an attachment of its page. Download that attachment (it is the Gliffy
JSON) and give it a `.gliffy` extension if it has none.

There is no Gliffy export; export as draw.io instead.

## What is read

| Gliffy | Meridian |
| --- | --- |
| `stage.objects`, or `pages[].scene.objects` in newer files | one diagram per page, named after the page title (a single page takes the file's name) |
| `x`, `y`, `width`, `height` | position and size (the drawing is moved to start at the top-left) |
| `uid` shape ids (`com.gliffy.shape.basic.basic_v1.default.ellipse`, `…flowchart_v1.default.decision`…) | the matching shape: rectangle, rounded, ellipse, diamond, triangle, hexagon, cylinder, cloud, parallelogram, document, note, text |
| `graphic.Shape` `fillColor`, `strokeColor` (`#rrggbb` or `rgb(…)`) | fill and line colour |
| `graphic.Text` `html`, or a Text child of a shape | the label (HTML removed, paragraphs as lines) |
| `graphic.Image` with an embedded `data:` URL | a picture |
| `graphic.Line` `controlPath` | the line's bends |
| `constraints.startConstraint` / `endConstraint` `nodeId` | the shapes the line joins; a line attached to a part of a shape (a class compartment) joins the shape |
| `startArrow` / `endArrow` numbers, `dashStyle` | arrow heads (open, filled, hollow triangle, diamonds, ER crow's feet) and dashes, read the way draw.io reads them |
| groups (`…default.group`, or an object with children and no graphic) | flattened: the shapes keep their places |
| `layers` | read for order and visibility; shapes on hidden layers are left out |

### Recognised UML and flowchart shapes

| Gliffy shape | Becomes (with **Recognise UML** on) |
| --- | --- |
| `uml_v1/v2 … class`, `class2`, `simple_class`, `abstract_class`, `interface`, `enumeration` | a block (interface block, enumeration) with its attribute and operation compartments read as members; the page becomes a block definition diagram |
| class lines: hollow triangle (generalization, dashed: realization), filled / hollow diamond, dashed open arrow | generalization, realization, composition with a part property, aggregation, dependency |
| `actor` with `use_case` ellipses | a use case diagram with actors, use cases and their links |
| `sequence … lifeline` with lines between them | a sequence diagram; dashed lines are replies |
| `initial` / `final` states, `decision`, `fork` / `join` | start and end nodes, decisions, bars: an activity or state machine diagram |
| flowchart `start_end` labelled *Start* / *Begin* or *End* / *Stop* / *Finish* / *Done* / *Exit* | the initial and final node, so a flowchart with them becomes an **activity diagram**: processes become actions, decisions decision nodes, line labels guards |
| UML `note` | a comment |

A flowchart without such start and end shapes, and every other page, becomes a whiteboard drawing. Turn off
**Recognise UML** in the import dialog to bring every page in as a drawing.

## Limits and warnings

All listed in the Output panel after the import:

- Lines not attached to a shape at both ends are left out (Meridian connectors always join two shapes).
- Rotated shapes are placed without their rotation; hidden objects are left out.
- Library icons (network, AWS, UI mock-ups…) become rectangles with their label. Linked pictures (a URL) are left out.
- Text styling (fonts, sizes, colours inside the label), gradients, shadows and line widths are not kept.
- The responsibilities compartment of Gliffy's `class2` shape is left out.
- Swimlane pools come in as framed rectangles, not as activity partitions.
- Files are limited to 32 MB and 20,000 objects per page. A file that is not valid JSON, or has no stage or pages,
  gives a clear error and imports nothing; in a folder import, the other files still import.
