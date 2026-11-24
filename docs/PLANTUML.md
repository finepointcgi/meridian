# PlantUML import

Meridian reads PlantUML text and turns each diagram into model elements with a laid-out diagram, the same way it reads
Mermaid ([MERMAID.md](MERMAID.md)).

## Where to use it

- **Open a file as a new project…** with a `.puml`, `.plantuml`, `.pu` or `.wsd` file. The desktop app opens them too.
- **Import into this project…**, or drop the file on a diagram: a preview lists each diagram first.
- **Import ▸ Folder of diagrams (Mermaid, PlantUML, draw.io)…** reads every PlantUML file in a folder, plus the
  ```` ```plantuml ```` blocks of Markdown files. Importing the folder again updates those diagrams in place.
- **Import Mermaid** (paste): paste PlantUML text that starts with `@startuml` and it is read as PlantUML.
- **Code sync** keeps PlantUML files in a Git repository in step with the model ([CODE-SYNC.md](CODE-SYNC.md), "Diagram files").

A file can hold several `@startuml … @enduml` blocks; each becomes a diagram. `title` names the diagram, otherwise it is
named after the file.

## What each kind becomes

| PlantUML | Becomes |
|---|---|
| Sequence (`participant`, `actor`, `A -> B : msg`) | Interaction with lifelines and messages. `-->` is a reply, `->>` an asynchronous call, `return` answers the open call. `alt / else / opt / loop / par / break / critical / group … end` become combined fragments; notes become comments. |
| Class (`class`, `abstract class`, `interface`, `enum`, `package {}`) | Block definition diagram. Members become properties and operations (`{static}`, `{abstract}`, visibility). `<|--` generalization, `<|..` realization, `*--` composition (with a part property), `o--` aggregation, `-->` association, `..>` dependency, multiplicities in quotes. Packages become packages. |
| Use case (`actor`, `usecase`, `(Use case)`, `:Actor:`, `rectangle {}`) | Use case diagram with actors, use cases, associations, «include», «extend» and generalizations. A `rectangle` around use cases is drawn as the system boundary. |
| Activity, current syntax (`start`, `:step;`, `if / else / endif`, `while`, `repeat`, `fork`, `switch`, `|Lane|`) | Activity with actions, decisions, loops, fork and join bars, initial and final nodes; arrow labels and `then (yes)` become guards; `|Lane|` swimlanes become partitions. |
| State (`[*] -->`, `state X {}`, `<<choice>>`, `X : entry / …`) | State machine with composite states, initial and final states, choice / fork / join / history pseudostates, and transitions `event [guard] / effect`. |

## Not imported

Each skipped part is listed as a warning in **Output**.

- Component, deployment, object, timing, mind map, WBS, Gantt, JSON / YAML and other `@start…` kinds.
- The old activity syntax (`(*) --> "Step"`).
- `!include`, `!define` and other preprocessor lines, `skinparam`, styles, colours and sprites.
- Activation bars (Meridian derives them from calls and replies), `ref over`, found / lost messages (`[->`).
- Concurrent regions in states (`--`) are merged into one region.
