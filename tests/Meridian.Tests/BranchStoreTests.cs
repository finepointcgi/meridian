using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Branches, branch commits and baselines in the SQLite store (same schema as PostgreSQL).</summary>
public sealed class BranchStoreTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-branch-{Guid.NewGuid():N}.db");
    private SqliteStore _store = default!;

    public async Task InitializeAsync()
    {
        _store = new SqliteStore(_path);
        await _store.MigrateAsync();
        await _store.MigrateAsync(); // idempotent
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Branches_Commits_And_Baselines_Work_End_To_End()
    {
        var ann = await _store.CreateUserAsync("ann", "Ann", "correct horse");
        var json = ProjectFile.Serialize(SampleModels.Vehicle());
        var pid = await _store.CreateProjectAsync("Vehicle", json, ann.Id);

        // Existing single-branch behaviour: main versions.
        var v1 = await _store.CommitBranchVersionAsync(pid, null, "Initial", json, ann.Id);
        Assert.Equal(1, v1.Number);
        Assert.Equal(2, await _store.CommitVersionAsync(pid, "Legacy commit", json, ann.Id));

        var bid = await _store.CreateBranchAsync(pid, " pdr-updates ", json, json, v1.Id, ann.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateBranchAsync(pid, "pdr-updates", json, json, null, ann.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateBranchAsync(pid, "Main", json, json, null, ann.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateBranchAsync(pid, "bad;name", json, json, null, ann.Id));

        var branch = Assert.Single(await _store.ListBranchesAsync(pid));
        Assert.Equal("pdr-updates", branch.Name);
        Assert.Equal(1, branch.BaseVersionNumber);
        Assert.Equal("Ann", branch.CreatedBy);
        Assert.Equal(bid, (await _store.GetBranchAsync(bid))!.Id);

        // Branch head is separate from main.
        var edited = ProjectFile.Deserialize(json);
        edited.Rename(edited.OfKind(ElementKind.Block).First(), "Renamed on branch");
        var editedJson = ProjectFile.Serialize(edited);
        Assert.Equal(1, await _store.SaveBranchAsync(bid, editedJson, ann.Id));
        var models = (await _store.LoadBranchAsync(bid))!;
        Assert.Equal(pid, models.ProjectId);
        Assert.Contains("Renamed on branch", models.HeadJson);
        Assert.DoesNotContain("Renamed on branch", models.BaseJson);
        Assert.DoesNotContain("Renamed on branch", (await _store.LoadProjectAsync(pid))!.Value.ModelJson);

        // Commits on the branch are numbered with the project but listed per line.
        var v3 = await _store.CommitBranchVersionAsync(pid, bid, "Branch work", editedJson, ann.Id);
        Assert.Equal(3, v3.Number);
        Assert.Equal([3], (await _store.ListBranchVersionsAsync(pid, bid)).Select(v => v.Number));
        Assert.Equal([2, 1], (await _store.ListBranchVersionsAsync(pid, null)).Select(v => v.Number));
        Assert.Equal(3, (await _store.ListVersionsAsync(pid)).Count);

        await _store.SetBranchBaseAsync(bid, editedJson, v3.Id);
        Assert.Equal(v3.Id, (await _store.LoadBranchAsync(bid))!.BaseVersionId);

        // Baselines: immutable named tags on versions.
        var b1 = await _store.CreateBaselineAsync(pid, v1.Id, "PDR 1.0", "Preliminary design review", ann.Id);
        await _store.CreateBaselineAsync(pid, v3.Id, "CDR candidate", "", ann.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateBaselineAsync(pid, v3.Id, "PDR 1.0", "", ann.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CreateBaselineAsync(Guid.NewGuid(), v3.Id, "Other", "", ann.Id));
        var baselines = await _store.ListBaselinesAsync(pid);
        Assert.Equal(["CDR candidate", "PDR 1.0"], baselines.Select(b => b.Name));
        Assert.Equal("pdr-updates", baselines[0].BranchName);
        Assert.Equal("main", baselines[1].BranchName);
        Assert.Equal("Preliminary design review", baselines[1].Description);

        // Compare two baselines.
        var diff = ModelDiff.Compare(ProjectFile.Deserialize((await _store.LoadVersionAsync(pid, baselines[1].VersionId))!),
                                     ProjectFile.Deserialize((await _store.LoadVersionAsync(pid, baselines[0].VersionId))!));
        Assert.Contains(diff, d => d.Kind == DiffKind.Changed && d.Name == "Renamed on branch");

        // Deleting a branch keeps its versions (and baselines) but hides them from main.
        await _store.DeleteBranchAsync(bid);
        Assert.Empty(await _store.ListBranchesAsync(pid));
        Assert.Equal(2, (await _store.ListBaselinesAsync(pid)).Count);
        Assert.Equal(2, (await _store.ListBranchVersionsAsync(pid, null)).Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _store.CommitBranchVersionAsync(pid, bid, "gone", json, ann.Id));

        await _store.DeleteBaselineAsync(b1);
        Assert.Single(await _store.ListBaselinesAsync(pid));

        // Deleting the project cascades everything.
        await _store.CreateBranchAsync(pid, "temp", json, json, null, null);
        await _store.DeleteProjectAsync(pid);
        Assert.Empty(await _store.ListBranchesAsync(pid));
        Assert.Empty(await _store.ListBaselinesAsync(pid));
    }

    [Fact]
    public void Diff_Csv_Lists_Field_Changes_And_Guards_Formulas()
    {
        var a = SampleModels.Vehicle();
        var b = ProjectFile.Deserialize(ProjectFile.Serialize(a));
        var block = b.OfKind(ElementKind.Block).First();
        b.Set(block, FieldKeys.Documentation, "=HYPERLINK(\"x\")");
        b.Create(b.Root, ElementKind.Package, "New, \"quoted\" pkg");
        var csv = DiffCsv.Export(ModelDiff.Compare(a, b), "PDR 1.0", "Current");
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("PDR 1.0", lines[0]);
        Assert.StartsWith("Change,Element Kind,Element", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("Added,") && l.Contains("\"New, \"\"quoted\"\" pkg\""));
        Assert.Contains(lines, l => l.StartsWith("Changed,") && l.Contains("documentation") && l.Contains("'=HYPERLINK"));
    }
}
