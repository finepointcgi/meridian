using Meridian.Core.Libraries;
using Meridian.Core.Model;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Cameo used projects at import time (docs/LIBRARIES.md, "Cameo import"): detected, and turned into a usage on request.</summary>
public sealed class LibraryCameoImportTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("meridian-md-lib-");

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { }
    }

    /// <summary>A vehicle project using the Cameo module "Standard Parts": a part typed by its wheel, a satisfy to its requirement, a copy of its package.</summary>
    private const string VehicleXmi = """
        <?xml version='1.0' encoding='UTF-8'?>
        <xmi:XMI xmlns:uml='http://www.omg.org/spec/UML/20131001' xmlns:xmi='http://www.omg.org/spec/XMI/20131001'
                 xmlns:sysml='http://www.omg.org/spec/SysML/20181001/SysML'>
          <uml:Model xmi:type='uml:Model' xmi:id='_vmodel' name='Vehicle'>
            <packagedElement xmi:type='uml:Package' xmi:id='_vs' name='Structure'>
              <packagedElement xmi:type='uml:Class' xmi:id='_car' name='Car'>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_fl' name='frontLeft' aggregation='composite'>
                  <type href='Standard%20Parts.mdzip#_wheel'>
                    <xmi:Extension extender='MagicDraw UML 2021x'><referenceExtension referentPath='Standard parts::Wheel' referentType='Class'/></xmi:Extension>
                  </type>
                </ownedAttribute>
                <ownedAttribute xmi:type='uml:Property' xmi:id='_speed' name='speed'>
                  <type href='http://www.omg.org/spec/UML/20131001/PrimitiveTypes.xmi#Real'/>
                </ownedAttribute>
              </packagedElement>
              <packagedElement xmi:type='uml:Abstraction' xmi:id='_sat'><client xmi:idref='_car'/><supplier href='Standard%20Parts.mdzip#_req'/></packagedElement>
            </packagedElement>
            <packagedElement xmi:type='uml:Package' xmi:id='_std' name='Standard parts'>
              <packagedElement xmi:type='uml:Class' xmi:id='_hub' name='Hub'/>
            </packagedElement>
          </uml:Model>
          <sysml:Block xmi:id='_car_b' base_Class='_car'/>
          <sysml:Satisfy xmi:id='_sat_st' base_Abstraction='_sat'/>
        </xmi:XMI>
        """;

    private static ModelRepository StandardParts()
    {
        var m = new ModelRepository("Standard Parts");
        var pkg = new Element(ElementKind.Package, "_std");
        ModelRepository.SetFieldForLoad(pkg, FieldKeys.Name, "Standard parts");
        m.AttachForLoad(m.Root, pkg);
        foreach (var (id, kind, name) in new[] { ("_wheel", ElementKind.Block, "Wheel"), ("_hub", ElementKind.Block, "Hub"), ("_req", ElementKind.Requirement, "Max load") })
        {
            var e = new Element(kind, id);
            ModelRepository.SetFieldForLoad(e, FieldKeys.Name, name);
            m.AttachForLoad(pkg, e);
        }
        return m;
    }

    [Fact]
    public void Used_Projects_Are_Detected_With_The_Elements_Referenced_In_Them()
    {
        var path = Path.Combine(_dir.FullName, "vehicle.mdxml");
        File.WriteAllText(path, VehicleXmi);
        var (m, report) = MagicDrawImporter.Import(path);

        var used = Assert.Single(report.UsedProjects);   // the UML primitive types are not a used project
        Assert.Equal("Standard Parts", used.Name);
        Assert.Equal(["_req", "_wheel"], used.ReferencedIds.Order());
        Assert.Equal("Wheel", m.Find("_fl")!.TypeRef);   // copying stays the default: the type is kept by name
        Assert.Contains(report.ExternalRelationships, r => r.Kind == ElementKind.Satisfy && r.TargetId == "_req");
        Assert.Contains(report.Lines("vehicle.mdxml"), l => l.Contains("Uses other Cameo projects: Standard Parts"));
        Assert.Equal(MagicDrawImporter.MatchKey("Standard Parts"), MagicDrawImporter.MatchKey("Standard_Parts"));
        Assert.Null(MagicDrawImporter.ModuleOf("SysML.mdzip#SysML_dataType_Length"));
    }

    [Fact]
    public void Using_An_Existing_Project_Drops_The_Copy_And_Links_References_By_Id()
    {
        var path = Path.Combine(_dir.FullName, "vehicle.mdxml");
        File.WriteAllText(path, VehicleXmi);
        var (m, report) = MagicDrawImporter.Import(path);
        var existing = StandardParts();
        var packages = MagicDrawImporter.SuggestPackages(existing, report.UsedProjects[0]);
        Assert.Equal(["_std"], packages);

        var (removed, retyped, linked) = MagicDrawImporter.UseLibraries(m, report, existing, packages);
        Assert.Equal((2, 1, 1), (removed, retyped, linked));
        Assert.Null(m.Find("_std"));                    // the copy is gone …
        Assert.Equal("_wheel", m.Find("_fl")!.TypeRef);  // … and references name the library's ids

        // Saved, then loaded with the existing project as a library, everything resolves.
        var json = ProjectFile.Serialize(m);
        Assert.DoesNotContain("\"Hub\"", json);
        var library = new LibraryPayload(new LibraryInfo("u", Guid.NewGuid(), "Standard Parts", "_std", LibraryPinKind.Branch, "main", "main (head)", ""),
                                         LibraryContent.Extract(existing, "_std")!);
        var loaded = ProjectFile.Deserialize(json, [library]);
        Assert.Equal("Wheel", loaded.TypeDisplayName(loaded.Find("_fl")!));
        var sat = Assert.Single(loaded.OfKind(ElementKind.Satisfy).Cast<Relationship>());
        Assert.Equal(("Car", "Max load"), (sat.Source.Name, sat.Target.Name));
        Assert.True(loaded.IsLibraryElement(sat.Target));
        Assert.False(loaded.IsLibraryElement(sat));
    }
}
