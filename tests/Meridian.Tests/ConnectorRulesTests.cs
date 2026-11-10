using Meridian.Core.Language;
using Meridian.Core.Model;

namespace Meridian.Tests;

/// <summary>The connector a line between two elements becomes automatically (diagram type + end kinds).</summary>
public class ConnectorRulesTests
{
    [Theory]
    [InlineData(DiagramType.UseCase, ElementKind.Actor, ElementKind.UseCase, ElementKind.Association, false)]
    [InlineData(DiagramType.UseCase, ElementKind.UseCase, ElementKind.Actor, ElementKind.Association, false)]
    [InlineData(DiagramType.UseCase, ElementKind.UseCase, ElementKind.UseCase, ElementKind.Include, false)]
    [InlineData(DiagramType.Requirement, ElementKind.Block, ElementKind.Requirement, ElementKind.Satisfy, false)]
    [InlineData(DiagramType.Requirement, ElementKind.Requirement, ElementKind.Block, ElementKind.Satisfy, true)]
    [InlineData(DiagramType.Requirement, ElementKind.Requirement, ElementKind.Requirement, ElementKind.DeriveReqt, false)]
    [InlineData(DiagramType.Requirement, ElementKind.TestCase, ElementKind.Requirement, ElementKind.Verify, false)]
    [InlineData(DiagramType.Requirement, ElementKind.Requirement, ElementKind.TestCase, ElementKind.Verify, true)]
    [InlineData(DiagramType.UseCase, ElementKind.UseCase, ElementKind.Requirement, ElementKind.Satisfy, false)]
    [InlineData(DiagramType.BlockDefinition, ElementKind.Block, ElementKind.Block, ElementKind.Composition, false)]
    [InlineData(DiagramType.Class, ElementKind.Class, ElementKind.Class, ElementKind.Association, false)]
    [InlineData(DiagramType.Class, ElementKind.Class, ElementKind.Interface, ElementKind.Realization, false)]
    [InlineData(DiagramType.InternalBlock, ElementKind.Port, ElementKind.Port, ElementKind.Connector, false)]
    [InlineData(DiagramType.InternalBlock, ElementKind.Property, ElementKind.Port, ElementKind.Connector, false)]
    [InlineData(DiagramType.Parametric, ElementKind.Property, ElementKind.Property, ElementKind.Binding, false)]
    [InlineData(DiagramType.StateMachine, ElementKind.State, ElementKind.State, ElementKind.Transition, false)]
    [InlineData(DiagramType.StateMachine, ElementKind.InitialState, ElementKind.State, ElementKind.Transition, false)]
    [InlineData(DiagramType.Activity, ElementKind.Action, ElementKind.Action, ElementKind.ControlFlow, false)]
    [InlineData(DiagramType.Activity, ElementKind.Action, ElementKind.ObjectNode, ElementKind.ObjectFlow, false)]
    [InlineData(DiagramType.Activity, ElementKind.Action, ElementKind.Block, ElementKind.Allocate, false)]
    [InlineData(DiagramType.BlockDefinition, ElementKind.Block, ElementKind.Activity, ElementKind.Allocate, true)]
    [InlineData(DiagramType.Package, ElementKind.Block, ElementKind.Package, ElementKind.Dependency, false)]
    [InlineData(DiagramType.UseCase, ElementKind.UseCase, ElementKind.Package, ElementKind.Dependency, false)]
    [InlineData(DiagramType.Sequence, ElementKind.Lifeline, ElementKind.Lifeline, ElementKind.Message, false)]
    [InlineData(DiagramType.Communication, ElementKind.CommunicationLifeline, ElementKind.CommunicationLifeline, ElementKind.CommunicationLink, false)]
    [InlineData(DiagramType.Database, ElementKind.Table, ElementKind.Table, ElementKind.ForeignKey, false)]
    [InlineData(DiagramType.FeatureModel, ElementKind.Feature, ElementKind.Feature, ElementKind.FeatureDecomposition, false)]
    public void Default_Connector_Follows_The_Diagram_And_End_Kinds(DiagramType d, ElementKind source, ElementKind target, ElementKind expected, bool reverse)
    {
        var choice = ConnectorRules.Default(d, source, target);
        Assert.NotNull(choice);
        Assert.Equal(expected, choice!.Kind);
        Assert.Equal(reverse, choice.Reverse);
    }

    [Fact]
    public void Bpmn_Picks_A_Sequence_Flow()
    {
        var c = ConnectorRules.Default(DiagramType.Bpmn, ElementKind.BpmnNode, ElementKind.BpmnNode)!;
        Assert.Equal(ElementKind.BpmnFlow, c.Kind);
        Assert.Equal("sequence", c.Subtype);
    }

    [Fact]
    public void Every_Candidate_Is_Valid_And_Alternatives_Are_Offered()
    {
        foreach (var d in Enum.GetValues<DiagramType>())
            foreach (var s in Metamodel.PaletteNodes(d))
                foreach (var t in Metamodel.PaletteNodes(d))
                    foreach (var c in ConnectorRules.Candidates(d, s, t))
                    {
                        var (a, b) = c.Reverse ? (t, s) : (s, t);
                        Assert.Null(Metamodel.CheckEnds(c.Kind, a, b));
                    }

        var uc = ConnectorRules.Candidates(DiagramType.UseCase, ElementKind.UseCase, ElementKind.UseCase).Select(c => c.Kind).ToList();
        Assert.Equal([ElementKind.Include, ElementKind.Extend, ElementKind.Generalization], uc.Take(3));
        var bdd = ConnectorRules.Candidates(DiagramType.BlockDefinition, ElementKind.Block, ElementKind.Block).Select(c => c.Kind).ToList();
        Assert.Contains(ElementKind.Generalization, bdd);
        Assert.Contains(ElementKind.Association, bdd);
        Assert.Equal("«include»", ConnectorRules.Default(DiagramType.UseCase, ElementKind.UseCase, ElementKind.UseCase)!.Label);
    }
}
