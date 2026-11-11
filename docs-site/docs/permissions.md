# Package permissions

Project roles (owner, editor, viewer in **Share & members**) say what someone may do in the whole project. Package
permissions refine that per package, like Teamwork Cloud permissions: a contractor edits only their subsystem, a
supplier's package is read-only, and an export-controlled package is hidden from people who must not see it.
Package permissions need a team server; the desktop app's private workspace has one user.

## Levels

| Level | The package's contents |
| --- | --- |
| **none** | Hidden. The package name stays in the tree with a red lock and no contents. |
| **read** | Visible, not changeable. |
| **edit** | Changeable. |
| **manage** | Changeable, and the person may change the package's permissions. |

- Rules are inherited down the model explorer; the **nearest rule wins**, so a rule on `Structure::Power` beats one
  on `Structure`.
- A rule for a person beats rules for their teams; among team rules the most permissive wins.
- Without a rule, the project role decides: editors edit, viewers read. A rule never lifts a viewer above read.
- **Project owners always manage everything.**

## Setting permissions

- Right-click a package in the model explorer ▸ **Permissions…**: its own rules, the ones it inherits, and a form to
  grant a person or a team a level.
- **Share & members ▸ Package permissions** lists every rule of the project.

Changes apply at once for everyone connected.

## What people see

- A lock badge in the tree: red when hidden from you, amber when read-only for you, grey when it has rules you manage.
- A read-only diagram says *Read-only — “Package” is protected*, has no shapes panel and refuses edits.
- Shapes of hidden elements on a visible diagram become nameless, hatched **Restricted** placeholders.
- An edit you may not make is not applied, and the status bar says why.

Hidden content never leaves the server: tree, diagrams, search, tables, reports, exports, the REST API, OSLC and the AI
assistant all work on the filtered model. People with hidden packages cannot merge branches or review code changes,
because those show the whole model.

Subject areas of a [database model](database.md) can carry permissions the same way.
