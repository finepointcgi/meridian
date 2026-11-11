using System.Globalization;
using System.Text;

namespace Meridian.Serialization;

/// <summary>A chaining <c>Append</c> facade over a <see cref="TextWriter"/>, so the HTML writers read like StringBuilder code
/// but stream their output (a whole-model report can exceed .NET's 2 GB string limit).</summary>
internal sealed class HtmlSink(TextWriter writer)
{
    public HtmlSink Append(string? s) { writer.Write(s); return this; }
    public HtmlSink Append(char c) { writer.Write(c); return this; }
    public HtmlSink Append(int i) { writer.Write(i.ToString(CultureInfo.InvariantCulture)); return this; }

    /// <summary>UTF-8 without a byte-order mark (what <c>Encoding.UTF8.GetBytes</c> of the whole string gave), large buffer.</summary>
    public static StreamWriter Utf8(Stream output, bool leaveOpen = true) =>
        new(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 64 * 1024, leaveOpen);
}
