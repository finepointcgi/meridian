# Performance at Cameo scale

Meridian is tested with projects of 200,000 and 500,000 elements and with 25 concurrent editors on one project. This page
records the numbers, how to reproduce them, and the known limits.

## Hardware and method

- Apple M4 Pro (12 cores), 48 GB RAM, macOS; .NET 10.0.4, Release build, workstation GC.
- PostgreSQL 17 (alpine) in a throwaway Testcontainers container under Docker Desktop (15.6 GB VM); SQLite on the local SSD.
- Timings are single runs after a warm-up run (Stopwatch), so expect ±20 % between runs; the large-heap scenarios
  (merge, reports) vary most because of GC.

## The test projects

`LargeModelGenerator` (`src/Meridian.Core/Samples/LargeModelGenerator.cs`) builds deterministic synthetic projects.
`LargeModelOptions.Cameo(n)` gives about `n` elements: nested requirement areas with derive / trace / satisfy / verify links
(some suspect), subsystems of blocks with value properties, parts, ports and operations, interface blocks, test cases,
activities, state machines, use cases with actors and numbered step descriptions, a 9-level "Product Breakdown" package
tree, a profile with stereotypes and tagged values, ~1,000 ordinary diagrams (20–30 shapes) and 24 big ones of 50–500
shapes (the biggest: 500 blocks and 629 paths). The default options (`new LargeModelOptions()`) still make the same
60,000-element model as before.

| | 200k | 500k |
|---|---|---|
| Elements | 199,775 | 500,962 |
| Relationships | 76,899 | 194,925 |
| Diagrams | 1,004 | 2,507 |
| Requirements | 16,197 | 41,052 |
| `.meridian` JSON | 79.7 MB | 194.1 MB |

## Running the benchmarks

```sh
# Stopwatch pass over every scenario (core + stores + session + reports + diagrams):
dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --quick --elements 200000 --repeat 1
dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --quick --elements 500000 --repeat 1
#   --postgres          also run the store scenarios against a throwaway Testcontainers PostgreSQL (needs Docker)
#   --only <text>       only scenarios whose name contains <text>
#   --scale 1.0         (without --elements) the original 60k mix, scaled
# BenchmarkDotNet suite of the core scenarios:
dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --filter '*'

# Opt-in heavy tests:
MERIDIAN_HEAVY=1 dotnet test tests/Meridian.Tests --filter Heavy_200k_Budgets
MERIDIAN_HEAVY=1 dotnet test tests/Meridian.Web.Tests -c Release --filter CameoUiTimings      # MERIDIAN_HEAVY_ELEMENTS=500000
# Concurrency load test (runs in the normal suite for 30 s; longer / bigger with):
MERIDIAN_LOAD_SECONDS=300 MERIDIAN_LOAD_CLIENTS=50 MERIDIAN_LOAD_ELEMENTS=200000 dotnet test tests/Meridian.Web.Tests --filter ConcurrencyLoadTests
```

The normal suite has a fast perf budget (`CameoScaleTests.Perf_Budget_Medium_Model`, a 30,000-element model with limits
far above what a laptop needs) plus equivalence tests for the optimised paths. It never touches a local PostgreSQL: the
PostgreSQL runs always start their own container on a random port.

## Before / after (200,000 elements)

| Scenario | Before | After | Change |
|---|---:|---:|---|
| Render the 500-shape diagram (SVG export, all shapes) | 4,444 ms | 178 ms | pruned route scoring + obstacle index, memoised bounds, routes computed once |
| Render the 500-shape diagram (canvas, viewport-culled) | 1,214 ms | 102 ms | same |
| Canvas first render in the workbench (bUnit, Release) | 3,164 ms | 1,148 ms | minimap and edge hit-testing route with memoised bounds |
| Canvas re-render after moving a shape (bUnit, Release) | 1,983 ms | 420 ms | same |
| HTML report (whole model, every diagram) | 13,200 ms | 1,630 ms | diagram rendering above |
| ⌘K: derivation offers for 200 use cases / activities | 583 ms | 0.1 ms | per-model index of derived elements by source |
| Delete one subsystem + undo | 109 ms | 10 ms | diagram views to remove found in linear time (was shapes × paths per diagram) |
| Three-way merge | 1,791–2,304 ms | 1,569 ms | pre-sized sets, iterative subtree walk |
| Replica memory (one deserialized model, per open tab) | 227 MB | 132 MB | short field values and keys shared while loading |
| SQLite: database growth per committed version | +63.8 MB | +6.8 MB | versions stored as Brotli blobs |
| SQLite: commit version | 187 ms | 136 ms | less to write |
| SQLite: load a version | 24 ms | 110 ms | decompression (trade-off) |
| Session: snapshot for a joining tab / lock resync / autosave | 137 ms each | once per model change | cached by sequence, held weakly |
| Session: 25 tabs, managed heap | 5,353 MB | 4,701 MB | replica sharing above |

Unchanged (already fine): load 450–550 ms, save 80–180 ms, apply 2,600 remote ops 4–8 ms, 500 edits + undo + redo 5–7 ms,
validation 130 ms, traceability table 90–160 ms, ModelDiff 140–160 ms, model search 8 ms, ⌘K element list 3 ms,
derive an activity from a use case 17–22 ms, code-sync apply (300 types) 76–85 ms, AutoDiagrammer plan 530–620 ms.

### Document generation (streamed output)

Reports and template documents are written straight to a `Stream` (`ReportEngine.Generate(…, Stream)`,
`HtmlReport.Generate(model, Stream)`): HTML through a 64 KB `StreamWriter`, the Word body element by element with an
`OpenXmlWriter` into a temp file (only styles, numbering, header / footer and images are built as SDK parts), PDF via
QuestPDF's `GeneratePdf(Stream)`. In the web app the dialog writes into a temp file; files over 4 MB
(`Meridian:Downloads:InlineLimitBytes`) are handed to the browser as a one-time `/_dl/<token>` URL (signed-in users,
deleted after one fetch or 10 minutes) instead of one base64 interop string, and the desktop app receives them as 4 MB
base64 chunks (`saveBegin` / `saveChunk` / `saveEnd`) spooled to a temp file.

A traceability matrix above 250,000 cells (`ResolveOptions.MaxMatrixCells`) lists only the rows and columns that have a
relationship, and each 20-column part only the rows it marks; a note in the document says so. The SDD's requirement × block
matrix at 200k is 16k rows × thousands of columns; in full it alone is ~2 GB of HTML and cannot be laid out as Word or PDF.

Single runs, `--quick --elements 200000 --only report`; peak = managed heap above the starting heap, sampled every 2 ms.

| Scenario (200k) | Before | After |
|---|---|---|
| HTML report (`HtmlReport`) | 2,127 ms, peak +441 MB, 1,943 MB allocated | 2,164 ms, peak +118 MB, 1,673 MB allocated (80 MB file) |
| SRS template (HTML) | 507 ms, peak +317 MB, 865 MB allocated | 499 ms, peak +120 MB, 660 MB allocated (41 MB) |
| SDD template (HTML) | `OverflowException` after 12.2 s, peak +6,330 MB | 4.4 s, peak +187 MB, 1,970 MB allocated (80 MB) |
| SDD template (Word) | not finished after 15 min (7–9 GB RSS), stopped | 79 s, peak +1,387 MB (278 MB file) |
| SDD template (PDF) | not run (same 10⁸-cell matrix as Word) | 46 s, peak +1,068 MB (124 MB file) |
| SDD template (HTML, full matrix, `MaxMatrixCells = int.MaxValue`) | `OverflowException` (above) | 11.2 s, peak +2,172 MB (2,044 MB file) |

The full-matrix row shows streaming alone: the 2 GB document is written, with memory bounded by the resolved
document rather than the output. Word and PDF are slower (they
lay out ~1,000 diagrams, and Word rasterizes a PNG fallback for each); where that time goes has not been profiled yet.

### Other numbers after the changes

| Scenario | 200k | 500k |
|---|---:|---:|
| Load `.meridian` (deserialize) | 551 ms | 1,196 ms |
| Save `.meridian` (serialize) | 176 ms | 206 ms |
| Validate (full suite) | 133 ms | 322 ms |
| ModelDiff | 161 ms | 428 ms |
| Three-way merge | 1,569 ms | 5,707 ms |
| XMI export / import | 771 / 988 ms | 2,935 / 2,323 ms |
| SysML v2 export / parse | 598 / 647 ms | 3,043 / 1,414 ms |
| HTML report | 1,630 ms | 2,048 ms |
| SRS template report (HTML) | 979 ms | 1,710 ms |
| AutoDiagrammer plan (whole model) | 619 ms | 1,380 ms |
| Render 500-shape diagram (canvas) | 102 ms | 88 ms |
| SQLite: load + deserialize (cold) | 665 ms | 1,225 ms |
| SQLite: autosave | 77 ms | 107 ms |
| SQLite: commit version | 136 ms (+6.8 MB) | 310 ms (+16.5 MB) |
| PostgreSQL: load + deserialize (cold) | 693 ms | – |
| PostgreSQL: autosave (`jsonb`) | 930 ms | – |
| PostgreSQL: commit version | 953 ms (+17.1 MB, TOAST) | – |
| Session: first tab (load + replica) | 1,610 ms | 2,954 ms |
| Session: each further tab | 541 ms | 1,362 ms |
| Session: op apply + relay to 24 tabs, p95 | 0.06 ms | 0.34 ms |
| Model tree first render / reveal deepest element | 55 / 14 ms | – |

## Concurrency load test

`tests/Meridian.Web.Tests/ConcurrencyLoadTests.cs`: 25 in-process tabs (real `Workspace` replicas, each draining its inbox on
its own logical thread like a Blazor circuit) against one `ProjectSession` for 30 s: field edits, creates, moves, deletes,
shape moves, undo / redo, element locks (including three "rogue" tabs whose edits inside someone's subtree lock are
rejected and resynchronised), comments and edits of a shared SysML v2 text. It asserts:

- an observer saw every accepted operation exactly once (count = session sequence, no duplicates), and every operation of
  a tab that was never rejected was accepted;
- every replica serializes identically to the session's model, every shared-text editor has the server's text, and the
  persisted project equals the session model;
- managed memory at the end is below 1.5 × the start + 256 MB;
- **p95 of a local edit (including the server's apply and relay) < 50 ms** and **p95 inbox wait before a tab applies a
  relayed operation < 250 ms**.

Results on the machine above (Debug test build, ~18,600-element project):

| Store | Editing | Accepted ops | Submit p50 / p95 / max | Delivery p50 / p95 / max | Heap start → peak → end |
|---|---|---|---|---|---|
| SQLite | 15 s | 21,828 (1,444/s) | 0.01 / 12.3 / 48 ms | 5.8 / 87 / 2,135 ms | 1,183 → 3,192 → 1,227 MB |
| PostgreSQL | 10 s | 11,782 (1,177/s) | 0.02 / 20.6 / 247 ms | 5.7 / 60 / 3,579 ms | 588 → 2,620 → 1,131 MB |

Delivery maxima are the rogue tabs' full resynchronisations (a whole-model snapshot is deserialized in the tab).

## Known limits

- **Concurrent writes to the same field without a lock can diverge.** Model operations are ordered by the server but not
  transformed, and a tab applies its own edit before relayed earlier ones; two tabs writing the same field at the same
  moment can end up with different values until the next resync or reload. Element locks prevent it (the load test edits
  shared elements only under a lock), and a tab should process its inbox after taking a lock before editing.
- **Whole-model Word / PDF documents take tens of seconds** (SDD at 200k: 79 s Word, 46 s PDF) and ~1–1.4 GB of heap,
  not yet profiled; the resolved document is held in memory before it is written.
- **Merge** is linear but allocation-heavy (three flat snapshots of the model): ~1.6 s at 200k, ~5.7 s at 500k.
- **Every open tab holds a full replica** (~130 MB at 200k, ~400 MB at 500k). 25 tabs on a 500k project need ~10 GB of
  server memory; size the server accordingly.
- **Joining a session** deserializes the model in the new tab (0.5 s at 200k, 1.4 s at 500k).
- **PostgreSQL autosave** writes the whole model as `jsonb` (~0.9 s at 200k). It runs outside the session lock and is
  batched (at most once per edit burst), so editors don't wait for it.
- **Version storage**: SQLite versions written by this release are compressed; older releases can't read them (opening an
  upgraded database with an older Meridian is not supported). PostgreSQL versions stay `jsonb`.
- **Shared SysML v2 text of the whole model** is re-exported on model changes under the session lock (~0.6 s at 200k); the
  refresh now backs off to four times its own cost, so it doesn't run more than ~20 % of the time.
- The canvas re-routes all paths of a big diagram on every render (~100 ms for 629 paths); the bUnit numbers above include
  bUnit's own HTML parsing and overstate what the browser sees.
