using Meridian.Core.Model;

namespace Meridian.Scripting;

/// <summary>A saved script.</summary>
public sealed record SavedScript(string Id, string Name, string Code);

/// <summary>
/// Saved scripts live in the model, so they travel with .meridian files, versions and collaboration:
/// each is a Comment element stereotyped «script» (name = script name, body = code) inside a top-level "Scripts" package.
/// A Comment is used (rather than a new metaclass) because every exporter and the metamodel already handle it.
/// </summary>
public static class ScriptLibrary
{
    public const string Stereotype = "script";
    public const string PackageName = "Scripts";

    public static bool IsScript(Element e) =>
        e.Kind == ElementKind.Comment && e.Stereotypes.Contains(Stereotype, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SavedScript> List(ModelRepository model) =>
        model.AllElements.Where(IsScript)
            .Select(e => new SavedScript(e.Id, string.IsNullOrEmpty(e.Name) ? "Untitled" : e.Name, e.Get(FieldKeys.Body)))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Creates or updates a saved script by name (one undo step). Returns its element id.</summary>
    public static string Save(ModelRepository model, string name, string code)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Untitled" : name.Trim();
        var existing = model.AllElements.FirstOrDefault(e => IsScript(e) && e.Name == name);
        using (model.History.Transaction($"Save script {name}"))
        {
            if (existing != null)
            {
                model.Set(existing, FieldKeys.Body, code);
                return existing.Id;
            }
            var package = model.Root.OwnedElements.FirstOrDefault(e => e.Kind == ElementKind.Package && e.Name == PackageName)
                          ?? model.Create(model.Root, ElementKind.Package, PackageName);
            var e = model.Create(package, ElementKind.Comment, name);
            model.Set(e, FieldKeys.Stereotypes, Stereotype);
            model.Set(e, FieldKeys.Body, code);
            return e.Id;
        }
    }

    public static void Delete(ModelRepository model, string id)
    {
        if (model.Find(id) is { } e && IsScript(e)) model.Delete(e);
    }
}

/// <summary>Example scripts shipped with Meridian (shown in the console's Examples menu).</summary>
public static class ExampleScripts
{
    public static readonly IReadOnlyList<SavedScript> All =
    [
        new("example:count", "Count elements by kind", """
            // Count every element in the model, grouped by kind.
            var counts = Model.All
                .Where(e => !e.IsRelationship)
                .GroupBy(e => e.Kind)
                .OrderByDescending(g => g.Count());
            foreach (var g in counts)
                Log($"{g.Key,-20} {g.Count(),5}");
            Log($"Relationships: {Model.Relationships().Count}");
            return Model.All.Count;
            """),

        new("example:verify", "Add a Verify test case for every unverified requirement", """
            // For each requirement without an incoming Verify, create a test case next to it and connect it.
            var created = 0;
            foreach (var req in Model.OfKind("Requirement"))
            {
                if (req.Incoming("Verify").Count > 0) continue;
                if (req.Children.Any(c => c.Kind == "Requirement")) continue;   // only leaf requirements
                var owner = req.Owner;
                while (owner != null && owner.Kind == "Requirement") owner = owner.Owner;
                var test = Model.Create(owner ?? Model.Root, "TestCase", $"Verify {req.Get(Fields.RequirementId)} {req.Name}".Trim());
                Model.Connect("Verify", test, req);
                created++;
            }
            Status($"Created {created} test cases");
            return created;
            """),

        new("example:pascal", "Rename blocks to PascalCase", """
            // "brake system" / "brake_system" / "brakeSystem" -> "BrakeSystem"
            string Pascal(string s) => string.Concat(
                Regex.Split(s, @"[^A-Za-z0-9]+|(?<=[a-z0-9])(?=[A-Z])")
                     .Where(w => w.Length > 0)
                     .Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));

            var renamed = 0;
            foreach (var block in Model.OfKind("Block"))
            {
                var name = Pascal(block.Name);
                if (name.Length == 0 || name == block.Name) continue;
                Log($"{block.Name} -> {name}");
                block.Name = name;
                renamed++;
            }
            return renamed;
            """),

        new("example:csv", "Export a custom CSV", """
            // Requirements with their satisfying and verifying elements, as CSV.
            string Q(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var sb = new StringBuilder();
            sb.AppendLine("Id,Name,Text,Satisfied by,Verified by,Priority");
            foreach (var r in Model.OfKind("Requirement").OrderBy(r => r.Get(Fields.RequirementId)))
            {
                var satisfiedBy = string.Join("; ", r.Incoming("Satisfy").Select(x => x.Source.Name));
                var verifiedBy = string.Join("; ", r.Incoming("Verify").Select(x => x.Source.Name));
                sb.AppendLine(string.Join(",", Q(r.Get(Fields.RequirementId)), Q(r.Name), Q(r.Get(Fields.Text)),
                    Q(satisfiedBy), Q(verifiedBy), Q(r.GetTag("priority"))));
            }
            Download(Model.ModelName + " requirements.csv", sb.ToString(), "text/csv");
            Log(sb.ToString());
            """),
    ];
}
