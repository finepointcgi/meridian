# SysML v2 text

Meridian's models are SysML 1.x / UML underneath, and they can also be read and edited as **SysML v2 textual
notation**. The text editor is live-synchronised with the model, so you can type structure quickly and keep drawing
diagrams of the same elements.

## Opening the editor

- **Generate → Edit as SysML** (ribbon), or *Edit as SysML v2 text* in the command palette, opens the *SysML v2 text*
  dock tab for the selected package.
- The scope picker switches between packages and the **Whole model**.
- **Generate → SysML v2** exports the whole model as a `.sysml` file.

> [!NOTE]
> **TODO (screenshot):** the SysML v2 text panel next to a block definition diagram of the same package.

## Editing

```sysml
package Structure {
    attribute def Power;
    port def FuelInterface;
    part def Vehicle {
        attribute mass : ScalarValues::Real = 1450;
        part engine : Engine;
        port fuelFiller : FuelInterface;
        satisfy Acceleration by Vehicle;
    }
    part def Engine {
        attribute maxPower : Power = 160;
        in port fuelIn : FuelInterface;
    }
}
package Requirements {
    requirement def <'REQ-002'> Acceleration {
        doc /* The vehicle shall accelerate from 0 to 100 km/h in less than 8 s. */
    }
}
```

- The text is parsed as you type; syntax errors are underlined with their message.
- **Apply** (or **⌘Enter** / **Ctrl+Enter**) applies the changes to the model as **one undo step**: Meridian compares
  the text with the model and only creates, renames, retypes, moves, deletes, connects or disconnects what changed.
  Untouched elements, their diagrams and their ids stay as they are.
- **Reload** discards your edits and reloads the text from the model.
- If someone (or you, on a diagram) changed the model while you were editing, Meridian warns before applying: applying
  overwrites those changes within the editor's scope. Choose **Keep editing** to review first.
- Elements are matched by a `/* @id */` comment when one is present, or else by name within their owner; a single
  renamed element of the same kind is recognised as a rename rather than a delete and create.
- **Tab** / **Shift+Tab** indent and outdent, **Enter** keeps the indentation (and indents after `{`), and typing `}`
  outdents.
- **⌘-click** / **Ctrl-click** a name to select the element it declares or references.

Viewers can read the text but not apply edits.

## What maps to what

| SysML v2 | Meridian (SysML 1.x) |
| --- | --- |
| `package` | package |
| `part def` / `part` | block / part property |
| `attribute def` / `attribute` | value type / value property (primitive types as `ScalarValues::Real`, `Integer`, `Boolean`, `String`) |
| `port def` / `port` (`in`, `out`, `inout`) | interface block / port with direction |
| `constraint def` with an equation, `bind a = usage.param` | constraint block, binding connector |
| `requirement def <'REQ-001'> Name { doc /* text */ }` | requirement with id and text (nested requirements stay nested) |
| `satisfy R by X`, `#derivation dependency from A to B` | satisfy, deriveReqt |
| `verification def` with `objective { verify R; }` | test case verifying a requirement |
| `use case def` with `include use case` | use case and include |
| `action def` with `action`, `decide`, `first … then …` | activity, actions, decisions and control flows (guards as `if`) |
| `state def` with `state` and `transition` | state machine, states and transitions |
| `occurrence def` with `message … from … to …` | interaction with lifelines and messages |
| `connect a to b` | connector |

Model content without a SysML v2 text form (for example diagram layout, BPMN or ArchiMate elements) is left alone
by **Apply**.

## Files and the API

- **Open a file as a new project** accepts `.sysml` files, and the desktop app opens `.sysml` files directly.
- A team server exposes a read-only subset of the OMG **Systems Modeling API** (SysML v2 REST) under `/api`: projects,
  commits, and the elements and root elements of a commit.
