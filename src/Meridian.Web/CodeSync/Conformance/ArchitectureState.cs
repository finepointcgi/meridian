using Meridian.Core.CodeSync.Conformance;
using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Web.CodeSync.Conformance;

/// <summary>One connected repository in the Architecture tab: its stored record and the result against the open model.</summary>
public sealed record ArchitectureRepo(Guid ConnectionId, string Name, ArchitectureRecord? Record, ConformanceResult? Result);

/// <summary>
/// Per-tab (per circuit) architecture check: runs "Check architecture" against the open model with the last synced code
/// of every connected repository, feeds the Architecture tab and draws the violations on diagrams
/// (<see cref="Workspace.ArchitectureViolations"/>). On main it also stores the result and posts the commit status again.
/// </summary>
public sealed class ArchitectureState : IDisposable
{
    private readonly Workspace _ws;
    private readonly ArchitectureCheckService _checks;
    private readonly IMeridianStore _db;
    private (Guid? Project, Guid? Branch) _for;
    private int _runs;

    public ArchitectureState(Workspace ws, ArchitectureCheckService checks, IMeridianStore db)
    {
        _ws = ws;
        _checks = checks;
        _db = db;
        _ws.Changed += OnWorkspaceChanged;
        _checks.Checked += OnChecked;
        OnWorkspaceChanged();
    }

    public IReadOnlyList<ArchitectureRepo> Repos { get; private set; } = [];

    /// <summary>A check ran in this tab (results are shown).</summary>
    public bool Checked { get; private set; }

    public bool Busy { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Draw the violations on diagrams.</summary>
    public bool ShowOnDiagrams { get; private set; } = true;

    public IEnumerable<ArchitectureViolation> Violations => Repos.SelectMany(r => r.Result?.Violations ?? []);

    public int ErrorCount => Repos.Sum(r => r.Result?.Errors ?? 0);

    public event Action? Changed;

    private void OnWorkspaceChanged()
    {
        var now = (_ws.Session?.ProjectId, _ws.BranchId);
        if (now == _for) return;
        _for = now;
        // Results belong to the model they were computed on: another project or branch starts over.
        Repos = [];
        Checked = false;
        if (_ws.ArchitectureViolations is not null) _ws.SetArchitectureViolations(null);
        _ = CheckAsync(post: false, onlyList: true);
    }

    private void OnChecked(Guid projectId)
    {
        if (_ws.Session?.ProjectId == projectId && !Busy) _ = _ws.Dispatch(() => _ = CheckAsync(post: false, onlyList: !Checked));
    }

    /// <summary>
    /// Checks every connected repository's last synced code against the open model. With <paramref name="post"/> on main
    /// (owners and editors), the stored result and the "meridian/architecture" commit status are updated too.
    /// <paramref name="onlyList"/> just loads the repositories (for the ribbon) without checking.
    /// </summary>
    public async Task CheckAsync(bool post = true, bool onlyList = false)
    {
        if (_ws.Session is not { } s) return;
        var run = Interlocked.Increment(ref _runs);
        Busy = !onlyList;
        Changed?.Invoke();
        var repos = new List<ArchitectureRepo>();
        string? error = null;
        try
        {
            var connections = (await _db.ListIntegrationsAsync(s.ProjectId)).Where(c => c.Kind == Hosts.CodeRepoKinds.CodeRepo).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var records = await _checks.Store.ListAsync(s.ProjectId);
            foreach (var c in connections)
            {
                var rec = records.GetValueOrDefault(c.Id);
                repos.Add(new ArchitectureRepo(c.Id, c.Name, rec, onlyList || rec is null ? null : ArchitectureCheckService.Check(_ws.Model, rec)));
            }
            // A filtered model (packages hidden from me, package permissions) would store and post a wrong verdict: check locally only.
            if (post && !onlyList && !_ws.ReadOnly && _ws.BranchId is null && !_ws.HasHiddenPackages)
                foreach (var r in repos.Where(r => r.Result is not null))
                    await _checks.RecheckAsync(s.ProjectId, r.ConnectionId, _ws.Model);
            if (post && !onlyList)
            {
                // Pick up the stored summaries the re-check wrote.
                var stored = await _checks.Store.ListAsync(s.ProjectId);
                repos = repos.Select(r => r with { Record = stored.GetValueOrDefault(r.ConnectionId) ?? r.Record }).ToList();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { error = ex.Message; }
        if (run != Volatile.Read(ref _runs)) return;
        await _ws.Dispatch(() =>
        {
            Repos = repos;
            Error = error;
            Busy = false;
            if (!onlyList) Checked = true;
            Apply();
            Changed?.Invoke();
        });
    }

    public void SetShowOnDiagrams(bool on)
    {
        ShowOnDiagrams = on;
        Apply();
        Changed?.Invoke();
    }

    public async Task SetFailPullRequestsAsync(Guid connectionId, bool on)
    {
        if (_ws.Session is not { } s || _ws.ReadOnly) return;
        await _checks.SetFailPullRequestsAsync(s.ProjectId, connectionId, on);
    }

    /// <summary>The red dashed edges: violations with a model element on both sides.</summary>
    private void Apply()
    {
        var edges = ShowOnDiagrams && Checked
            ? Violations.Where(v => v.ElementId is not null && v.TargetElementId is not null && v.Severity == ViolationSeverity.Error)
                .Select(v => new ViolationEdge(v.ElementId!, v.TargetElementId!, v.Message)).ToList()
            : [];
        _ws.SetArchitectureViolations(edges.Count > 0 ? edges : null);
    }

    public void Dispose()
    {
        _ws.Changed -= OnWorkspaceChanged;
        _checks.Checked -= OnChecked;
    }
}
