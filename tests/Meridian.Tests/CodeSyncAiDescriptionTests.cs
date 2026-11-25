using System.Text.Json;
using Meridian.Assistant.Engine;
using Meridian.Assistant.Providers;
using Meridian.Core.CodeSync;
using Meridian.Core.Model;
using Meridian.Serialization.CodeSync;

namespace Meridian.Tests;

/// <summary>
/// The optional AI pass of code sync: what it asks about, how answers land on the model and stay on re-syncs, that
/// unchanged code is not asked again, and the writer's prompt and answer handling. Only fakes: no real provider is called.
/// </summary>
public class CodeSyncAiDescriptionTests
{
    private const string Sdk = """<Project Sdk="Microsoft.NET.Sdk"></Project>""";

    private static readonly IReadOnlyList<CodeFile> Files =
    [
        new("src/Shop.Core/Shop.Core.csproj", Sdk),
        new("src/Shop.Core/Orders.cs", """
            namespace Shop.Core
            {
                public interface IStore { void Save(Order order); }
                public class Order { public double Total { get; set; } }
                public class Checkout
                {
                    private readonly IStore _store;
                    public void Pay(Order order) { if (order.Total > 0) _store.Save(order); }
                }
            }
            """),
        new("src/Shop.Data/Shop.Data.csproj", Sdk),
        new("src/Shop.Data/Sql.cs", "namespace Shop.Data { public class SqlStore : Shop.Core.IStore { public void Save(Shop.Core.Order order) { } } }"),
    ];

    private static CodeSyncOptions Options(string commit = "c1") => new() { RepoId = "r", Commit = commit, RootPackage = "Shop", Behaviour = CodeBehaviourMode.AllPublic };

    /// <summary>Answers every item with "AI " + its kind and code, and records what it was asked.</summary>
    private sealed class FakeDescriber : ICodeDescriber
    {
        public List<CodeDescribeItem> Asked { get; } = [];
        public string Name => "Fake";

        public Task<CodeDescribeResult> DescribeAsync(IReadOnlyList<CodeDescribeItem> items, CancellationToken ct = default)
        {
            Asked.AddRange(items);
            return Task.FromResult(new CodeDescribeResult(items.Select(i => new CodeDescription(i.Key, $"AI {i.Kind} {i.Code.Split('.')[^1]}", i.Kind is "decision" or "step" ? "" : $"Purpose of {i.Code}.")).ToList()));
        }
    }

    private static async Task<(ModelRepository M, CodeModel Code, FakeDescriber Ai, IReadOnlyList<CodeDescribeItem> Items)> SyncedAndDescribed()
    {
        var m = new ModelRepository("Shop");
        var code = new CSharpCodeReader().Read(Files);
        CodeModelMapper.Apply(m, code, Options());
        var root = CodeModelMapper.FindRootPackage(m, "Shop")!;
        var items = CodeAiDescriptions.Collect(m, root, "r");
        var ai = new FakeDescriber();
        var result = await ai.DescribeAsync(items);
        CodeAiDescriptions.Apply(m, items, result.Items);
        return (m, code, ai, items);
    }

    private static Element S(ModelRepository m, string symbol) => m.AllElements.Single(e => e.Get(CodeFieldKeys.Symbol) == symbol && m.Contains(e));

    [Fact]
    public async Task It_asks_about_subsystems_packages_key_blocks_and_behaviour_and_the_answers_stay_on_resyncs()
    {
        var (m, code, ai, items) = await SyncedAndDescribed();
        var kinds = items.Select(i => i.Kind).ToHashSet();
        Assert.Subset(kinds, new HashSet<string> { "subsystem", "package", "block", "interface", "decision", "step" });
        Assert.Equal("subsystem", items[0].Kind);   // the most important first

        var checkout = S(m, "Shop.Core.Checkout");
        Assert.Equal(("AI block Checkout", "AI block Checkout", "Purpose of Shop.Core.Checkout."), (checkout.Name, checkout.Get(CodeModelMapper.LabelKey), checkout.Documentation));
        var decision = m.AllElements.Single(e => e.Kind == ElementKind.DecisionNode && e.Documentation.StartsWith("if (order.Total > 0)", StringComparison.Ordinal));
        Assert.StartsWith("AI decision", decision.Name);

        // A re-sync of the same code keeps every AI name and has nothing new to ask.
        var resync = CodeModelMapper.Apply(m, code, Options("c2"));
        Assert.True(resync.IsEmpty, string.Join(", ", resync.Changed.Select(id => m.Find(id)?.Name)));
        Assert.Equal("AI block Checkout", checkout.Name);
        Assert.StartsWith("AI decision", decision.Name);
        Assert.Empty(CodeAiDescriptions.Collect(m, CodeModelMapper.FindRootPackage(m, "Shop")!, "r"));
        Assert.Equal("Shop.Core.Checkout", checkout.Get(CodeFieldKeys.Symbol));
    }

    [Fact]
    public async Task Changed_code_is_asked_again_and_a_changed_condition_goes_back_to_the_code_wording()
    {
        var (m, _, _, _) = await SyncedAndDescribed();
        var changed = Files.Select(f => f.Path.EndsWith("Orders.cs") ? f with { Content = f.Content.Replace("order.Total > 0", "order.Total > 100").Replace("public double Total", "public string Note { get; set; } public double Total") } : f).ToList();
        CodeModelMapper.Apply(m, new CSharpCodeReader().Read(changed), Options("c2"));
        var decision = m.AllElements.Single(e => e.Kind == ElementKind.DecisionNode && e.Documentation.StartsWith("if (order.Total > 100)", StringComparison.Ordinal));
        Assert.Equal("Is order total more than 100?", decision.Name);   // the AI wording was for the old condition
        var again = CodeAiDescriptions.Collect(m, CodeModelMapper.FindRootPackage(m, "Shop")!, "r");
        Assert.Contains(again, i => i.Key == decision.Id);
        Assert.Contains(again, i => i.Code == "Shop.Core.Order");   // its members changed
        Assert.DoesNotContain(again, i => i.Code == "Shop.Core.Checkout");
    }

    [Fact]
    public async Task A_doc_comment_wins_over_an_AI_purpose()
    {
        var files = Files.Select(f => f.Path.EndsWith("Orders.cs") ? f with { Content = f.Content.Replace("public class Order {", "/// <summary>A customer's order.</summary>\n public class Order {") } : f).ToList();
        var m = new ModelRepository("Shop");
        CodeModelMapper.Apply(m, new CSharpCodeReader().Read(files), Options());
        var items = CodeAiDescriptions.Collect(m, CodeModelMapper.FindRootPackage(m, "Shop")!, "r");
        CodeAiDescriptions.Apply(m, items, (await new FakeDescriber().DescribeAsync(items)).Items);
        Assert.Equal("A customer's order.", S(m, "Shop.Core.Order").Documentation);
        Assert.Equal("AI block Order", S(m, "Shop.Core.Order").Name);
    }

    // ================================================================ the writer

    /// <summary>Answers with a submit_descriptions tool call built from the prompt's keys, and keeps the request.</summary>
    private sealed class FakeProvider(Func<IReadOnlyList<string>, string>? answer = null) : IChatProvider
    {
        public List<ChatRequest> Requests { get; } = [];
        public ChatProviderInfo Info => new(ChatProviderKind.Anthropic, "fake-model");

        public Task<ChatResponse> CompleteAsync(ChatRequest request, IChatStream stream, CancellationToken ct)
        {
            Requests.Add(request);
            var prompt = string.Concat(request.Messages.Select(x => x.Text));
            var keys = prompt.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..].Split(" | ")[0]).ToList();
            var json = answer?.Invoke(keys) ?? JsonSerializer.Serialize(new { items = keys.Select(k => new { key = k, name = "Named " + k, purpose = "Does " + k }) });
            var input = JsonDocument.Parse(json).RootElement.Clone();
            var message = new ChatMessage(ChatRole.Assistant, [new ToolCallPart("t1", CodeDescriptionWriter.ToolName, input, json)]);
            return Task.FromResult(new ChatResponse { Stop = ChatStopReason.ToolUse, Message = message });
        }

        public string? DescribeError(Exception e) => e.Message;
    }

    [Fact]
    public async Task The_writer_sends_items_in_batches_and_keeps_only_answers_for_keys_it_asked()
    {
        var items = Enumerable.Range(0, CodeDescriptionWriter.BatchSize + 5)
            .Select(i => new CodeDescribeItem($"k{i}", "block", $"App.Thing{i}", $"Thing {i}", "members: Run()")).ToList();
        var provider = new FakeProvider();
        var result = await new CodeDescriptionWriter(provider).DescribeAsync(items);
        Assert.Null(result.Error);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(items.Count, result.Items.Count);
        Assert.Equal(("Named k3", "Does k3"), (result.Items[3].Name, result.Items[3].Purpose));
        Assert.Contains("App.Thing0", string.Concat(provider.Requests[0].Messages.Select(x => x.Text)));

        var odd = new FakeProvider(_ => """{"items":[{"key":"nope","name":"X","purpose":""},{"key":"k0","name":"  ","purpose":""},{"key":"k1","name":"Good","purpose":""}]}""");
        var few = await new CodeDescriptionWriter(odd).DescribeAsync(items.Take(3).ToList());
        Assert.Equal(["k1"], few.Items.Select(i => i.Key));
    }

    [Fact]
    public async Task A_failing_provider_is_reported_not_thrown()
    {
        var result = await new CodeDescriptionWriter(new ThrowingProvider()).DescribeAsync([new CodeDescribeItem("k", "block", "A", "A", "")]);
        Assert.Empty(result.Items);
        Assert.Equal("The provider is down.", result.Error);
    }

    private sealed class ThrowingProvider : IChatProvider
    {
        public ChatProviderInfo Info => new(ChatProviderKind.Anthropic, "fake");
        public Task<ChatResponse> CompleteAsync(ChatRequest request, IChatStream stream, CancellationToken ct) => throw new ChatProviderException("The provider is down.");
        public string? DescribeError(Exception e) => null;
    }
}
