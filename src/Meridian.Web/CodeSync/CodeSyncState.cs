using Meridian.Data;
using Meridian.Serialization;
using Meridian.Web.Services;

namespace Meridian.Web.CodeSync;

/// <summary>
/// Per-tab (per circuit) view of the open project's code-sync reviews: loads them when the project or branch changes
/// and when a sync or a review decision happens anywhere, and feeds the diagram highlights (<see cref="Workspace.CodeChanges"/>).
/// The "Code changes" badge and the "What changed" tab read it.
/// </summary>
public sealed class CodeSyncState : IDisposable
{
    private readonly Workspace _ws;
    private readonly CodeSyncReviewService _review;
    private readonly CodeSyncEvents _events;
    private (Guid? Project, Guid? Branch) _for;
    private Guid? _autoHighlightedFor;
    private int _loads;

    public CodeSyncState(Workspace ws, CodeSyncReviewService review, CodeSyncEvents events)
    {
        _ws = ws;
        _review = review;
        _events = events;
        _ws.Changed += OnWorkspaceChanged;
        _events.RunsChanged += OnRunsChanged;
    }

    public IReadOnlyList<CodeSyncReview> Reviews { get; private set; } = [];

    /// <summary>Changes waiting for review across every code-sync branch of the project.</summary>
    public int PendingCount => Reviews.Sum(r => r.Changes.Count);

    /// <summary>Runs waiting for review.</summary>
    public int OpenRuns => Reviews.Sum(r => r.Runs.Count);

    /// <summary>The review of the branch being edited, if it is a code-sync branch with open runs.</summary>
    public CodeSyncReview? OnThisBranch => Reviews.FirstOrDefault(r => r.Branch.BranchId is { } b && b == _ws.BranchId);

    /// <summary>The last load failed with this message (shown by the tab).</summary>
    public string? Error { get; private set; }

    public bool Loaded { get; private set; }

    public event Action? Changed;

    private void OnWorkspaceChanged()
    {
        var now = (_ws.Session?.ProjectId, _ws.BranchId);
        if (now == _for) return;
        _for = now;
        _ = ReloadAsync();
    }

    private void OnRunsChanged(Guid projectId)
    {
        if (_ws.Session?.ProjectId == projectId) _ = _ws.Dispatch(() => _ = ReloadAsync());
    }

    /// <summary>Reloads the reviews (the newest load wins when several overlap).</summary>
    public async Task ReloadAsync()
    {
        var load = Interlocked.Increment(ref _loads);
        _for = (_ws.Session?.ProjectId, _ws.BranchId);
        IReadOnlyList<CodeSyncReview> reviews = [];
        string? error = null;
        // Code-change reviews show main's whole model (names, ghosts of removed shapes): not for users with hidden packages.
        if (_ws.Session is { } s && !_ws.HasHiddenPackages)
        {
            try { reviews = await _review.LoadAsync(s.ProjectId); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException) { error = ex.Message; }
        }
        if (load != Volatile.Read(ref _loads)) return;
        await _ws.Dispatch(() =>
        {
            Reviews = reviews;
            Error = error;
            Loaded = true;
            Apply();
            Changed?.Invoke();
        });
    }

    /// <summary>Highlights: the review of this branch (with ghosts of removed elements from main), or on another line every pending change.</summary>
    private void Apply()
    {
        var here = OnThisBranch;
        if (here is not null && _autoHighlightedFor != here.Branch.BranchId)
        {
            _autoHighlightedFor = here.Branch.BranchId;   // on by default when you arrive on a review branch
            _ws.SetHighlightCodeChanges(true);
        }
        if (Reviews.Count == 0) { if (_ws.CodeChanges is not null) _ws.SetCodeChanges(null); return; }
        IReadOnlyList<CodeSyncReview> source = here is not null ? [here] : Reviews;
        var kinds = new Dictionary<string, ChangeHighlight>(StringComparer.Ordinal);
        foreach (var c in source.SelectMany(r => r.Changes)) kinds[c.ElementId] = Highlight(c.Kind);
        _ws.SetCodeChanges(new CodeChangeOverlay(kinds, here?.Main));
    }

    public static ChangeHighlight Highlight(CodeSyncChangeKind k) => k switch
    {
        CodeSyncChangeKind.Added => ChangeHighlight.Added,
        CodeSyncChangeKind.Changed => ChangeHighlight.Changed,
        _ => ChangeHighlight.Removed,
    };

    public void Dispose()
    {
        _ws.Changed -= OnWorkspaceChanged;
        _events.RunsChanged -= OnRunsChanged;
    }
}
