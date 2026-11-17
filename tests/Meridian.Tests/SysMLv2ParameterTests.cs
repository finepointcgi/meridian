using Meridian.Core.Model;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>SysML v2 action parameters, pins and item flows, and calc results, mapped onto the 1.x model.</summary>
public class SysMLv2ParameterTests
{
    private const string Actions = """
        package P {
            attribute def Scene;
            attribute def Image;
            attribute def Picture;
            action def Focus {
                in scene : Scene;
                out image : Image;
            }
            action def Shoot {
                in image : Image;
                out picture : Picture;
            }
            action def TakePicture {
                in scene : Scene;
                out picture : Picture[0..1];
                action focus : Focus;
                action shoot : Shoot;
                flow focus.image to shoot.image;
                first focus then shoot;
            }
        }
        """;

    private static (ModelRepository Model, SysmlSyncResult Result) Apply(string text, ModelRepository? model = null)
    {
        model ??= new ModelRepository("M");
        var doc = SysMLv2Parser.Parse(text);
        Assert.False(doc.HasErrors, string.Join("\n", doc.Diagnostics));
        return (model, SysMLv2Sync.Apply(model, model.Root, doc));
    }

    private static Element Named(ModelRepository m, string name) => m.AllElements.Single(e => e.Name == name && e is not Relationship);

    [Fact]
    public void Action_Def_Parameters_Become_Directed_Parameter_Nodes()
    {
        var (m, r) = Apply(Actions);
        Assert.DoesNotContain(r.Diagnostics, d => d.Message.Contains("kept as text"));

        var tp = Named(m, "TakePicture");
        var scene = tp.OwnedElements.Single(e => e.Name == "scene");
        Assert.Equal(ElementKind.ObjectNode, scene.Kind);
        Assert.Equal("in", scene.Get(FieldKeys.Direction));
        Assert.Equal("Scene", m.ResolveType(scene)?.Name);
        var picture = tp.OwnedElements.Single(e => e.Name == "picture");
        Assert.Equal("out", picture.Get(FieldKeys.Direction));
        Assert.Equal("0..1", picture.Get(FieldKeys.Multiplicity));
    }

    [Fact]
    public void Flow_Between_Action_Usages_Creates_Typed_Pins_And_An_Object_Flow()
    {
        var (m, _) = Apply(Actions);
        var focus = Named(m, "focus");
        var shoot = Named(m, "shoot");
        var outPin = focus.OwnedElements.Single();
        var inPin = shoot.OwnedElements.Single();
        Assert.Equal((ElementKind.OutputPin, "image"), (outPin.Kind, outPin.Name));
        Assert.Equal((ElementKind.InputPin, "image"), (inPin.Kind, inPin.Name));
        Assert.Equal("Image", m.ResolveType(outPin)?.Name);

        var flow = Assert.Single(m.Relationships.Where(x => x.Kind == ElementKind.ObjectFlow));
        Assert.Same(outPin, flow.Source);
        Assert.Same(inPin, flow.Target);
        Assert.Equal("TakePicture", flow.Owner!.Name);   // flows live with the activity, not the action

        // The pins come from the called activities' parameters, so the export does not declare them.
        var text = SysMLv2Text.Export(m);
        Assert.Contains("action focus : Focus;", text);
        Assert.Contains("flow focus.image to shoot.image;", text);
        Assert.Contains("out picture : Picture[0..1];", text);
    }

    [Fact]
    public void Round_Trip_Is_Stable_And_Reapplying_Changes_Nothing()
    {
        var (m, _) = Apply(Actions);
        var text = SysMLv2Text.Export(m);
        var again = SysMLv2Sync.Apply(m, m.Root, SysMLv2Parser.Parse(text));
        Assert.True(again.Changes.Count == 0, string.Join("\n", again.Changes.Select(c => c.Description)));
        Assert.Equal(text, SysMLv2Text.Export(m));
    }

    [Fact]
    public void Removing_The_Flow_Removes_The_Pins_It_Needed()
    {
        var (m, _) = Apply(Actions);
        var (_, r) = Apply(Actions.Replace("        flow focus.image to shoot.image;\n", ""), m);
        Assert.Empty(m.Relationships.Where(x => x.Kind == ElementKind.ObjectFlow));
        Assert.Empty(Named(m, "focus").OwnedElements);
        Assert.Empty(Named(m, "shoot").OwnedElements);
        Assert.Contains(r.Changes, c => c.Description.Contains("unused pin"));
    }

    [Fact]
    public void Pins_Declared_On_A_Usage_Are_Written_Back()
    {
        var text = """
            package P {
                attribute def Image;
                action def Pipeline {
                    action grab {
                        out img : Image;
                    }
                    action store {
                        in img : Image;
                    }
                    flow grab.img to store.img;
                }
            }
            """;
        var (m, r) = Apply(text);
        Assert.DoesNotContain(r.Diagnostics, d => d.Severity != SysmlSeverity.Info);
        var grab = Named(m, "grab");
        Assert.Equal(ElementKind.OutputPin, grab.OwnedElements.Single().Kind);
        Assert.Single(m.Relationships.Where(x => x.Kind == ElementKind.ObjectFlow));

        var exported = SysMLv2Text.Export(m);
        Assert.Contains("out img : Image;", exported);
        Assert.Contains("flow grab.img to store.img;", exported);
        Assert.Empty(SysMLv2Sync.Apply(m, m.Root, SysMLv2Parser.Parse(exported)).Changes);
    }

    [Fact]
    public void Unknown_Flow_Parameter_Is_A_Warning_And_Creates_Nothing()
    {
        var (m, r) = Apply(Actions.Replace("flow focus.image to shoot.image;", "flow focus.nothing to shoot.image;"));
        Assert.Contains(r.Diagnostics, d => d.Message.Contains("focus.nothing"));
        Assert.Empty(m.Relationships.Where(x => x.Kind == ElementKind.ObjectFlow));
        Assert.Empty(Named(m, "focus").OwnedElements);
    }

    [Fact]
    public void Calc_Return_Is_The_Constraint_Blocks_Output_Parameter()
    {
        var text = """
            package P {
                calc def Acceleration {
                    in tp : Real;
                    in tm : Real;
                    return : Real = tp / tm;
                }
                calc def Named {
                    in x : Real;
                    return y : Real;
                }
            }
            """;
        var (m, r) = Apply(text);
        Assert.DoesNotContain(r.Diagnostics, d => d.Message.Contains("return"));
        var result = Named(m, "Acceleration").OwnedElements.Single(p => p.Name == "result");
        Assert.Equal(("out", "tp / tm"), (result.Get(FieldKeys.Direction), result.Get(FieldKeys.DefaultValue)));
        Assert.Equal("out", Named(m, "Named").OwnedElements.Single(p => p.Name == "y").Get(FieldKeys.Direction));

        var exported = SysMLv2Text.Export(m);
        Assert.Contains("return : ScalarValues::Real = tp / tm;", exported);
        Assert.Contains("return y : ScalarValues::Real;", exported);
        Assert.Empty(SysMLv2Sync.Apply(m, m.Root, SysMLv2Parser.Parse(exported)).Changes);
    }
}
