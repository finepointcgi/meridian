using Meridian.Core.Libraries;
using Meridian.Web.Services;

namespace Meridian.Web.Libraries;

/// <summary>
/// Per browser tab: the open project's used projects and whether newer pins exist ("Check for library updates" runs when a
/// project opens; ⌘K and the ribbon run it again). Drives the update badge and the "Used projects" dialog.
/// </summary>
public sealed class LibraryState : IDisposable
{
    private readonly Workspace _ws;
    private readonly LibraryService _service;
    private Guid? _for;
    private int _checks;

    public LibraryState(Workspace ws, LibraryService service)
    {
        _ws = ws;
        _service = service;
        _ws.Changed += OnWorkspaceChanged;
    }

    /// <summary>Newer pins found by the last check.</summary>
    public IReadOnlyList<LibraryUpdateOffer> Updates { get; private set; } = [];

    public bool Checking { get; private set; }
    public DateTime? LastChecked { get; private set; }

    /// <summary>Libraries loaded in this tab (what I may read).</summary>
    public IReadOnlyList<LibraryInfo> Loaded => _ws.Model.Libraries.Select(m => m.Info).ToList();

    /// <summary>What didn't load (cycles, conflicting pins, unavailable projects).</summary>
    public IReadOnlyList<string> Warnings => _ws.Session?.Libraries.Warnings ?? [];

    public event Action? Changed;

    private void OnWorkspaceChanged()
    {
        var now = _ws.Session?.ProjectId;
        if (now == _for) return;
        _for = now;
        Updates = [];
        if (now is not null) _ = CheckAsync();   // automatically when a project opens
    }

    /// <summary>Checks the project's usages for newer versions, branch heads or baselines.</summary>
    public async Task<IReadOnlyList<LibraryUpdateOffer>> CheckAsync()
    {
        if (_ws.Session is not { } s || _ws.User is not { } me) return [];
        var check = Interlocked.Increment(ref _checks);
        Checking = true;
        Changed?.Invoke();
        try
        {
            var found = await _service.CheckUpdatesAsync(s.ProjectId, me.Id, Loaded);
            if (check == _checks) Updates = found;
            LastChecked = DateTime.Now;
            return found;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            _ws.Log($"Checking for library updates failed: {ex.Message}");
            return [];
        }
        finally
        {
            Checking = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Accepts a reviewed update: the pin moves, the model is rebuilt and suspect references are marked, as one undo step.</summary>
    public async Task<LibraryUpdateResult> ApplyAsync(LibraryUpdatePlan plan)
    {
        var me = _ws.User ?? throw new InvalidOperationException("Sign in first.");
        var (json, result) = await _service.ApplyUpdateAsync(plan, me.Id, _ws.ParticipantId);
        var message = $"Updated “{plan.LibraryName}” to {plan.ToLabel}" + (result.Suspect > 0 ? $" — {result.Suspect} reference{(result.Suspect == 1 ? " is" : "s are")} now suspect" : "");
        if (json != null)
            _ws.InstallLibraryChange(json, message, new ExternalCommand($"Update “{plan.LibraryName}” to {plan.ToLabel}",
                () => _ = _ws.Dispatch(() => _ = UndoAsync(result))));
        Updates = Updates.Where(u => u.UsageId != plan.Usage.Id).ToList();
        Changed?.Invoke();
        return result;
    }

    private async Task UndoAsync(LibraryUpdateResult result)
    {
        if (_ws.User is not { } me) return;
        try
        {
            var json = await _service.UndoUpdateAsync(result, me.Id, _ws.ParticipantId);
            if (json != null) _ws.InstallLibraryChange(json, "Library update undone");
            await CheckAsync();
        }
        catch (InvalidOperationException ex) { _ws.SetStatus(ex.Message); }
    }

    /// <summary>Runs a usage change (add / re-pin / remove) and installs the rebuilt model in this tab.</summary>
    public async Task ChangeAsync(Func<Task> change, string message)
    {
        await change();   // every tab, this one included, gets the rebuilt model from the session
        Changed?.Invoke();
        _ = CheckAsync();
        _ws.SetStatus(message);
    }

    public void Dispose() => _ws.Changed -= OnWorkspaceChanged;
}
