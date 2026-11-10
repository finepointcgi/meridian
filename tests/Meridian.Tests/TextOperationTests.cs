using Meridian.Core.Collaboration;

namespace Meridian.Tests;

public class TextOperationTests
{
    private const string Alphabet = "abc xyz\n{};";

    private static string RandomText(Random r, int max = 40) =>
        new(Enumerable.Range(0, r.Next(max)).Select(_ => Alphabet[r.Next(Alphabet.Length)]).ToArray());

    /// <summary>A random edit of <paramref name="s"/>: retains, deletes and inserts walking the whole text.</summary>
    private static TextOperation RandomOp(Random r, string s)
    {
        var op = new TextOperation();
        var left = s.Length;
        while (left > 0)
        {
            var n = Math.Min(left, r.Next(1, Math.Max(2, left / 2 + 2)));
            switch (r.Next(4))
            {
                case 0: op.Insert(RandomText(r, 6)); break;
                case 1: op.Delete(n); left -= n; break;
                default: op.Retain(n); left -= n; break;
            }
        }
        if (r.Next(3) == 0) op.Insert(RandomText(r, 6));
        return op;
    }

    [Fact]
    public void Apply_Compose_And_Transform_Basics()
    {
        var a = new TextOperation().Retain(6).Insert("big ").Retain(5);    // "hello world" -> "hello big world"
        Assert.Equal("hello big world", a.Apply("hello world"));
        var b = new TextOperation().Delete(5).Insert("goodbye").Retain(6);
        Assert.Equal("goodbye world", b.Apply("hello world"));
        var (ap, bp) = TextOperation.Transform(a, b);
        Assert.Equal(ap.Apply(b.Apply("hello world")), bp.Apply(a.Apply("hello world")));
        Assert.Equal("goodbye big world", bp.Apply(a.Apply("hello world")));

        // the first operand's insert wins a tie
        var x = new TextOperation().Retain(2).Insert("X");
        var y = new TextOperation().Retain(2).Insert("Y");
        var (xp, yp) = TextOperation.Transform(x, y);
        Assert.Equal("abXY", xp.Apply(y.Apply("ab")));
        Assert.Equal("abXY", yp.Apply(x.Apply("ab")));

        // wire format round trip
        Assert.Equal(a, TextOperation.FromWire(a.ToWire()));
        Assert.Equal(3, a.TransformIndex(3));    // before the insert: unchanged
        Assert.Equal(11, a.TransformIndex(7));   // after it: shifted by "big "
        Assert.Equal(7, b.TransformIndex(3));    // inside a replaced word: moves after the replacement
    }

    [Fact]
    public void Random_Operations_Transform_And_Compose_Consistently()
    {
        var r = new Random(1234);
        for (var k = 0; k < 2000; k++)
        {
            var s = RandomText(r);
            var a = RandomOp(r, s);
            var b = RandomOp(r, s);
            var (ap, bp) = TextOperation.Transform(a, b);
            Assert.Equal(bp.Apply(a.Apply(s)), ap.Apply(b.Apply(s)));

            var c = RandomOp(r, a.Apply(s));
            Assert.Equal(c.Apply(a.Apply(s)), a.Compose(c).Apply(s));

            var t = RandomText(r);
            Assert.Equal(t, TextOperation.Diff(s, t).Apply(s));
        }
    }

    [Fact]
    public void Diff_Keeps_Separate_Line_Edits_Separate()
    {
        const string text = "a\nb\nc\nd\ne\nf\n";
        var op = TextOperation.Diff(text, "a\nB\nc\nd\nE\nf\n");
        Assert.Equal(2, op.ChangedRanges().Count(r => r.End > r.Start));
    }

    /// <summary>Several clients edit concurrently; messages are delivered in random interleavings (FIFO per client, as over a
    /// connection). When everything is delivered, every client and the server hold the same text.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Concurrent_Random_Edits_Converge(int seed)
    {
        var r = new Random(seed);
        const int clients = 3;
        var initial = RandomText(r, 30);
        var server = new SharedTextDocument(initial);
        var cs = Enumerable.Range(0, clients).Select(_ => new TextClient(initial, 0)).ToList();
        var toServer = Enumerable.Range(0, clients).Select(_ => new Queue<(int Rev, TextOperation Op)>()).ToList();
        var toClient = Enumerable.Range(0, clients).Select(_ => new Queue<(bool Ack, TextOperation? Op)>()).ToList();

        void ServerReceive(int c)
        {
            var (rev, op) = toServer[c].Dequeue();
            var applied = server.Receive(rev, op);
            for (var k = 0; k < clients; k++) toClient[k].Enqueue(k == c ? (true, null) : (false, applied));
        }

        void ClientReceive(int c)
        {
            var (ack, op) = toClient[c].Dequeue();
            if (ack)
            {
                if (cs[c].ServerAck() is { } next) toServer[c].Enqueue((cs[c].Revision, next));
            }
            else cs[c].ApplyServer(op!);
        }

        for (var step = 0; step < 600; step++)
        {
            var c = r.Next(clients);
            switch (r.Next(3))
            {
                case 0:
                    if (cs[c].ApplyLocal(RandomOp(r, cs[c].Text)) is { } send) toServer[c].Enqueue((cs[c].Revision, send));
                    break;
                case 1:
                    if (toServer[c].Count > 0) ServerReceive(c);
                    break;
                default:
                    if (toClient[c].Count > 0) ClientReceive(c);
                    break;
            }
        }
        // drain
        while (toServer.Any(q => q.Count > 0) || toClient.Any(q => q.Count > 0))
            for (var c = 0; c < clients; c++)
            {
                if (toServer[c].Count > 0) ServerReceive(c);
                if (toClient[c].Count > 0) ClientReceive(c);
            }

        Assert.All(cs, c => Assert.True(c.Synchronized));
        Assert.All(cs, c => Assert.Equal(server.Text, c.Text));
        Assert.True(server.Revision > 10);
        Assert.Equal(server.Text, server.Since(0).Apply(initial));
    }
}
