using Meridian.Core.CodeSync;
using Meridian.Core.CodeSync.Conformance;
using Meridian.Core.Model;
using Meridian.Data;
using Meridian.Serialization.CodeSync;
using Meridian.Web.CodeSync.Conformance;
using Meridian.Web.CodeSync.Hosts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>Behaviour from code: a push of a small C# project brings an activity and a sequence diagram to review.</summary>
public sealed class CodeBehaviourTests(ServerFixture f) : E2ETest(f)
{
    private const string Source = """
        namespace Sat.Power
        {
            public class Battery
            {
                public double Level { get; set; }
                public bool IsLow() => Level < 0.2;
                public void Charge(double watts) { Level += watts; }
                public void Drain() { Level = 0; }
            }
        }
        namespace Sat.Control
        {
            using Sat.Power;
            public class PowerController
            {
                private readonly Battery _battery = new();
                public void Balance(int rounds)
                {
                    for (var i = 0; i < rounds; i++)
                    {
                        if (_battery.IsLow()) _battery.Charge(5);
                        else _battery.Drain();
                    }
                }
            }
        }
        """;

    [E2EFact]
    public async Task A_push_generates_an_activity_and_a_sequence_with_decisions_and_an_alt_fragment()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var name = "Behaviour " + Guid.NewGuid().ToString("N")[..6];
        await p.NewProjectAsync("Vehicle sample", name);
        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var project = (await db.ListProjectsAsync(alice.Id)).Single(x => x.Name == name).Id;
        var connection = Guid.NewGuid();
        var files = new[] { new CodeFile("src/Power.cs", Source) };

        // The real reader and mapper, with "Entry points" (what a new connection gets).
        CodeSyncE2E.Script.Enqueue(m => CodeModelMapper.Apply(m, new CSharpCodeReader().Read(files),
            new CodeSyncOptions { RootPackage = "sat", RepoId = connection.ToString(), Commit = "c0ffee", Behaviour = CodeBehaviourMode.EntryPoints }));
        var outcome = await F.App.Services.GetRequiredService<ICodeSyncRunner>().RunAsync(new CodeSyncRequest(
            project, connection, "acme/sat", "main", "c0ffee0123456789c0ffee0123456789c0ffee01", null, "Dana Dev", "Balance batteries", files, IsFullSnapshot: true));
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);

        var badge = p.Locator("[data-testid=code-changes-badge]");
        await Expect(badge).ToBeVisibleAsync();
        await badge.ClickAsync();
        var review = p.ViewTab();

        // The activity: opened from its row, with decision diamonds (the loop and the if).
        var activityRow = review.Locator(".chg-row[data-kind=added]").Filter(new() { HasTextString = "PowerController.Balance" })
            .Filter(new() { HasNotTextString = "sequence" }).First;
        await activityRow.GetByRole(AriaRole.Button, new() { Name = "Show on diagram" }).ClickAsync();
        await Expect(p.Locator(".vc-branch .name")).ToHaveTextAsync("code-sync/acme/sat");
        await Expect(p.Nodes().Filter(new() { Has = p.Locator("polygon") })).Not.ToHaveCountAsync(0);
        await Expect(p.Canvas()).ToContainTextAsync("_battery.Charge()");

        // The sequence: lifelines for the controller and its battery, an alt fragment inside the loop.
        await badge.ClickAsync();
        var sequenceRow = review.Locator(".chg-row[data-kind=added]").Filter(new() { HasTextString = "PowerController.Balance sequence" }).First;
        await sequenceRow.GetByRole(AriaRole.Button, new() { Name = "Show on diagram" }).ClickAsync();
        await Expect(p.Canvas()).ToContainTextAsync("alt");
        await Expect(p.Canvas()).ToContainTextAsync("loop");
        await Expect(p.Canvas()).ToContainTextAsync("battery");
        await Expect(p.Canvas()).ToContainTextAsync("Charge(5)");
        if (Environment.GetEnvironmentVariable("MERIDIAN_E2E_SCREENSHOTS") is { Length: > 0 } shots)
            await p.ScreenshotAsync(new() { Path = Path.Combine(shots, "code-sync-sequence.png") });
    }

    [E2EFact]
    public async Task Generate_sequence_from_code_asks_for_the_depth()
    {
        var s = await OpenAsync(F.Alice);
        var p = s.Page;
        var name = "Depth " + Guid.NewGuid().ToString("N")[..6];
        await p.NewProjectAsync("Vehicle sample", name);
        var db = F.App.Services.GetRequiredService<IMeridianStore>();
        var alice = (await db.FindUserAsync(F.Alice.Username))!;
        var project = (await db.ListProjectsAsync(alice.Id)).Single(x => x.Name == name).Id;
        var connection = Guid.NewGuid();
        var config = new CodeRepoConfig { Host = GitHostKind.GitHub, Owner = "acme", Repo = "sat", PostStatus = false, PreviewPullRequests = false, BehaviourDiagrams = CodeBehaviourMode.OnDemand };
        await db.SaveIntegrationAsync(new IntegrationConnection(connection, project, CodeRepoKinds.CodeRepo, "acme/sat", config.ToJson(), null, DateTime.UtcNow));
        var files = new[] { new CodeFile("src/PowerDepth.cs", Source) };
        var code = new CSharpCodeReader().Read(files);
        CodeSyncE2E.Code["src/PowerDepth.cs"] = code;   // what the architecture check stores after the run

        // On demand: the sync makes no behaviour; the operation's inspector offers it.
        CodeSyncE2E.Script.Enqueue(m => CodeModelMapper.Apply(m, code,
            new CodeSyncOptions { RootPackage = "sat", RepoId = connection.ToString(), Commit = "c0ffee", Behaviour = CodeBehaviourMode.OnDemand }));
        var outcome = await F.App.Services.GetRequiredService<ICodeSyncRunner>().RunAsync(new CodeSyncRequest(
            project, connection, "acme/sat", "main", "c0ffee0123456789c0ffee0123456789c0ffee01", null, "Dana Dev", "Power", files, IsFullSnapshot: true));
        Assert.Equal(CodeSyncStatus.AwaitingReview, outcome.Status);
        Assert.Contains("0 activity, 0 sequence diagrams", outcome.Summary);
        await new ArchitectureStore(db).UpdateAsync(project, connection, r => { r.Commit = "c0ffee"; r.PackedCode = CodeModelJson.Pack(code); });

        // To the review branch, where the operation is.
        var badge = p.Locator("[data-testid=code-changes-badge]");
        await Expect(badge).ToBeVisibleAsync();
        await badge.ClickAsync();
        var row = p.ViewTab().Locator(".chg-row[data-kind=added]").Filter(new() { HasTextString = "PowerController" }).First;
        await row.GetByRole(AriaRole.Button, new() { Name = "Show on diagram" }).ClickAsync();
        await Expect(p.Locator(".vc-branch .name")).ToHaveTextAsync("code-sync/acme/sat");

        await p.GetByPlaceholder("Search elements…").FillAsync("Balance");
        await p.Tree().GetByText("Balance").First.ClickAsync();
        await p.Inspector().Locator(".code-behaviour-sequence").ClickAsync();
        var dialog = p.MudDialog();
        await Expect(dialog).ToContainTextAsync("How deep should the sequence");
        await Expect(dialog.Locator("input[data-testid=sequence-depth-2]")).ToBeCheckedAsync();
        await dialog.Locator("input[data-testid=sequence-depth-1]").ClickAsync();
        await dialog.Locator("button.sequence-depth-ok").ClickAsync();

        await Expect(p.Tabs().Filter(new() { HasTextString = "PowerController.Balance sequence" })).ToHaveCountAsync(1);
        await Expect(p.Canvas()).ToContainTextAsync("Charge(5)");
        await Expect(p.Canvas()).ToContainTextAsync("loop");
        await Expect(p.Inspector()).ToContainTextAsync("Sequence: PowerController.Balance sequence");
    }
}
