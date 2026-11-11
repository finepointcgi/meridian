# Find and replace

**Find and replace** searches the whole model, or part of it, for text and replaces it in one step you can undo. It is
the model-wide counterpart of Cameo's *Find and Replace* and EA's *Find in project*.

Open it with **⇧⌘F** (Ctrl+Shift+F on Windows and Linux), the ⌘K command *Find and replace in the model…*, the
**Find & replace** button in the ribbon's **Model views** tab, or the **Find** tab of the bottom panel.

## Finding

Type in the **Find** box: results appear as you type, grouped by element, each with the field it was found in and a
snippet with the match highlighted. The status line shows the number of matches and elements.

| Option | What it does |
| --- | --- |
| **Aa** | match case |
| **ab** | whole word only (letters, digits and `_` form words) |
| **.\*** | the find text is a .NET regular expression |
| Scope | the whole model, the selected element's package and its contents, the active diagram, or the selection |
| Kind | only elements of one kind (for example *Requirement* or *Column*) |
| Search in | which kinds of text to look in (below) |

### What is searched

| Search in | Fields | Replace |
| --- | --- | --- |
| Names | names of elements, relationships and diagrams | yes |
| Documentation | the documentation of every element | yes |
| Requirement text/id | requirement text, id and rationale | yes |
| Values | default values, constraint expressions and bodies, guards, effects, entry / exit / do behaviours, durations, distributions | yes |
| Tagged values | tagged values of stereotypes and custom requirement attributes | yes |
| Stereotypes | applied stereotype names | find only |
| Diagram notes | notes (comments on diagrams) and the text of free shapes | yes |
| Comments | discussion comments on elements (server projects) | your own comments only |
| SysML v2 text | the model's SysML v2 text, line by line | find only |

The first six (without stereotypes) and diagram notes are on by default. SysML v2 text is generated from the model: a
replace changes the model, and the text follows. Clicking a SysML v2 result opens the text editor; the text and its
collaborative sync are left alone.

Ids, references (types, ends, called behaviours) and layout are never searched.

### Results

- Click a result to select the element in the model explorer and the inspector. Notes and free shapes, and every
  result when the scope is a diagram, also open a diagram that shows them.
- Results stop at 10,000 matches with *refine your search*; narrow the scope, the kind or the fields.
- The search runs in the background: results stream in, and typing again cancels the previous search. A plain text
  search of a 200,000-element model takes well under a second.
- Matches you can't change are shown greyed out with the reason: elements of a used project (library), packages that
  are read-only for you, a view-only project, and the find-only fields above. Elements in packages hidden from you are
  never searched.

## Replacing

Type the replacement in the **Replace** box. Each result shows what it will become. With a regular expression,
`$1`, `${name}` and `$0` insert groups of the match: find `(\d+) km/h`, replace with `$1 kph`.

Every match and every element has a checkbox (all checked at first).

- **Replace selected…** previews replacing the checked matches.
- **Replace all…** previews replacing every match that can be changed.

The preview lists each field with its text before and after. Rows that will be left alone are greyed with the
reason. **Apply as one undo step** makes the change; **⌘Z** takes the whole replace back.

A replace follows the same rules as editing by hand:

- **Names stay unique** where the model requires it: a rename that would give two elements of the same kind in one
  owner the same name (tables: ignoring case) is skipped, as is a name that would become empty and a requirement id
  that is already used.
- **Renames propagate** the way they do in the inspector: renaming a database column updates the foreign keys,
  indexes and constraints that list it, in the same undo step; renaming a requirement marks its trace links suspect.
- **Read-only** elements are never changed.

### Working together

The replace is sent to the server as one batch, like any other edit, so collaborators see it at once and the server
checks your permissions for every field. If someone changed a field after your preview, that field is skipped and
listed under **Not replaced**, so you never overwrite their edit with a stale value; search again to pick it up.

Comments live in the project database, not the model: replacing in your own comments saves them at once and is not
part of ⌘Z.

## Tips

- Regular expressions run with a time limit, so a pattern with catastrophic backtracking (such as `(a+)+$`) stops
  with a message instead of hanging.
- To rename one element everywhere it is *referenced*, rename the element itself: references are by id, so they
  follow automatically. Find and replace is for text that repeats across many elements.
