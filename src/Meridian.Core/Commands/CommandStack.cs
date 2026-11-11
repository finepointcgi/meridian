namespace Meridian.Core.Commands;

public interface IModelCommand
{
    string Description { get; }
    void Do();
    void Undo();
}

public sealed class CompositeCommand : IModelCommand
{
    private readonly List<IModelCommand> _children = new();
    public CompositeCommand(string description) => Description = description;
    public string Description { get; }
    public bool IsEmpty => _children.Count == 0;
    internal void AddExecuted(IModelCommand c) => _children.Add(c);
    public void Do() { foreach (var c in _children) c.Do(); }
    public void Undo() { for (var i = _children.Count - 1; i >= 0; i--) _children[i].Undo(); }
}

/// <summary>Undo/redo history with nestable transactions and a save point for dirty tracking.</summary>
public sealed class CommandStack
{
    private readonly List<IModelCommand> _undo = new();
    private readonly List<IModelCommand> _redo = new();
    private readonly Stack<CompositeCommand> _open = new();
    private IModelCommand? _savePoint;
    private bool _savePointValid = true;

    public int Limit { get; set; } = 500;
    public event EventHandler? Changed;

    public bool CanUndo => !ReadOnly && _undo.Count > 0 && _open.Count == 0;
    public bool CanRedo => !ReadOnly && _redo.Count > 0 && _open.Count == 0;
    public string? UndoDescription => _undo.Count > 0 ? _undo[^1].Description : null;
    public string? RedoDescription => _redo.Count > 0 ? _redo[^1].Description : null;
    public bool IsDirty => !_savePointValid || (_undo.Count > 0 ? _undo[^1] : null) != _savePoint;

    /// <summary>When set (viewer role), every edit is refused.</summary>
    public bool ReadOnly { get; set; }

    private int _running;

    /// <summary>
    /// True while a command, undo, redo or transaction is in progress. Collaboration sends the replication operations of one
    /// user action together once it is over (<see cref="ActionCompleted"/>), so the server accepts or refuses them as a whole.
    /// </summary>
    public bool InAction => _running > 0 || _open.Count > 0;

    /// <summary>Raised when the outermost command, undo, redo or transaction has finished (also when it threw).</summary>
    public event Action? ActionCompleted;

    private enum LastKind { None, Do, Undo, Redo }
    private LastKind _lastKind;
    private IModelCommand? _lastCommand;
    private List<IModelCommand>? _lastClearedRedo;

    private void Run(Action work)
    {
        if (!InAction) ForgetLast();
        _running++;
        try { work(); }
        finally
        {
            _running--;
            if (!InAction) ActionCompleted?.Invoke();
        }
    }

    public void Execute(IModelCommand command)
    {
        if (ReadOnly) throw new InvalidOperationException("This project is view-only for you.");
        Run(() =>
        {
            command.Do();
            if (_open.Count > 0) { _open.Peek().AddExecuted(command); return; }
            Push(command);
        });
    }

    /// <summary>Groups every command executed until disposal into one undo step.</summary>
    public IDisposable Transaction(string description)
    {
        if (!InAction) ForgetLast();
        _open.Push(new CompositeCommand(description));
        return new TransactionScope(this);
    }

    private void Close() => Run(() =>
    {
        var c = _open.Pop();
        if (c.IsEmpty) return;
        if (_open.Count > 0) _open.Peek().AddExecuted(c);
        else Push(c);
    });

    private void Push(IModelCommand command)
    {
        _undo.Add(command);
        if (_undo.Count > Limit)
        {
            if (ReferenceEquals(_undo[0], _savePoint)) _savePointValid = false;
            _undo.RemoveAt(0);
        }
        // Redo history is discarded; if the save point lived there it can never be reached again.
        if (_redo.Contains(_savePoint!)) _savePointValid = false;
        (_lastKind, _lastCommand, _lastClearedRedo) = (LastKind.Do, command, _redo.Count > 0 ? [.. _redo] : null);
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var c = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        // The history moves before the action completes, so a listener of ActionCompleted can still discard it.
        Run(() =>
        {
            c.Undo();
            _redo.Add(c);
            (_lastKind, _lastCommand) = (LastKind.Undo, c);
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var c = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Run(() =>
        {
            c.Do();
            _undo.Add(c);
            (_lastKind, _lastCommand) = (LastKind.Redo, c);
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void ForgetLast() => (_lastKind, _lastCommand, _lastClearedRedo) = (LastKind.None, null, null);

    /// <summary>
    /// Forgets the most recent action's effect on the history after its model changes were taken back (a collaboration
    /// server refused them): a new command leaves the undo stack (and the redo steps it cleared come back), an undo goes back
    /// to the undo stack, a redo back to the redo stack. False when there is nothing to discard.
    /// </summary>
    public bool DiscardLastAction()
    {
        var (kind, c, cleared) = (_lastKind, _lastCommand, _lastClearedRedo);
        ForgetLast();
        if (c is null || InAction) return false;
        switch (kind)
        {
            case LastKind.Do when _undo.Count > 0 && ReferenceEquals(_undo[^1], c):
                _undo.RemoveAt(_undo.Count - 1);
                if (cleared != null) _redo.AddRange(cleared);
                break;
            case LastKind.Undo when _redo.Count > 0 && ReferenceEquals(_redo[^1], c):
                _redo.RemoveAt(_redo.Count - 1);
                _undo.Add(c);
                break;
            case LastKind.Redo when _undo.Count > 0 && ReferenceEquals(_undo[^1], c):
                _undo.RemoveAt(_undo.Count - 1);
                _redo.Add(c);
                break;
            default:
                return false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void MarkSaved()
    {
        _savePoint = _undo.Count > 0 ? _undo[^1] : null;
        _savePointValid = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        var wasOpen = _open.Count > 0;
        _open.Clear();
        if (wasOpen && _running == 0) ActionCompleted?.Invoke();
        _savePoint = null;
        _savePointValid = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class TransactionScope(CommandStack stack) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            stack.Close();
        }
    }
}

/// <summary>A command built from two delegates; handy for small reversible edits.</summary>
public sealed class DelegateCommand(string description, Action @do, Action undo) : IModelCommand
{
    public string Description { get; } = description;
    public void Do() => @do();
    public void Undo() => undo();
}
