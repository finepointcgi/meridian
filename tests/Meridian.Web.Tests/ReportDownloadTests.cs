using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Meridian.Core.Model;
using Meridian.Core.Permissions;
using Meridian.Core.Samples;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Serialization.Reports;
using Meridian.Web;
using Meridian.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Web.Tests;

/// <summary>
/// GET /api/reports/{id}/file against a real server: the report jobs, streaming, and the checks on who may download —
/// the owner only, still a project member, package permissions not narrowed since; the desktop host's single-use ticket.
/// Reports are made from the requesting user's (redacted) model, so hidden packages never reach the file.
/// </summary>
public sealed class ReportDownloadTests : IAsyncLifetime
{
    private const string SecretName = "XJ9ReactorCore", SecretDoc = "TOP SECRET fuel ratios";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-report-dl-");
    private WebApplication _app = default!;
    private HttpClient _http = default!;
    private IMeridianStore _db = default!;
    private CollaborationHub _hub = default!;
    private ReportJobService _jobs = default!;
    private UserAccount _alice = default!, _bob = default!;
    private Guid _project;
    private string _secretPkg = "", _openPkg = "";

    public async Task InitializeAsync()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Meridian:Storage"] = "sqlite",
            ["Meridian:DataPath"] = _dir.FullName,
            ["Meridian:KeysPath"] = Path.Combine(_dir.FullName, "keys"),
            ["Logging:LogLevel:Default"] = "Warning",
        };
        _app = await MeridianServer.CreateAsync([], b =>
        {
            b.Configuration.AddInMemoryCollection(settings);
            b.WebHost.UseUrls("http://127.0.0.1:0");
        }, new WebApplicationOptions { ApplicationName = "Meridian.Web", EnvironmentName = "Production", ContentRootPath = AppContext.BaseDirectory });
        await _app.StartAsync();
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        _http = new HttpClient { BaseAddress = new Uri(address) };
        _db = _app.Services.GetRequiredService<IMeridianStore>();
        _hub = _app.Services.GetRequiredService<CollaborationHub>();
        _jobs = _app.Services.GetRequiredService<ReportJobService>();

        _alice = await _db.CreateUserAsync("alice", "Alice", "correct horse");
        _bob = await _db.CreateUserAsync("bob", "Bob", "battery staple");
        var m = SampleModels.Vehicle();
        var secret = m.Create(m.Root, ElementKind.Package, "Skunkworks");
        m.Set(m.Create(secret, ElementKind.Block, SecretName), FieldKeys.Documentation, SecretDoc);
        var open = m.Create(m.Root, ElementKind.Package, "Open");
        m.Create(open, ElementKind.Block, "OpenPump");
        (_secretPkg, _openPkg) = (secret.Id, open.Id);
        _project = await _db.CreateProjectAsync("Plant", ProjectFile.Serialize(m), _alice.Id);
        await _db.SetMemberAsync(_project, _bob.Id, ProjectRole.Editor);
        await _db.SetPackagePermissionAsync(_project, _secretPkg, PrincipalKind.User, _bob.Id, PackageAccess.None, _alice.Id);
        await _hub.ReloadPermissionsAsync(_project);
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { _dir.Delete(true); } catch (IOException) { }
    }

    private async Task<Workspace> Open(UserAccount user)
    {
        var ws = new Workspace { User = user };
        Assert.True(await ws.JoinAsync(_hub, _project, ProjectRole.Editor));
        return ws;
    }

    /// <summary>What the report dialog does: snapshot the user's model, prepare, and run the job.</summary>
    private async Task<ReportJob> Generate(Workspace ws, ReportFormat format = ReportFormat.Html, DocTemplate? template = null)
    {
        var snapshot = ReportEngine.Snapshot(ws.Model);
        var t = template ?? ReportTemplates.ModelOverview();
        var job = _jobs.Start(t.Name, "overview." + ReportEngine.Extension(format), ReportEngine.MimeType(format),
            (s, p, ct) => ReportEngine.Generate(snapshot, t, format, s, ReportSplit.None, null, p, ct), ws.User!.Id, _project, model: snapshot, access: ws.Access);
        await _jobs.WhenDone(job);
        Assert.Equal(ReportJobState.Completed, job.State);
        return job;
    }

    private HttpRequestMessage Get(ReportJob job, UserAccount? user, string? query = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ReportDownloads.Url(job) + query);
        if (user != null)
        {
            var password = user.Username == "alice" ? "correct horse" : "battery staple";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user.Username}:{password}")));
        }
        return request;
    }

    [Fact]
    public async Task Owner_Downloads_A_Streamed_Report_Without_Hidden_Content()
    {
        var bob = await Open(_bob);
        var job = await Generate(bob);
        using var response = await _http.SendAsync(Get(job, _bob));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("overview.html", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(job.Length, response.Content.Headers.ContentLength);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("OpenPump", html);
        Assert.Contains("Skunkworks", html);   // the package stays listed…
        Assert.DoesNotContain(SecretName, html); // …its contents don't
        Assert.DoesNotContain(SecretDoc, html);

        // Range requests (resumed downloads) and inline viewing.
        var range = Get(job, _bob);
        range.Headers.Range = new RangeHeaderValue(0, 99);
        using (var partial = await _http.SendAsync(range))
        {
            Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
            Assert.Equal(100, (await partial.Content.ReadAsByteArrayAsync()).Length);
        }
        using (var inline = await _http.SendAsync(Get(job, _bob, "?inline=true")))
        {
            Assert.Equal(HttpStatusCode.OK, inline.StatusCode);
            Assert.Null(inline.Content.Headers.ContentDisposition);
        }

        // Word and PDF from the same redacted model.
        foreach (var format in new[] { ReportFormat.Docx, ReportFormat.Pdf })
        {
            var other = await Generate(bob, format);
            using var r = await _http.SendAsync(Get(other, _bob));
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            var bytes = await r.Content.ReadAsByteArrayAsync();
            Assert.Equal(other.Length, bytes.Length);
            if (format == ReportFormat.Docx)
            {
                using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes));
                using var body = new StreamReader(zip.GetEntry("word/document.xml")!.Open());
                var xml = await body.ReadToEndAsync();
                Assert.Contains("OpenPump", xml);
                Assert.DoesNotContain(SecretName, xml);
            }
            else Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));
        }
    }

    [Fact]
    public async Task Only_The_Owner_May_Download_And_Only_When_Signed_In()
    {
        var job = await Generate(await Open(_bob));
        using (var anonymous = await _http.SendAsync(Get(job, null)))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var alice = await _http.SendAsync(Get(job, _alice)))
            Assert.Equal(HttpStatusCode.NotFound, alice.StatusCode);   // not hers: as if it didn't exist
        var wrongPassword = Get(job, null);
        wrongPassword.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("bob:wrong")));
        using (var wrong = await _http.SendAsync(wrongPassword))
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        using (var missing = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"api/reports/{Guid.NewGuid()}/file") { Headers = { Authorization = Get(job, _bob).Headers.Authorization } }))
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Desktop_Ticket_Works_Once_For_Its_Own_Job()
    {
        var bob = await Open(_bob);
        var job = await Generate(bob);
        var other = await Generate(bob);
        var ticket = _jobs.IssueTicket(job);
        using (var wrongJob = await _http.SendAsync(Get(other, null, "?ticket=" + Uri.EscapeDataString(_jobs.IssueTicket(job)))))
            Assert.Equal(HttpStatusCode.Unauthorized, wrongJob.StatusCode);
        using (var first = await _http.SendAsync(Get(job, null, "?ticket=" + Uri.EscapeDataString(ticket))))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            Assert.DoesNotContain(SecretName, await first.Content.ReadAsStringAsync());
        }
        using (var again = await _http.SendAsync(Get(job, null, "?ticket=" + Uri.EscapeDataString(ticket))))
            Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        using (var forged = await _http.SendAsync(Get(job, null, "?ticket=not-a-ticket")))
            Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
    }

    [Fact]
    public async Task Download_Is_Refused_After_Permissions_Narrow_Or_Membership_Ends()
    {
        var bob = await Open(_bob);
        var job = await Generate(bob);
        using (var ok = await _http.SendAsync(Get(job, _bob))) Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        // Alice hides the "Open" package from Bob: the report holds its contents, so it is no longer handed out.
        await _db.SetPackagePermissionAsync(_project, _openPkg, PrincipalKind.User, _bob.Id, PackageAccess.None, _alice.Id);
        await _hub.ReloadPermissionsAsync(_project);
        using (var narrowed = await _http.SendAsync(Get(job, _bob)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, narrowed.StatusCode);
            Assert.Contains("Generate it again", await narrowed.Content.ReadAsStringAsync());
        }
        // Widening (or an unrelated rule) is fine: give it back.
        await _db.SetPackagePermissionAsync(_project, _openPkg, PrincipalKind.User, _bob.Id, PackageAccess.Edit, _alice.Id);
        using (var back = await _http.SendAsync(Get(job, _bob))) Assert.Equal(HttpStatusCode.OK, back.StatusCode);

        // Bob leaves the project.
        await _db.RemoveMemberAsync(_project, _bob.Id);
        using (var gone = await _http.SendAsync(Get(job, _bob))) Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Unfinished_Cancelled_And_Failed_Jobs_Have_No_File()
    {
        using var gate = new ManualResetEventSlim();
        var running = _jobs.Start("Slow", "slow.html", "text/html", (s, p, ct) =>
        {
            p.Report(new ReportProgress("Working", 0.5, 1, 2, 10));
            gate.Wait(ct);
            s.Write("<p>done</p>"u8);
        }, _bob.Id, _project);
        await WaitFor(() => running.State == ReportJobState.Running && running.Fraction == 0.5);
        Assert.Equal("Section 1 of 2 · 10 elements", running.Detail);
        using (var early = await _http.SendAsync(Get(running, _bob))) Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        gate.Set();
        await _jobs.WhenDone(running);
        using (var done = await _http.SendAsync(Get(running, _bob))) Assert.Equal("<p>done</p>", await done.Content.ReadAsStringAsync());

        var cancelled = _jobs.Start("Cancelled", "c.html", "text/html", (s, p, ct) => { Task.Delay(Timeout.Infinite, ct).Wait(ct); }, _bob.Id, _project);
        await WaitFor(() => cancelled.State == ReportJobState.Running);
        _jobs.Cancel(cancelled);
        await _jobs.WhenDone(cancelled);
        Assert.Equal(ReportJobState.Cancelled, cancelled.State);
        using (var c = await _http.SendAsync(Get(cancelled, _bob))) Assert.Equal(HttpStatusCode.Conflict, c.StatusCode);

        var failed = _jobs.Start("Failed", "f.html", "text/html", (s, p, ct) => throw new InvalidOperationException("The document is too large"), _bob.Id, _project);
        await _jobs.WhenDone(failed);
        Assert.Equal(ReportJobState.Failed, failed.State);
        Assert.Contains("too large", failed.Error);
        Assert.Empty(Directory.GetFiles(_jobs.Directory, cancelled.Id.ToString("N") + "*"));
        Assert.Empty(Directory.GetFiles(_jobs.Directory, failed.Id.ToString("N") + "*"));

        // Finished jobs expire with their files.
        _jobs.Sweep(DateTime.UtcNow + ReportJobService.Retention + TimeSpan.FromMinutes(1));
        Assert.Null(_jobs.Find(running.Id));
        Assert.Empty(Directory.GetFiles(_jobs.Directory));
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 400 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}
