using System.Text;
using System.Text.Json;

namespace Meridian.Core.Collaboration;

/// <summary>
/// A plain-text operation for operational transformation (the classic retain / insert / delete model of ot.js, re-implemented):
/// a sequence of components that walks the whole base text. A positive int retains that many characters, a negative int deletes,
/// a string inserts. Operations compose and transform; inserts of the first operand win ties, so a server that transforms an
/// incoming client operation against everything it applied since and a client that transforms its pending operation against
/// incoming server operations converge on the same text. The browser editor (wwwroot/js/sysml-editor.js) runs the same algorithm.
/// </summary>
public sealed class TextOperation : IEquatable<TextOperation>
{
    private readonly List<object> _ops = new();

    public IReadOnlyList<object> Ops => _ops;

    /// <summary>Length of the text the operation applies to.</summary>
    public int BaseLength { get; private set; }

    /// <summary>Length of the text after applying it.</summary>
    public int TargetLength { get; private set; }

    public bool IsNoop => _ops.Count == 0 || _ops.Count == 1 && _ops[0] is int r && r > 0;

    public TextOperation Retain(int n)
    {
        if (n < 0) throw new ArgumentOutOfRangeException(nameof(n));
        if (n == 0) return this;
        BaseLength += n;
        TargetLength += n;
        if (_ops.Count > 0 && _ops[^1] is int last && last > 0) _ops[^1] = last + n;
        else _ops.Add(n);
        return this;
    }

    public TextOperation Insert(string s)
    {
        if (string.IsNullOrEmpty(s)) return this;
        TargetLength += s.Length;
        if (_ops.Count > 0 && _ops[^1] is string prev) _ops[^1] = prev + s;
        else if (_ops.Count > 0 && _ops[^1] is int d && d < 0)
        {   // canonical order: insert before delete
            if (_ops.Count > 1 && _ops[^2] is string before) _ops[^2] = before + s;
            else _ops.Insert(_ops.Count - 1, s);
        }
        else _ops.Add(s);
        return this;
    }

    public TextOperation Delete(int n)
    {
        if (n == 0) return this;
        if (n > 0) n = -n;
        BaseLength -= n;
        if (_ops.Count > 0 && _ops[^1] is int last && last < 0) _ops[^1] = last + n;
        else _ops.Add(n);
        return this;
    }

    public string Apply(string text)
    {
        if (text.Length != BaseLength) throw new InvalidOperationException($"Operation base length {BaseLength} does not match the text length {text.Length}");
        var sb = new StringBuilder(TargetLength);
        var i = 0;
        foreach (var op in _ops)
        {
            switch (op)
            {
                case string s: sb.Append(s); break;
                case int r when r > 0: sb.Append(text, i, r); i += r; break;
                case int d: i -= d; break;
            }
        }
        return sb.ToString();
    }

    /// <summary>This operation followed by <paramref name="b"/>, as one operation.</summary>
    public TextOperation Compose(TextOperation b)
    {
        if (TargetLength != b.BaseLength) throw new InvalidOperationException("Compose: the second operation's base length must equal the first one's target length");
        var result = new TextOperation();
        var ops1 = _ops; var ops2 = b._ops;
        int i1 = 0, i2 = 0;
        object? op1 = Next(ops1, ref i1), op2 = Next(ops2, ref i2);
        while (op1 != null || op2 != null)
        {
            if (op1 is int d1 && d1 < 0) { result.Delete(d1); op1 = Next(ops1, ref i1); continue; }
            if (op2 is string ins2) { result.Insert(ins2); op2 = Next(ops2, ref i2); continue; }
            if (op1 == null || op2 == null) throw new InvalidOperationException("Compose: operations don't line up");
            switch (op1, op2)
            {
                case (int r1, int r2) when r1 > 0 && r2 > 0:
                    if (r1 > r2) { result.Retain(r2); op1 = r1 - r2; op2 = Next(ops2, ref i2); }
                    else if (r1 == r2) { result.Retain(r1); op1 = Next(ops1, ref i1); op2 = Next(ops2, ref i2); }
                    else { result.Retain(r1); op2 = r2 - r1; op1 = Next(ops1, ref i1); }
                    break;
                case (string s1, int del2) when del2 < 0:
                    if (s1.Length > -del2) { op1 = s1[-del2..]; op2 = Next(ops2, ref i2); }
                    else if (s1.Length == -del2) { op1 = Next(ops1, ref i1); op2 = Next(ops2, ref i2); }
                    else { op2 = del2 + s1.Length; op1 = Next(ops1, ref i1); }
                    break;
                case (string s1, int r2) when r2 > 0:
                    if (s1.Length > r2) { result.Insert(s1[..r2]); op1 = s1[r2..]; op2 = Next(ops2, ref i2); }
                    else if (s1.Length == r2) { result.Insert(s1); op1 = Next(ops1, ref i1); op2 = Next(ops2, ref i2); }
                    else { result.Insert(s1); op2 = r2 - s1.Length; op1 = Next(ops1, ref i1); }
                    break;
                case (int r1, int del2) when r1 > 0 && del2 < 0:
                    if (r1 > -del2) { result.Delete(del2); op1 = r1 + del2; op2 = Next(ops2, ref i2); }
                    else if (r1 == -del2) { result.Delete(del2); op1 = Next(ops1, ref i1); op2 = Next(ops2, ref i2); }
                    else { result.Delete(r1); op2 = del2 + r1; op1 = Next(ops1, ref i1); }
                    break;
                default:
                    throw new InvalidOperationException("Compose: invalid operation components");
            }
        }
        return result;
    }

    /// <summary>Transforms two concurrent operations on the same text: apply(apply(s, a), b') == apply(apply(s, b), a').
    /// <paramref name="a"/>'s inserts go first when both insert at the same place.</summary>
    public static (TextOperation APrime, TextOperation BPrime) Transform(TextOperation a, TextOperation b)
    {
        if (a.BaseLength != b.BaseLength) throw new InvalidOperationException("Transform: both operations must apply to the same text");
        var ap = new TextOperation();
        var bp = new TextOperation();
        var ops1 = a._ops; var ops2 = b._ops;
        int i1 = 0, i2 = 0;
        object? op1 = Next(ops1, ref i1), op2 = Next(ops2, ref i2);
        while (op1 != null || op2 != null)
        {
            if (op1 is string s1) { ap.Insert(s1); bp.Retain(s1.Length); op1 = Next(ops1, ref i1); continue; }
            if (op2 is string s2) { ap.Retain(s2.Length); bp.Insert(s2); op2 = Next(ops2, ref i2); continue; }
            if (op1 == null || op2 == null) throw new InvalidOperationException("Transform: operations don't line up");
            int x = (int)op1, y = (int)op2, min;
            if (x > 0 && y > 0)
            {
                if (x > y) { min = y; op1 = x - y; op2 = Next(ops2, ref i2); }
                else if (x == y) { min = y; op1 = Next(ops1, ref i1); op2 = Next(ops2, ref i2); }
                else { min = x; op2 = y - x; op1 = Next(ops1, ref i1); }
                ap.Retain(min);
                bp.Retain(min);
            }
            else if (x < 0 && y < 0)
            {
                if (-x > -y) { op1 = x - y; op2 = Next(ops2, ref i2); }
                else if (x == y) { op1 = Next(ops1, ref i1); op2 = Next(ops2, ref i2); }
                else { op2 = y - x; op1 = Next(ops1, ref i1); }
            }
            else if (x < 0 && y > 0)
            {
                if (-x > y) { min = y; op1 = x + y; op2 = Next(ops2, ref i2); }
                else if (-x == y) { min = y; op1 = Next(ops1, ref i1); op2 = Next(ops2, ref i2); }
                else { min = -x; op2 = y + x; op1 = Next(ops1, ref i1); }
                ap.Delete(min);
            }
            else
            {
                if (x > -y) { min = -y; op1 = x + y; op2 = Next(ops2, ref i2); }
                else if (x == -y) { min = x; op1 = Next(ops1, ref i1); op2 = Next(ops2, ref i2); }
                else { min = x; op2 = y + x; op1 = Next(ops1, ref i1); }
                bp.Delete(min);
            }
        }
        return (ap, bp);
    }

    /// <summary>Where a caret at <paramref name="index"/> of the base text ends up after this operation.</summary>
    public int TransformIndex(int index)
    {
        var newIndex = index;
        foreach (var op in _ops)
        {
            switch (op)
            {
                case int r when r > 0: index -= r; break;
                case string s: newIndex += s.Length; break;
                case int d: newIndex -= Math.Min(index, -d); index += d; break;
            }
            if (index < 0) break;
        }
        return newIndex;
    }

    private static object? Next(List<object> ops, ref int i) => i < ops.Count ? ops[i++] : null;

    // ------------------------------------------------------------------ helpers

    /// <summary>The operation turning <paramref name="from"/> into <paramref name="to"/>: common prefix and suffix are retained and
    /// the middle is diffed line by line, so independent edits in different places stay separate components.</summary>
    public static TextOperation Diff(string from, string to)
    {
        var op = new TextOperation();
        var prefix = 0;
        var max = Math.Min(from.Length, to.Length);
        while (prefix < max && from[prefix] == to[prefix]) prefix++;
        var suffix = 0;
        while (suffix < max - prefix && from[from.Length - 1 - suffix] == to[to.Length - 1 - suffix]) suffix++;
        // keep whole lines in the middle so the line diff below sees complete lines
        while (prefix > 0 && from[prefix - 1] != '\n') prefix--;
        while (suffix > 0 && from.Length - suffix - 1 >= prefix && from[from.Length - suffix - 1] != '\n') suffix--;
        op.Retain(prefix);
        var a = from[prefix..(from.Length - suffix)];
        var b = to[prefix..(to.Length - suffix)];
        DiffLines(op, a, b);
        op.Retain(suffix);
        return op;
    }

    private static void DiffLines(TextOperation op, string a, string b)
    {
        var la = SplitLines(a);
        var lb = SplitLines(b);
        if (la.Count == 0 || lb.Count == 0 || (long)la.Count * lb.Count > 4_000_000)
        {
            op.Delete(a.Length);
            op.Insert(b);
            return;
        }
        // longest common subsequence of lines
        var n = la.Count; var m = lb.Count;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                lcs[i, j] = la[i] == lb[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        int x = 0, y = 0;
        while (x < n || y < m)
        {
            if (x < n && y < m && la[x] == lb[y]) { op.Retain(la[x].Length); x++; y++; }
            else if (y < m && (x == n || lcs[x, y + 1] >= lcs[x + 1, y])) { op.Insert(lb[y]); y++; }
            else { op.Delete(la[x].Length); x++; }
        }
    }

    private static List<string> SplitLines(string s)
    {
        var list = new List<string>();
        var start = 0;
        for (var i = 0; i < s.Length; i++)
            if (s[i] == '\n') { list.Add(s[start..(i + 1)]); start = i + 1; }
        if (start < s.Length) list.Add(s[start..]);
        return list;
    }

    /// <summary>The base-text ranges an operation changes: [start, end) of deleted text, or an empty range at an insertion point.</summary>
    public IEnumerable<(int Start, int End)> ChangedRanges()
    {
        var i = 0;
        foreach (var op in _ops)
        {
            switch (op)
            {
                case int r when r > 0: i += r; break;
                case string: yield return (i, i); break;
                case int d: yield return (i, i - d); i -= d; break;
            }
        }
    }

    // ------------------------------------------------------------------ JSON (ot.js wire format: [5, "ab", -2])

    public object[] ToWire() => _ops.ToArray();

    public static TextOperation FromWire(IEnumerable<object?> components)
    {
        var op = new TextOperation();
        foreach (var c in components)
        {
            switch (c)
            {
                case string s: op.Insert(s); break;
                case int n when n > 0: op.Retain(n); break;
                case int n when n < 0: op.Delete(n); break;
                case long n when n > 0: op.Retain(checked((int)n)); break;
                case long n when n < 0: op.Delete(checked((int)n)); break;
                case double n when n > 0: op.Retain(checked((int)n)); break;
                case double n when n < 0: op.Delete(checked((int)n)); break;
                case JsonElement { ValueKind: JsonValueKind.String } js: op.Insert(js.GetString()!); break;
                case JsonElement { ValueKind: JsonValueKind.Number } jn when jn.GetInt32() > 0: op.Retain(jn.GetInt32()); break;
                case JsonElement { ValueKind: JsonValueKind.Number } jn when jn.GetInt32() < 0: op.Delete(jn.GetInt32()); break;
                default: throw new FormatException("Invalid text operation component");
            }
        }
        return op;
    }

    public static TextOperation FromJson(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? FromWire(array.EnumerateArray().Select(e => (object?)e)) : throw new FormatException("Expected an array");

    public bool Equals(TextOperation? other) =>
        other != null && BaseLength == other.BaseLength && TargetLength == other.TargetLength && _ops.SequenceEqual(other._ops);

    public override bool Equals(object? obj) => Equals(obj as TextOperation);
    public override int GetHashCode() => HashCode.Combine(BaseLength, TargetLength, _ops.Count);
    public override string ToString() => "[" + string.Join(", ", _ops.Select(o => o is string s ? JsonSerializer.Serialize(s) : o.ToString())) + "]";
}

/// <summary>The server side of a shared text: one authoritative text and the history of applied operations. Clients send an
/// operation with the revision it was based on; it is transformed against everything applied since and appended.</summary>
public sealed class SharedTextDocument(string text)
{
    private readonly List<TextOperation> _history = new();

    public string Text { get; private set; } = text;
    public int Revision => _history.Count;

    /// <summary>Applies a client operation made against <paramref name="revision"/>; returns it transformed to the current revision.</summary>
    public TextOperation Receive(int revision, TextOperation op)
    {
        if (revision < 0 || revision > Revision) throw new InvalidOperationException($"Unknown revision {revision} (server is at {Revision})");
        for (var k = revision; k < _history.Count; k++) op = TextOperation.Transform(op, _history[k]).APrime;
        Text = op.Apply(Text);
        _history.Add(op);
        return op;
    }

    /// <summary>Everything applied after <paramref name="revision"/>, as one operation.</summary>
    public TextOperation Since(int revision)
    {
        if (revision < 0 || revision > Revision) throw new InvalidOperationException($"Unknown revision {revision}");
        var op = new TextOperation().Retain(revision == Revision ? Text.Length : _history[revision].BaseLength);
        for (var k = revision; k < _history.Count; k++) op = op.Compose(_history[k]);
        return op;
    }

    /// <summary>Transforms a caret position from <paramref name="revision"/> to the current revision.</summary>
    public int TransformIndex(int revision, int index)
    {
        for (var k = Math.Max(0, revision); k < _history.Count; k++) index = _history[k].TransformIndex(index);
        return Math.Clamp(index, 0, Text.Length);
    }
}

/// <summary>The client side of a shared text (the algorithm the browser editor runs): at most one operation in flight
/// (<see cref="Outstanding"/>), later local edits are composed into <see cref="Buffer"/> until it is acknowledged.</summary>
public sealed class TextClient(string text, int revision)
{
    public string Text { get; private set; } = text;
    public int Revision { get; private set; } = revision;
    public TextOperation? Outstanding { get; private set; }
    public TextOperation? Buffer { get; private set; }
    public bool Synchronized => Outstanding == null;

    /// <summary>A local edit. Returns the operation to send (with <see cref="Revision"/>) or null when one is already in flight.</summary>
    public TextOperation? ApplyLocal(TextOperation op)
    {
        Text = op.Apply(Text);
        if (Outstanding == null) { Outstanding = op; return op; }
        Buffer = Buffer == null ? op : Buffer.Compose(op);
        return null;
    }

    /// <summary>The server acknowledged the outstanding operation. Returns the buffered operation to send next, if any.</summary>
    public TextOperation? ServerAck()
    {
        if (Outstanding == null) throw new InvalidOperationException("No operation is waiting for an acknowledgement");
        Revision++;
        Outstanding = Buffer;
        Buffer = null;
        return Outstanding;
    }

    /// <summary>An operation from someone else. Returns it transformed for the local text (already applied).</summary>
    public TextOperation ApplyServer(TextOperation op)
    {
        Revision++;
        if (Outstanding != null)
        {
            (Outstanding, op) = TextOperation.Transform(Outstanding, op);
            if (Buffer != null) (Buffer, op) = TextOperation.Transform(Buffer, op);
        }
        Text = op.Apply(Text);
        return op;
    }

    /// <summary>Maps a server-side caret position to the local text (through the pending operations).</summary>
    public int ToLocal(int index)
    {
        if (Outstanding != null) index = Outstanding.TransformIndex(index);
        if (Buffer != null) index = Buffer.TransformIndex(index);
        return index;
    }
}
