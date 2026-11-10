using Meridian.Core.Collaboration;
using Meridian.Serialization;

namespace Meridian.Web.Services;

/// <summary>What the SysML v2 text panel of one browser tab receives about a shared text. Calls are made while the session
/// lock is held, so implementations must only queue work (Blazor <c>InvokeAsync</c>), never block.</summary>
public interface ISharedTextClient
{
    /// <summary>Someone else's operation, transformed to follow everything this client was sent before.</summary>
    void TextOperation(string key, TextOperation op);

    /// <summary>The server applied this client's outstanding operation.</summary>
    void TextAck(string key);

    /// <summary>Everyone's carets and selections in server coordinates at <paramref name="revision"/>.</summary>
    void TextCursors(string key, int revision, IReadOnlyList<TextCursor> cursors);

    /// <summary>Unapplied edits / stale model / who applied what.</summary>
    void TextStatus(string key, SharedTextStatus status);

    /// <summary>The client fell out of step (an invalid operation): start over from this text.</summary>
    void TextReset(string key, string text, int revision);
}

/// <summary>An editor in a shared text and their selection (Anchor / Head are -1 until they place a caret).</summary>
public sealed record TextCursor(string ParticipantId, string Name, string Color, int Anchor, int Head);

/// <param name="Dirty">The shared text has edits that aren't applied to the model.</param>
/// <param name="Stale">The model changed where someone has unapplied edits, so the text couldn't follow it.</param>
/// <param name="Notice">A one-off message (e.g. "Ann applied 3 changes").</param>
public sealed record SharedTextStatus(bool Dirty, bool Stale, string? Notice = null);

public sealed record SharedTextSnapshot(string Key, string Text, int Revision, SharedTextStatus Status);

/// <summary>
/// Collaborative SysML v2 text: one shared document per project + branch (this session) + scope (+ id comments on/off).
/// Edits are merged with server-ordered operational transformation (<see cref="SharedTextDocument"/>): each client sends an
/// operation against the revision it has seen, the server transforms it over everything applied since, applies it and relays
/// it to the other editors. Applying the text to the model happens once, in the tab that clicked Apply; the model operations
/// replicate as usual and the server then rewrites the shared text into its canonical form for everyone. When the model
/// changes elsewhere (diagrams), the text follows unless someone has unapplied edits in the same lines — then editors get the
/// "model changed" notice instead.
/// </summary>
public sealed partial class ProjectSession
{
    private sealed class SharedText(string key, string? scopeId, bool ids, string text)
    {
        public string Key { get; } = key;
        public string? ScopeId { get; } = scopeId;
        public bool Ids { get; } = ids;
        public SharedTextDocument Doc { get; } = new(text);
        /// <summary>The model's canonical export the text was last in step with.</summary>
        public string Baseline { get; set; } = text;
        public bool Stale { get; set; }
        public bool Dirty => Doc.Text != Baseline;
        /// <summary>The dirty flag editors were last told about.</summary>
        public bool SentDirty { get; set; }
        public readonly Dictionary<string, (ISharedTextClient Client, int Anchor, int Head)> Editors = new(StringComparer.Ordinal);
        public SharedTextStatus Status(string? notice = null) => new(Dirty, Stale, notice);
    }

    private readonly Dictionary<string, SharedText> _texts = new(StringComparer.Ordinal);
    private Timer? _textRefresh;

    public static string TextKey(string? scopeId, bool ids) => (scopeId ?? "*") + (ids ? "|ids" : "|plain");

    /// <summary>The canonical text of a scope in the authoritative model, or null when the scope no longer exists.</summary>
    private string? CanonicalText(string? scopeId, bool ids)
    {
        var scope = scopeId == null ? _master.Root : _master.Find(scopeId);
        if (scope == null) return null;
        return SysMLv2Text.Export(_master, new SysMLv2ExportOptions { Scope = scope, EmitIds = ids }).Replace("\r\n", "\n");
    }

    /// <summary>Joins (creating if needed) the shared text of a scope.</summary>
    public SharedTextSnapshot OpenText(string participantId, string? scopeId, bool ids, ISharedTextClient client)
    {
        lock (_gate)
        {
            // Shared texts are exported from the whole model: not for participants with hidden packages (their panel works on their own filtered copy).
            if (!_access.TryGetValue(participantId, out var a) || a.View.HidesAnything)
                throw new PermissionDeniedException("Shared SysML v2 text isn't available when some packages are hidden from you.");
            var key = TextKey(scopeId, ids);
            if (!_texts.TryGetValue(key, out var t))
                _texts[key] = t = new SharedText(key, scopeId, ids, CanonicalText(scopeId, ids) ?? "");
            t.Editors[participantId] = (client, -1, -1);
            BroadcastCursors(t);
            return new SharedTextSnapshot(key, t.Doc.Text, t.Doc.Revision, t.Status());
        }
    }

    public void CloseText(string key, string participantId)
    {
        lock (_gate)
        {
            if (!_texts.TryGetValue(key, out var t) || !t.Editors.Remove(participantId)) return;
            if (t.Editors.Count == 0 && !t.Dirty) _texts.Remove(key);   // unapplied edits stay for the next editor
            else BroadcastCursors(t);
        }
    }

    /// <summary>A tab left the session: it leaves every shared text (called under the session lock).</summary>
    private void LeaveTexts(string participantId)
    {
        foreach (var t in _texts.Values.Where(t => t.Editors.ContainsKey(participantId)).ToList())
        {
            t.Editors.Remove(participantId);
            if (t.Editors.Count == 0 && !t.Dirty) _texts.Remove(t.Key);
            else BroadcastCursors(t);
        }
    }

    /// <summary>Current text and revision of a shared document (tests, diagnostics).</summary>
    public (string Text, int Revision, SharedTextStatus Status)? TextState(string key)
    {
        lock (_gate) return _texts.TryGetValue(key, out var t) ? (t.Doc.Text, t.Doc.Revision, t.Status()) : null;
    }

    /// <summary>True when the participant may change shared texts (not a viewer). Called under the session lock.</summary>
    private bool CanEditText(string participantId) => _participants.TryGetValue(participantId, out var p) && p.Presence.CanEdit
        && _access.TryGetValue(participantId, out var a) && !a.View.HidesAnything;

    /// <summary>An editor's operation, made against <paramref name="revision"/>. Viewers' operations are refused here, not
    /// only by their panel.</summary>
    public void SubmitText(string key, string participantId, int revision, TextOperation op)
    {
        lock (_gate)
        {
            if (!_texts.TryGetValue(key, out var t) || !t.Editors.TryGetValue(participantId, out var me)) return;
            if (!CanEditText(participantId))
            {
                me.Client.TextReset(key, t.Doc.Text, t.Doc.Revision);
                return;
            }
            TextOperation applied;
            try { applied = t.Doc.Receive(revision, op); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                _log.LogWarning("Shared text {Key}: rejected an operation from {Participant}: {Message}", key, participantId, ex.Message);
                me.Client.TextReset(key, t.Doc.Text, t.Doc.Revision);
                return;
            }
            Distribute(t, applied, participantId);
        }
    }

    /// <summary>Applies a server-made operation (model refresh, canonical rewrite) and sends it to every editor.</summary>
    private void ApplyServerText(SharedText t, TextOperation op)
    {
        if (op.IsNoop) return;
        var applied = t.Doc.Receive(t.Doc.Revision, op, fromClient: false);
        Distribute(t, applied, author: null);
    }

    private void Distribute(SharedText t, TextOperation applied, string? author)
    {
        foreach (var (id, e) in t.Editors.ToList())
        {
            t.Editors[id] = (e.Client, e.Anchor < 0 ? -1 : applied.TransformIndex(e.Anchor), e.Head < 0 ? -1 : applied.TransformIndex(e.Head));
            if (id == author) e.Client.TextAck(t.Key);
            else e.Client.TextOperation(t.Key, applied);
        }
        if (t.SentDirty != t.Dirty) SendStatus(t);
    }

    private void SendStatus(SharedText t, string? notice = null)
    {
        t.SentDirty = t.Dirty;
        var status = t.Status(notice);
        foreach (var e in t.Editors.Values) e.Client.TextStatus(t.Key, status);
    }

    /// <summary>An editor's caret / selection at <paramref name="revision"/> (offsets in the text of that revision).</summary>
    public void UpdateTextCursor(string key, string participantId, int revision, int anchor, int head)
    {
        lock (_gate)
        {
            if (!_texts.TryGetValue(key, out var t) || !t.Editors.TryGetValue(participantId, out var me)) return;
            if (revision < 0 || revision > t.Doc.Revision) return;
            t.Editors[participantId] = (me.Client, t.Doc.TransformIndex(revision, anchor), t.Doc.TransformIndex(revision, head));
            BroadcastCursors(t);
        }
    }

    private void BroadcastCursors(SharedText t)
    {
        var cursors = new List<TextCursor>();
        foreach (var (id, e) in t.Editors)
        {
            if (!_participants.TryGetValue(id, out var p)) continue;   // Head < 0: in the text, no caret yet
            cursors.Add(new TextCursor(id, p.Presence.User.DisplayName, p.Presence.User.Color, e.Anchor, e.Head));
        }
        foreach (var e in t.Editors.Values) e.Client.TextCursors(t.Key, t.Doc.Revision, cursors);
    }

    /// <summary>
    /// Called by the tab that applied the shared text at <paramref name="revision"/> (its <paramref name="appliedText"/>) to the
    /// model — after its model operations reached this session. Rewrites the shared text into the model's canonical form,
    /// keeping edits others made meanwhile, and tells every editor.
    /// </summary>
    public void TextApplied(string key, int revision, string appliedText, string? notice) =>
        TextApplied(key, participantId: null, revision, appliedText, notice);

    /// <inheritdoc cref="TextApplied(string, int, string, string?)"/>
    public void TextApplied(string key, string? participantId, int revision, string appliedText, string? notice)
    {
        lock (_gate)
        {
            if (!_texts.TryGetValue(key, out var t)) return;
            if (participantId != null && !CanEditText(participantId)) return;
            var canonical = CanonicalText(t.ScopeId, t.Ids);
            if (canonical == null) { t.Stale = true; SendStatus(t, notice); return; }
            TextOperation concurrent;
            try { concurrent = revision >= 0 && revision <= t.Doc.Revision ? t.Doc.Since(revision) : TextOperation.Diff(appliedText, t.Doc.Text); }
            catch (InvalidOperationException) { concurrent = TextOperation.Diff(appliedText, t.Doc.Text); }
            if (concurrent.BaseLength != appliedText.Length) concurrent = TextOperation.Diff(appliedText, t.Doc.Text);
            var rewrite = TextOperation.Diff(appliedText, canonical);
            ApplyServerText(t, TextOperation.Transform(concurrent, rewrite).BPrime);
            t.Baseline = canonical;
            t.Stale = false;
            SendStatus(t, notice);
        }
    }

    /// <summary>Discards everyone's unapplied edits: the shared text becomes the model's canonical text again.</summary>
    public void ResetText(string key, string? notice = null) => ResetText(key, participantId: null, notice);

    /// <summary>A participant discards everyone's unapplied edits (editors only: a viewer must not wipe others' work).</summary>
    public void ResetText(string key, string? participantId, string? notice)
    {
        lock (_gate)
        {
            if (!_texts.TryGetValue(key, out var t)) return;
            if (participantId != null && !CanEditText(participantId)) return;
            var canonical = CanonicalText(t.ScopeId, t.Ids) ?? t.Baseline;
            ApplyServerText(t, TextOperation.Diff(t.Doc.Text, canonical));
            t.Baseline = canonical;
            t.Stale = false;
            SendStatus(t, notice);
        }
    }

    /// <summary>Clears the "model changed" flag without touching the text (keep editing).</summary>
    public void DismissTextStale(string key, string? participantId = null)
    {
        lock (_gate)
        {
            if (!_texts.TryGetValue(key, out var t) || !t.Stale) return;
            if (participantId != null && !CanEditText(participantId)) return;
            t.Stale = false;
            SendStatus(t);
        }
    }

    /// <summary>Debounced <see cref="RefreshSharedTexts"/> after model operations.</summary>
    internal void ScheduleTextRefresh()
    {
        if (_texts.Count == 0) return;
        _textRefresh ??= new Timer(_ => RefreshSharedTexts(), null, Timeout.Infinite, Timeout.Infinite);
        // A whole-model text of a huge project takes long to export (under the session lock), so refreshes back off to a few
        // times their own cost instead of stalling every editor's operations four times a second.
        _textRefresh.Change(Math.Max(250, (int)(_lastTextRefreshCost.TotalMilliseconds * 4)), Timeout.Infinite);
    }

    private TimeSpan _lastTextRefreshCost;

    /// <summary>
    /// Brings every shared text in step with the model: a text without unapplied edits simply becomes the new canonical text;
    /// otherwise the model's changes are merged in when they touch other lines than the unapplied edits, and when they overlap
    /// the editors are told the model changed (the text is left alone).
    /// </summary>
    public void RefreshSharedTexts()
    {
        lock (_gate)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try { RefreshSharedTextsLocked(); }
            finally { _lastTextRefreshCost = System.Diagnostics.Stopwatch.GetElapsedTime(started); }
        }
    }

    private void RefreshSharedTextsLocked()
    {
        {
            foreach (var t in _texts.Values.ToList())
            {
                var canonical = CanonicalText(t.ScopeId, t.Ids);
                if (canonical == null)
                {
                    if (!t.Stale) { t.Stale = true; SendStatus(t, "The package of this text no longer exists"); }
                    continue;
                }
                if (canonical == t.Baseline) continue;
                if (!t.Dirty)
                {
                    ApplyServerText(t, TextOperation.Diff(t.Doc.Text, canonical));
                    t.Baseline = canonical;
                    if (t.Stale) { t.Stale = false; SendStatus(t); }
                    continue;
                }
                var edits = TextOperation.Diff(t.Baseline, t.Doc.Text);
                var model = TextOperation.Diff(t.Baseline, canonical);
                if (Overlap(t.Baseline, edits, model))
                {
                    if (!t.Stale) { t.Stale = true; SendStatus(t); }
                    continue;
                }
                ApplyServerText(t, TextOperation.Transform(edits, model).BPrime);
                t.Baseline = canonical;
                SendStatus(t);
            }
        }
    }

    /// <summary>True when two edits of <paramref name="text"/> touch a common line.</summary>
    private static bool Overlap(string text, TextOperation a, TextOperation b)
    {
        static List<(int From, int To)> Lines(string s, TextOperation op) => op.ChangedRanges()
            .Select(r => (LineOf(s, r.Start), LineOf(s, Math.Max(r.Start, r.End - 1)))).ToList();
        var la = Lines(text, a);
        var lb = Lines(text, b);
        return la.Any(x => lb.Any(y => x.From <= y.To && y.From <= x.To));
    }

    private static int LineOf(string s, int offset)
    {
        var line = 0;
        for (var i = 0; i < offset && i < s.Length; i++) if (s[i] == '\n') line++;
        return line;
    }
}
