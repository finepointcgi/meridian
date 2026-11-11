using Meridian.Core.Analysis;
using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Data.History;
using Meridian.Serialization;
using Meridian.Serialization.History;
using Microsoft.Data.Sqlite;

namespace Meridian.Tests;

/// <summary>
/// Element history: the flat version reader, the per-commit change computation, the change index (building, backfill, merge
/// attribution), field diffs (word diff, references by name), "restore this field" and what viewers with hidden packages see.
/// </summary>
public sealed class ElementHistoryTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-history-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private UserAccount _ann = default!;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _ann = await _db.CreateUserAsync("ann", "Ann", "correct horse");
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private static Element Named(ModelRepository m, string name) => m.AllElements.First(e => e.Name == name);

    // ================================================================= reading versions

    [Fact]
    public void Flat_Reader_Matches_The_Model_It_Was_Written_From()
    {
        var m = SampleModels.Vehicle();
        var json = ProjectFile.Serialize(m);
        var parsed = FlatModel.Parse(json);
        var from = FlatModel.From(ProjectFile.Deserialize(json));
        Assert.Equal(m.Count, parsed.Count);
        foreach (var e in m.AllElements)
        {
            var a = parsed.Find(e.Id)!;
            var b = from.Find(e.Id)!;
            Assert.Equal((e.Kind.ToString(), e.Owner?.Id, e.Name), (a.Kind, a.OwnerId, a.Name));
            Assert.Equal(b.FieldKeys, a.FieldKeys);
            Assert.Empty(ElementChangeComputer.ChangedKeys(a, b));   // same hashes and layout either way
            if (e is Diagram d) Assert.Equal(d.Nodes.Select(n => n.Element.Id).ToHashSet(), a.Shown);
        }
        Assert.Empty(ElementChangeComputer.Compute(parsed, from));
    }

    [Fact]
    public void Flat_Reader_Ignores_Key_Order_As_PostgreSQL_Stores_It()
    {
        // jsonb orders keys by length, then bytes: "id", "kind", "edges", "nodes", "fields", "children"... — and escapes differently.
        var m = SampleModels.Vehicle();
        var json = ProjectFile.Serialize(m);
        var reordered = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        Reorder(reordered);
        var a = FlatModel.Parse(json);
        var b = FlatModel.Parse(reordered.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default }));
        Assert.Empty(ElementChangeComputer.Compute(a, b));

        static void Reorder(System.Text.Json.Nodes.JsonNode n)
        {
            if (n is System.Text.Json.Nodes.JsonObject o)
            {
                var props = o.OrderBy(p => p.Key.Length).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
                o.Clear();
                foreach (var (k, v) in props) { if (v != null) Reorder(v); o[k] = v; }
            }
            else if (n is System.Text.Json.Nodes.JsonArray arr) foreach (var x in arr) if (x != null) Reorder(x);
        }
    }

    // ================================================================= what a commit changed

    [Fact]
    public void Changes_Of_A_Commit_Cover_Fields_Moves_Ends_Layout_And_Diagrams()
    {
        var m = SampleModels.Vehicle();
        var before = FlatModel.Parse(ProjectFile.Serialize(m));
        var original = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        var engine = Named(m, "Engine");
        var diagram = m.Diagrams.First(d => d.Nodes.Any(n => n.Element == engine));
        m.Rename(engine, "Motor");
        m.Set(engine, FieldKeys.Documentation, "Combustion");
        var pkg = m.Create(m.Root, ElementKind.Package, "Moved here");
        var other = m.AllElements.First(e => e.Kind == ElementKind.Block && e != engine && e.Owner != null);
        m.Move(other, pkg);
        var added = m.Create(pkg, ElementKind.Block, "Brand new");
        m.AddNode(diagram, added, new Bounds(900, 900, 100, 60));
        m.RemoveFromDiagram(diagram, [diagram.NodeFor(engine)!]);
        var after = FlatModel.Parse(ProjectFile.Serialize(m));

        var rows = ElementChangeComputer.Compute(before, after);
        var engineRow = rows.Single(r => r.ElementId == engine.Id && r.Kind == ElementChangeKind.Changed);
        Assert.Equal([FieldKeys.Documentation, FieldKeys.Name], engineRow.Fields.Order(StringComparer.Ordinal));
        Assert.Equal("Motor", engineRow.Label);
        Assert.Contains(rows, r => r.ElementId == other.Id && r.Kind == ElementChangeKind.Moved && r.OwnerId == pkg.Id);
        Assert.Contains(rows, r => r.ElementId == added.Id && r.Kind == ElementChangeKind.Added);
        Assert.Contains(rows, r => r.ElementId == added.Id && r.Kind == ElementChangeKind.AddedToDiagram && r.DiagramId == diagram.Id && r.DiagramName == diagram.Name);
        Assert.Contains(rows, r => r.ElementId == engine.Id && r.Kind == ElementChangeKind.RemovedFromDiagram && r.DiagramId == diagram.Id);
        Assert.Contains(rows, r => r.ElementId == diagram.Id && r.Fields.Contains(HistoryKeys.Layout));

        // The same elements as ModelDiff reports (the index is the per-element view of the same comparison).
        var diff = ModelDiff.Compare(original, m);
        Assert.Equal(diff.Select(d => d.Id).ToHashSet(),
                     rows.Where(r => r.Kind is not (ElementChangeKind.AddedToDiagram or ElementChangeKind.RemovedFromDiagram)).Select(r => r.ElementId).ToHashSet());
        Assert.Empty(ElementChangeComputer.Compute(null, after));   // the first version has no rows
    }

    // ================================================================= the index

    private async Task<(Guid Project, ModelRepository Model)> ProjectAsync()
    {
        var m = SampleModels.Vehicle();
        var project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(m), _ann.Id);
        return (project, m);
    }

    private Task<(long Id, int Number)> Commit(Guid project, ModelRepository m, string message, Guid? branch = null, Guid? author = null) =>
        _db.CommitBranchVersionAsync(project, branch, message, ProjectFile.Serialize(m), author ?? _ann.Id);

    [Fact]
    public async Task Index_Is_Built_Lazily_Resumes_And_Answers_Element_History()
    {
        var (project, m) = await ProjectAsync();
        var engine = Named(m, "Engine");
        await Commit(project, m, "Start");
        m.Rename(engine, "Motor");
        await Commit(project, m, "Rename to Motor");
        m.Set(engine, FieldKeys.Documentation, "Petrol engine");
        await Commit(project, m, "Document");
        m.Rename(engine, "Engine");
        var v4 = await Commit(project, m, "Rename back");

        var indexer = ElementHistoryIndexer.For(_db);
        Assert.All(await _db.ListHistoryVersionsAsync(project), v => Assert.False(v.Indexed));

        // More pending than the inline limit: indexing goes to the background, with progress.
        var progress = await indexer.EnsureIndexedAsync(project, inlineLimit: 1);
        Assert.Equal(4, progress.Total);
        await indexer.WhenIdleAsync(project);
        Assert.True(indexer.Progress(project).Complete);
        var versions = await _db.ListHistoryVersionsAsync(project);
        Assert.All(versions, v => Assert.True(v.Indexed));
        Assert.Null(versions[0].ParentVersionId);
        Assert.Equal(versions[0].Id, versions[1].ParentVersionId);

        var history = await ElementHistoryService.LoadAsync(_db, project, null, engine, m, AccessView.Full);
        Assert.Equal([4, 3, 2, 1], history.Entries.Select(e => e.Version.Number));
        Assert.Equal(["Rename back", "Document", "Rename to Motor", "Start"], history.Entries.Select(e => e.Version.Message));
        Assert.Equal([FieldKeys.Name], history.Entries[0].Fields);
        Assert.Equal([FieldKeys.Documentation], history.Entries[1].Fields);
        Assert.True(history.Entries[^1].PresentSinceFirst);
        Assert.All(history.Entries, e => Assert.Equal("Ann", e.Version.Author));

        // Resumable: a version indexed once is never written again; a reset rebuilds everything the same.
        Assert.False(await _db.SaveElementChangesAsync(project, v4.Id, null, HistoryVersionKind.Commit, null, []));
        await _db.ResetElementChangesAsync(project);
        Assert.All(await _db.ListHistoryVersionsAsync(project), v => Assert.False(v.Indexed));
        await indexer.IndexAllAsync(project);
        var again = await ElementHistoryService.LoadAsync(_db, project, null, engine, m, AccessView.Full);
        Assert.Equal(history.Entries.Select(e => (e.Version.Number, e.Change, string.Join(",", e.Fields))),
                     again.Entries.Select(e => (e.Version.Number, e.Change, string.Join(",", e.Fields))));

        // A new commit is indexed right after it is made (in the background), from the JSON it was given.
        m.Set(engine, FieldKeys.Documentation, "Electric");
        var json = ProjectFile.Serialize(m);
        var v5 = await _db.CommitBranchVersionAsync(project, null, "Electric", json, _ann.Id);
        indexer.VersionCommitted(project, v5.Id, json);
        await indexer.WhenIdleAsync(project);
        Assert.True((await _db.ListHistoryVersionsAsync(project)).Single(v => v.Id == v5.Id).Indexed);
        Assert.True(indexer.LastVersionCost > TimeSpan.Zero);
    }

    [Fact]
    public async Task Merges_Are_Attributed_To_The_Merge_Commit_With_Their_Source_Branch()
    {
        var (project, m) = await ProjectAsync();
        var engine = Named(m, "Engine");
        var v1 = await Commit(project, m, "Start");
        var branch = await _db.CreateBranchAsync(project, "feature", ProjectFile.Serialize(m), ProjectFile.Serialize(m), v1.Id, _ann.Id);

        var feature = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        feature.Set(Named(feature, "Engine"), FieldKeys.Documentation, "From the branch");
        await Commit(project, feature, "Branch work", branch);
        var other = Named(m, "Vehicle");
        m.Set(other, FieldKeys.Documentation, "Main work");
        await Commit(project, m, "Main work");

        m.Set(engine, FieldKeys.Documentation, "From the branch");   // the merged result
        var merge = await Commit(project, m, "Bring the feature in");
        await _db.SetVersionKindAsync(project, merge.Id, HistoryVersionKind.Merge, "feature");   // what CompleteMergeAsync records
        // A merge whose kind nobody recorded is recognised by its default message.
        m.Set(engine, FieldKeys.Documentation, "Again");
        await Commit(project, m, "Merge “hotfix” into “main”");

        await ElementHistoryIndexer.For(_db).IndexAllAsync(project);
        var main = await ElementHistoryService.LoadAsync(_db, project, null, engine, m, AccessView.Full);
        var mergeEntry = main.Entries.Single(e => e.Version.Id == merge.Id);
        Assert.Equal((HistoryVersionKind.Merge, "feature"), (mergeEntry.Version.Kind, mergeEntry.Version.MergeSource));
        Assert.Equal([FieldKeys.Documentation], mergeEntry.Fields);
        Assert.DoesNotContain(main.Entries, e => e.Version.Message == "Branch work");   // on main, the merge commit carries it
        Assert.Equal((HistoryVersionKind.Merge, "hotfix"), (main.Entries[0].Version.Kind, main.Entries[0].Version.MergeSource));

        // On the branch: its own commit, then main's history before the branch.
        var onBranch = await ElementHistoryService.LoadAsync(_db, project, branch, Named(feature, "Engine"), feature, AccessView.Full);
        Assert.Equal(["Branch work", "Start"], onBranch.Entries.Select(e => e.Version.Message));
        Assert.Equal("feature", onBranch.Entries[0].Version.BranchName);

        // All branches: everything that touched the element anywhere.
        var all = await ElementHistoryService.LoadAsync(_db, project, null, engine, m, AccessView.Full, allBranches: true);
        Assert.Contains(all.Entries, e => e.Version.Message == "Branch work");
        Assert.Contains(all.Entries, e => e.Version.Id == merge.Id);

        // System and code-sync commits are labelled.
        Assert.Equal(HistoryVersionKind.CodeSync, ElementHistoryIndexer.Classify(main.Entries[0].Version with { Message = "Code sync: abc1234 Fix", KindRecorded = false }).Kind);
        Assert.Equal(HistoryVersionKind.System, ElementHistoryIndexer.Classify(main.Entries[0].Version with { Message = "Library update", AuthorId = null }).Kind);
    }

    [Fact]
    public async Task Connected_Relationships_And_Diagrams_Are_Optional_And_Cheap()
    {
        var (project, m) = await ProjectAsync();
        var engine = Named(m, "Engine");
        await Commit(project, m, "Start");
        var req = m.Create(m.Root, ElementKind.Requirement, "Torque");
        var satisfy = m.Connect(ElementKind.Satisfy, engine, req);
        await Commit(project, m, "Trace");
        m.Delete(satisfy);
        await Commit(project, m, "Untrace");

        await ElementHistoryIndexer.For(_db).IndexAllAsync(project);
        var plain = await ElementHistoryService.LoadAsync(_db, project, null, engine, m, AccessView.Full);
        Assert.DoesNotContain(plain.Entries, e => e.IsConnected);
        var connected = await ElementHistoryService.LoadAsync(_db, project, null, engine, m, AccessView.Full, includeRelationships: true);
        Assert.Equal([ElementChangeKind.Removed, ElementChangeKind.Added], connected.Entries.Where(e => e.IsConnected).Select(e => e.Change));
        Assert.All(connected.Entries.Where(e => e.IsConnected), e => Assert.Equal("«satisfy» Engine → Torque", e.Label));
    }

    // ================================================================= diffs and restore

    [Fact]
    public async Task Diffs_Show_Words_References_By_Name_And_Restore_One_Field_With_Undo()
    {
        var (project, m) = await ProjectAsync();
        var engine = Named(m, "Engine");
        var part = m.Create(Named(m, "Vehicle"), ElementKind.Property, "engine");
        m.Set(engine, FieldKeys.Documentation, "The engine drives the wheels");
        await Commit(project, m, "Start");
        m.Set(engine, FieldKeys.Documentation, "The electric engine drives both wheels");
        m.Set(part, FieldKeys.Type, engine.Id);
        m.Set(engine, ReqFields.LibraryChange, new LibraryChangeMark("_x", false, Guid.NewGuid(), "v:1", "v:2").Encode());
        await Commit(project, m, "Edit");
        await ElementHistoryIndexer.For(_db).IndexAllAsync(project);

        var reader = new VersionReader(_db, project, AccessView.Full);
        var history = await ElementHistoryService.LoadAsync(_db, project, null, engine, m, AccessView.Full);
        var diff = await ElementHistoryService.DiffAsync(reader, history.Entries[0]);
        var doc = diff.Rows.Single(r => r.Key == FieldKeys.Documentation);
        Assert.Equal([(WordOp.Same, "The "), (WordOp.Added, "electric "), (WordOp.Same, "engine drives "), (WordOp.Removed, "the"), (WordOp.Added, "both"), (WordOp.Same, " wheels")],
                     doc.Words!.Select(w => (w.Op, w.Text)));
        var mark = diff.Rows.Single(r => r.Key == ReqFields.LibraryChange);
        Assert.Equal(("Library suspect mark", "Suspect: a used-project element changed", false), (mark.Label, mark.AfterText, mark.Restorable));

        var partHistory = await ElementHistoryService.LoadAsync(_db, project, null, part, m, AccessView.Full);
        var typeRow = (await ElementHistoryService.DiffAsync(reader, partHistory.Entries[0])).Rows.Single(r => r.Key == FieldKeys.Type);
        Assert.Equal(("∅", "Engine", engine.Id), (typeRow.BeforeText, typeRow.AfterText, typeRow.After));

        // Restore the documentation to v1: one undoable change.
        var first = (await ElementHistoryService.CompareWithCurrentAsync(reader, history.Entries[^1].Version.Id, engine.Id, m)).Rows.Single(r => r.Key == FieldKeys.Documentation);
        FieldDiff.Restore(m, engine, FieldKeys.Documentation, first.Before);
        Assert.Equal("The engine drives the wheels", engine.Documentation);
        m.History.Undo();
        Assert.Equal("The electric engine drives both wheels", engine.Documentation);

        // A reference to an element that no longer exists can't be restored; pseudo-fields and system marks never can.
        var v2 = await reader.LoadAsync(history.Entries[0].Version.Id, part.Id);
        m.Delete(engine);
        Assert.Throws<InvalidOperationException>(() => FieldDiff.Restore(m, part, FieldKeys.Type, engine.Id, v2));
        Assert.Throws<InvalidOperationException>(() => FieldDiff.Restore(m, part, HistoryKeys.Owner, null));
        Assert.Throws<InvalidOperationException>(() => FieldDiff.Restore(m, part, ReqFields.LibraryChange, null));
    }

    [Fact]
    public async Task Uncommitted_Changes_Compare_The_Live_Element_With_The_Last_Commit()
    {
        var (project, m) = await ProjectAsync();
        var engine = Named(m, "Engine");
        await Commit(project, m, "Start");
        await ElementHistoryIndexer.For(_db).IndexAllAsync(project);
        var reader = new VersionReader(_db, project, AccessView.Full);
        var history = await ElementHistoryService.LoadAsync(_db, project, null, engine, m, AccessView.Full);
        Assert.Null(await ElementHistoryService.UncommittedAsync(reader, history.Head, engine, m));

        m.Rename(engine, "Motor");
        var change = await ElementHistoryService.UncommittedAsync(reader, history.Head, engine, m);
        Assert.Equal(ElementChangeKind.Changed, change!.Change);
        Assert.Equal([FieldKeys.Name], change.Fields);
        var fresh = m.Create(m.Root, ElementKind.Block, "Fresh");
        Assert.Equal(ElementChangeKind.Added, (await ElementHistoryService.UncommittedAsync(reader, history.Head, fresh, m))!.Change);
    }

    // ================================================================= permissions

    [Fact]
    public async Task Viewers_With_Hidden_Packages_See_No_Hidden_History_Or_Values()
    {
        var m = new ModelRepository("Plant");
        var pub = m.Create(m.Root, ElementKind.Package, "Public");
        var secret = m.Create(m.Root, ElementKind.Package, "Secret");
        var block = m.Create(pub, ElementKind.Block, "Pub");
        var reactor = m.Create(secret, ElementKind.Block, "ReactorCoreXJ9");
        var part = m.Create(block, ElementKind.Property, "reactor");
        var project = await _db.CreateProjectAsync("Plant", ProjectFile.Serialize(m), _ann.Id);
        await Commit(project, m, "Start");
        m.Set(part, FieldKeys.Type, reactor.Id);
        m.Set(reactor, FieldKeys.Documentation, "TOP SECRET");
        var assoc = m.Connect(ElementKind.Association, block, reactor, pub);
        m.Move(block, secret);   // was in Secret for a while...
        await Commit(project, m, "Wire up");
        m.Move(block, pub);      // ...and back
        await Commit(project, m, "Move back");
        await ElementHistoryIndexer.For(_db).IndexAllAsync(project);

        var bob = Guid.NewGuid();
        var view = AccessView.For([new(secret.Id, PrincipalKind.User, bob, PackageAccess.None)], bob, [], PackageAccess.Edit, PackageAccess.Manage);
        var visible = ProjectFile.Deserialize(ProjectFile.Serialize(m));
        ModelRedactor.Redact(visible, view);

        // Nothing for an element the viewer can't see.
        Assert.Empty((await ElementHistoryService.LoadAsync(_db, project, null, reactor, m, view)).Entries);
        // The block: the commit that moved it out of the hidden package is shown; the one that put it there isn't.
        var vBlock = visible.Find(block.Id)!;
        var history = await ElementHistoryService.LoadAsync(_db, project, null, vBlock, visible, view, includeRelationships: true);
        Assert.Equal(["Move back", "Start"], history.Entries.Select(e => e.Version.Message));
        Assert.DoesNotContain(history.Entries, e => e.ElementId == assoc.Id);   // a relationship to a hidden element
        Assert.DoesNotContain(history.Entries, e => e.Label.Contains("Reactor"));

        // Values come from versions redacted for the viewer: the part's type (a hidden block) is not there.
        var partHistory = await ElementHistoryService.LoadAsync(_db, project, null, visible.Find(part.Id)!, visible, view);
        var reader = new VersionReader(_db, project, view);
        foreach (var e in partHistory.Entries)
        {
            var diff = await ElementHistoryService.DiffAsync(reader, e);
            Assert.DoesNotContain(diff.Rows, r => (r.After ?? "").Contains(reactor.Id) || r.AfterText.Contains("Reactor"));
            Assert.Null(diff.After?.Find(reactor.Id));
        }
        // The owner sees everything.
        Assert.Contains((await ElementHistoryService.LoadAsync(_db, project, null, block, m, AccessView.Full)).Entries, e => e.Version.Message == "Wire up");
    }
}
