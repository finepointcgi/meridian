using System.Text;
using Meridian.Assistant.Providers;
using Meridian.Assistant.Changes;
using Meridian.Assistant.Tools;

namespace Meridian.Assistant.Engine;

/// <summary>One entry of the chat transcript the panel renders.</summary>
public abstract class ChatItem
{
    public DateTime At { get; } = DateTime.Now;
}

public sealed class UserItem(string text, string? label = null) : ChatItem
{
    public string Text { get; } = text;

    /// <summary>Short label for quick actions (the full prompt is not shown).</summary>
    public string? Label { get; } = label;
}

public sealed class AssistantTextItem : ChatItem
{
    private readonly StringBuilder _text = new();
    public string Text => _text.ToString();
    public bool Streaming { get; set; } = true;
    public void Append(string s) => _text.Append(s);
}

public sealed class ToolActivityItem(string tool, string summary) : ChatItem
{
    public string Tool { get; } = tool;
    public string Summary { get; } = summary;
    public bool IsError { get; set; }
    public string? Error { get; set; }
    public bool Done { get; set; }
}

public sealed class ChangeSetItem(ChangeSet changes) : ChatItem
{
    public ChangeSet Changes { get; } = changes;
}

public sealed class FindingsItem(IReadOnlyList<ReviewFinding> findings) : ChatItem
{
    public IReadOnlyList<ReviewFinding> Findings { get; } = findings;
}

public enum NoticeKind { Info, Warning, Error, Refusal }

public sealed class NoticeItem(NoticeKind kind, string text) : ChatItem
{
    public NoticeKind Kind { get; } = kind;
    public string Text { get; } = text;
}

/// <summary>
/// A conversation: the transcript shown in the panel and the append-only message history sent to the API. History is
/// only extended by complete turns; a turn that is stopped, refused or fails is dropped from the history again so the
/// next request is always well-formed (the transcript keeps what the user saw).
/// </summary>
public sealed class Conversation
{
    private int _refs;
    private int _changeSets;

    public Guid Id { get; } = Guid.NewGuid();
    public List<ChatItem> Items { get; } = new();
    /// <summary>Provider-neutral history (Claude's own blocks are kept on each message and replayed unchanged).</summary>
    public List<ChatMessage> History { get; } = new();

    /// <summary>A short progress line while the model works ("Loading the model…"); null when there is none.</summary>
    public string? Status { get; internal set; }
    public bool Busy { get; internal set; }

    /// <summary>Notes for the model about what happened since the last turn (change sets applied or discarded).</summary>
    public List<string> PendingNotes { get; } = new();

    public long InputTokens { get; internal set; }
    public long OutputTokens { get; internal set; }
    public long CacheReadTokens { get; internal set; }

    internal int NextRef() => Interlocked.Increment(ref _refs);
    internal ChangeSet NewChangeSet() => new(Interlocked.Increment(ref _changeSets));

    public IEnumerable<ChangeSet> ChangeSets => Items.OfType<ChangeSetItem>().Select(i => i.Changes);

    /// <summary>Records the user's decision on a change set for the next turn's context.</summary>
    public void NoteOutcome(ChangeSet set)
    {
        var note = set.State switch
        {
            ChangeSetState.Applied => $"The user applied change set #{set.Id}." +
                                      (set.CreatedIds.Count > 0 ? " Created ids: " + string.Join(", ", set.CreatedIds.Select(kv => $"{kv.Key} = {kv.Value}")) : ""),
            ChangeSetState.Discarded => $"The user discarded change set #{set.Id}; none of it was applied.",
            ChangeSetState.Failed => $"Change set #{set.Id} could not be applied: {set.Outcome}",
            _ => null,
        };
        if (note != null) PendingNotes.Add(note);
    }
}
