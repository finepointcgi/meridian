# Tables and queries

Generic tables, model queries and query-based validation rules are Meridian's equivalent of Cameo's generic tables
and EA's model search and model views. All three are saved **in the model** (as *Table View*, *Query* and
*Validation Rule* elements, by default in a top-level `Views` package), so they are versioned, merged, shared with
collaborators and exported in `.meridian` files.

Open them from the ribbon's **Model views** group (**Table** and **Query**) or the *Tables* dock tab. The panel lists
the saved views of the model on the left.

> [!NOTE]
> **TODO (screenshot):** the Tables panel showing the *Hazard analysis* table of the Infusion pump sample.

## Generic tables

A table's **rows** are the elements a query matches; its **columns** are value keys (see below).

1. Choose the element kind shown as rows (for example *Block* or *Requirement*) and optionally a package scope.
2. Add columns with **+**. The column picker suggests general keys, the fields of the row kind, tagged values,
   stereotype properties, value properties and relationship columns. *Custom key…* accepts any key.
3. Reorder (*Move left*) or remove columns, click a header to sort (ascending, descending, unsorted), and type a per-column filter:
   plain text matches a substring, `>10`, `<=5`, `=x`, `!=x` compare, and `!x` excludes.
4. Edit cells in place where the key is editable (names, fields, tagged values, value properties). Each edit is
   undoable and replicated to collaborators.
5. **Add row** creates a new element of the row kind (*Create in* chooses the package).
6. Save the table under a name, or *Save a copy under a new name*. **Export** writes the visible rows as CSV.

## Queries

The query builder combines an element-kind filter, an optional stereotype, a package scope and conditions:

- **Value condition**: `field op value`, where `op` is `=`, `!=`, `<`, `>`, `<=`, `>=`, `contains`, `is empty` or
  `is not empty`. Numbers compare numerically, everything else case-insensitively.
- **Relationship condition**: the element has (or, negated, has no) relationship of a kind, optionally only outgoing
  or incoming.
- Conditions combine with *and* / *or*; **Negate** turns a condition into its opposite.

Every query also has a compact **text form**, shown and editable in the panel:

```text
kind:Block,InterfaceBlock stereotype:critical in:Vehicle::Structure where mass > 50 and not has:satisfy
kind:Requirement where not has:in:verify
kind:Property where type = Real and defaultValue is empty
kind:Block stereotype:Hazard where not has:in:trace
```

| Part | Meaning |
| --- | --- |
| `kind:A,B` | element kinds (omit for all ordinary elements) |
| `stereotype:S` | only elements carrying stereotype *S* |
| `in:Pkg::Sub` | only elements inside this package or element (qualified name, unique name or id) |
| `where …` | the condition |
| `has:satisfy`, `has:out:allocate`, `has:in:verify` | relationship existence (any direction, outgoing, incoming) |
| `no:satisfy` | shorthand for `not has:satisfy` |
| `not`, `and`, `or`, `( … )` | logic |

Results are ordered by qualified name. **Open as table** shows the results as an editable table.

## Value keys

Columns, conditions and rule messages share one vocabulary:

| Key | Value |
| --- | --- |
| `name`, `kind`, `owner`, `qualifiedName`, `stereotype`, `type`, `documentation`, `id` | general properties |
| `field:key` | a raw field, for example `field:reqId`, `field:status`, `field:verdict` |
| `tag:name` | a tagged value or stereotype property, for example `tag:ASIL` |
| `prop:name` | the default value of an owned value property, for example a block's `prop:mass` |
| `rel:out:Satisfy`, `rel:in:Verify`, `rel:any:Trace` | names of related elements |
| `satisfies`, `satisfiedBy`, `verifies`, `verifiedBy`, `allocatedTo`, `allocatedFrom`, `refines`, `refinedBy`, `derivedFrom`, `derived`, `tracesTo`, `tracedFrom`, `dependsOn`, `dependents`, `supertypes`, `subtypes` | friendly aliases for the relationship keys |
| a bare word | a special key, then a raw field, a tagged value and finally an owned value property (so `mass` finds a block's `mass` value) |

## Validation rules

A **validation rule** is a saved query with a severity (error, warning or info) and a message. Every element the
query matches is reported by **Validate** and counted in the model health indicator. Messages can use placeholders
from the value keys, for example:

```text
query:    kind:Block where maxPower < 150
severity: warning
message:  {name}: max power {maxPower} kW is below the 150 kW target of REQ-005
```

In the query panel, **Save as rule** saves the current query as a rule (and **Update rule** changes a saved one). A rule
with an invalid query is itself reported as an error (`USR-000`).

## Examples from the samples

| Sample | View | What it shows |
| --- | --- | --- |
| Vehicle | *Block overview*, *Requirement status* tables; *Unverified requirements* query; *Engine power target* rule | the basics |
| CubeSat mission | *Verification status* table; *Failed tests* query | `field:verdict` columns and conditions |
| Adaptive cruise control | *Safety requirements by ASIL* table; *Safety requirement verification* rule | stereotype tags (`tag:ASIL`) and sorting |
| Infusion pump | *Hazard analysis* table; *Hazard without risk control* rule | a hazard log built from stereotyped blocks and `tracedFrom` |
