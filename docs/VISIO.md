# Visio import

Meridian opens Visio drawings (`.vsdx`, and `.vsdm` with macros, which are ignored). Each foreground page becomes a
diagram, the same way as draw.io pages ([DRAWIO.md](DRAWIO.md)).

## Where to use it

- **Open a file as a new project…**, or open it in the desktop app.
- **Import into this project…**, or drop the file on a diagram.
- **Import ▸ Folder of diagrams…** picks up `.vsdx` files with the Mermaid, PlantUML and draw.io files in a folder.

## What each page becomes

Shapes are recognised by the name of the stencil master they were dragged from.

| Visio page | Becomes |
|---|---|
| Actors and use cases (UML Use Case stencil) | Use case diagram with associations, «include», «extend» and generalizations. |
| Class, Interface, Enumeration shapes (UML Class stencil) | Block definition diagram: the name, attribute and operation compartments become properties and operations; Generalization, Realization, Composition, Aggregation, Association and Dependency connectors become those relationships. |
| Lifelines | Sequence diagram. |
| Initial / final nodes, decisions, fork / join bars, actions | Activity diagram (or a state machine when it has states). |
| Any other page (flowcharts, network diagrams, block diagrams…) | A whiteboard drawing with the same shapes, text, sizes, positions, fill, line and text colours, and the glued connectors with their labels and arrows. Flowchart shapes keep their kind (process, decision, document, database, data, preparation, start / end). |

Shapes are sized and placed as on the page (1 inch = 96 pixels). Groups are taken apart into their shapes, and shapes
inherit size, geometry, text and colours from their master.

## Not imported

Each skipped part is listed as a warning in **Output**.

- The old binary `.vsd` format: open it in Visio and save it as `.vsdx`.
- Background pages (left out); layers are not kept.
- Rotation (shapes are placed upright), gradients, shadows, themes and fonts.
- Pictures, embedded objects and stencil icons without text or outline.
- Connectors that are not glued to a shape at both ends.
