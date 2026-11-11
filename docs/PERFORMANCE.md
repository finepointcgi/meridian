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
MERIDIAN_HEAVY=1 dotnet test tests/Meridian.Tests -c Release --filter HeavyReportTests --logger "console;verbosity=detailed"   # whole-model reports, prints the numbers below
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
| SDD / model overview template report (HTML, streamed) | 3.7 / 1.7 s | – |
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
| Session: rebase of a pending edit under a relayed one, p95 | 0.004 ms | – |
| Model tree first render / reveal deepest element | 55 / 14 ms | – |

## Reports

Template reports (SRS, ICD, SDD, VCRM, data dictionary, model overview, custom templates) are **streamed**: a numbering
pass resolves the template once without keeping anything (it numbers headings, figures and tables, collects the
cross-reference targets and counts the output), then the writing pass resolves it again on a worker thread and hands each
paragraph, table and figure to the writer as it is made, through a bounded queue. Nothing is ever one big string or one
big DOM:

- **HTML** goes to a `TextWriter` over the output stream.
- **Word** is written with the OpenXML SDK's `OpenXmlWriter` straight into the `word/document.xml` zip entry; the package
  (styles, numbering, header / footer, media, relationships, content types) is assembled by hand around it, so the output
  stream needn't be seekable and no part is buffered. Each diagram's SVG and PNG fallback is written once, after the body.
- **PDF**: QuestPDF composes the document lazily from the node stream (orientation runs are split on the fly); diagrams
  are parsed once per diagram and drawn when their page is laid out. QuestPDF itself keeps the laid-out document in memory
  (it needs it for "page X of Y" and the table of contents), so PDF is the most memory-hungry format.
- Diagrams are rendered once per diagram and model version (`DiagramImageCache`, by diagram id and
  `ModelRepository.Revision`) and shared by both passes, every section that shows them and every document of a split
  report.
- Plain-text cross-references in headings and table cells are resolved in one pass (they used to look each heading up
  in the heading list: quadratic in the number of headings).

The server writes reports to a temporary file (`ReportJobService`) and the browser downloads them from
`GET /api/reports/{id}/file` (chunked, with range support); the desktop app streams the same URL to the chosen file. The
numbering pass is also the **size estimate**: above ~2,000 estimated pages the dialog warns and offers to split per
top-level package or to narrow the scope; above ~150 pages (or 20,000 elements visited) the report runs as a background
job with progress, cancel and a "Download" notification.

The SDD's requirement × block matrix (and the VCRM's requirement × test case matrix) now **hide empty rows and columns**:
in the 200k project the full matrix has 16,197 rows in each of ~300 tables (4.9 million rows, 100 million cells), almost
all empty.

200,000-element project (`MERIDIAN_HEAVY=1 HeavyReportTests`, Release; memory is the managed heap the report adds on top
of the loaded model, sampled every 50 ms):

| Report | Before | After | Output |
|---|---|---|---|
| SDD, HTML | `OverflowException` after 18.6 s, +6.2 GB | 3.7 s, +225 MB | 79.8 MB; 26,025 blocks, 128,240 table rows, 1,221 figures |
| SDD, Word | stopped after 5 min at 3.5 GB (still building the DOM) | 44.7 s, +418 MB | 183.1 MB |
| SDD, PDF | (not reached) | 47.1 s, +995 MB | 123.8 MB, 6,997 pages (estimated 8,097) |
| SDD with the unfiltered matrix, HTML | `OverflowException` | 14.2 s, +455 MB; the size check estimates ~303,000 pages and warns first | 2,044 MB; 4,948,081 table rows |
| Model overview, HTML | 4.9 s, +553 MB | 1.7 s, +47 MB | 65.0 MB; 77,805 blocks, 21,727 table rows, 1,004 figures |
| Model overview, Word | 98.3 s, +1,793 MB | 37.3 s, +305 MB | 177.4 MB |
| Model overview, PDF | 34.2 s, +550 MB | 28.2 s, +326 MB | 104.3 MB, 4,190 pages (estimated 5,963) |
| Quick HTML model report | 3.3 s, +438 MB, an 80-million-character string sent to the browser through JS interop | streamed to a file and downloaded | 80 MB |

"Before" is commit a847aca (one pass into memory, HTML in a `StringBuilder`, Word through the SDK's package DOM), run the
same way. The Word time is mostly the PNG fallbacks of ~1,000 diagrams (rasterised a few at a time, at 80 dpi when a
document has more than 200 diagrams, never more than 2,400 px on a side); the body takes ~5 s. The page estimate was
16 % (SDD) and 42 % (model overview) above QuestPDF's real page count.

The normal suite checks the streamed output against hashes of the pre-streaming output (HTML byte for byte, Word part
XML) for every built-in template, the streaming and in-memory pipelines against each other (HTML, Word and PDF text),
cancellation mid-stream, progress, the estimate, split per package, scope, hidden-empty matrices, linear numbering and
the diagram cache (`ReportStreamingTests`), the download endpoint's checks (`ReportDownloadTests` in the web suite), the
dialog's progress, background mode, warning and cancel (bUnit), the desktop save bridge, and a real browser download (E2E).

## Element history

Element history ("who changed this and when", docs-site version control) reads a per-element change index instead of stored
versions. When a version is committed, `VersionControl.CommitAsync` hands the committed JSON to `ElementHistoryIndexer`,
which compares it with its parent in the background: both versions are read with `FlatModel` (a forward-only
`Utf8JsonReader` pass that keeps ids, owners, relationship ends, names and a 64-bit hash per field, no object graph) and the
changed elements are written to `element_changes` in one transaction. The parsed parent of each line stays cached (105 MB at
200k, the two most recently committed lines, dropped after five minutes idle), so a commit costs one parse. Field values are
read from the two versions only when a diff is opened (`VersionReader`, redacted for the viewer).

`MERIDIAN_HISTORY_VERSIONS=100 dotnet run -c Release --project benchmarks/Meridian.Benchmarks -- --quick --elements 200000 --only history`
(`HistoryScenarios.cs`): 100 versions of the 200k project, each changing ~25 elements (a requirement renamed every fifth
version, its text every seventh, a «satisfy» added every tenth). PostgreSQL was run with 25 versions (`--postgres`; 100
versions of 17 MB each filled the Docker VM's disk). Other test suites were running on the machine at the same time.

| Scenario | SQLite (100 versions) | PostgreSQL (25 versions) |
|---|---:|---:|
| Commit a version (unchanged by this feature) | 116 ms | 721 ms |
| Added to the commit path (hand the JSON to the indexer) | < 0.01 ms (max 2 ms) | < 0.01 ms |
| Index one commit, in the background (parse 240, compare 40, write 3 ms) | 300 ms (p95 435) | 274 ms (p95 326) |
| **Open an element's history, indexed** (52 entries incl. relationships) | **0.4 ms** | **1.8 ms** |
| … for a viewer with a hidden package | 0.4 ms | 2.0 ms |
| Open a diff (read two versions) | 1.0 s | 0.83 s |
| … redacted for a viewer with a hidden package (deserialize + redact) | 1.7 s | 1.4 s |
| Uncommitted check (last commit vs the live element, first time; then cached) | 358 ms | 382 ms |
| Backfill (lazy, background, resumable), per version | 1.13 s | 0.81 s |
| Parse a version with `FlatModel` / deserialize it with `ProjectFile` | 257–296 / 471–605 ms | |

The backfill runs once per project (the first time someone opens a history), oldest version first, one transaction per
version; the panel shows its progress, and an interrupted backfill continues where it stopped. Its cost is reading each stored
version (SQLite decompression ~100 ms, PostgreSQL `jsonb` to text ~200 ms) and parsing it; the per-version average is about
twice a single commit's cost because the 80 MB strings churn a 2 GB benchmark heap (gen 2 collections). The first version of a
project stores no rows (an element without an "added" row is shown as present since the first version), so the index grows
with what commits change, not with the model: ~25 rows per version here.

On SQLite, `element_change_versions` keeps a copy of each version's author and date: in `project_versions` they come after
the multi-megabyte model column, and reading them made listing 100 versions take ~250 ms (SQLite walks the row's overflow
pages to reach later columns).

## Concurrency load test

`tests/Meridian.Web.Tests/ConcurrencyLoadTests.cs`: 25 in-process tabs (real `Workspace` replicas, each draining its inbox on
its own logical thread like a Blazor circuit) against one `ProjectSession` for 30 s: field edits, creates, moves, deletes,
shape moves, undo / redo, comments and edits of a shared SysML v2 text — and **shared elements edited without locks**:
everyone writes the same "hot" fields, creates, renames, moves and deletes elements in shared packages, nests the shared
packages into each other (moves that would make a cycle are refused) and moves and adds shapes on one shared diagram
(docs/COLLABORATION.md). Some tabs still use element locks as a check-out, and three "rogue" tabs edit inside another tab's
subtree lock; those actions are refused and rolled back in the rogue tab. It asserts:

- an observer saw every accepted operation exactly once (count = session sequence, no duplicates), every operation a tab
  emitted was either accepted or refused (and refused ones were never applied), no tab was ever resynchronised, and no
  tab has unacknowledged edits left;
- every replica serializes identically to the session's model, every shared-text editor has the server's text, and the
  persisted project equals the session model;
- managed memory at the end is below 1.5 × the start + 256 MB;
- **p95 of a local edit (including the server's apply and relay) < 50 ms** and **p95 inbox wait before a tab applies a
  relayed operation < 250 ms**.

Results on the machine above (Debug test build, ~18,600-element project; both stores run together, as in a full test run):

| Store | Editing | Accepted ops | Refused actions | Rebases | Submit p50 / p95 / max | Delivery p50 / p95 / max | Heap start → peak → end |
|---|---|---|---|---|---|---|---|
| SQLite | 30 s | 64,023 (2,134/s) | 757 (697 rogue) | 25,175 | 0.01 / 1.1 / 14 ms | 4.6 / 15 / 2,104 ms | 517 → 1,489 → 627 MB |
| PostgreSQL | 15 s | 31,813 (2,120/s) | 364 (331 rogue) | 12,465 | 0.02 / 1.2 / 36 ms | 4.6 / 12 / 2,487 ms | 644 → 901 → 603 MB |

Before this change (edits of shared elements only under locks; refused edits answered with a full resync) the same test
measured submit p95 12–21 ms and delivery p95 60–87 ms: a refusal now costs a local rollback instead of deserializing a
whole-model snapshot. Delivery maxima are isolated stalls of a few seconds under full CPU load; no tab is resynchronised.

## Known limits

- **Element history diffs read two stored versions** (~1 s at 200k, ~1.5 s for a viewer with hidden packages, who gets
  versions redacted for them); the timeline itself opens in a few milliseconds. Indexing existing versions the first time takes
  ~1 s per version at 200k, in the background.
- **PDF reports keep the laid-out document in memory** (QuestPDF needs every page for page numbers and the table of
  contents): ~1 GB for the 7,000-page SDD of the 200k project. Very large PDFs are best split per package.
- **Word reports of diagram-heavy models** spend most of their time rasterising PNG fallbacks (~25 ms each at 80 dpi);
  Word 2016 and later draw the SVG.
- **Report size estimates** are within about ±40 % of the real page count; they decide when to warn and when to run in
  the background, not the output.
- **Finished reports** stay on the server's temporary disk for two hours (`ReportJobService.Retention`); a whole-model
  SDD is ~80 MB (HTML) to ~180 MB (Word).
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
