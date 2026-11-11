using System.Diagnostics;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization.Reports;
using Xunit.Abstractions;

namespace Meridian.Tests;

/// <summary>
/// Whole-model reports of the 200,000-element model (opt-in: MERIDIAN_HEAVY=1): the SDD and the model overview in HTML,
/// Word and PDF, streamed to files with bounded memory and time, plus the original failure (the unfiltered SDD, whose HTML
/// is bigger than a .NET string can hold). The numbers it prints are the ones in docs/PERFORMANCE.md.
/// </summary>
public class HeavyReportTests(ITestOutputHelper output)
{
    private static bool Heavy => Environment.GetEnvironmentVariable("MERIDIAN_HEAVY") is "1" or "true";

    [SkippableFact]
    public void Heavy_200k_Reports_Stream_With_Bounded_Memory_And_Time()
    {
        Skip.IfNot(Heavy, "Set MERIDIAN_HEAVY=1 to generate the 200,000-element reports.");
        var m = LargeModelGenerator.Generate(LargeModelOptions.Cameo(200_000));
        Assert.InRange(m.Count, 190_000, 215_000);
        var baseline = Settle();
        Log($"Model: {m.Count:N0} elements, {baseline / 1048576:N0} MB managed heap after loading");

        // Limits are ~5× the M4 Pro numbers; memory is what the report adds on top of the model.
        (DocTemplate Template, ReportFormat Format, int Seconds, long ExtraMb)[] runs =
        [
            (ReportTemplates.Sdd(), ReportFormat.Html, 60, 1_024),
            (ReportTemplates.Sdd(), ReportFormat.Docx, 240, 1_536),
            (ReportTemplates.Sdd(), ReportFormat.Pdf, 300, 3_072),
            (ReportTemplates.ModelOverview(), ReportFormat.Html, 60, 1_024),
            (ReportTemplates.ModelOverview(), ReportFormat.Docx, 240, 1_536),
            (ReportTemplates.ModelOverview(), ReportFormat.Pdf, 300, 3_072),
        ];
        foreach (var (template, format, seconds, extraMb) in runs)
        {
            var path = Path.Combine(Path.GetTempPath(), $"meridian-heavy-{Guid.NewGuid():N}.{ReportEngine.Extension(format)}");
            try
            {
                PreparedReport? prepared = null;
                var (elapsed, peak) = Measure(baseline, () =>
                {
                    prepared = ReportEngine.Prepare(m, template);
                    using var file = File.Create(path);
                    ReportEngine.Write(prepared, format, file);
                });
                var size = new FileInfo(path).Length;
                Log($"{template.Name} {format}: {elapsed.TotalSeconds:0.0} s, +{peak / 1048576:N0} MB peak, {size / 1048576.0:0.0} MB file, " +
                    $"{prepared!.Stats.Nodes:N0} blocks, {prepared.Stats.TableRows:N0} table rows, {prepared.Stats.FigureCount:N0} figures, ~{prepared.EstimatedPages:N0} pages estimated");
                Assert.True(size > 1_000_000, $"{template.Name} {format}: {size} bytes");
                Assert.True(elapsed < TimeSpan.FromSeconds(seconds), $"{template.Name} {format} took {elapsed} (limit {seconds} s)");
                Assert.True(peak < extraMb * 1048576, $"{template.Name} {format} added {peak / 1048576:N0} MB (limit {extraMb:N0} MB)");
            }
            finally { File.Delete(path); }
        }

        // The original failure: the SDD with its requirement × block matrix unfiltered (16,197 rows × ~300 tables) — ~2 GB of
        // HTML, which threw OverflowException when it was built as one string. Now it streams, and the size check flags it first.
        var unfiltered = ReportTemplates.Sdd();
        foreach (var s in unfiltered.AllSections())
            foreach (var b in s.Blocks)
                if (b is TraceMatrixBlock x) x.HideEmpty = false;
        var sink = new CountingStream();
        PreparedReport? full = null;
        var (time, extra) = Measure(baseline, () =>
        {
            full = ReportEngine.Prepare(m, unfiltered);
            ReportEngine.Write(full, ReportFormat.Html, sink);
        });
        Log($"Unfiltered SDD HTML: {time.TotalSeconds:0.0} s, +{extra / 1048576:N0} MB peak, {sink.Written / 1048576.0:N0} MB written, " +
            $"{full!.Stats.TableRows:N0} table rows, ~{full.EstimatedPages:N0} pages estimated");
        // A string holds at most ~1.07 billion characters; this HTML is (nearly all ASCII) about twice that.
        Assert.True(sink.Written > 1_100_000_000L, $"{sink.Written:N0} bytes: more than a .NET string holds");
        Assert.True(ReportEngine.ShouldWarn(full.Stats));
        Assert.True(time < TimeSpan.FromSeconds(150), $"unfiltered SDD took {time}");
        Assert.True(extra < 1_536L * 1048576, $"unfiltered SDD added {extra / 1048576:N0} MB");
    }

    private void Log(string line)
    {
        output.WriteLine(line);
        Console.WriteLine(line);
    }

    private static long Settle()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(true);
    }

    /// <summary>Runs <paramref name="work"/>, sampling the managed heap every 50 ms; returns the time and the peak above <paramref name="baseline"/>.</summary>
    private static (TimeSpan Elapsed, long Peak) Measure(long baseline, Action work)
    {
        Settle();
        long peak = 0;
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                peak = Math.Max(peak, GC.GetTotalMemory(false));
                try { await Task.Delay(50, stop.Token); } catch (OperationCanceledException) { }
            }
        });
        var sw = Stopwatch.StartNew();
        work();
        sw.Stop();
        stop.Cancel();
        sampler.Wait();
        return (sw.Elapsed, Math.Max(0, peak - baseline));
    }

    private sealed class CountingStream : Stream
    {
        public long Written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written += count;
        public override void Write(ReadOnlySpan<byte> buffer) => Written += buffer.Length;
    }
}
