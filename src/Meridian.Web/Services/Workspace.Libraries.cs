using Meridian.Core.Commands;
using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>Used projects (docs/LIBRARIES.md) in this tab: what a library element is, and installing a model rebuilt with new libraries.</summary>
public sealed partial class Workspace
{
    /// <summary>The used project an element comes from (read-only here), or null for the project's own elements.</summary>
    public LibraryInfo? LibraryOf(Element? e) => Model.LibraryOf(e)?.Info;

    /// <summary>
    /// Installs a model the session rebuilt for this tab (a usage was added, re-pinned or removed by me), keeping the open diagram
    /// and selection; <paramref name="undo"/> becomes the one undo step for it.
    /// </summary>
    public void InstallLibraryChange(string modelJson, string message, IModelCommand? undo = null)
    {
        var activeId = Active?.Diagram.Id;
        var selectedId = Selected?.Id;
        Install(ProjectFile.Deserialize(modelJson));
        if (activeId != null && Model.Find(activeId) is Diagram d) Open(d, recordHistory: false);
        else OpenDefaultDiagrams();
        if (Model.Find(selectedId) is { } sel) Select(sel);
        if (undo != null && !Model.History.ReadOnly) Model.History.Execute(undo);
        Log(message);
        SetStatus(message);
    }
}

/// <summary>
/// An undo step whose effect lives outside the model (a library pin): the first <see cref="Do"/> is the change already made;
/// undo (and redo) run <paramref name="undo"/> / <paramref name="redo"/>, which rebuild the model.
/// </summary>
public sealed class ExternalCommand(string description, Action undo, Action? redo = null) : IModelCommand
{
    private bool _done;
    public string Description { get; } = description;

    public void Do()
    {
        if (_done) redo?.Invoke();
        _done = true;
    }

    public void Undo() => undo();
}
