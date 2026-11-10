using Meridian.Core.CodeSync;
using Meridian.Serialization.CodeSync;
using Meridian.Web.CodeSync;
using Meridian.Web.CodeSync.Hosts;
using Microsoft.Extensions.DependencyInjection;

namespace Meridian.Web.Tests;

public class CodeSyncLanguageTests
{
    [Fact]
    public void New_connections_default_to_auto_and_accept_every_language()
    {
        var c = new CodeRepoConfig { Owner = "acme", Repo = "sat" };
        Assert.Equal("auto", c.Language);
        c.Language = "  ";
        c.Normalize();
        Assert.Equal("auto", c.Language);
        foreach (var lang in new[] { "auto", "csharp", "typescript", "java", "python", "Java" })
        {
            c.Language = lang;
            c.Normalize();
            Assert.Null(c.Validate());
        }
        c.Language = "cobol";
        Assert.NotNull(c.Validate());
        Assert.Equal("csharp", CodeRepoConfig.Parse("""{"language":"csharp"}""").Language);   // existing connections keep theirs
    }

    [Fact]
    public void Readers_are_registered_and_the_snapshot_filter_follows_the_language()
    {
        var services = new ServiceCollection().AddMeridianCodeSync();
        var readers = services.Where(s => s.ServiceType == typeof(ICodeReader)).Select(s => s.ImplementationType).ToList();
        Assert.Equal([typeof(MultiLanguageCodeReader), typeof(CSharpCodeReader), typeof(TypeScriptCodeReader), typeof(JavaCodeReader), typeof(PythonCodeReader)], readers);

        var auto = CodeSyncHostService.DefaultInclude("auto");
        Assert.True(auto("src/a.cs") && auto("web/a.ts") && auto("A.java") && auto("pkg/a.py"));
        Assert.False(auto("README.md"));
        Assert.False(auto("node_modules/x/index.d.ts"));
        Assert.True(CodeSyncHostService.DefaultInclude("python")("a.py"));
        Assert.False(CodeSyncHostService.DefaultInclude("python")("a.cs"));
    }
}
