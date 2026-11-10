using System.Text;
using Meridian.Scripting;

namespace HelloPlugin;

/// <summary>
/// A minimal Meridian plugin showing every extension point: a command, a validation rule, an exporter and an importer.
/// </summary>
public sealed class HelloPlugin : IMeridianPlugin
{
    public string Name => "Hello Plugin";
    public string Version => "1.0.0";

    public void Initialize(IPluginHost host)
    {
        host.Log($"Hello from {Name} (API {host.ApiVersion}, {(host.LocalMode ? "local" : "server")} mode)");

        // 1. A command: shows up in the command palette (⌘K) and the Plugins ribbon menu. One undo step.
        host.RegisterCommand("Hello: say hello", model =>
        {
            var blocks = model.OfKind("Block").Count;
            model.Log($"Hello, {model.ModelName}! It has {blocks} blocks.");
            model.Status($"Hello! {blocks} blocks");
        });

        host.RegisterCommand("Hello: mark selection as reviewed", model =>
        {
            if (model.Selection is not { } e) { model.Status("Select an element first"); return; }
            e.SetTag("reviewed", DateTime.UtcNow.ToString("yyyy-MM-dd"));
            model.Log($"Marked {e.QualifiedName} as reviewed");
        });

        // 2. A validation rule: runs with every validation and model-health refresh. The API it receives is read-only.
        host.RegisterValidationRule("HELLO-001", "Blocks should be documented", model =>
            model.OfKind("Block")
                .Where(b => string.IsNullOrWhiteSpace(b.Documentation))
                .Select(b => new PluginIssue(IssueSeverity.Info, $"Block '{b.Name}' has no documentation", b)));

        // 3. An exporter: Plugins ▸ Export ▸ "Element outline (Markdown)" downloads the returned bytes.
        host.RegisterExporter("Element outline (Markdown)", ".md", model =>
        {
            var sb = new StringBuilder($"# {model.ModelName}\n\n");
            void Walk(IElement e, int depth)
            {
                foreach (var c in e.Children.Where(c => !c.IsRelationship && !c.IsDiagram))
                {
                    sb.Append(new string(' ', depth * 2)).Append("- **").Append(c.Name).Append("** _").Append(c.Kind).AppendLine("_");
                    Walk(c, depth + 1);
                }
            }
            Walk(model.Root, 0);
            return Encoding.UTF8.GetBytes(sb.ToString());
        });

        // 4. An importer: one block per non-empty line of a text file, in a package named after the file.
        host.RegisterImporter("Block names (text, one per line)", ".txt", (model, bytes, fileName) =>
        {
            var package = model.Create(model.Root, "Package", Path.GetFileNameWithoutExtension(fileName));
            foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
                model.Create(package, "Block", line);
        });
    }
}
