using Meridian.Core.Collaboration;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Serialization;
using Xunit.Abstractions;

namespace Meridian.Tests;

/// <summary>
/// An in-memory stand-in for the collaboration session (ProjectSession.SubmitBatch): one global order, operations relayed
/// with their sequence number and the author acknowledged through the same FIFO inbox, batches refused as a whole. Each
/// client is a replica with a <see cref="ReplicaSync"/>; the test decides when messages are delivered, so every interleaving
/// can be scripted.
/// </summary>
internal sealed class SimServer(ModelRepository master)
{
    public ModelRepository Master { get; } = master;
    public long Sequence { get; private set; }
    public List<SimClient> Clients { get; } = new();

    /// <summary>A stand-in for permissions and locks: a reason to refuse an operation from a client, or null.</summary>
    public Func<SimClient, ModelOp, string?>? Refuse { get; set; }

    /// <summary>What happened, in order (written to MERIDIAN_CONVERGENCE_DUMP when a replica diverges).</summary>
    public List<string> Trace { get; } = new();

    public SimClient Join(string name)
    {
        var c = new SimClient(this, name, ProjectFile.Deserialize(ProjectFile.Serialize(Master)));
        Clients.Add(c);
        return c;
    }

    public string? Submit(SimClient from, IReadOnlyList<ModelOp> ops)
    {
        foreach (var op in ops)
            if (Refuse?.Invoke(from, op) is { } why) { Trace.Add($"{from.Name} refused: {string.Join("; ", ops)}"); return why; }
        if (BatchCheck.MoveCycle(Master, ops) is { } cycle) { Trace.Add($"{from.Name} cycle: {string.Join("; ", ops)}"); return cycle; }
        Trace.Add($"{from.Name} accepted at {Sequence + 1}: {string.Join("; ", ops)}");
        foreach (var op in ops)
        {
            Master.TryApplyRemote(op);
            Sequence++;
            foreach (var c in Clients)
                if (!ReferenceEquals(c, from)) c.Inbox.Enqueue(new SimClient.Op(op, Sequence));
        }
        from.Inbox.Enqueue(new SimClient.Ack(Sequence));
        return null;
    }

    /// <summary>A resync / merge / restore: the client gets the whole model as it is now (later operations follow it).</summary>
    public void SendSnapshot(SimClient to)
    {
        Trace.Add($"snapshot for {to.Name} at {Sequence}");
        to.Inbox.Enqueue(new SimClient.Snapshot(ProjectFile.Serialize(Master)));
    }

    public string Json => ProjectFile.Serialize(Master);

    public void DeliverAll()
    {
        while (Clients.Any(c => c.Inbox.Count > 0))
            foreach (var c in Clients) c.Deliver();
    }

    /// <summary>Delivers everything, then asserts that every replica serializes exactly like the server's model.</summary>
    public void AssertConverged()
    {
        DeliverAll();
        var json = Json;
        foreach (var c in Clients)
        {
            Assert.Empty(c.Sync.Pending);
            if (json != c.Json && Environment.GetEnvironmentVariable("MERIDIAN_CONVERGENCE_DUMP") is { Length: > 0 } dir)
            {
                File.WriteAllText(Path.Combine(dir, "server.json"), json);
                File.WriteAllText(Path.Combine(dir, c.Name + ".json"), c.Json);
                File.WriteAllLines(Path.Combine(dir, "trace.txt"), Trace);
            }
            Assert.True(json == c.Json, $"{c.Name} diverged from the server");
        }
    }
}

internal sealed class SimClient
{
    public sealed record Op(ModelOp Value, long Sequence);
    public sealed record Ack(long Sequence);
    public sealed record Snapshot(string Json);

    private readonly SimServer _server;
    private void Log(string what) => _server.Trace.Add($"  {Name} {what}");

    public SimClient(SimServer server, string name, ModelRepository model)
    {
        _server = server;
        Name = name;
        Sync = new ReplicaSync(model);
        model.History.ActionCompleted += Flush;
    }

    public string Name { get; }
    public ReplicaSync Sync { get; }
    public ModelRepository Model => Sync.Model;
    public Queue<object> Inbox { get; } = new();
    public List<DroppedEdit> Dropped { get; } = new();
    public List<string> Refusals { get; } = new();
    public string Json => ProjectFile.Serialize(Model);

    /// <summary>What the tab does at the end of each user action: submit it; on refusal take it back and forget it in the history.</summary>
    private void Flush()
    {
        if (Sync.Close() is not { } batch) return;
        if (_server.Submit(this, batch.Ops) is { } why)
        {
            Sync.Reject(batch);
            Model.History.DiscardLastAction();
            Refusals.Add(why);
        }
        else ReplicaSync.Accepted(batch, _server.Sequence);
    }

    /// <summary>Processes up to <paramref name="max"/> inbox messages; consecutive operations are applied as one rebase.</summary>
    public void Deliver(int max = int.MaxValue)
    {
        var ops = new List<ModelOp>();
        void ApplyOps()
        {
            if (ops.Count == 0) return;
            Log($"applies {ops.Count} ops (pending {Sync.Pending.Count}, open {Sync.OpenOps.Count})");
            Dropped.AddRange(Sync.ApplyRemote(ops));
            ops.Clear();
        }
        for (var n = 0; n < max && Inbox.Count > 0; n++)
        {
            switch (Inbox.Dequeue())
            {
                case Op o:
                    ops.Add(o.Value);
                    if (Inbox.Count > 0 && Inbox.Peek() is Op && n + 1 < max) continue;
                    ApplyOps();
                    break;
                case Ack:
                    ApplyOps();
                    Log("ack");
                    Sync.Acknowledge();
                    break;
                case Snapshot s:
                    ApplyOps();
                    var old = Model;
                    old.History.ActionCompleted -= Flush;
                    var fresh = ProjectFile.Deserialize(s.Json);
                    fresh.History.ActionCompleted += Flush;
                    Log("snapshot");
                    Dropped.AddRange(Sync.Rebind(fresh));
                    break;
            }
        }
        ApplyOps();
    }

    public Element E(string name) => Model.AllElements.Single(e => e.Name == name);
    public Diagram D => Model.Diagrams.Single(d => d.Name == "View");
}

/// <summary>
/// Convergence of concurrent edits without locks (docs/COLLABORATION.md): scripted conflicting interleavings, and a
/// randomized property test over thousands of seeded interleavings between three to five replicas.
/// </summary>
public sealed class ConvergenceTests(ITestOutputHelper output)
{
    /// <summary>Root ▸ P (X, Y, Z, View [X, Y, X→Y]), Q, R.</summary>
    private static ModelRepository Base()
    {
        var m = new ModelRepository("Model");
        var p = m.Create(m.Root, ElementKind.Package, "P");
        m.Create(m.Root, ElementKind.Package, "Q");
        m.Create(m.Root, ElementKind.Package, "R");
        var x = m.Create(p, ElementKind.Block, "X");
        var y = m.Create(p, ElementKind.Block, "Y");
        m.Create(p, ElementKind.Block, "Z");
        var dep = m.Connect(ElementKind.Dependency, x, y, p);
        var d = m.CreateDiagram(p, DiagramType.BlockDefinition, "View");
        m.AddNode(d, x, new Bounds(10, 10, 100, 60), showRelationships: false);
        m.AddNode(d, y, new Bounds(300, 10, 100, 60), showRelationships: false);
        m.AddEdge(d, dep);
        m.History.Clear();
        return m;
    }

    private static (SimServer S, SimClient A, SimClient B) Two()
    {
        var s = new SimServer(Base());
        return (s, s.Join("A"), s.Join("B"));
    }

    // ------------------------------------------------------------------ same field

    [Fact]
    public void Same_Field_Set_Concurrently_Last_In_Server_Order_Wins_Everywhere()
    {
        var (s, a, b) = Two();
        a.Model.Rename(a.E("X"), "From A");   // sequenced first
        b.Model.Rename(b.E("X"), "From B");   // sequenced second, applied by B before A's arrives
        Assert.Equal("From A", a.E("From A").Name);
        s.AssertConverged();
        Assert.Equal("From B", s.Master.Find(a.E("From B").Id)!.Name);
        Assert.Equal(1, b.Sync.Rebases);   // B rewound its rename under A's and re-applied it
    }

    [Fact]
    public void Same_Field_Many_Writers_Partial_Deliveries_Converge()
    {
        var s = new SimServer(Base());
        var cs = Enumerable.Range(0, 4).Select(i => s.Join($"C{i}")).ToList();
        for (var round = 0; round < 5; round++)
            for (var i = 0; i < cs.Count; i++)
            {
                cs[i].Model.Set(cs[i].E("Y"), FieldKeys.Documentation, $"round {round} by {i}");
                cs[(i + round) % cs.Count].Deliver(2);
            }
        s.AssertConverged();
        Assert.Equal("round 4 by 3", s.Master.AllElements.Single(e => e.Name == "Y").Documentation);
    }

    // ------------------------------------------------------------------ delete vs edit

    [Fact]
    public void Delete_Beats_A_Concurrent_Edit_Which_Is_Dropped_With_A_Notice()
    {
        var (s, a, b) = Two();
        var x = a.E("X").Id;
        a.Model.Delete(a.E("X"));
        b.Model.Rename(b.E("X"), "Renamed");
        b.Model.Set(b.E("Renamed"), FieldKeys.Documentation, "docs");
        s.AssertConverged();
        Assert.Null(s.Master.Find(x));
        Assert.Equal(2, b.Dropped.Count);
        Assert.Equal("Someone else deleted “X” — your change to it was dropped.", ReplicaSync.Notice(b.Dropped));
    }

    [Fact]
    public void An_Edit_Sequenced_Before_The_Delete_Is_Deleted_With_The_Element()
    {
        var (s, a, b) = Two();
        b.Model.Rename(b.E("X"), "Renamed");   // first
        a.Model.Delete(a.E("X"));              // second: A rewinds the delete, applies the rename, deletes again
        s.AssertConverged();
        Assert.DoesNotContain(s.Master.AllElements, e => e.Name is "X" or "Renamed");
        Assert.Empty(a.Dropped);
        Assert.Empty(b.Dropped);
    }

    [Fact]
    public void Create_Inside_A_Package_Deleted_Meanwhile_Is_Dropped()
    {
        var (s, a, b) = Two();
        a.Model.Delete(a.E("Q"));
        var made = b.Model.Create(b.E("Q"), ElementKind.Block, "Orphan");
        s.AssertConverged();
        Assert.Null(s.Master.Find(made.Id));
        Assert.Single(b.Dropped);
    }

    // ------------------------------------------------------------------ move vs move

    [Fact]
    public void Move_Into_Each_Other_The_Later_Move_Is_Refused_And_Rolled_Back()
    {
        var (s, a, b) = Two();
        a.Model.Move(a.E("Q"), a.E("R"));   // Q into R: accepted
        b.Model.Move(b.E("R"), b.E("Q"));   // R into Q: would make a cycle on the server, refused
        Assert.Single(b.Refusals);
        Assert.Contains("can't be moved into", b.Refusals[0]);
        Assert.Same(b.Model.Root, b.E("R").Owner);   // B's move was taken back at once
        Assert.False(b.Model.History.CanUndo);       // and it left no undo step
        s.AssertConverged();
        Assert.Equal("R", s.Master.AllElements.Single(e => e.Name == "Q").Owner!.Name);
    }

    [Fact]
    public void A_Batch_Of_Moves_Is_Checked_In_Order()
    {
        var (s, a, _) = Two();
        // R into Q, then Q to the root's end and R … a legal sequence inside one action (checked with the batch's own moves).
        using (a.Model.History.Transaction("Shuffle"))
        {
            a.Model.Move(a.E("R"), a.E("Q"));
            a.Model.Move(a.E("R"), a.Model.Root);
            a.Model.Move(a.E("Q"), a.E("R"));
        }
        Assert.Empty(a.Refusals);
        s.AssertConverged();
    }

    [Fact]
    public void Move_Into_A_Package_Deleted_Meanwhile_Is_Dropped_And_The_Element_Stays()
    {
        var (s, a, b) = Two();
        a.Model.Delete(a.E("Q"));
        b.Model.Move(b.E("Z"), b.E("Q"));
        s.AssertConverged();
        Assert.Equal("P", s.Master.AllElements.Single(e => e.Name == "Z").Owner!.Name);
    }

    [Fact]
    public void Concurrent_Moves_Of_One_Element_Last_Wins()
    {
        var (s, a, b) = Two();
        a.Model.Move(a.E("Z"), a.E("Q"));
        b.Model.Move(b.E("Z"), b.E("R"));
        s.AssertConverged();
        Assert.Equal("R", s.Master.AllElements.Single(e => e.Name == "Z").Owner!.Name);
    }

    // ------------------------------------------------------------------ relationship ends

    [Fact]
    public void Relationship_Created_Then_Its_End_Deleted_Goes_With_The_End()
    {
        var (s, a, b) = Two();
        var r = a.Model.Connect(ElementKind.Dependency, a.E("Z"), a.E("Y"), a.E("P"));   // first
        b.Model.Delete(b.E("Y"));                                                      // second, without knowing r
        s.AssertConverged();
        Assert.Null(s.Master.Find(r.Id));
        AssertNoDangling(s.Master);
    }

    [Fact]
    public void Relationship_To_An_End_Deleted_Meanwhile_Is_Dropped()
    {
        var (s, a, b) = Two();
        b.Model.Delete(b.E("Y"));                                                      // first
        var r = a.Model.Connect(ElementKind.Dependency, a.E("Z"), a.E("Y"), a.E("P"));   // second
        s.AssertConverged();
        Assert.Null(s.Master.Find(r.Id));
        Assert.Single(a.Dropped);
        AssertNoDangling(s.Master);
    }

    [Fact]
    public void Undo_Of_A_Delete_After_Someone_Deleted_The_Other_End_Leaves_No_Dangling_Relationship()
    {
        var (s, a, b) = Two();
        a.Model.Delete(a.E("X"));   // takes X→Y along
        s.DeliverAll();
        b.Model.Delete(b.E("Y"));
        s.DeliverAll();
        a.Model.History.Undo();     // X comes back; X→Y can't (Y is gone)
        s.AssertConverged();
        Assert.NotNull(s.Master.AllElements.SingleOrDefault(e => e.Name == "X"));
        Assert.Empty(s.Master.Relationships);
        AssertNoDangling(s.Master);
    }

    // ------------------------------------------------------------------ diagrams

    [Fact]
    public void Concurrent_Shape_Moves_And_A_Removed_Shape_Converge()
    {
        var (s, a, b) = Two();
        var na = a.D.NodeFor(a.E("X"))!;
        var nb = b.D.NodeFor(b.E("X"))!;
        a.Model.SetBounds(a.D, new Dictionary<DiagramNode, Bounds> { [na] = new(50, 50, 100, 60) });
        b.Model.SetBounds(b.D, new Dictionary<DiagramNode, Bounds> { [nb] = new(90, 90, 100, 60) });
        s.AssertConverged();
        Assert.Equal(new Bounds(90, 90, 100, 60), s.Master.Diagrams.Single().NodeFor(s.Master.AllElements.Single(e => e.Name == "X"))!.Bounds);

        a.Model.RemoveFromDiagram(a.D, [a.D.NodeFor(a.E("Y"))!]);   // takes the path along
        b.Model.SetBounds(b.D, new Dictionary<DiagramNode, Bounds> { [b.D.NodeFor(b.E("Y"))!] = new(0, 200, 100, 60) });
        s.AssertConverged();
        Assert.Single(s.Master.Diagrams.Single().Nodes);
        Assert.Empty(s.Master.Diagrams.Single().Edges);
    }

    [Fact]
    public void A_Shape_For_An_Element_Deleted_Meanwhile_Is_Dropped()
    {
        var (s, a, b) = Two();
        a.Model.Delete(a.E("Z"));
        b.Model.AddNode(b.D, b.E("Z"), new Bounds(500, 10, 100, 60));
        s.AssertConverged();
        Assert.Equal(2, s.Master.Diagrams.Single().Nodes.Count);
    }

    [Fact]
    public void A_Path_Added_While_Its_Shape_Is_Removed_Is_Dropped()
    {
        var (s, a, b) = Two();
        var dep = a.Model.Relationships.Single();
        a.Model.RemoveFromDiagram(a.D, [], [a.D.EdgeFor(dep)!]);
        s.DeliverAll();
        a.Model.RemoveFromDiagram(a.D, [a.D.NodeFor(a.E("Y"))!]);
        b.Model.AddEdge(b.D, (Relationship)b.Model.Find(dep.Id)!);
        s.AssertConverged();
        Assert.Empty(s.Master.Diagrams.Single().Edges);
    }

    // ------------------------------------------------------------------ ordered lists

    [Fact]
    public void Concurrent_Inserts_At_The_Same_Position_Keep_One_Order_Everywhere()
    {
        var s = new SimServer(Base());
        var cs = Enumerable.Range(0, 3).Select(i => s.Join($"C{i}")).ToList();
        foreach (var c in cs) c.Model.Create(c.E("P"), ElementKind.Block, $"Inserted by {c.Name}", index: 1);
        cs[1].Model.Move(cs[1].E("Z"), cs[1].E("P"), 0);   // and a reorder
        s.AssertConverged();
        var order = s.Master.AllElements.Single(e => e.Name == "P").OwnedElements.Select(e => e.DisplayName).ToList();
        Assert.Equal(["Z", "X", "Inserted by C2", "Inserted by C1", "Inserted by C0", "Y"], order.Take(6));
    }

    [Fact]
    public void Concurrent_Shapes_And_Operand_Reorders_Keep_One_Order()
    {
        var m = Base();
        var p = m.AllElements.Single(e => e.Name == "P");
        var interaction = m.Create(p, ElementKind.Interaction, "Talk");
        var fragment = m.Create(interaction, ElementKind.CombinedFragment);
        foreach (var g in new[] { "a", "b", "c" }) m.AddOperand(fragment, g);
        m.History.Clear();
        var s = new SimServer(m);
        var (a, b) = (s.Join("A"), s.Join("B"));
        Element Operand(SimClient c, string guard) => c.Model.AllElements.Single(e => e.Kind == ElementKind.InteractionOperand && e.Get(FieldKeys.Guard) == guard);
        a.Model.Move(Operand(a, "c"), Operand(a, "c").Owner!, 0);
        b.Model.Move(Operand(b, "a"), Operand(b, "a").Owner!, 2);
        a.Model.AddNode(a.D, a.E("Z"), new Bounds(0, 0, 50, 50));
        b.Model.AddNode(b.D, b.E("Talk"), new Bounds(0, 100, 50, 50));
        s.AssertConverged();
    }

    // ------------------------------------------------------------------ ids

    [Fact]
    public void Client_Generated_Ids_Stay_Unique_When_Two_Undo_The_Same_Delete()
    {
        var (s, a, b) = Two();
        a.Model.Delete(a.E("Z"));
        b.Model.Delete(b.E("Z"));   // both deleted it (B's is a no-op on the server)
        s.DeliverAll();
        a.Model.History.Undo();
        b.Model.History.Undo();     // both bring it back: one copy survives
        s.AssertConverged();
        Assert.Single(s.Master.AllElements, e => e.Name == "Z");
        Assert.Equal(s.Master.Count, s.Master.AllElements.Select(e => e.Id).Distinct().Count());
    }

    // ------------------------------------------------------------------ undo / redo

    [Fact]
    public void Undo_After_A_Rebase_Undoes_Only_My_Own_Edit()
    {
        var (s, a, b) = Two();
        b.Model.Set(b.E("Y"), FieldKeys.Documentation, "B's");   // first
        var created = a.Model.Create(a.E("P"), ElementKind.Block, "Mine");   // second: rebased under B's
        a.Model.Set(a.E("X"), FieldKeys.Documentation, "A's");
        s.DeliverAll();
        Assert.Same(created, a.Model.Find(created.Id));   // re-applied on the same object
        a.Model.History.Undo();
        a.Model.History.Undo();
        s.AssertConverged();
        Assert.Null(s.Master.Find(created.Id));
        Assert.Equal("B's", s.Master.AllElements.Single(e => e.Name == "Y").Documentation);
        Assert.Equal("", s.Master.AllElements.Single(e => e.Name == "X").Documentation);
        a.Model.History.Redo();
        a.Model.History.Redo();
        s.AssertConverged();
        Assert.NotNull(s.Master.Find(created.Id));
    }

    [Fact]
    public void Undo_Skips_What_Others_Deleted_And_Says_So()
    {
        var (s, a, b) = Two();
        a.Model.Rename(a.E("X"), "Mine");
        a.Model.SetBounds(a.D, new Dictionary<DiagramNode, Bounds> { [a.D.NodeFor(a.E("Mine"))!] = new(1, 1, 100, 60) });
        s.DeliverAll();
        b.Model.Delete(b.E("Mine"));
        s.DeliverAll();
        var skipped = a.Model.SkippedEdits;
        a.Model.History.Undo();
        a.Model.History.Undo();
        Assert.True(a.Model.SkippedEdits > skipped);
        Assert.Empty(a.Sync.Pending);   // nothing was emitted
        s.AssertConverged();
    }

    [Fact]
    public void A_Refused_Undo_Stays_Undoable()
    {
        var (s, a, b) = Two();
        a.Model.Rename(a.E("X"), "Mine");
        s.DeliverAll();
        s.Refuse = (c, op) => ReferenceEquals(c, a) ? "Locked by B — your change was not applied." : null;
        a.Model.History.Undo();
        Assert.Equal("Mine", a.Model.AllElements.Single(e => e.Id == b.E("Mine").Id).Name);   // rolled back
        Assert.True(a.Model.History.CanUndo);
        Assert.False(a.Model.History.CanRedo);
        s.Refuse = null;
        a.Model.History.Undo();
        s.AssertConverged();
        Assert.Equal("X", s.Master.AllElements.Single(e => e.Id == b.E("X").Id).Name);
    }

    [Fact]
    public void A_Refused_Edit_Rolls_Back_Cleanly_While_Earlier_Edits_Are_Pending()
    {
        var (s, a, b) = Two();
        b.Model.Rename(b.E("Y"), "Y2");
        a.Model.Rename(a.E("X"), "X2");                                       // accepted, pending
        s.Refuse = (c, op) => op is SetFieldOp { Key: FieldKeys.Documentation } ? "Read-only package" : null;
        a.Model.Set(a.E("X2"), FieldKeys.Documentation, "refused");          // refused: taken back, nothing else
        Assert.Equal(["Read-only package"], a.Refusals);
        Assert.Equal("", a.E("X2").Documentation);
        Assert.Equal("X2", a.E("X2").Name);
        s.Refuse = null;
        s.AssertConverged();
        Assert.Equal($"Set {FieldKeys.Name}", a.Model.History.UndoDescription);   // the refused step left no trace
    }

    // ------------------------------------------------------------------ snapshots

    [Fact]
    public void Pending_Edits_Survive_A_Replacement_Snapshot_Taken_Before_Them()
    {
        var (s, a, b) = Two();
        s.SendSnapshot(a);                                  // queued before A's next edits are sequenced
        a.Model.Rename(a.E("X"), "After the snapshot");
        b.Model.Rename(b.E("Y"), "B too");
        s.AssertConverged();
        Assert.Single(s.Master.AllElements, e => e.Name == "After the snapshot");
    }

    [Fact]
    public void A_Late_Joiner_Converges()
    {
        var (s, a, b) = Two();
        a.Model.Rename(a.E("X"), "A1");
        var c = s.Join("C");
        b.Model.Rename(b.E("X"), "B1");
        c.Model.Create(c.E("Q"), ElementKind.Block, "From C");
        s.AssertConverged();
    }

    // ------------------------------------------------------------------ the property test

    /// <summary>
    /// Thousands of seeded random interleavings between three to five replicas: field writes, creates at random positions,
    /// deletes, moves (including package nesting that can make cycles), shapes and paths, multi-operation actions, undo and
    /// redo, refusals and replacement snapshots, with messages delivered in random partial amounts. Every replica must end
    /// equal to the server, and the server's model must hold no dangling references. Seeds: MERIDIAN_CONVERGENCE_SEEDS.
    /// </summary>
    [Fact]
    public void Random_Interleavings_Always_Converge()
    {
        var seeds = int.TryParse(Environment.GetEnvironmentVariable("MERIDIAN_CONVERGENCE_SEEDS"), out var n) && n > 0 ? n : 2_000;
        var stats = new int[4];
        var first = int.TryParse(Environment.GetEnvironmentVariable("MERIDIAN_CONVERGENCE_FIRST"), out var f) ? f : 0;
        for (var seed = first; seed < first + seeds; seed++)
        {
            try { RunRandom(seed, stats); }
            catch (Exception ex) { throw new Xunit.Sdk.XunitException($"seed {seed}: {ex.Message}\n{ex}"); }
        }
        output.WriteLine($"{seeds} seeds: {stats[0]} actions, {stats[1]} rebases, {stats[2]} refusals, {stats[3]} dropped edits");
    }

    internal static void RunRandom(int seed, int[] stats)
    {
        var rng = new Random(seed);
        var s = new SimServer(Base());
        var clients = Enumerable.Range(0, rng.Next(3, 6)).Select(i => s.Join($"C{i}")).ToList();
        var lockedBy = new Dictionary<string, SimClient>(StringComparer.Ordinal);   // a few "locked" elements: refusals
        s.Refuse = (c, op) => op is SetFieldOp sf && lockedBy.TryGetValue(sf.ElementId, out var holder) && !ReferenceEquals(holder, c) ? "locked" : null;
        var steps = rng.Next(20, 60);
        for (var step = 0; step < steps; step++)
        {
            var c = clients[rng.Next(clients.Count)];
            switch (rng.Next(10))
            {
                case < 6:
                    RandomAction(rng, c, lockedBy);
                    stats[0]++;
                    break;
                case < 9:
                    clients[rng.Next(clients.Count)].Deliver(rng.Next(1, 6));
                    break;
                default:
                    if (rng.Next(4) == 0) s.SendSnapshot(c);
                    else c.Deliver();
                    break;
            }
        }
        s.AssertConverged();
        try { AssertNoDangling(s.Master); }
        catch when (Environment.GetEnvironmentVariable("MERIDIAN_CONVERGENCE_DUMP") is { Length: > 0 } dir)
        {
            File.WriteAllLines(Path.Combine(dir, "trace.txt"), s.Trace);
            File.WriteAllText(Path.Combine(dir, "server.json"), s.Json);
            throw;
        }
        foreach (var c in clients)
        {
            stats[1] += c.Sync.Rebases;
            stats[2] += c.Refusals.Count;
            stats[3] += c.Dropped.Count;
        }
    }

    private static void RandomAction(Random rng, SimClient c, Dictionary<string, SimClient> lockedBy)
    {
        var m = c.Model;
        var all = m.AllElements.Where(e => e.Owner != null).ToList();   // dictionary order: deterministic for a seed (ids are not)
        var packages = m.OfKind(ElementKind.Package).ToList();
        var blocks = m.OfKind(ElementKind.Block).ToList();
        var diagram = m.Diagrams.FirstOrDefault();
        T Pick<T>(IReadOnlyList<T> xs) => xs[rng.Next(xs.Count)];
        try
        {
            switch (rng.Next(19))
            {
                case 0 or 1 when all.Count > 0:
                    m.Set(Pick(all), rng.Next(2) == 0 ? FieldKeys.Name : FieldKeys.Documentation, $"{c.Name}-{rng.Next(1000)}");
                    break;
                case 2 when packages.Count > 0:
                {
                    var owner = Pick(packages);
                    m.Create(owner, rng.Next(3) == 0 ? ElementKind.Package : ElementKind.Block, $"{c.Name} new {rng.Next(1000)}", rng.Next(-1, owner.OwnedElements.Count + 1));
                    break;
                }
                case 3 when all.Count > 0:
                    m.Delete(Pick(all));
                    break;
                case 4 or 5 when all.Count > 0 && packages.Count > 0:
                {
                    var e = Pick(all);
                    var to = rng.Next(4) == 0 ? m.Root : Pick(packages);
                    if (Metamodel.CanOwn(to.Kind, e.Kind)) m.Move(e, to, rng.Next(-1, to.OwnedElements.Count + 1));
                    break;
                }
                case 6 when diagram != null && blocks.Count > 0:
                    m.AddNode(diagram, Pick(blocks), new Bounds(rng.Next(500), rng.Next(500), 100, 60), showRelationships: rng.Next(2) == 0);
                    break;
                case 7 when diagram is { Nodes.Count: > 0 }:
                {
                    var node = Pick(diagram.Nodes);
                    m.SetBounds(diagram, new Dictionary<DiagramNode, Bounds> { [node] = node.Bounds.Offset(rng.Next(-50, 51), rng.Next(-50, 51)) });
                    break;
                }
                case 8 when diagram is { Nodes.Count: > 0 }:
                    m.RemoveFromDiagram(diagram, [Pick(diagram.Nodes)]);
                    break;
                case 9 when blocks.Count > 1:
                {
                    var r = m.Connect(ElementKind.Dependency, Pick(blocks), Pick(blocks));
                    if (diagram != null && rng.Next(2) == 0) m.AddEdge(diagram, r);
                    break;
                }
                case 10 when diagram is { Edges.Count: > 0 }:
                    m.SetWaypoints(diagram, Pick(diagram.Edges), [new Pt(rng.Next(100), rng.Next(100))]);
                    break;
                case 11 when all.Count > 1:
                    using (m.History.Transaction("Several"))
                    {
                        m.Set(Pick(all), FieldKeys.Documentation, $"{c.Name} multi");
                        var target = Pick(all);
                        if (m.Contains(target)) m.Delete(target);
                        if (packages.Count > 0 && m.Contains(packages[0])) m.Create(packages[0], ElementKind.Block, $"{c.Name} in batch");
                    }
                    break;
                case 12 when m.History.CanUndo:
                case 13 when m.History.CanUndo:
                    m.History.Undo();
                    break;
                case 14 when m.History.CanRedo:
                    m.History.Redo();
                    break;
                case 15 when all.Count > 0:
                {
                    var e = Pick(all);
                    if (rng.Next(2) == 0) lockedBy[e.Id] = c;
                    else lockedBy.Remove(e.Id);
                    break;
                }
                case 16 when diagram is { Nodes.Count: > 0 }:
                    m.SetNodeStyle(diagram, Pick(diagram.Nodes), "fill", rng.Next(3) == 0 ? null : $"#{rng.Next(0x1000000):x6}");
                    break;
                case 17 when blocks.Count > 0 && packages.Count > 0:
                    m.Duplicate([Pick(blocks)], Pick(packages));
                    break;
                case 18 when diagram is { Nodes.Count: > 0 } && all.Count > 0:   // a shape nested in another one
                    m.AddNode(diagram, Pick(all), new Bounds(rng.Next(500), rng.Next(500), 40, 40), showRelationships: false, parent: Pick(diagram.Nodes));
                    break;
            }
        }
        catch (InvalidOperationException) { }   // a move into itself, an owner that can't own the kind …
    }

    /// <summary>Every reference in the model resolves inside it.</summary>
    internal static void AssertNoDangling(ModelRepository m)
    {
        foreach (var r in m.Relationships)
        {
            Assert.True(m.Contains(r.Source), $"{r} has a source outside the model");
            Assert.True(m.Contains(r.Target), $"{r} has a target outside the model");
        }
        foreach (var d in m.Diagrams)
        {
            foreach (var n in d.Nodes) Assert.True(m.Contains(n.Element), $"a shape on {d.Name} shows {n.Element}, which is gone");
            foreach (var x in d.Edges)
            {
                Assert.True(m.Contains(x.Relationship), $"a path on {d.Name} shows {x.Relationship}, which is gone");
                Assert.Contains(x.Source, d.Nodes);
                Assert.Contains(x.Target, d.Nodes);
            }
        }
        Assert.Equal(m.Count, m.AllElements.Select(e => e.Id).Distinct().Count());
    }
}
