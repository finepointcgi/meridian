using System.Collections.Concurrent;
using System.Diagnostics;
using Meridian.Core.Collaboration;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>
/// Many concurrent editors against one <see cref="ProjectSession"/>: 25 in-process tabs (each a real <see cref="Workspace"/>
/// whose inbox is drained on its own logical thread, as a Blazor circuit would) edit fields, create, move and delete
/// elements, move shapes, undo and redo, take element locks, comment and type into a shared SysML v2 text, for 30 seconds.
/// Afterwards every replica must equal the authoritative model, an observer must have seen every accepted operation exactly
/// once, memory must not have grown without bound and the p95 latencies must stay under the documented targets
/// (docs/PERFORMANCE.md). Knobs: MERIDIAN_LOAD_SECONDS, MERIDIAN_LOAD_CLIENTS, MERIDIAN_LOAD_ELEMENTS.
/// </summary>
/// <remarks>
/// Shared elements are edited <b>without locks</b> (docs/COLLABORATION.md): every tab writes the same "hot" fields, creates,
/// renames, moves and deletes elements in shared packages, nests the shared packages into each other (concurrent moves that
/// would make a cycle are refused and rolled back), and moves and adds shapes on one shared diagram — and every replica still
/// converges. Some tabs also use the optional lock as a check-out, and three "rogue" tabs edit inside another tab's subtree
/// lock: those actions are refused and taken back in the rogue tab itself (no resync).
/// </remarks>
[Collection(TimingCollection.Name)]
public sealed class ConcurrencyLoadTests(ITestOutputHelper output)
{
    /// <summary>p95 of a local edit, including the server's apply and relay to every other tab (lock wait included).</summary>
    public const double SubmitP95TargetMs = 50;
    /// <summary>p95 of the time a relayed operation waits in a tab's inbox before that tab applies it.</summary>
    public const double DeliveryP95TargetMs = 250;

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;
    private static int Seconds => Env("MERIDIAN_LOAD_SECONDS", 30);
    private static int Clients => Math.Max(4, Env("MERIDIAN_LOAD_CLIENTS", 25));
    private static int Elements => Env("MERIDIAN_LOAD_ELEMENTS", 20_000);

    [Fact]
    public async Task Many_Editors_On_Sqlite_Converge()
    {
        var path = Path.Combine(Path.GetTempPath(), $"meridian-load-{Guid.NewGuid():N}.db");
        try
        {
            await RunAsync(new SqliteStore(path), Seconds);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" }) try { File.Delete(f); } catch (IOException) { }
        }
    }

    /// <summary>The same against PostgreSQL in a throwaway Testcontainers server (random host port). Skipped without Docker.</summary>
    [SkippableFact]
    public async Task Many_Editors_On_Postgres_Converge()
    {
        PostgreSqlContainer container;
        try
        {
            container = new PostgreSqlBuilder("postgres:17-alpine").Build();
            await container.StartAsync();
        }
        catch (Exception ex) when (Environment.GetEnvironmentVariable("MERIDIAN_REQUIRE_POSTGRES") is not ("1" or "true"))
        {
            Skip.If(true, $"Docker is not available ({ex.GetType().Name}); set MERIDIAN_REQUIRE_POSTGRES=1 to make this a failure.");
            return;
        }
        await using (container)
        {
            await using var store = new PostgresStore(container.GetConnectionString());
            await RunAsync(store, Math.Max(10, Seconds / 2));
        }
    }

    // ================================================================== the run

    private sealed record Stats(List<double> Submit, List<double> Delivery);

    private async Task RunAsync(IMeridianStore store, int seconds)
    {
        await store.MigrateAsync();
        var users = new List<UserAccount>();
        for (var i = 0; i < Clients; i++) users.Add(await store.CreateUserAsync($"editor{i:00}", $"Editor {i}", "correct horse battery"));

        // The project: a Cameo-style model plus a private area per tab (two packages and a diagram) and shared hot elements.
        var model = LargeModelGenerator.Generate(LargeModelOptions.Cameo(Elements));
        var work = model.Create(model.Root, ElementKind.Package, "Load test");
        var hot = Enumerable.Range(0, 10).Select(i => model.Create(work, ElementKind.Requirement, $"Hot {i}")).ToList();
        for (var i = 0; i < Clients; i++)
        {
            var a = model.Create(work, ElementKind.Package, $"Tab {i} A");
            model.Create(work, ElementKind.Package, $"Tab {i} B");
            var d = model.CreateDiagram(a, DiagramType.BlockDefinition, $"Tab {i} view");
            for (var k = 0; k < 4; k++) model.AddNode(d, model.Create(a, ElementKind.Block, $"Tab {i} block {k}"), new Bounds(40 + k * 220, 40, 180, 100), showRelationships: false);
        }
        var guarded = model.Create(work, ElementKind.Package, "Guarded");
        var guardedReq = model.Create(guarded, ElementKind.Requirement, "Guarded requirement");
        var textScope = model.Create(work, ElementKind.Package, "Shared text");
        for (var k = 0; k < 5; k++) model.Create(textScope, ElementKind.Block, $"Texted {k}");
        // The shared area: everyone creates, edits, moves and deletes in it, without locks.
        var sharedRoot = model.Create(work, ElementKind.Package, "Shared area");
        var shared = new Shared(sharedRoot.Id, Enumerable.Range(0, 5).Select(i => model.Create(sharedRoot, ElementKind.Package, $"Shared {i}").Id).ToList());
        var sharedView = model.CreateDiagram(sharedRoot, DiagramType.BlockDefinition, "Shared view");
        shared.DiagramId = sharedView.Id;
        for (var k = 0; k < 6; k++)
        {
            var b = model.Create(model.Find(shared.Packages[k % 5])!, ElementKind.Block, $"Shared block {k}");
            model.AddNode(sharedView, b, new Bounds(40 + k * 200, 40, 160, 90), showRelationships: false);
            shared.Elements[b.Id] = 0;
        }
        var project = await store.CreateProjectAsync("Load", ProjectFile.Serialize(model), users[0].Id);
        foreach (var u in users.Skip(1)) await store.SetMemberAsync(project, u.Id, ProjectRole.Editor);

        var hub = new CollaborationHub(store, NullLogger<CollaborationHub>.Instance);
        var observer = new Observer();
        var observerSession = await hub.JoinAsync(project, new Presence("observer", users[0], null, null, CanEdit: false), observer);
        Assert.NotNull(observerSession);
        var startSequence = observerSession!.Sequence;

        var circuits = new List<Circuit>();
        for (var i = 0; i < Clients; i++)
        {
            var c = new Circuit(i, users[i], store, project, hot.Select(h => h.Id).ToList(), textScope.Id, guardedReq.Id, rogue: i < 3, shared);
            Assert.True(await c.Ws.JoinAsync(hub, project, ProjectRole.Editor));
            circuits.Add(c);
        }
        var session = circuits[0].Ws.Session!;
        Assert.Same(observerSession, session);
        foreach (var c in circuits) c.OpenText();
        circuits[3].KeepGuard(guarded.Id);

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var memoryAtStart = GC.GetTotalMemory(true);
        long memoryPeak = memoryAtStart;
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                memoryPeak = Math.Max(memoryPeak, GC.GetTotalMemory(false));
                try { await Task.Delay(250, stop.Token); } catch (OperationCanceledException) { }
            }
        });
        var wall = Stopwatch.StartNew();
        await Task.WhenAll(circuits.Select(c => Task.Run(() => c.RunAsync(stop.Token))).Append(sampler));
        var editing = wall.Elapsed;

        // Quiesce: the shared-text refresh timer may still be due; deliver everything until nothing is left.
        for (var round = 0; round < 40; round++)
        {
            await Task.Delay(100);
            var any = false;
            foreach (var c in circuits) any |= c.Drain();
            if (!any && round > 10) break;
        }

        // ---- no lost or duplicated operations
        var accepted = observer.Ops;
        Assert.Equal(session.Sequence - startSequence, accepted.Count);
        Assert.Equal(accepted.Count, accepted.Distinct(ReferenceEqualityComparer.Instance).Count());
        var emitted = new HashSet<object>(circuits.SelectMany(c => c.Emitted), ReferenceEqualityComparer.Instance);
        Assert.All(accepted, op => Assert.Contains(op, emitted));
        var acceptedSet = new HashSet<object>(accepted, ReferenceEqualityComparer.Instance);
        // Refused actions are taken back in the tab itself: no tab is ever resynchronised, and each of its operations was
        // either accepted (exactly once) or refused (never applied anywhere).
        Assert.All(circuits, c => Assert.False(c.Resynced, $"tab {c.Index} was resynchronised"));
        foreach (var c in circuits)
        {
            Assert.All(c.Emitted, op => Assert.True(acceptedSet.Contains(op) || c.Refused.Contains(op), $"tab {c.Index}: an operation was lost"));
            Assert.All(c.Refused, op => Assert.False(acceptedSet.Contains(op), $"tab {c.Index}: a refused operation was applied"));
            Assert.Equal(0, c.Ws.PendingBatches);
        }
        Assert.True(circuits.Take(3).All(c => c.Refused.Count > 0), "the rogue tabs' edits inside the lock are refused");

        // ---- every replica converged to the authoritative model (no locks on the shared elements)
        var master = session.Snapshot();
        foreach (var c in circuits) Assert.True(ProjectFile.Serialize(c.Ws.Model) == master, $"tab {c.Index} diverged from the session model");

        // ---- shared text: every editor has the server's text
        var key = circuits[0].TextKey;
        var serverText = session.TextState(key)!.Value.Text;
        foreach (var c in circuits) Assert.Equal(serverText, c.Text);

        // ---- persistence: the stored project is the session model
        await session.PersistAsync();
        var stored = await store.LoadProjectAsync(project);
        Assert.Equal(master, ProjectFile.Serialize(ProjectFile.Deserialize(stored!.Value.ModelJson)));

        // ---- memory stays bounded (histories are capped; snapshots are not pinned)
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var memoryAtEnd = GC.GetTotalMemory(true);

        var submit = circuits.SelectMany(c => c.Submit).Order().ToList();
        var delivery = circuits.SelectMany(c => c.Delivery).Order().ToList();
        double P(List<double> xs, double q) => xs.Count == 0 ? 0 : xs[Math.Min(xs.Count - 1, (int)(xs.Count * q))];
        var comments = (await store.ListCommentsAsync(project)).Count;
        output.WriteLine($"{store.GetType().Name}: {Clients} tabs, {model.Count:N0} elements, {editing.TotalSeconds:0.0} s editing");
        output.WriteLine($"  accepted ops {accepted.Count:N0} ({accepted.Count / editing.TotalSeconds:N0}/s), emitted {emitted.Count:N0}, " +
                         $"refused actions {circuits.Sum(c => c.RefusedActions)} (rogue {circuits.Take(3).Sum(c => c.RefusedActions)}), " +
                         $"rebases {circuits.Sum(c => c.Ws.Rebases)}, locks taken {circuits.Sum(c => c.LocksTaken)}, undos {circuits.Sum(c => c.Undos)}, " +
                         $"comments {comments}, text edits {circuits.Sum(c => c.TextEdits)}");
        output.WriteLine($"  unlocked shared edits: hot fields {circuits.Sum(c => c.HotWrites)}, shared structure {circuits.Sum(c => c.SharedEdits)}, shared elements at end {shared.Elements.Count}");
        output.WriteLine($"  submit p50 {P(submit, 0.5):0.00} ms, p95 {P(submit, 0.95):0.00} ms, max {P(submit, 1):0.0} ms");
        output.WriteLine($"  delivery p50 {P(delivery, 0.5):0.00} ms, p95 {P(delivery, 0.95):0.00} ms, max {P(delivery, 1):0.0} ms");
        output.WriteLine($"  managed heap: start {memoryAtStart / 1048576:N0} MB, peak {memoryPeak / 1048576:N0} MB, end {memoryAtEnd / 1048576:N0} MB");

        Assert.True(accepted.Count > Clients * 20, $"too little happened ({accepted.Count} ops)");
        Assert.True(P(submit, 0.95) < SubmitP95TargetMs, $"submit p95 {P(submit, 0.95):0.0} ms (target {SubmitP95TargetMs} ms)");
        Assert.True(P(delivery, 0.95) < DeliveryP95TargetMs, $"delivery p95 {P(delivery, 0.95):0.0} ms (target {DeliveryP95TargetMs} ms)");
        Assert.True(memoryAtEnd < memoryAtStart * 1.5 + 256L * 1024 * 1024, $"memory grew from {memoryAtStart / 1048576} MB to {memoryAtEnd / 1048576} MB");

        foreach (var c in circuits) c.Ws.Dispose();
        session.Leave("observer");
    }

    /// <summary>The shared area's ids (packages, the shared diagram, and elements anyone created there).</summary>
    private sealed class Shared(string rootId, IReadOnlyList<string> packages)
    {
        public string RootId { get; } = rootId;
        public IReadOnlyList<string> Packages { get; } = packages;
        public string DiagramId { get; set; } = "";
        public ConcurrentDictionary<string, byte> Elements { get; } = new();

        public string? Any(Random rng)
        {
            var all = Elements.Keys.ToList();
            return all.Count == 0 ? null : all[rng.Next(all.Count)];
        }
    }

    /// <summary>A participant that never edits: it records every relayed operation, in the session's order.</summary>
    private sealed class Observer : ISessionClient
    {
        public List<ModelOp> Ops { get; } = new();   // appended under the session lock
        public void RemoteOp(ModelOp op) => Ops.Add(op);
        public void PresenceChanged() { }
        public void Reset(SessionModel model) => throw new InvalidOperationException("no resets in this test");
        public void CommentsChanged() { }
    }

    /// <summary>One simulated browser tab: a workspace, an inbox drained on the tab's own logical thread, and random edits.</summary>
    private sealed class Circuit : ISharedTextClient
    {
        private readonly ConcurrentQueue<(long At, Action Work)> _inbox = new();
        private readonly Random _rng;
        private readonly IMeridianStore _store;
        private readonly Guid _project;
        private readonly IReadOnlyList<string> _hot;
        private readonly string _textScope;
        private readonly bool _rogue;
        private readonly List<string> _mine = new();
        private ModelRepository? _subscribed;
        private string? _locked;
        private int _lockedFor;
        private TextClient? _text;
        private int _undoable, _redoable;
        private readonly string _guarded;
        private readonly Shared _shared;
        private int _refusals;

        public Circuit(int index, UserAccount user, IMeridianStore store, Guid project, IReadOnlyList<string> hot, string textScope, string guarded, bool rogue, Shared shared)
        {
            _guarded = guarded;
            _shared = shared;
            Index = index;
            _rng = new Random(1000 + index);
            _store = store;
            _project = project;
            _hot = hot;
            _textScope = textScope;
            _rogue = rogue;
            Ws = new Workspace { User = user };
            Ws.Dispatch = a => { _inbox.Enqueue((Stopwatch.GetTimestamp(), a)); return Task.CompletedTask; };
            Ws.EditRefused += (ops, _) =>
            {
                _refusals++;
                RefusedActions++;
                foreach (var op in ops) Refused.Add(op);
            };
        }

        /// <summary>Operations of this tab's actions the session refused (taken back here).</summary>
        public HashSet<object> Refused { get; } = new(ReferenceEqualityComparer.Instance);
        public int RefusedActions { get; private set; }
        public int HotWrites { get; private set; }
        public int SharedEdits { get; private set; }

        public int Index { get; }
        public Workspace Ws { get; }
        public ConcurrentBag<ModelOp> Emitted { get; } = new();
        public List<double> Submit { get; } = new();
        public List<double> Delivery { get; } = new();
        public bool Resynced { get; private set; }
        public int LocksTaken { get; private set; }
        public int Undos { get; private set; }
        public int TextEdits { get; private set; }
        public string TextKey { get; private set; } = "";
        public string Text => _text!.Text;

        private ProjectSession Session => Ws.Session!;

        public void OpenText()
        {
            var snap = Session.OpenText(Ws.ParticipantId, _textScope, false, this);
            TextKey = snap.Key;
            _text = new TextClient(snap.Text, snap.Revision);
        }

        /// <summary>Runs everything queued for this tab; true when there was something.</summary>
        public bool Drain()
        {
            var any = false;
            while (_inbox.TryDequeue(out var item))
            {
                Delivery.Add(Stopwatch.GetElapsedTime(item.At).TotalMilliseconds);
                item.Work();
                any = true;
            }
            if (!ReferenceEquals(_subscribed, Ws.Model))
            {
                if (_subscribed != null) { _subscribed.OpEmitted -= Emitted.Add; Resynced = true; (_undoable, _redoable) = (0, 0); }
                _subscribed = Ws.Model;
                _subscribed.OpEmitted += Emitted.Add;
            }
            return any;
        }

        public async Task RunAsync(CancellationToken stop)
        {
            while (!stop.IsCancellationRequested)
            {
                Drain();
                await ActAsync();
                try { await Task.Delay(_rng.Next(2, 12), stop); } catch (OperationCanceledException) { }
            }
            if (_locked != null) Session.Unlock(_locked, Ws.User!.Id);
        }

        /// <summary>A local edit, timed (it includes the session's apply and relay). <paramref name="history"/>: it adds an undo step.</summary>
        private T Mine<T>(Func<ModelRepository, T> edit, bool history = true)
        {
            var refusals = _refusals;
            var t = Stopwatch.GetTimestamp();
            var r = edit(Ws.Model);
            Submit.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            // A refused action left no trace in the history (CommandStack.DiscardLastAction).
            if (history && refusals == _refusals) (_undoable, _redoable) = (_undoable + 1, 0);
            return r;
        }

        /// <summary>Runs an undo or redo; false when the session refused it.</summary>
        private bool Accepted(Action<ModelRepository> step)
        {
            var refusals = _refusals;
            Mine(x => { step(x); return 0; }, history: false);
            return refusals == _refusals;
        }

        /// <summary>
        /// One unlocked edit in the shared area: create, rename, delete or move someone's element, nest the shared packages into
        /// each other (two tabs nesting two packages into each other at once: the later move is refused), or move / add / remove
        /// shapes on the shared diagram.
        /// </summary>
        private void SharedEdit(ModelRepository m)
        {
            SharedEdits++;
            Element? Pkg() => m.Find(_shared.Packages[_rng.Next(_shared.Packages.Count)]);
            switch (_rng.Next(8))
            {
                case 0 when Pkg() is { } owner:
                {
                    var e = Mine(x => x.Create(owner, ElementKind.Block, $"S{Index} {_rng.Next(100_000)}"));
                    if (m.Contains(e)) _shared.Elements[e.Id] = 0;
                    break;
                }
                case 1 when _shared.Any(_rng) is { } id && m.Find(id) is { } e:   // delete vs. edit / move of the same element
                    if (_rng.Next(3) == 0)
                    {
                        _shared.Elements.TryRemove(id, out _);
                        Mine(x => { x.Delete(e); return 0; });
                    }
                    else Mine(x => { x.Rename(e, $"{e.Name.Split('·')[0]}·{Index}"); return 0; });
                    break;
                case 2 when _shared.Any(_rng) is { } id && m.Find(id) is { } e && Pkg() is { } to && !ReferenceEquals(e.Owner, to):
                    Mine(x => { x.Move(e, to, _rng.Next(to.OwnedElements.Count + 1)); return 0; });
                    break;
                case 3 when Pkg() is { } p && Pkg() is { } into:   // nest packages (or put one back at the top)
                {
                    var target = _rng.Next(3) == 0 ? m.Find(_shared.RootId)! : into;
                    if (!ReferenceEquals(p, target) && !p.IsAncestorOf(target) && !ReferenceEquals(p.Owner, target))
                        Mine(x => { x.Move(p, target); return 0; });
                    break;
                }
                case 4 or 5 when m.Find(_shared.DiagramId) is Diagram d && d.Nodes.Count > 0:   // the same shapes, moved by everyone
                {
                    var n = d.Nodes[_rng.Next(d.Nodes.Count)];
                    Mine(x => { x.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [n] = n.Bounds.Offset(_rng.Next(-30, 31), _rng.Next(-30, 31)) }); return 0; });
                    break;
                }
                case 6 when m.Find(_shared.DiagramId) is Diagram d:
                    if (d.Nodes.Count > 30)
                    {
                        var n = d.Nodes[_rng.Next(d.Nodes.Count)];
                        Mine(x => { x.RemoveFromDiagram(d, [n]); return 0; });
                    }
                    else if (_shared.Any(_rng) is { } id && m.Find(id) is { } e && d.NodeFor(e) is null)
                        Mine(x => x.AddNode(d, e, new Bounds(_rng.Next(0, 1200), _rng.Next(0, 800), 160, 90), showRelationships: false));
                    break;
                case 7 when _shared.Any(_rng) is { } id && m.Find(id) is { } e:
                    Mine(x => { x.Set(e, FieldKeys.Documentation, $"tab {Index} {_rng.Next()}"); return 0; });
                    break;
            }
        }

        /// <summary>The keeper (tab 3) holds a subtree lock on the guarded package for the whole run.</summary>
        public void KeepGuard(string packageId) => Assert.Null(Session.TryLock(Ws.ParticipantId, packageId, subtree: true));

        private Element Area(char which) => Ws.Model.OfKind(ElementKind.Package).First(p => p.Name == $"Tab {Index} {which}");

        private async Task ActAsync()
        {
            var m = Ws.Model;
            if (_locked != null && --_lockedFor <= 0) { Session.Unlock(_locked, Ws.User!.Id); _locked = null; }
            var roll = _rng.Next(100);
            switch (roll)
            {
                case < 25:   // edit one of my elements (or a block of my diagram)
                {
                    var target = _mine.Count > 0 && _rng.Next(2) == 0 ? m.Find(_mine[_rng.Next(_mine.Count)]) : Area('A').OwnedElements.FirstOrDefault(e => e.Kind == ElementKind.Block);
                    if (target != null) Mine(x => { x.Set(target, FieldKeys.Documentation, $"tab {Index} edit {_rng.Next()}"); return 0; });
                    break;
                }
                case < 32:
                {
                    var owner = Area(_rng.Next(2) == 0 ? 'A' : 'B');
                    var e = Mine(x => x.Create(owner, _rng.Next(2) == 0 ? ElementKind.Requirement : ElementKind.Block, $"T{Index} {_rng.Next(100_000)}"));
                    _mine.Add(e.Id);
                    break;
                }
                case < 35 when _mine.Count > 0:
                {
                    var id = _mine[_rng.Next(_mine.Count)];
                    _mine.Remove(id);
                    if (m.Find(id) is { } e) Mine(x => { x.Delete(e); return 0; });
                    break;
                }
                case < 38 when _mine.Count > 0:
                {
                    if (m.Find(_mine[_rng.Next(_mine.Count)]) is { } e)
                    {
                        var to = e.Owner == Area('A') ? Area('B') : Area('A');
                        Mine(x => { x.Move(e, to); return 0; });
                    }
                    break;
                }
                case < 43:   // move a shape on my diagram
                {
                    var d = (Diagram)Area('A').OwnedElements.First(e => e is Diagram);
                    if (d.Nodes.Count > 0)
                    {
                        var n = d.Nodes[_rng.Next(d.Nodes.Count)];
                        Mine(x => { x.SetBounds(d, new Dictionary<DiagramNode, Bounds> { [n] = n.Bounds.Offset(_rng.Next(-20, 21), _rng.Next(-20, 21)) }); return 0; });
                    }
                    break;
                }
                // Undo / redo reach only this tab's own actions, including unlocked edits of shared elements (parts whose
                // target someone deleted are skipped; an undo the session refuses is taken back and stays undoable).
                case < 49 when _undoable > 0 && m.History.CanUndo:
                    if (Accepted(x => x.History.Undo())) (_undoable, _redoable) = (_undoable - 1, _redoable + 1);
                    Undos++;
                    break;
                case < 52 when _redoable > 0 && m.History.CanRedo:
                    if (Accepted(x => x.History.Redo())) (_undoable, _redoable) = (_undoable + 1, _redoable - 1);
                    break;
                case < 56 when _rogue:   // inside the keeper's subtree lock: refused, and taken back in this tab
                {
                    if (m.Find(_guarded) is { } g && Session.BlockingLock(Ws.User!.Id, g.Id) != null)
                        Mine(x => { x.Set(g, FieldKeys.Text, $"rogue {Index} {_rng.Next()}"); return 0; });
                    break;
                }
                case < 62:   // a shared "hot" field, written by everyone at once, without a lock
                {
                    if (m.Find(_hot[_rng.Next(_hot.Count)]) is { } target && Session.BlockingLock(Ws.User!.Id, target.Id) == null)
                    {
                        var key = _rng.Next(3) switch { 0 => FieldKeys.Name, 1 => FieldKeys.Text, _ => FieldKeys.Documentation };
                        Mine(x => { x.Set(target, key, $"tab {Index} says {_rng.Next()}"); return 0; });
                        HotWrites++;
                    }
                    break;
                }
                case < 64:   // the optional check-out: lock a hot element, edit it, keep it a few rounds
                {
                    var id = _hot[_rng.Next(_hot.Count)];
                    if (_locked == null && Session.TryLock(Ws.ParticipantId, id, subtree: false) == null)
                    {
                        _locked = id;
                        _lockedFor = _rng.Next(2, 8);
                        LocksTaken++;
                    }
                    if (_locked != null && Ws.Model.Find(_locked) is { } target)
                        Mine(x => { x.Set(target, FieldKeys.Text, $"tab {Index} checked out {_rng.Next()}"); return 0; });
                    break;
                }
                case < 78:   // the shared area: concurrent structure edits, no locks
                    SharedEdit(m);
                    break;
                case < 82:   // comment on one of my elements
                {
                    var on = _mine.Count > 0 ? _mine[^1] : Area('A').Id;
                    await _store.AddCommentAsync(_project, on, Ws.User!.Id, $"comment from tab {Index}");
                    Session.NotifyComments();
                    break;
                }
                case < 92:   // type into the shared SysML v2 text (never applied, so it only exercises OT)
                {
                    var text = _text!.Text;
                    var at = _rng.Next(text.Length + 1);
                    var op = new TextOperation().Retain(at).Insert($"/*{Index}*/").Retain(text.Length - at);
                    if (_text.ApplyLocal(op) is { } send) Session.SubmitText(TextKey, Ws.ParticipantId, _text.Revision, send);
                    TextEdits++;
                    break;
                }
                default:     // look around: selection and presence
                    Ws.Select(m.Find(_hot[_rng.Next(_hot.Count)]));
                    break;
            }
        }

        void ISharedTextClient.TextOperation(string key, TextOperation op) => _inbox.Enqueue((Stopwatch.GetTimestamp(), () => _text!.ApplyServer(op)));

        void ISharedTextClient.TextAck(string key) => _inbox.Enqueue((Stopwatch.GetTimestamp(), () =>
        {
            if (_text!.ServerAck() is { } next) Session.SubmitText(TextKey, Ws.ParticipantId, _text.Revision, next);
        }));

        void ISharedTextClient.TextCursors(string key, int revision, IReadOnlyList<TextCursor> cursors) { }
        void ISharedTextClient.TextStatus(string key, SharedTextStatus status) { }
        void ISharedTextClient.TextReset(string key, string text, int revision) => _inbox.Enqueue((Stopwatch.GetTimestamp(), () => _text = new TextClient(text, revision)));
    }
}
