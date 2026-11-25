using Meridian.Core.CodeSync;

namespace Meridian.Tests;

public class CodeSyncReadableTests
{
    [Theory]
    [InlineData("CodeSyncRunner", false, "Code sync runner")]
    [InlineData("IGitHost", true, "Git host")]
    [InlineData("IO", true, "IO")]
    [InlineData("MapDesktopUpdates", false, "Map desktop updates")]
    [InlineData("HttpClientFactory", false, "HTTP client factory")]
    [InlineData("HTTPServer2", false, "HTTP server 2")]
    [InlineData("parse_xml_request", false, "Parse XML request")]
    [InlineData("Box`1", false, "Box")]
    [InlineData("Box<T>", false, "Box")]
    [InlineData("ReaderImpl", false, "Reader")]
    [InlineData("Battery", false, "Battery")]
    [InlineData("SysmlV2Importer", false, "SysML v2 importer")]
    [InlineData("N0", false, "N0")]
    [InlineData("Version2", false, "Version 2")]
    public void Identifiers_read_as_words(string identifier, bool isInterface, string expected) =>
        Assert.Equal(expected, CodeNames.Humanize(identifier, isInterface));

    [Theory]
    [InlineData("user != null", "Is user set?")]
    [InlineData("user is null", "Is user missing?")]
    [InlineData("_project == null", "Is project missing?")]
    [InlineData("items.Count == 0", "Is items empty?")]
    [InlineData("items.Count > 0", "Is items not empty?")]
    [InlineData("string.IsNullOrEmpty(name)", "Is name empty?")]
    [InlineData("order.Lines.Any()", "Does order lines have any?")]
    [InlineData("IsReadOnly", "Is read only?")]
    [InlineData("user.IsSignedIn", "Is user signed in?")]
    [InlineData("!user.IsSignedIn", "Is user not signed in?")]
    [InlineData("HasErrors", "Has errors?")]
    [InlineData("count > 3", "Is count more than 3?")]
    [InlineData("mode == Mode.Safe", "Is mode safe?")]
    [InlineData("shape is Diagram d", "Is shape a diagram?")]
    [InlineData("cache.TryGetValue(key, out var hit)", "Is value found in cache?")]
    [InlineData("map.ContainsKey(id)", "Map contains key?")]
    [InlineData("_battery.IsLow()", "Is battery low?")]
    [InlineData("overview is { Diagram: { } od, Created: false }", "Overview matches?")]
    [InlineData("user is { }", "Is user set?")]
    [InlineData("a != null && b.IsReady", "A is set and b is ready?")]
    [InlineData("(x > 0)", "Is x more than 0?")]
    [InlineData("", "Condition holds?")]
    public void Conditions_read_as_questions(string condition, string expected) =>
        Assert.Equal(expected, CodePhrases.Question(condition));

    [Theory]
    [InlineData("LoadProjectAsync", "db", "Load project from DB")]
    [InlineData("Add", "_items", "Add to items")]
    [InlineData("Validate", null, "Validate")]
    [InlineData("SaveChanges", "this", "Save changes")]
    [InlineData("Render", "renderer", "Render renderer")]
    [InlineData("SendAsync", "client", "Send to client")]
    [InlineData("ToHashSet", "changes.Added", "Collect changes added")]
    public void Calls_read_as_steps(string method, string? receiver, string expected) =>
        Assert.Equal(expected, CodePhrases.Step(method, receiver));

    [Fact]
    public void Loops_switches_and_endings_read_as_words()
    {
        Assert.Equal("More items?", CodePhrases.LoopQuestion(CodeLoopKind.ForEach, "var item in items"));
        Assert.Equal("More request pending files?", CodePhrases.LoopQuestion(CodeLoopKind.ForEach, "f in request.PendingFiles"));
        Assert.Equal("Is i less than n?", CodePhrases.LoopQuestion(CodeLoopKind.For, "int i = 0; i < n; i++"));
        Assert.Equal("Keep going?", CodePhrases.LoopQuestion(CodeLoopKind.While, "true"));
        Assert.Equal("More diagrams?", CodePhrases.LoopQuestion(CodeLoopKind.ForEach, "d in m.Diagrams.Where(d => !addedIds.Contains(d.Id) && !CodeBehaviourMapper.IsGenera…"));
        Assert.Equal("What is state mode?", CodePhrases.SwitchQuestion("state.Mode"));
        Assert.Equal("safe or off", CodePhrases.CaseLabel("Mode.Safe, Mode.Off"));
        Assert.Equal("otherwise", CodePhrases.CaseLabel("default"));
        Assert.Equal("Fail: invalid operation", CodePhrases.Fail("new InvalidOperationException(\"x\")"));
        Assert.Equal("Return result", CodePhrases.Return("result"));
        Assert.Equal("Done", CodePhrases.Return(""));
        Assert.Equal("Create order", CodePhrases.Create("Order"));
        Assert.Equal("Set total, count", CodePhrases.Set(["total", "count"]));
    }
}
