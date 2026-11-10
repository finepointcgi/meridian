using Meridian.Core.Analysis;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Core.Validation;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>The SysML v2 text that docs-site/tutorials/drone-in-20-minutes.md asks readers to paste really builds the drone.</summary>
public class DocsTutorialTests
{
    // Keep in sync with the code blocks in docs-site/tutorials/drone-in-20-minutes.md.
    public const string StructureText = """
        package Structure {
            port def PowerPort;
            port def DataPort;
            part def Drone {
                attribute mass : ScalarValues::Real = 1.2;
                attribute batteryCapacity : ScalarValues::Real = 60;
                attribute hoverPower : ScalarValues::Real = 180;
                attribute endurance : ScalarValues::Real;
                attribute enduranceEq : Endurance;
                part frame : Frame;
                part propulsion : Propulsion;
                part battery : Battery;
                part flightController : FlightController;
                part camera : Camera;
                bind batteryCapacity = enduranceEq.capacity;
                bind hoverPower = enduranceEq.power;
                bind endurance = enduranceEq.minutes;
            }
            part def Frame;
            part def Battery {
                out port powerOut : PowerPort;
            }
            part def Propulsion {
                in port powerIn : PowerPort;
                in port command : DataPort;
            }
            part def FlightController {
                in port powerIn : PowerPort;
                out port command : DataPort;
            }
            part def Camera {
                in port powerIn : PowerPort;
            }
            constraint def Endurance {
                attribute capacity : ScalarValues::Real;
                attribute power : ScalarValues::Real;
                attribute minutes : ScalarValues::Real;
                minutes = capacity * 0.8 / power * 60
            }
        }
        """;

    public const string RequirementsText = """
        package Requirements {
            requirement def <'REQ-001'> 'Flight Time' {
                doc /* The drone shall hover for at least 15 minutes on one battery. */
            }
            requirement def <'REQ-002'> 'Takeoff Mass' {
                doc /* The takeoff mass shall not exceed 1.5 kg. */
            }
            requirement def <'REQ-003'> 'Aerial Imaging' {
                doc /* The drone shall record 4K video of the ground. */
            }
        }
        """;

    private static SysmlSyncResult Apply(ModelRepository m, string packageName, string text)
    {
        var doc = SysMLv2Parser.Parse(text);
        Assert.False(doc.HasErrors, string.Join("\n", doc.Diagnostics));
        var scope = m.Root.OwnedElements.Single(e => e.Kind == ElementKind.Package && e.Name == packageName);
        var r = SysMLv2Sync.Apply(m, scope, doc);
        Assert.True(r.Applied, string.Join("\n", r.Diagnostics));
        return r;
    }

    [Fact]
    public void Drone_Tutorial_Text_Builds_The_Structure_And_Solves_The_Endurance()
    {
        var m = Templates.CreateProject("blank", "Drone");
        Apply(m, "Structure", StructureText);
        Apply(m, "Requirements", RequirementsText);

        var drone = m.OfKind(ElementKind.Block).Single(b => b.Name == "Drone");
        Assert.Equal(6, m.OfKind(ElementKind.Block).Count());
        Assert.Equal(5, drone.OwnedElements.Count(p => p.Kind == ElementKind.Property && m.ResolveType(p)?.Kind == ElementKind.Block));
        Assert.Equal(3, m.OfKind(ElementKind.Requirement).Count());
        Assert.Equal("The takeoff mass shall not exceed 1.5 kg.", m.OfKind(ElementKind.Requirement).Single(r => r.Get(FieldKeys.RequirementId) == "REQ-002").Get(FieldKeys.Text));

        var result = Parametrics.Solve(m, drone);
        Assert.True(result.Complete, string.Join("; ", result.Unsolved.Concat(result.Problems)));
        Assert.Equal(16, result.Values.Single(v => v.Property?.Name == "endurance").Value!.Value, 6);

        Assert.DoesNotContain(new ModelValidator().Validate(m), i => i.Severity == Severity.Error);
    }

    [Fact]
    public void Trade_Study_Tutorial_Scores_Match_The_Page()
    {
        // docs-site/tutorials/first-trade-study.md quotes these scores for the CubeSat battery trade.
        var m = CubeSatSample.Create();
        var study = m.OfKind(ElementKind.TradeStudy).Single();
        var result = TradeStudies.Evaluate(m, TradeStudyDefinition.Parse(study));
        var scores = result.Ranked.Select(r => (r.Alternative.Name, Math.Round(r.Score, 2))).ToList();
        Assert.Equal([("Battery 18650 Li-ion", 0.66), ("Battery Space-Qualified", 0.56), ("Battery LiPo Pouch", 0.48)], scores);

        // "Try raising the energy weight to 6: the space-qualified battery takes the lead (about 0.67 against 0.61)."
        var heavy = TradeStudies.Evaluate(m, TradeStudyDefinition.Parse(study), [6, 2, 2, 2]);
        Assert.Equal(("Battery Space-Qualified", 0.67), (heavy.Winner!.Alternative.Name, Math.Round(heavy.Winner.Score, 2)));
        Assert.Equal(0.61, Math.Round(heavy.Ranked.ElementAt(1).Score, 2));
    }
}
