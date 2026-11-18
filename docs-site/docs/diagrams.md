# Diagram types

Create a diagram from the ribbon's **Diagram** group (BOARD, BDD, IBD, REQ, ACT, STM, SD, UC, and **More** for
parametric, BPMN, ArchiMate, ER, class and package diagrams), from **+** on the tab strip, or from the model
explorer's context menu. Meridian creates the diagram *for the selection*:

- internal block and parametric diagrams belong to a block, so select the block (or something inside it) first;
- activity, state machine and sequence diagrams go into the selected activity, state machine or interaction; when the
  selection is a block, use case or package, Meridian first creates a new behavior there to own the diagram;
- every other diagram goes into the nearest package.

| Diagram | Header | Owned by | Typical content |
| --- | --- | --- | --- |
| Block definition | `bdd` | package | blocks, interface blocks, value types, constraint blocks, enumerations, actors, instances; association, composition, aggregation, generalization, realization, dependency, allocate |
| Internal block | `ibd` | block | parts, ports, connectors, item flows, allocate |
| Parametric | `par` | block | value properties, constraint properties and their parameters, binding connectors |
| Requirement | `req` | package | requirements, test cases, blocks, use cases, failure modes; derive, satisfy, verify, refine, trace, copy, mitigate |
| Use case | `uc` | package | actors, use cases, subject blocks; association, include, extend, generalization |
| Activity | `act` | activity | actions, initial/final nodes, decisions/merges, forks/joins, swimlanes, object nodes, pins; control and object flows |
| State machine | `stm` | state machine | states (also composite), initial and final states, pseudostates (choice, junction, fork, join, shallow / deep history, entry / exit point, terminate), transitions |
| Sequence | `sd` | interaction | lifelines, messages, combined fragments with operands |
| Package | `pkg` | package | packages, profiles, stereotypes; dependency, generalization |
| Class | `class` | package | UML classes, interfaces, data types, enumerations, components |
| BPMN | `bpmn` | package | pools, lanes, events, tasks, gateways, data objects; sequence, message and association flows |
| ArchiMate | `archi` | package | business, application and technology elements and ArchiMate relationships |
| Entity-relationship | `erd` | package | tables, columns, foreign keys |
| Feature model | `fm` | package | features, configurations; feature decomposition, requires, excludes |
| Deployment | `deploy` | package | nodes, devices, execution environments (nested), artifacts, deployment specifications; deploy, manifest, communication paths ([details](deployment-diagrams.md)) |
| Whiteboard | `board` | package | free shapes, sticky notes and lines |
| Object | `obj` | package | instances (`name : Type`, underlined, with slots) and the blocks they instantiate; links ([details](instances.md)) |
| Fault tree | `fta` | package | top, intermediate, basic, undeveloped and house events; AND, OR and k-of-n vote gates; fault tree links (see [Safety and reliability](safety.md)) |

Every diagram can also hold comments and the general shapes from [Drawing basics](drawing.md).

## Block definition diagrams

Blocks show their compartments: parts, references, values, operations, ports, and, when traceability exists,
*satisfies*, *verifies* and *allocatedFrom* callouts. Edit a block's features in the inspector (**Structure**
section) or in the model explorer. A property typed by another block is a part; a property typed by a value type or
a primitive (`Real`, `Integer`, `Boolean`, `String`) is a value property, optionally with a default value and a unit.
Ports have a direction (`in`, `out`, `inout`) and a kind (proxy or full).

## Internal block diagrams

An IBD shows the inside of one block. A new IBD starts with the block's own properties and ports. Right-click a part
for **Show ports on border** (the ports of its type) and **Show nested parts** (the parts of its type, drawn inside it).
Add parts and ports to a block with **Add feature** in the canvas menu of the block (or in the model explorer) and set
their **Type** in the inspector. Draw a **connector** between two ports or parts; when the
same port type appears on two parts, Meridian records which part each end belongs to. Set **Item flow** on a connector
(inspector) to show what it conveys, with a direction.

## Parametric diagrams

A constraint block holds one or more equations in its body, for example `power = force * speed / 1000` (separate
several with `;` or new lines). Its properties are the parameters. In a block, a property typed by the constraint
block is a *constraint property*; drag it onto the parametric diagram and its parameters appear on its border. Connect
value properties to parameters with **binding connectors**. (**Show ports on border** on a constraint property shows
its parameters.) The [Parametrics panel](simulation.md#parametrics) solves
the equations.

## Requirement diagrams

Requirements show their id and text and their trace callouts. Draw *deriveReqt* from a derived requirement to its
source, *satisfy* from a design element to a requirement, *verify* from a test case to a requirement, and *refine*,
*trace* and *copy* as needed. Nested requirements (containment) appear in the model explorer as a tree. See
[Requirements and traceability](requirements.md).

## Use case diagrams

Associate actors with use cases; use *include*, *extend* and *generalization* between use cases. Use cases and actors
can own behaviors (activities, state machines, interactions) that describe them.

## Activity diagrams

- **Actions** can carry a body (statements used by [simulation](simulation.md), for example `rpm = 800`), a
  **Duration** (`5`, `2.5s`, `200ms`, `1 min`) for timed simulation, and *send signal* / *accept event* names.
- Set **Behavior** on an action to make it a *call behavior action*; it shows `name : Activity`, and the canvas menu
  offers to open the called diagram.
- **Decision/merge** nodes split and join flows; put guards such as `started` or `else` on the outgoing control flows.
- **Fork/join** bars run flows in parallel and synchronise them.
- **Pins** (*Add input pin* / *Add output pin*) and **object nodes** with **object flows** carry items. An object node
  can be a **Data Store** («datastore», keeps what it is given) or a **Central Buffer** («centralBuffer»): pick them
  from the shapes panel or change the *Type* in the inspector.
- **Swimlanes** represent blocks or actors (set **Represents** in the inspector). **Allocate actions to swimlanes**
  creates an *allocate* relationship from every action to the block of the lane it sits in, and removes allocations
  that no longer match after you move actions between lanes.

## State machine diagrams

States can have `entry`, `do` and `exit` behaviors. Transitions have a name that acts as the trigger, an optional
guard and an optional effect:

| Trigger (transition name) | Meaning |
| --- | --- |
| *(empty)* | completion transition, taken when the state's work is done |
| `ignition` | a signal or event called *ignition* |
| `after(30 min)` | a time event, relative to entering the source state |
| `when(soc < 20)` | a change event, taken when the expression becomes true |

Put states inside a state to make a **composite state**; it needs its own initial state. Validation warns about
states that nothing transitions into and about state machines without exactly one initial state.

**Pseudostates** come from the shapes panel with their UML notation: **choice** ◇ (guards on the outgoing transitions
are evaluated after the trigger), **junction** ●, **fork** / **join** bars, **shallow history** Ⓗ and **deep history**
Ⓗ*, **entry point** ○ and **exit point** ⊗ (dropped on a state, they belong to it and sit on its border) and
**terminate** ✕. The inspector's *Type* changes one kind into another.

## Sequence diagrams

Lifelines can be typed by a block or actor. Messages have a sort (synchronous call, asynchronous call, reply) and an
optional signature; a message from a lifeline to itself is a self call. Activation bars are drawn automatically.
Combined fragments (`alt`, `opt`, `loop`, `par`, `break`, `critical`, `neg`, `seq`, `strict`) have a guard.
A fragment is split into **operands** by dashed lines, each with its own guard (`[card ok]`, `[else]`): right-click the
fragment and pick **Add operand**, or use **+ Operand** under *Operands* in the inspector, where each guard is edited
(and an operand removed). Select the fragment and drag a dashed line up or down to change where an operand starts; the
messages between two lines belong to that operand. Each change is one undo step.

## BPMN, ArchiMate and ER diagrams

- **BPMN**: pools, lanes, start/intermediate/timer/end events, tasks (user, service, sub-process), exclusive and
  parallel gateways, data objects and stores. Export a diagram as BPMN 2.0 XML and import `.bpmn` files.
- **ArchiMate**: business, application and technology layer elements with ArchiMate relationship types (composition,
  aggregation, assignment, realization, serving, access, influence, triggering, flow, specialization, association).
  Elements are coloured by layer. Exchange with the ArchiMate model exchange format.
- **Database (ER)**: crow's-foot diagrams of tables and views with key markers, column-to-column foreign keys and
  subject-area frames; show all columns, keys only or none. Import DDL, `.dacpac` or a live database, generate DDL
  and migration scripts for PostgreSQL, SQL Server, MySQL, SQLite or Oracle. See [Database modelling](database.md).

## Create diagrams from diagrams

Meridian can derive the next view of the MBSE chain from the one you have, and the derived view keeps its trace links:

**Requirements → Use case → Activity → Sequence → BDD / IBD (→ State machine)**

Right-click a requirements package, use case, activity, interaction or block (in the model explorer or on a diagram) and
pick an entry under **Create from…**. The same entries are in **Ribbon ▸ Diagram ▸ Create from…** and in ⌘K ("Create
activity from use case", "Create sequence from activity", …).

| From | Creates | Link kept |
| --- | --- | --- |
| Requirements | use cases named after each functional requirement's "shall …" phrase, with actors | «refine» |
| Use case | an activity with swimlanes; the steps come from the use case's description (numbered lines, "If …", "Alternate: …") | the activity is the use case's behaviour |
| Activity | a sequence diagram: one lifeline per swimlane, one message per hand-over between swimlanes, alt fragments with an operand per decision branch, par fragments with an operand per fork branch, and loop fragments | «allocate» action → block |
| Sequence | a BDD and an IBD: a block per lifeline, a context block with parts, an operation per message, and ports with connectors | «trace» message → operation |
| Sequence or activity | a state machine for a chosen block, built from the events it receives and its replies, with guarded transitions (through a choice when branches start with the same event) for alternatives (approximate) | classifier behaviour |
| Blocks / requirements | «satisfy» and test case «verify» proposals, each reviewed one by one | «satisfy», «verify» |

A dialog lists what will be created, and **Create** applies it as one undo step. Derived diagrams can later be updated
from their source without losing your own edits. See [Creating diagrams from diagrams](derivation.md).

## Whiteboards

A whiteboard is free drawing: general shapes, sticky notes and lines. The *Flowchart* and *Mind map* diagram templates
are good starting points.

## Profiles and stereotypes

A **profile** holds **stereotypes**. Each stereotype says which metaclasses it applies to (for example `Block, Actor`),
can have a header colour, and owns **tag definitions** (String, Integer, Real, Boolean or an enumeration with listed
values). In the inspector, **Stereotypes** lists the ones that fit the selected element; once applied, its tags appear
under **Tagged values**. Tagged values can be shown as table columns and used in queries (`tag:ASIL`).

**Add UAF profile** (project menu) adds a practical subset of the Unified Architecture Framework (capabilities,
operational and resource performers, services, security controls and more).

## Legends

Select a diagram and switch on **Show legend** in the inspector to add an automatic legend of the element kinds,
stereotype colours and highlights used on it. Colour rules that colour at least one shape are listed first, with
their label.

## Colour rules

Colour rules colour shapes by their data, like conditional formatting in a spreadsheet: failed requirements in red,
ASIL D blocks in orange, anything heavier than 50 kg with a badge.

1. Select the diagram in the model explorer, or open it and use ⌘K *Colour rules for this diagram* (or *Add colour
   rule to this diagram*).
2. Under **Colour rules** in the inspector, press **+**.
3. Pick the condition: a kind (*Shapes of kind*), then *Where* a value, an operator and a value. For example
   `tag:ASIL` `=` `D`, or `verificationResult` `contains` `fail`.
4. Pick a **Fill**, **Line** and **Text** colour (each is optional), an optional **Badge** (a short mark such as `!`
   drawn on the shape's corner) and a **Label** for the legend.

The condition is a [model query](tables-queries.md#queries), so the **Condition (query)** field accepts the full text
form: `kind:Block stereotype:critical where mass > 50 and not has:satisfy`. Numbers compare as numbers, also with
units (`mass > "1 t"` matches `1200 kg`). Useful keys:

| Key | Matches |
| --- | --- |
| `kind:Requirement` | one kind of element |
| `stereotype:S`, `stereotype contains S` | elements with a stereotype |
| `tag:ASIL`, `field:status`, `mass` | tagged values, raw fields, value properties |
| `verificationResult` | a requirement's last verification result (`pass`, `fail`, `inconclusive`, with `(stale)`) |

How rules apply:

- Rules are checked from the top; the **first rule that matches** a shape colours it. Use the arrows to reorder.
- A rule's colours **replace the shape's own colours** (from the format bar or the inspector), but only the ones the
  rule sets: a rule with just a fill keeps a manual line colour. Shapes no rule matches keep their own colours.
- Rules with a kind of relationship (`kind:Satisfy`) colour the matching paths with their line colour.
- Each rule shows how many shapes it colours; a condition that doesn't parse shows *invalid* and colours nothing.
- Rules belong to the diagram: they are saved with the project and in XMI, shared with collaborators, and every
  change is one undo step. SVG and PNG export, printing and reports show the same colours as the canvas.
