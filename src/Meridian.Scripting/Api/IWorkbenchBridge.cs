using Meridian.Core.Model;

namespace Meridian.Scripting;

/// <summary>
/// Implemented by the host application (the web workbench) to connect <see cref="ModelApi"/> to the UI.
/// This is a host-side interface; scripts and plugins never see it.
/// </summary>
public interface IWorkbenchBridge
{
    /// <summary>
    /// Runs <paramref name="work"/> where the model may be touched (the UI/circuit thread) and returns its result.
    /// Must execute inline when already on that thread.
    /// </summary>
    T Invoke<T>(Func<T> work);

    Element? Selected { get; }
    Diagram? ActiveDiagram { get; }
    void Select(Element? element);
    void Open(Diagram diagram);
    void Log(string message);
    void Status(string message);
    void Download(string fileName, string mimeType, byte[] content);
}

/// <summary>Bridge for running without a UI (tests, CLI tools, validation rules): collects log lines, status and downloads.</summary>
public sealed class HeadlessBridge : IWorkbenchBridge
{
    private readonly object _gate = new();
    public List<string> Lines { get; } = new();
    public List<(string FileName, string MimeType, byte[] Content)> Downloads { get; } = new();
    public string? LastStatus { get; private set; }
    public Element? Selected { get; private set; }
    public Diagram? ActiveDiagram { get; private set; }

    public T Invoke<T>(Func<T> work)
    {
        lock (_gate) return work();
    }

    public void Select(Element? element) => Selected = element;
    public void Open(Diagram diagram) => ActiveDiagram = diagram;
    public void Log(string message) { lock (Lines) Lines.Add(message); }
    public void Status(string message) => LastStatus = message;
    public void Download(string fileName, string mimeType, byte[] content) { lock (Downloads) Downloads.Add((fileName, mimeType, content)); }
}
