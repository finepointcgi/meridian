# Instances and object diagrams

An **instance specification** (an *instance* for short) is a concrete object of a block: "vehicle1", with a mass of
1600 kg, a 140 kW engine and a 55 L tank. Instances let you describe particular configurations of a design — the test
vehicle, the customer's variant, the launch configuration of a satellite — and check them against the requirements,
without changing the blocks' default values. This is what Cameo calls instance specifications and the *instance table*,
and what Enterprise Architect calls objects with a run state.

## What an instance holds

| Part | What it is |
| --- | --- |
| **Classifiers** | the blocks, value types, classes, signals … the instance is an instance of (one or more) |
| **Slots** | one per feature that has a value in this instance: `mass = 1600 kg`, `colour = Blue`, `engine = engine`, `topSpeed = = 40 + 10` |
| **Nested instances** | instances for its parts, owned by it: *vehicle1* owns *engine*, *tank* and *brakes* |
| **Links** | instance-level associations between two instances, typed by the association (or composition) they are an instance of |

A slot's value is one of:

- a **literal** with an optional unit — `1600 kg`, `true`, `Blue paint`. Values of a property with a unit are stored in that
  unit: typing `1.6 t` for a mass in kg gives `1600 kg`; a unit that doesn't convert (`3 m` for a mass) is refused;
- an **enumeration literal**, by name — `Blue`;
- one or more **instances**, by name — `engine`, or `wheel1, wheel2` for a part with several values;
- an **expression**, typed with a leading `=` — `= 40 + 10`.

Instances live in packages, next to the blocks they describe. Slots and nested instances show in the model explorer
under their instance (`mass = 1600 kg`).

## Creating instances

**Create instance of…** builds a whole instance tree in one step (one undo):

- right-click a block in the model explorer or on a diagram ▸ **Create instance of Vehicle**;
- the block's inspector ▸ **Instances ▸ Create instance**;
- ⌘K ▸ *Create instance of Vehicle*.

The new instance gets a slot for every property that has a default value, and an instance for every part (as many as
the part's lower multiplicity: one for `engine : Engine`, four for `wheels : Wheel [4]`), built the same way, with the
part slots pointing at them. When exactly one association connects the whole's block and the part's block (the
composition you drew on the BDD), whole and part are connected by a **link** typed by it. Created from a shape on a block
definition or object diagram, the instance tree is also drawn beside the block.

The **Instance** tool on the shapes panel of object and block definition diagrams creates an empty instance; give it
classifiers in the inspector.

## The instance table

**Instance table** (block inspector, right-click a block, or ⌘K ▸ *Instance table: Vehicle*) opens a tab next to the
diagrams listing the instances of the block and its specializations: a row per instance, a column per property of the
block (with its unit in the header).

- **Edit** a cell to set a slot: units are converted, enumeration properties have a drop-down, parts take instance names.
  An empty cell removes the slot (the default value then applies, shown greyed). Values that don't fit are refused with
  the reason under the toolbar. Every edit is one undo step.
- **Add** creates a new instance tree, **Duplicate** copies the selected instance with its part instances (references to
  the copied parts point at the copies), the bin deletes an instance with its parts (references to them in other slots go
  too).
- **nested** also lists part instances (*vehicle1.engine*) — useful for the instance table of *Engine*.
- **CSV** and **XLSX** export the table.

## Object diagrams

An **object diagram** (**+ ▸ Object Diagram**, or **Object diagram** on an instance's inspector or the instance table)
shows instances as boxes with the underlined label `vehicle1 : Vehicle` and a compartment of slots, and links as lines
labelled with their association. Opened for an instance, it is created with the whole instance tree laid out — the whole
above, its parts in a row below. Instances can also go on block definition diagrams, as in Cameo.

Drawing a line from one instance to another makes a **link**. When exactly one association connects their classifiers,
the link is typed by it; when several do, Meridian asks which one (you can leave it untyped and choose later in the
inspector).

## Using instances in analyses

- **Verification**: set a verification run's **Subject** to an instance. Its slot values replace the default values of its
  block — and the slots of its part instances those of the part blocks — on the copy of the model that is analysed, so one
  set of criteria checks several configurations. Results depend on the instance: editing a slot makes them stale. See
  [Verifying requirements](verification.md).
- **Parametrics**: pick an instance next to the block in the Parametrics panel to solve with its values.
- **Reports**: the **Instances** block of a document template lists the instances of a block as a table (one row per
  instance) or as a slot table per instance. Inside *For each kind:Block* it uses the current block.

## Validation

| Rule | Severity | What it finds |
| --- | --- | --- |
| INST-001 | error | a slot whose feature no longer exists, or is no longer a feature of the instance's classifiers (after a classifier change) |
| INST-002 | error | a slot whose value doesn't fit its feature: a literal for a part, an instance of the wrong block, a literal of another enumeration, a non-number or a unit that doesn't convert for a value with a unit, too many values for the multiplicity |
| INST-003 | warning | a required feature (lower multiplicity ≥ 1) with no slot and no default value; values computed by parametrics (bound to a constraint parameter) are exempt |
| INST-004 | warning / error | an instance with no classifier, or one whose classifier was deleted |

Select a stale slot (INST-001) to **delete** it or **remap** it to another feature of the instance — a feature with the same
name comes first, and its value is re-read for the new feature.

## Exchange

| Format | Instances |
| --- | --- |
| Meridian project | everything |
| XMI | `uml:InstanceSpecification` with `classifier` and `uml:Slot`s whose values are `LiteralReal`, `LiteralInteger`, `LiteralBoolean`, `LiteralString`, `InstanceValue` (instances and enumeration literals) or `OpaqueExpression`. UML instances don't nest, so part instances are written next to their whole (marked for Meridian to re-nest them). Links are instance specifications classified by their association with one slot per end. |
| SysML v2 text | individual usages: `individual part vehicle1 : Vehicle { :>> mass = 1600 [kg]; :>> engine = engine; individual part engine : Engine { … } }`; links as `#link dependency from vehicle1 to engine;` (typed on import by the one matching association) |
| Cameo / MagicDraw (`.mdzip`, `.mdxml`) | instance specifications with their slots; instance specifications classified by an association become links |
| Enterprise Architect (`.qea`) | *Object* elements become instances of their classifier, their **run state** (`@VAR;Variable=mass;Value=1500;…`) becomes slots, and associations between objects become links |
