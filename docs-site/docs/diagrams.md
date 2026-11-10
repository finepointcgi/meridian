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
| Block definition | `bdd` | package | blocks, interface blocks, value types, constraint blocks, enumerations, actors; association, composition, aggregation, generalization, realization, dependency, allocate |
| Internal block | `ibd` | block | parts, ports, connectors, item flows, allocate |
| Parametric | `par` | block | value properties, constraint properties and their parameters, binding connectors |
| Requirement | `req` | package | requirements, test cases, blocks, use cases; derive, satisfy, verify, refine, trace, copy |
| Use case | `uc` | package | actors, use cases, subject blocks; association, include, extend, generalization |
| Activity | `act` | activity | actions, initial/final nodes, decisions/merges, forks/joins, swimlanes, object nodes, pins; control and object flows |
| State machine | `stm` | state machine | states (also composite), initial and final states, transitions |
| Sequence | `sd` | interaction | lifelines, messages, combined fragments |
| Package | `pkg` | package | packages, profiles, stereotypes; dependency, generalization |
| Class | `class` | package | UML classes, interfaces, data types, enumerations, components |
| BPMN | `bpmn` | package | pools, lanes, events, tasks, gateways, data objects; sequence, message and association flows |
| ArchiMate | `archi` | package | business, application and technology elements and ArchiMate relationships |
| Entity-relationship | `erd` | package | tables, columns, foreign keys |
| Feature model | `fm` | package | features, configurations; feature decomposition, requires, excludes |
| Whiteboard | `board` | package | free shapes, sticky notes and lines |

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
- **Pins** (*Add input pin* / *Add output pin*) and **object nodes** with **object flows** carry items.
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

## Sequence diagrams

Lifelines can be typed by a block or actor. Messages have a sort (synchronous call, asynchronous call, reply) and an
optional signature; a message from a lifeline to itself is a self call. Activation bars are drawn automatically.
Combined fragments (`alt`, `opt`, `loop`, `par`, `break`, `critical`, `neg`, `seq`, `strict`) have a guard.

## BPMN, ArchiMate and ER diagrams

- **BPMN**: pools, lanes, start/intermediate/timer/end events, tasks (user, service, sub-process), exclusive and
  parallel gateways, data objects and stores. Export a diagram as BPMN 2.0 XML and import `.bpmn` files.
- **ArchiMate**: business, application and technology layer elements with ArchiMate relationship types (composition,
  aggregation, assignment, realization, serving, access, influence, triggering, flow, specialization, association).
  Elements are coloured by layer. Exchange with the ArchiMate model exchange format.
- **ER**: tables with typed columns, primary keys, nullable and unique flags, and foreign keys. Generate SQL DDL for
  PostgreSQL, MySQL, SQL Server or SQLite, and import DDL.

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
stereotype colours and highlights used on it.
