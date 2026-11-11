using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Meridian.E2E;

/// <summary>
/// Cameo import in the browser: an .mdzip whose sequence diagram has an alt fragment with two operands opens as a new project, and
/// the fragment is drawn with its dashed separator where Cameo had it and both guards.
/// </summary>
public sealed class CameoImportTests(ServerFixture f) : E2ETest(f)
{
    private const string Model = """
        <?xml version='1.0' encoding='UTF-8'?>
        <xmi:XMI xmlns:uml='http://www.omg.org/spec/UML/20131001' xmlns:xmi='http://www.omg.org/spec/XMI/20131001'>
          <uml:Model xmi:type='uml:Model' xmi:id='_m' name='Payments'>
            <packagedElement xmi:type='uml:Package' xmi:id='_pkg' name='Payments'>
              <packagedElement xmi:type='uml:Interaction' xmi:id='_pay' name='Pay'>
                <lifeline xmi:type='uml:Lifeline' xmi:id='_shop' name='shop'/>
                <lifeline xmi:type='uml:Lifeline' xmi:id='_bank' name='bank'/>
                <fragment xmi:type='uml:CombinedFragment' xmi:id='_alt' interactionOperator='alt'>
                  <operand xmi:type='uml:InteractionOperand' xmi:id='_ok'>
                    <guard xmi:type='uml:InteractionConstraint' xmi:id='_okg'><specification xmi:type='uml:OpaqueExpression' xmi:id='_oks'><body>card valid</body></specification></guard>
                  </operand>
                  <operand xmi:type='uml:InteractionOperand' xmi:id='_else'>
                    <guard xmi:type='uml:InteractionConstraint' xmi:id='_elseg'><specification xmi:type='uml:LiteralString' xmi:id='_elses' value='else'/></guard>
                  </operand>
                </fragment>
                <xmi:Extension extender='MagicDraw UML 2021x'>
                  <modelExtension>
                    <ownedDiagram xmi:type='uml:Diagram' xmi:id='_sd' name='Pay sequence' context='_pay' ownerOfDiagram='_pay'>
                      <xmi:Extension extender='MagicDraw UML 2021x'>
                        <diagramRepresentation>
                          <diagram:DiagramRepresentationObject ID='_sd_r' type='SysML Sequence Diagram' umlType='Sequence Diagram' xmi:id='_sd_ro'
                              xmlns:diagram='http://www.nomagic.com/ns/magicdraw/core/diagram/1.0' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'>
                            <diagramContents xmi:id='_sd_c'><binaryObject streamContentID='BINARY-sd' xmi:id='_sd_b' xsi:type='binary:StreamIdentifierBinaryObject'/></diagramContents>
                          </diagram:DiagramRepresentationObject>
                        </diagramRepresentation>
                      </xmi:Extension>
                    </ownedDiagram>
                  </modelExtension>
                </xmi:Extension>
              </packagedElement>
            </packagedElement>
          </uml:Model>
        </xmi:XMI>
        """;

    private static string Shape(string id, string cls, string element, string geometry, string nested = "") =>
        $"<mdElement elementClass='{cls}' xmi:id='{id}'><elementID xmi:idref='{element}'/><geometry>{geometry}</geometry>" +
        (nested.Length > 0 ? $"<mdOwnedViews>{nested}</mdOwnedViews>" : "") + "</mdElement>";

    // The fragment spans y 100–340; its second operand's shape starts at 220, so the separator is 120 below the fragment's top.
    private static readonly string Symbols =
        "<?xml version='1.0' encoding='UTF-8'?><diagram:DiagramContents xmlns:xmi='http://www.omg.org/XMI' xmlns:diagram='http://www.nomagic.com/ns/magicdraw/core/diagram/1.0'>" +
        "<mdElement elementClass='DiagramPresentationElement' xmi:id='_dpe'><elementID xmi:idref='_sd'/><mdOwnedViews>" +
        Shape("_v1", "Lifeline", "_shop", "60, 40, 150, 400") + Shape("_v2", "Lifeline", "_bank", "320, 40, 150, 400") +
        Shape("_vf", "CombinedFragment", "_alt", "30, 100, 480, 240",
            Shape("_vo1", "InteractionOperand", "_ok", "30, 100, 480, 120") + Shape("_vo2", "InteractionOperand", "_else", "30, 220, 480, 120")) +
        "</mdOwnedViews></mdElement></diagram:DiagramContents>";

    private static string WriteMdzip(string dir)
    {
        var path = Path.Combine(dir, "payments.mdzip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in new[] { ("com.nomagic.magicdraw.uml_umodel.model", Model), ("BINARY-sd", Symbols), ("com.nomagic.ci.metamodel.project", "<project/>") })
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        return path;
    }

    [E2EFact]
    public async Task A_cameo_mdzip_with_a_two_operand_alt_opens_with_its_separator()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-e2e-mdzip-");
        try
        {
            var s = await OpenAsync(F.Alice);
            var p = s.Page;
            await p.Locator("#file-open").SetInputFilesAsync(WriteMdzip(dir.FullName));
            await p.SnackbarAsync("Imported payments.mdzip");
            await Expect(p.ProjectTitle()).ToContainTextAsync("payments");

            await p.Tree().GetByText("Pay sequence", new() { Exact = true }).First.DblClickAsync();
            await Expect(p.Locator(".crumbs .dtype")).ToHaveTextAsync("SD");
            var fragment = p.Node("alt");
            await Expect(fragment).ToHaveCountAsync(1);
            var separator = p.Locator(".diagram-host svg.main line.operand-sep");
            await Expect(separator).ToHaveCountAsync(1);
            await Expect(p.Locator(".diagram-host svg.main text", new() { HasTextString = "[card valid]" })).ToHaveCountAsync(1);
            await Expect(p.Locator(".diagram-host svg.main text", new() { HasTextString = "[else]" })).ToHaveCountAsync(1);
            // In diagram coordinates the separator is where Cameo drew the second operand (y = 220).
            Assert.Equal(220, float.Parse((await separator.GetAttributeAsync("y1"))!, CultureInfo.InvariantCulture), 0.5);
            s.AssertNoErrors();
        }
        finally
        {
            try { dir.Delete(true); } catch (IOException) { }
        }
    }
}
