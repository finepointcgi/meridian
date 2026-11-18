# Org charts from a spreadsheet

Meridian can draw an org chart on a whiteboard from an Excel or CSV list of people.

## Where to find it

- Project menu → **Import ▸ Org chart from spreadsheet…**
- ⌘K → **Org chart from spreadsheet (Excel / CSV)…**
- For a small example to start from: **+** on the tab strip → **From template…** → **Org chart**.

## The sheet

One row per person. Meridian finds the columns by their headers (case and punctuation do not matter):

| Column | Headers it recognises |
| --- | --- |
| Name | *Name*, *Full name*, *Employee*, *Person* — or *First name* plus *Last name* / *Surname* |
| Title | *Title*, *Job title*, *Role*, *Position* |
| Manager | *Manager*, *Reports to*, *Supervisor*, *Parent*, *Manager id* |
| Id (optional) | *Id*, *Employee id*, *Staff id*, *Key* |
| Department (optional) | *Department*, *Dept*, *Team*, *Division*, *Group* |
| Email (optional) | *Email*, *E-mail* |

The **Manager** cell holds the manager's id or their name. People with no manager go at the top; a sheet can have
several people at the top. Photo columns are ignored. Excel (`.xlsx`) and CSV / TSV files work, and so do cells pasted
from Excel.

```
Id,Name,Title,Reports to,Department
1,Ada Lovelace,CEO,,Leadership
2,Grace Hopper,CTO,1,Engineering
3,Alan Turing,Engineer,Grace Hopper,Engineering
```

## The preview

Before anything is drawn the dialog shows the columns it found, how many people there are, how many are at the top,
and any warnings:

- a manager that is not in the sheet (that person goes to the top),
- someone listed as their own manager,
- people who report to each other in a loop (the loop is cut, and the first of them goes to the top),
- two people with the same name or id,
- rows with no name (skipped).

Warnings never stop the import. They are also written to the **Output** panel.

## The chart

Each person becomes a card with their name in bold, their title below and their department in small text. Each
department gets its own colour band. Cards are laid out top-down: each manager sits above their reports, and a
manager with many reports who have no reports of their own gets a compact two-column stack. Lines go from each manager
to their reports.

The chart is an ordinary whiteboard: move cards, recolour them, add notes. Importing is one undo step.

## Importing again

Import the same file again (same file name, into the same package) to **update** the chart instead of making a new
one. People are matched by id, or by name when there is no id column. Changed names, titles and departments are
updated, new people are added, people no longer in the sheet are removed, and the chart is laid out again (so cards you
moved go back into the tree).
