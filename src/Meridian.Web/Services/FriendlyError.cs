using Microsoft.JSInterop;

namespace Meridian.Web.Services;

/// <summary>
/// Plain-language reasons for the exceptions an import, export or browser call can end with, for messages shown to users.
/// Domain exceptions (<see cref="InvalidOperationException"/>, <see cref="FormatException"/>) already carry a message
/// written for people and are passed through (first line only); framework exceptions, whose messages are technical
/// ("'&lt;' is an unexpected token", "The given key was not present in the dictionary"), get a sentence instead.
/// </summary>
public static class FriendlyError
{
    private const int MaxLength = 300;

    /// <summary>Why it failed, as a lower-case clause to follow "Could not import X: ".</summary>
    public static string Describe(Exception ex) => ex switch
    {
        System.Xml.XmlException x => x.LineNumber > 0 ? $"the file is not valid XML (near line {x.LineNumber})" : "the file is not valid XML",
        System.Text.Json.JsonException => "the file is not a valid Meridian project (JSON)",
        InvalidDataException => "the file is damaged or not in the expected format",
        DocumentFormat.OpenXml.Packaging.OpenXmlPackageException => "the file is not a valid Excel workbook",
        UnauthorizedAccessException => "Meridian is not allowed to read or write that file",
        IOException => "the file could not be read",
        JSDisconnectedException => "the connection to the browser was lost; reload the page",
        JSException => "the browser blocked it",
        OperationCanceledException => "it took too long and was stopped",
        KeyNotFoundException or ArgumentException or NullReferenceException or IndexOutOfRangeException =>
            "something in the model was not as expected (please report this with the model if it happens again)",
        _ => Clean(ex.Message),
    };

    /// <summary>The first line of a message, without a trailing full stop, at most <see cref="MaxLength"/> characters.</summary>
    private static string Clean(string message)
    {
        var line = message.Split('\n', 2)[0].Trim().TrimEnd('.');
        if (line.Length == 0) return "an unexpected problem occurred";
        return line.Length <= MaxLength ? line : line[..(MaxLength - 1)] + "…";
    }
}
