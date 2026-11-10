---
title: Meridian User Guide
---

# Meridian User Guide

Meridian is a model-based systems engineering (MBSE) workbench that is as easy to draw with as a whiteboard. You get
SysML block, internal block, requirement, activity, state machine, sequence, use case and parametric diagrams, all
linked to requirements and traceability. Meridian also covers BPMN, ArchiMate, entity-relationship diagrams and free
drawing, with live multi-user editing.

Meridian runs as a **desktop app** (Windows, macOS, Linux) that works offline or connects to a team server, and as a
**web app** on a team server backed by PostgreSQL.

## Where to start

| If you want to… | Read |
| --- | --- |
| Install Meridian and open your first model | [Getting started](docs/getting-started.md) |
| Learn the canvas: shapes, arrows, formatting, layers | [Drawing basics](docs/drawing.md) |
| Build a complete model step by step | [Model a drone in 20 minutes](tutorials/drone-in-20-minutes.md) |
| Trace requirements down to tests | [Requirements to verification](tutorials/requirements-to-verification.md) |
| Compare design alternatives | [Your first trade study](tutorials/first-trade-study.md) |
| Move from Cameo Systems Modeler or Enterprise Architect | [Coming from Cameo / EA](docs/migration.md) |
| Automate the model with C# | [Scripting](docs/scripting.md) and the [API reference](api/index.md) |

## What is in the box

- **Drawing**: a searchable shapes panel, hover arrows that add connected shapes, alignment tools, a format bar with a
  format painter, layers, pictures and links. See [Drawing basics](docs/drawing.md).
- **SysML and UML diagrams**: block definition, internal block, parametric, activity, state machine, sequence, use
  case, requirement, package and class diagrams, plus profiles and stereotypes. See [Diagram types](docs/diagrams.md).
- **Requirements**: a requirements table, traceability matrix and table, impact analysis, coverage and suspect links.
  See [Requirements and traceability](docs/requirements.md).
- **Model views**: generic tables, model queries and custom validation rules saved in the model. See
  [Tables and queries](docs/tables-queries.md).
- **Version control**: commits, branches with three-way merge, baselines and element locks. See
  [Branches, merge and baselines](docs/version-control.md) and [Reviews](docs/reviews.md).
- **Analysis**: parametric solving, stepping and timed simulation, Monte Carlo, FMI import, variants and trade studies.
  See [Simulation](docs/simulation.md) and [Variants and trade studies](docs/variants-trade-studies.md).
- **Exchange**: XMI 2.5.1, SysML v2 text, ReqIF, CSV and Excel, Enterprise Architect `.qea`, BPMN, ArchiMate and SQL.
  See [Import and export](docs/import-export.md).
- **Extensibility**: a C# script console and .NET plugins. See [Scripting](docs/scripting.md) and [Plugins](docs/plugins.md).

## Sample projects

**New project** offers a gallery of templates. Besides the starter templates it has four complete worked examples:
the *Vehicle sample*, a *CubeSat mission* (aerospace), *Adaptive cruise control* (automotive, ISO 26262-style) and an
*Infusion pump* (medical, ISO 14971 / IEC 62304-style). [Sample projects](docs/samples.md) describes each one.
