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

    public void Execute(IModelCommand command)
    {
        if (ReadOnly) throw new InvalidOperationException("This project is view-only for you.");
        command.Do();
        if (_open.Count > 0) { _open.Peek().AddExecuted(command); return; }
        Push(command);
    }

    /// <summary>Groups every command executed until disposal into one undo step.</summary>
    public IDisposable Transaction(string description)
    {
        _open.Push(new CompositeCommand(description));
        return new TransactionScope(this);
    }

    private void Close()
    {
        var c = _open.Pop();
        if (c.IsEmpty) return;
        if (_open.Count > 0) _open.Peek().AddExecuted(c);
        else Push(c);
    }

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
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var c = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        c.Undo();
        _redo.Add(c);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var c = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        c.Do();
        _undo.Add(c);
        Changed?.Invoke(this, EventArgs.Empty);
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
        _open.Clear();
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
