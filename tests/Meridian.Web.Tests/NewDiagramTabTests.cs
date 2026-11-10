using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Web.Services;

namespace Meridian.Web.Tests;

/// <summary>A diagram created here opens as a tab, whatever created it; diagrams from collaborators and bulk imports don't flood the tab strip.</summary>
public class NewDiagramTabTests
{
    [Fact]
    public void A_Diagram_Created_Directly_On_The_Model_Opens_As_The_Active_Tab()
    {
        var ws = new Workspace();
        ws.UseLocalModel(SampleModels.Vehicle());
        var before = ws.OpenDiagrams.Count;
        var structure = ws.Model.Root.OwnedElements.Single(e => e.Name == "Structure");

        Diagram d = null!;
        OnUiThread(() => d = ws.Model.CreateDiagram(structure, DiagramType.BlockDefinition, "Made by a script"));

        Assert.Equal(before + 1, ws.OpenDiagrams.Count);
        Assert.Same(d, ws.Active?.Diagram);
    }

    [Fact]
    public void Opening_It_Again_Does_Not_Duplicate_The_Tab()
    {
        var ws = new Workspace();
        ws.UseLocalModel(SampleModels.Vehicle());
        var structure = ws.Model.Root.OwnedElements.Single(e => e.Name == "Structure");
        OnUiThread(() => ws.Open(ws.Model.CreateDiagram(structure, DiagramType.BlockDefinition, "Twice")));
        var d = ws.Model.Diagrams.Single(x => x.Name == "Twice");
        Assert.Single(ws.OpenDiagrams, c => ReferenceEquals(c.Diagram, d));
    }

    [Fact]
    public void A_Diagram_From_A_Collaborator_Does_Not_Open()
    {
        var mine = new Workspace();
        mine.UseLocalModel(SampleModels.Vehicle());
        var before = mine.OpenDiagrams.Count;

        // Replicate an operation made elsewhere: an identical model creates a diagram, and its op is applied remotely here.
        var theirs = Meridian.Serialization.ProjectFile.Deserialize(Meridian.Serialization.ProjectFile.Serialize(mine.Model));
        ModelOp? op = null;
        theirs.OpEmitted += o => op ??= o;
        theirs.CreateDiagram(theirs.Root.OwnedElements.Single(e => e.Name == "Structure"), DiagramType.BlockDefinition, "Theirs");
        Assert.NotNull(op);
        OnUiThread(() => mine.Model.ApplyRemote(op!));

        Assert.Contains(mine.Model.Diagrams, d => d.Name == "Theirs");
        Assert.Equal(before, mine.OpenDiagrams.Count);
    }

    [Fact]
    public void Many_Diagrams_Created_At_Once_Open_Only_The_First()
    {
        var ws = new Workspace();
        ws.UseLocalModel(SampleModels.Vehicle());
        var before = ws.OpenDiagrams.Count;
        var structure = ws.Model.Root.OwnedElements.Single(e => e.Name == "Structure");

        // Without a UI thread each creation flushes on its own, so batch the way an import does: inside one action.
        var ctx = new QueueingContext();
        var old = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try
        {
            for (var i = 0; i < Workspace.AutoOpenLimit + 3; i++) ws.Model.CreateDiagram(structure, DiagramType.BlockDefinition, $"Imported {i}");
        }
        finally { SynchronizationContext.SetSynchronizationContext(old); }
        ctx.RunAll();

        Assert.Equal(before + 1, ws.OpenDiagrams.Count);
        Assert.Equal("Imported 0", ws.Active?.Diagram.Name);
    }

    /// <summary>Runs <paramref name="action"/> as one UI action, then the work it posted (deferred tab opening).</summary>
    private static void OnUiThread(Action action)
    {
        var ctx = new QueueingContext();
        var old = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(ctx);
        try { action(); }
        finally { SynchronizationContext.SetSynchronizationContext(old); }
        ctx.RunAll();
    }

    /// <summary>A minimal UI-thread stand-in: posted work runs when <see cref="RunAll"/> is called.</summary>
    private sealed class QueueingContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback, object?)> _work = new();
        public override void Post(SendOrPostCallback d, object? state) => _work.Enqueue((d, state));

        public void RunAll()
        {
            var old = Current;
            SetSynchronizationContext(this);
            try { while (_work.Count > 0) { var (d, s) = _work.Dequeue(); d(s); } }
            finally { SetSynchronizationContext(old); }
        }
    }
}
