using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Serialization.DiagramFiles;
using Meridian.Web.CodeSync;
using Meridian.Web.CodeSync.Hosts;
using Meridian.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meridian.Web.Tests;

/// <summary>
/// Diagrams as code: Mermaid and draw.io files in a synced repository become diagrams on the review branch, a push only
/// re-imports the files it changed, and a deleted file takes its diagrams with it. Against a real SQLite store.
/// </summary>
public sealed class CodeSyncDiagramFilesTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"meridian-codesync-diagrams-{Guid.NewGuid():N}.db");
    private SqliteStore _db = default!;
    private CollaborationHub _hub = default!;
    private Guid _project;
    private readonly Guid _connection = Guid.NewGuid();
    private int _commits;
    private UserAccount _ann = default!;

    private const string Flow = "flowchart LR\n  Start --> Check{Ok?}\n  Check --> Done";
    private const string Board = """
        <mxfile><diagram name="Pipeline"><mxGraphModel><root><mxCell id="0"/><mxCell id="1" parent="0"/>
        <mxCell id="a" value="Build" style="rounded=1;" vertex="1" parent="1"><mxGeometry x="0" y="0" width="80" height="40" as="geometry"/></mxCell>
        <mxCell id="b" value="Deploy" style="rounded=1;" vertex="1" parent="1"><mxGeometry x="200" y="0" width="80" height="40" as="geometry"/></mxCell>
        <mxCell id="e" style="endArrow=classic;" edge="1" parent="1" source="a" target="b"><mxGeometry relative="1" as="geometry"/></mxCell>
        </root></mxGraphModel></diagram></mxfile>
        """;

    public async Task InitializeAsync()
    {
        _db = new SqliteStore(_path);
        await _db.MigrateAsync();
        _hub = new CollaborationHub(_db, NullLogger<CollaborationHub>.Instance);
        _ann = await _db.CreateUserAsync("ann", "Ann", "correct horse");
        _project = await _db.CreateProjectAsync("Vehicle", ProjectFile.Serialize(SampleModels.Vehicle()), _ann.Id);
        await Connect(diagramFiles: true);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" }) if (File.Exists(f)) File.Delete(f);
        return Task.CompletedTask;
    }

    private Task Connect(bool? diagramFiles) =>
        _db.SaveIntegrationAsync(new IntegrationConnection(_connection, _project, CodeRepoKinds.CodeRepo, "acme/sat",
            new CodeRepoConfig { Owner = "acme", Repo = "sat", ReviewFirstImport = true, DiagramFiles = diagramFiles }.ToJson(), null, DateTime.UtcNow));

    /// <summary>Reads no code: a repository of documentation only.</summary>
    private sealed class CsReader : ICodeReader
    {
        public string Language => "C#";
        public bool Handles(string path) => path.EndsWith(".cs", StringComparison.Ordinal);
        public CodeModel Read(IReadOnlyList<CodeFile> files, CancellationToken ct = default) => new("C#", [], []);
    }

    private sealed class NoApplier : ICodeModelApplier
    {
        public CodeSyncChanges Apply(ModelRepository model, CodeModel code, CodeSyncOptions options) => throw new InvalidOperationException("No code in these pushes.");
    }

    private CodeSyncRunner Runner() => new(_db, _hub, [new CsReader()], new NoApplier(), new CodeSyncEvents(), NullLogger<CodeSyncRunner>.Instance);

    private CodeSyncRequest Push(params CodeFile[] files) => new(
        _project, _connection, "acme/sat", "main", $"{++_commits:D2}c0ffee{Guid.NewGuid():N}"[..40], null, "Dana", "Docs", files, IsFullSnapshot: true);

    private async Task<ModelRepository> BranchAsync()
    {
        var branch = (await _db.ListBranchesAsync(_project)).Single(b => b.Name == CodeSyncRunner.BranchNameFor("acme/sat"));
        return ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, branch.Id));
    }

    private static List<Diagram> FromFiles(ModelRepository m) => m.Diagrams.Where(CodeSyncRunner.IsFromDiagramFile).OrderBy(d => d.Name).ToList();

    [Fact]
    public async Task A_Push_Of_Diagram_Files_Puts_Them_On_The_Review_Branch_One_Package_Per_Folder()
    {
        var outcome = await Runner().RunAsync(Push(new CodeFile("docs/flows/release.mmd", Flow), new CodeFile("docs/board.drawio", Board), new CodeFile("README.md", "# Sat")));
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Contains("2 from diagram files", outcome.Summary);

        var m = await BranchAsync();
        var diagrams = FromFiles(m);
        Assert.Equal(["Pipeline", "release"], diagrams.Select(d => d.Name));
        var package = m.OfKind(ElementKind.Package).Single(p => p.Name == CodeSyncRunner.DiagramsPackage);
        Assert.Equal("sat", package.Owner!.Name);
        Assert.Equal(_connection.ToString(), package.Get(CodeFieldKeys.Repo));
        Assert.Equal("docs", diagrams[0].Owner!.Name);
        Assert.Equal("flows", diagrams[1].AncestorsAndSelf().First(a => a.Kind == ElementKind.Package).Name);
        Assert.False(CodeSyncRunner.IsFirstImport(Push(), null, [], ProjectFile.Serialize(m)));   // the package marks the repository as synced
    }

    [Fact]
    public async Task Only_Changed_Files_Are_Imported_Again_And_Deleted_Files_Take_Their_Diagrams()
    {
        await Runner().RunAsync(Push(new CodeFile("docs/release.mmd", Flow), new CodeFile("docs/board.drawio", Board)));
        var first = FromFiles(await BranchAsync()).ToDictionary(d => d.Name, d => d.Id);

        var same = await Runner().RunAsync(Push(new CodeFile("docs/release.mmd", Flow), new CodeFile("docs/board.drawio", Board)));
        Assert.Equal(CodeSyncStatus.NoChanges, same.Status);

        await Runner().RunAsync(Push(new CodeFile("docs/release.mmd", Flow + "\n  Done --> Ship"), new CodeFile("docs/board.drawio", Board)));
        var m = await BranchAsync();
        var second = FromFiles(m).ToDictionary(d => d.Name, d => d.Id);
        Assert.Equal(first["Pipeline"], second["Pipeline"]);   // untouched file: same diagram
        Assert.NotEqual(first["release"], second["release"]);  // changed file: imported again, not copied
        Assert.Equal(2, second.Count);
        Assert.Contains(m.AllElements, e => e.Name == "Ship");

        await Runner().RunAsync(Push(new CodeFile("docs/release.mmd", Flow + "\n  Done --> Ship")));
        Assert.Equal(["release"], FromFiles(await BranchAsync()).Select(d => d.Name));
    }

    [Fact]
    public async Task Accepting_The_Review_Brings_The_Diagrams_Into_Main()
    {
        await Runner().RunAsync(Push(new CodeFile("docs/release.mmd", Flow), new CodeFile("docs/board.drawio", Board)));
        var review = new CodeSyncReviewService(_db, _hub, new CodeSyncEvents(), NullLogger<CodeSyncReviewService>.Instance);
        await review.AcceptAsync(_project, null, null, _ann);
        var main = ProjectFile.Deserialize(await VersionControl.HeadJsonAsync(_db, _hub, _project, null));
        Assert.Equal(["Pipeline", "release"], FromFiles(main).Select(d => d.Name));
        Assert.All(FromFiles(main), d => Assert.NotEmpty(d.Nodes));
    }

    [Fact]
    public async Task Connections_That_Do_Not_Sync_Diagram_Files_Ignore_Them()
    {
        await Connect(diagramFiles: null);   // saved before the setting existed
        var outcome = await Runner().RunAsync(Push(new CodeFile("docs/release.mmd", Flow)));
        Assert.Equal(CodeSyncStatus.NoChanges, outcome.Status);
        Assert.False(new CodeRepoConfig().EffectiveDiagramFiles);
    }

    [Theory]
    [InlineData("docs/a.mmd", true)]
    [InlineData("docs/readme.md", true)]
    [InlineData("docs/login.puml", true)]
    [InlineData("arch/system.drawio", true)]
    [InlineData("arch/system.drawio.svg", true)]
    [InlineData("arch/system.drawio.png", false)]
    [InlineData("arch/system.vsdx", false)]
    [InlineData("src/a.cs", false)]
    public void Diagram_Files_Are_Picked_By_Extension(string path, bool picked) => Assert.Equal(picked, CodeSyncRunner.IsDiagramFile(path));

    [Fact]
    public void Folder_Import_Skips_Unchanged_Files_And_Removes_Missing_Ones()
    {
        var m = new ModelRepository("Docs");
        DiagramSourceFile F(string p, string t) => new(p, System.Text.Encoding.UTF8.GetBytes(t));
        var options = new DiagramFolderOptions { SkipUnchanged = true, RemoveMissing = true };
        DiagramFolderImport.Import(m, m.Root, [F("d/a.mmd", Flow), F("d/b.drawio", Board)], options);
        var again = DiagramFolderImport.Import(m, m.Root, [F("d/a.mmd", Flow)], options);
        Assert.Equal(1, again.Unchanged);
        Assert.Equal(1, again.Removed);
        Assert.Empty(again.Diagrams);
        Assert.Single(m.Diagrams);
    }
}
