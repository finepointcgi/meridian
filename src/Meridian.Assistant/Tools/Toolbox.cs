namespace Meridian.Assistant.Tools;

/// <summary>The fixed tool list sent with every request (stable order, so the prompt cache keeps hitting).</summary>
public static class Toolbox
{
    public static IReadOnlyList<AssistantTool> All { get; } =
    [
        new SearchElementsTool(),
        new GetElementTool(),
        new ListDiagramsTool(),
        new GetTraceabilityTool(),
        new ValidateModelTool(),
        new GetSysmlTextTool(),
        new ReportFindingsTool(),
        new CreateElementTool(),
        new UpdateFieldsTool(),
        new CreateRelationshipTool(),
        new DeleteElementTool(),
        new CreateDiagramTool(),
        new ApplySysmlTextTool(),
    ];

    public static AssistantTool? Find(string name) => All.FirstOrDefault(t => t.Name == name);
}
