namespace Meridian.Serialization.Reports;

/// <summary>An enumerator with one item of lookahead, so the writers can group consecutive nodes (list items, a heading
/// with what follows it) while the nodes are still being produced.</summary>
internal sealed class Lookahead<T> : IDisposable
{
    private readonly IEnumerator<T> _e;
    private bool _has;
    private T _peeked = default!;

    public Lookahead(IEnumerable<T> items) => _e = items.GetEnumerator();

    /// <summary>0-based position of the item <see cref="Next"/> returned last (-1 before the first).</summary>
    public long Index { get; private set; } = -1;

    public bool Next(out T value)
    {
        if (_has)
        {
            value = _peeked;
            _has = false;
            _peeked = default!;
        }
        else if (_e.MoveNext()) value = _e.Current;
        else { value = default!; return false; }
        Index++;
        return true;
    }

    public bool Peek(out T value)
    {
        if (!_has)
        {
            if (!_e.MoveNext()) { value = default!; return false; }
            _peeked = _e.Current;
            _has = true;
        }
        value = _peeked;
        return true;
    }

    public void Dispose() => _e.Dispose();
}
