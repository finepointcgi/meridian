# Mermaid import and export

Meridian reads [Mermaid](https://mermaid.js.org/) diagrams and turns them into SysML model elements with laid-out
diagrams, and writes the main SysML diagram kinds back out as Mermaid. The parser is written in C# (no JavaScript
runtime, no extra packages) and never calls the network.

## Where to find it

| Entry point | What it does |
| --- | --- |
| Project menu → Import → **Paste Mermaid…** (also ⌘K "Paste Mermaid") | Paste text, see a live preview of what each diagram becomes (counts per diagram, warnings), choose *create new* or *match by name*, then **Import**. |
| Project menu → **Import into this project…** with a `.mmd`, `.mermaid` or `.md` file | Opens the same dialog pre-filled with the file. |
| Project menu → **Open a file as a new project…** with a `.mmd`, `.mermaid` or `.md` file | Creates a new project holding the imported diagrams. The desktop app opens these files from its file dialog, the start screen and the command line too. |
| Drop a `.mmd` / `.mermaid` / `.md` file on the canvas | Imports it straight into the active diagram's package. |
| Project menu → Export → **Export diagram as Mermaid (.mmd)**, **Copy diagram as Mermaid** (also ⌘K, and Generate → Formats → **Mermaid .mmd**) | Writes the active activity, state machine, sequence, block definition / class or requirement diagram. |

### Mermaid from an AI assistant

Any LLM (ChatGPT, Claude, Copilot …) can describe a process, a structure or a set of requirements as Mermaid. Ask it for
"a Mermaid flowchart / classDiagram / requirementDiagram", copy the answer (with or without its ```` ```mermaid ````
fence) and use **Paste Mermaid…**. Whole Markdown answers with several fenced diagrams work too: every ```` ```mermaid ````
block becomes one diagram, the prose around them is ignored. Meridian's own assistant is not involved and nothing is
sent anywhere.

## What the importer accepts

- One diagram, several diagrams one after the other, or a Markdown file with ```` ```mermaid ```` (or `~~~mermaid`,
  `:::mermaid`) blocks. Code fences of other languages are skipped, even when they contain Mermaid-looking text.
- `%%` comments, `%%{init: …}%%` directives (also spread over several lines) and `---` front matter (`config:` is
  ignored; `title:` names the diagram).
- Quoted labels, `<br>` line breaks, `#quot;` / `#35;` style entities, markdown strings in backticks, simple HTML
  formatting tags and any Unicode text.
- Statements separated by `;` in flowcharts.

**Diagram names.** Each Mermaid diagram becomes one diagram named after its front matter `title:` (or `accTitle:`, or a
`title` statement where Mermaid has one); otherwise "Flowchart 1", "Class diagram 1", … numbered so they don't clash
with the model's existing diagrams. New diagrams open as tabs.

**Undo.** The whole import, however many diagrams, is one undo step ("Import Mermaid").

**Layout.** Diagrams are laid out with Meridian's auto-diagrammer along the Mermaid direction (`TD`/`TB`, `BT`, `LR`,
`RL`, from the header or a `direction` statement), with orthogonal connector routing. Composite states and swimlanes
are nested.

**Target package.** Elements go to the selected package (or the package of the selection or of the active diagram,
else the model root). Behaviors (activities, state machines, interactions) are created there and own their nodes.

**Create new vs. match by name.** With *create new* (the default) every import makes new elements; the same name used
twice inside one import is still one element (for example a block used by a class diagram and a requirement diagram in
the same Markdown file). With *match by name*, existing blocks, interface blocks, value types, enumerations, actors and
requirements (requirements also by `id`) are reused: only missing properties, operations and relationships are added,
so importing the same file twice changes nothing. Behavior nodes (actions, states, lifelines) are always new; lifelines
are typed by an existing block or actor of the same name in this mode.

**Warnings, never errors.** Anything not imported is listed in the import report (the preview, the **Output** panel
and the status bar), with the diagram and line number. Broken input never stops the import of the other diagrams.

**Limits.** Inputs are cut at 4 M characters, lines at 16 384 characters; at most 100 diagrams, 20 000 statements,
2 000 nodes and 5 000 edges per diagram, and nesting (subgraphs, composite states, namespaces, fragments) 32 levels
deep. Each limit produces a warning; uploads and drops are limited to 8 MB.

## Support matrix

### Flowchart (`flowchart` / `graph`) → activity diagram

| Mermaid | Meridian |
| --- | --- |
| the diagram | an **Activity** (named after the diagram) owning the nodes, with an activity diagram |
| `id`, `id[text]`, `id(text)`, `id([text])`, `id[[text]]`, `id>text]`, `id{{text}}`, `id[/text/]`, `id[\text\]`, trapezoids | **Action** (the label is the name; the id when there is no label) |
| `id{text}` (rhombus), `@{ shape: diamond }` | **Decision node** (named after the label) |
| `id((text))` circle | **Initial node** when it has only outgoing links, **activity final node** when it has only incoming links, else an action |
| `id(((text)))` double circle, `@{ shape: fr-circ }` / `stop` | **Activity final node** |
| `@{ shape: sm-circ }` / `start` | **Initial node** |
| `id[(text)]` cylinder, `@{ shape: cyl / db / disk }` | **Object node** (central buffer; Meridian has no separate datastore node) |
| `@{ shape: fork }` / `join` | **Fork / join node** |
| `-->`, `---`, `-.->`, `==>`, `--o`, `--x`, longer variants | **Control flow** (an **object flow** when either end is an object node) |
| `-->\|text\|`, `-- text -->`, `-. text .->`, `== text ==>` | the text becomes the flow's **guard** |
| `A & B --> C & D`, chains `A --> B --> C` | one flow per pair |
| `<-->` | two flows (warning) |
| `subgraph id [title] … end` | a **partition (swimlane)** holding its nodes; nested subgraphs become separate lanes (warning) |
| links to or from a subgraph | connected to its first / last node (warning) |
| `~~~` invisible links | skipped (warning) |
| `classDef`, `class`, `:::`, `style`, `linkStyle`, `click` | skipped (one warning) |

Flowcharts with subgraphs are laid out top to bottom with one vertical swimlane per subgraph and rows shared across
lanes (an `LR` direction is noted in a warning).

### Class diagram (`classDiagram`) → block definition diagram

| Mermaid | Meridian |
| --- | --- |
| `class A`, `class A { … }`, `A : member`, `class A["Label"]` | **Block** |
| `<<interface>>` (statement, in the body or after the name) | **Interface block** |
| `<<enumeration>>` / `<<enum>>` | **Enumeration**; members become **enumeration literals** |
| `<<abstract>>` | abstract block |
| `<<dataType>>` / `<<valueType>>` / `<<struct>>` | **Value type** |
| other annotations (`<<service>>` …) | ignored (warning) |
| `class Box~T~`, `List~int~` | name `Box<T>`, type `List<int>` |
| `namespace N { … }` | **Package** N holding its classes (nested namespaces nest) |
| attribute `+String name`, `-mass : Real`, `+int wheels = 4`, `items[0..*]`, `Type[]` | **property** with visibility, type, default value and multiplicity; a type naming a class of the diagram is a reference to it, `int`/`float`/`bool`/`string` … become `Integer`/`Real`/`Boolean`/`String` |
| method `+start(speed: Real) bool`, `stop()$`, `area()*` | **operation** with signature `(speed: Real) : bool` and visibility; `*` abstract; `$` static is imported as an ordinary member (warning) |
| `A <\|-- B`, `B --\|> A` | **Generalization** B → A (a **realization** when A is an interface block and B is not) |
| `B ..\|> A`, `A <\|.. B` | **Realization** B → A |
| `A *-- B` | **Composition** A → B plus a **part property** in A typed by B (named after the label, else after B) |
| `A o-- B` | **Aggregation** A → B |
| `A --> B`, `A -- B` | **Association** (the label is its name) |
| `A ..> B`, `A .. B` | **Dependency** |
| `"1" … "*"` cardinalities | the target end's multiplicity on the relationship (and on the part property); the source end is not stored (warning) |
| `note "…"`, `note for A "…"` | **Comment** (placed next to A) |
| `direction LR` | layout direction |

### State diagram (`stateDiagram`, `stateDiagram-v2`) → state machine diagram

| Mermaid | Meridian |
| --- | --- |
| the diagram | a **State machine** owning the states, with a state machine diagram |
| `A`, `state A`, `state "Long name" as A`, `A : description` | **State** (description lines become documentation; `entry / x`, `exit / y`, `do / z` lines fill the entry, exit and do behaviors) |
| `[*] --> A` / `A --> [*]` | **Initial state** / **final state**, one of each per composite state |
| `A --> B : event [guard] / effect` | **Transition** with trigger (name), guard and effect |
| `state A { … }` | **Composite state** owning its substates (nested on the diagram) |
| `<<choice>>`, `<<fork>>`, `<<join>>`, `<<history>>` | a **state** of that name (Meridian has no pseudostates of these kinds; warning) |
| `--` concurrent regions | one region (warning) |
| `note right of A : …`, `note left of A … end note` | **Comment** next to A |
| `classDef`, `class`, `:::` | skipped (warning) |

### Sequence diagram (`sequenceDiagram`) → sequence diagram

| Mermaid | Meridian |
| --- | --- |
| the diagram | an **Interaction** owning the lifelines, fragments and notes, with a sequence diagram |
| `participant A`, `participant A as Alice`, `actor B`, participants used without a declaration | **Lifeline** (left to right in order of appearance) |
| `A->>B: text`, `A->B` | **Message**, synchronous call |
| `A-->>B: text`, `A-->B` | **Message**, reply |
| `A-)B`, `A--)B` | **Message**, asynchronous call |
| `A-xB`, `A--xB` (lost) | asynchronous call (warning) |
| `<<->>` | one synchronous call (warning) |
| `activate` / `deactivate`, `+` / `-` suffixes | Meridian derives activation bars from call / reply pairs; the explicit markers are not stored (warning) |
| `loop`, `alt`, `opt`, `par`, `critical`, `break` … `end` | **Combined fragment** with that operator and the condition as guard, sized around its messages |
| `else`, `and`, `option` | merged into the enclosing fragment; their conditions are appended to its guard, separated by `\|` (warning) |
| `Note left of / right of / over A[,B]: text` | **Comment** at that point |
| `title`, `autonumber`, `box … end`, `rect … end`, `create`, `destroy`, `link(s)` | title names the diagram; the rest is skipped (warnings) |

### Requirement diagram (`requirementDiagram`) → requirement diagram

| Mermaid | Meridian |
| --- | --- |
| `requirement`, `functionalRequirement`, `interfaceRequirement`, `performanceRequirement`, `physicalRequirement`, `designConstraint` | **Requirement**; a type other than `requirement` is kept in the custom attribute `Type` |
| `id:` | requirement id (auto-numbered when missing) |
| `text:` | requirement text |
| `risk: low / medium / high` | risk |
| `verifymethod: analysis / inspection / test / demonstration` | verification method |
| `element X { type: …, docref: … }` | **Block** (a **test case** when the type contains "test"); type and docref go to the documentation |
| `A - satisfies -> B` | **Satisfy** |
| `A - verifies -> B` | **Verify** |
| `A - traces -> B` | **Trace** |
| `A - derives -> B` | **DeriveReqt** (A → B, as written) |
| `A - refines -> B` | **Refine** |
| `A - copies -> B` | **Copy** |
| `A - contains -> B` | B becomes a child requirement of A |
| `B <- verb - A` | the same, written backwards |
| a relationship SysML does not allow between those ends | a **Trace** (warning) |

### ER diagram (`erDiagram`) → block definition diagram

| Mermaid | Meridian |
| --- | --- |
| entity, `p[Person]` alias | **Block** |
| `type name PK, FK "comment"` | **property** with the type (common SQL types mapped to SysML primitives); keys and comment go to its documentation |
| `A \|\|--o{ B : label` (any cardinality markers, `--` or `..`) | **Association** A → B named after the label, with B's multiplicity (`1`, `0..1`, `*`, `1..*`); both ends are written in its documentation |

### C4 (`C4Context`, `C4Container`, `C4Component`) → block definition diagram

| Mermaid | Meridian |
| --- | --- |
| `Person(…)`, `Person_Ext(…)` | **Actor** |
| `System`, `SystemDb`, `SystemQueue`, `Container…`, `Component…` (and `_Ext`) | **Block**; description and technology go to the documentation |
| `Rel(…)`, `Rel_U/D/L/R`, `Rel_Back`, `BiRel` | **Dependency** named after the label (two for `BiRel`) |
| boundaries | not imported as elements, their contents are (warning) |
| `UpdateElementStyle`, `UpdateRelStyle`, `UpdateLayoutConfig` … | skipped (warning) |

### Skipped diagram types

`C4Dynamic`, `C4Deployment`, `mindmap`, `gantt`, `pie`, `journey`, `gitGraph`, `quadrantChart`, `timeline`, `sankey`,
`xychart`, `block`, `packet`, `kanban`, `architecture`, `radar`, `treemap` and `zenuml` have no SysML equivalent. They are
listed as *Skipped* in the preview and reported with a warning; the other diagrams of the same input are still
imported.

## Export

**Export diagram as Mermaid (.mmd)** and **Copy diagram as Mermaid** write the active diagram with a front matter
`title:` (the diagram's name):

| Meridian diagram | Mermaid |
| --- | --- |
| Activity | `flowchart` (TD or LR from the layout): actions `id("…")`, decisions `{…}`, initial `((…))`, final `(((…)))`, object nodes `[(…)]`, fork / join `@{ shape: fork }`, swimlanes as `subgraph`, flows with their guard as `-->\|guard\|` |
| State machine | `stateDiagram-v2`: states with entry / do / exit and documentation lines, composite states as `state X { … }`, `[*]` in the right scope, transitions as `event [guard] / effect` |
| Sequence | `sequenceDiagram`: participants (actors when typed by an actor), messages in time order (`->>`, `-->>`, `-)`), combined fragments with `else` / `and` / `option` for merged operands, comments as notes |
| Block definition, class | `classDiagram`: blocks with annotations, properties, operations and enumeration literals, generalization, realization, composition (with the part name), aggregation, association, dependency; comments as notes; other relationships (satisfy, allocate …) as `%%` comments |
| Requirement | `requirementDiagram`: requirements with id, text, risk and verification method (and their Mermaid type), other elements as `element`, containment as `contains`, satisfy / verify / trace / deriveReqt / refine / copy |

Importing an exported diagram gives back the same elements and the same relationships between them (the round trip is
covered by tests). What is not carried: layout positions, styles, pins, comments on activity and state diagrams, source
end multiplicities, and the position of `else` separators inside fragments.
