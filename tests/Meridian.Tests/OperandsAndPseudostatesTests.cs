using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Samples;
using Meridian.Serialization;

namespace Meridian.Tests;

/// <summary>Interaction operands, state machine pseudostates, datastore / central buffer nodes and source multiplicities.</summary>
public class OperandsAndPseudostatesTests
{
    private static (ModelRepository M, Diagram D, Element Fragment, DiagramNode Node) Fragment(string op = "alt", string guard = "ok")
    {
        var m = new ModelRepository("Ops");
        var ia = m.Create(m.Root, ElementKind.Interaction, "Checkout");
        var d = m.CreateDiagram(ia, DiagramType.Sequence, "Checkout");
        var f = m.Create(ia, ElementKind.CombinedFragment);
        m.Set(f, FieldKeys.Operator, op);
        m.Set(f, FieldKeys.Guard, guard);
        var n = m.AddNode(d, f, new Bounds(40, 100, 300, 200));
        m.History.Clear();
        return (m, d, f, n);
    }

    // ================================================================== operands

    [Fact]
    public void A_Fragment_Without_Operands_Is_One_Band_Guarded_By_Its_Own_Guard()
    {
        var (_, _, f, n) = Fragment();
        var band = Assert.Single(InteractionOperands.Layout(n, n.Bounds));
        Assert.Null(band.Operand);
        Assert.Equal((100.0, 300.0), (band.Top, band.Bottom));
        Assert.Equal(["ok"], InteractionOperands.Guards(f));
    }

    [Fact]
    public void Adding_An_Operand_Moves_The_Guard_Splits_The_Last_Band_And_Undoes_In_One_Step()
    {
        var (m, d, f, n) = Fragment();
        var op = m.AddOperand(f);
        var ops = InteractionOperands.Of(f);
        Assert.Equal(2, ops.Count);
        Assert.Same(op, ops[1]);
        Assert.Equal(["ok", "else"], InteractionOperands.Guards(f));
        Assert.Equal("", f.Get(FieldKeys.Guard));
        var bands = InteractionOperands.Layout(n, n.Bounds);
        Assert.Equal(200, bands[1].Top);   // the middle of the fragment
        Assert.Equal("100", n.Style[InteractionOperands.StyleKey(op)]);

        // A third operand halves the last band again; the second stays where it is.
        var third = m.AddOperand(f, "timeout");
        bands = InteractionOperands.Layout(n, n.Bounds);
        Assert.Equal([100.0, 200.0, 250.0], bands.Select(b => b.Top));
        Assert.Equal("timeout", third.Get(FieldKeys.Guard));
        Assert.Equal(1, InteractionOperands.At(n, n.Bounds, 210).Index);

        m.History.Undo();
        m.History.Undo();
        Assert.Empty(InteractionOperands.Of(f));
        Assert.Equal("ok", f.Get(FieldKeys.Guard));
        Assert.DoesNotContain(n.Style.Keys, k => k.StartsWith(InteractionOperands.StylePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void Par_Operands_Have_No_Default_Guard()
    {
        var (m, _, f, _) = Fragment("par", "");
        m.AddOperand(f);
        Assert.Equal(["", ""], InteractionOperands.Guards(f));
    }

    [Fact]
    public void Moving_A_Separator_Is_Clamped_Between_Its_Neighbours_And_Removing_An_Operand_Clears_It()
    {
        var (m, d, f, n) = Fragment();
        var second = m.AddOperand(f);
        var third = m.AddOperand(f);
        // Bands start at 100, 200 and 250: the first separator can't go below the second (less half a band).
        Assert.Equal(140, m.MoveSeparator(d, n, 1, 260));
        Assert.Equal(140, InteractionOperands.StoredOffset(n, second));
        var bands = InteractionOperands.Layout(n, n.Bounds);
        Assert.True(bands[1].Top < bands[2].Top);
        Assert.Equal(250, bands[2].Top);   // pinned where it was drawn
        // Never above the operator tag.
        m.MoveSeparator(d, n, 1, 0);
        Assert.Equal(100 + InteractionOperands.HeaderHeight + InteractionOperands.MinBand / 2, InteractionOperands.Layout(n, n.Bounds)[1].Top);
        m.RemoveOperand(third);
        Assert.Equal(2, InteractionOperands.Of(f).Count);
        Assert.False(n.Style.ContainsKey(InteractionOperands.StyleKey(third)));
        Assert.Throws<InvalidOperationException>(() => m.MoveSeparator(d, n, 0, 150));
    }

    [Fact]
    public void Stored_Offsets_Outside_The_Fragment_Fall_Back_To_An_Even_Split()
    {
        var (m, d, f, n) = Fragment();
        var op = m.AddOperand(f);
        m.SetNodeStyle(d, n, InteractionOperands.StyleKey(op), "999");
        Assert.Equal(200, InteractionOperands.Layout(n, n.Bounds)[1].Top);
        m.SetNodeStyle(d, n, InteractionOperands.StyleKey(op), "junk");
        Assert.Equal(200, InteractionOperands.Layout(n, n.Bounds)[1].Top);
    }

    [Fact]
    public void Operands_Are_Owned_By_Fragments_Only()
    {
        Assert.True(Metamodel.CanOwn(ElementKind.CombinedFragment, ElementKind.InteractionOperand));
        Assert.False(Metamodel.CanOwn(ElementKind.Interaction, ElementKind.InteractionOperand));
        Assert.Contains(FieldKeys.Guard, Metamodel.EditableFields(ElementKind.InteractionOperand));
        Assert.DoesNotContain(FieldKeys.Name, Metamodel.EditableFields(ElementKind.InteractionOperand));
    }

    [Fact]
    public void Renderer_Draws_A_Dashed_Separator_Per_Later_Operand_With_Its_Guard()
    {
        var (m, d, f, _) = Fragment(guard: "card accepted");
        m.AddOperand(f, "declined");
        var svg = SvgRenderer.Render(m, d);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(svg, "class=\"operand-sep\""));
        Assert.Contains("[card accepted]", svg);
        Assert.Contains("[declined]", svg);
    }

    // ================================================================== pseudostates and object nodes

    [Fact]
    public void Pseudostates_Have_Their_Standard_Shapes_Sizes_And_Palette_Entries()
    {
        var m = new ModelRepository("States");
        var sm = m.Create(m.Root, ElementKind.StateMachine, "Door");
        var expected = new Dictionary<string, NodeShape>
        {
            ["choice"] = NodeShape.Diamond, ["junction"] = NodeShape.FilledCircle, ["fork"] = NodeShape.Bar, ["join"] = NodeShape.Bar,
            ["shallowHistory"] = NodeShape.Pseudostate, ["deepHistory"] = NodeShape.Pseudostate, ["entryPoint"] = NodeShape.Pseudostate,
            ["exitPoint"] = NodeShape.Pseudostate, ["terminate"] = NodeShape.Pseudostate,
        };
        foreach (var (kind, shape) in expected)
        {
            var p = m.Create(sm, ElementKind.Pseudostate);
            m.Set(p, FieldKeys.Subtype, kind);
            Assert.Equal(shape, Notation.ShapeOf(p));
            Assert.Equal(kind, Pseudostates.KindOf(p));
        }
        Assert.True(Metamodel.IsVertex(ElementKind.Pseudostate));
        Assert.Equal(Pseudostates.Choice, Pseudostates.KindOf(m.Create(sm, ElementKind.Pseudostate)));   // no subtype: a choice
        Assert.Equal((16, 16), Notation.DefaultSizeOf(ElementKind.Pseudostate, "junction"));
        var palette = Metamodel.PaletteItems(DiagramType.StateMachine);
        Assert.Equal(Subtypes.PseudostateKinds, palette.Where(i => i.Kind == ElementKind.Pseudostate).Select(i => i.Subtype!));
        Assert.Contains(palette, i => i.Title == "Deep History");
        var act = Metamodel.PaletteItems(DiagramType.Activity);
        Assert.Contains(act, i => i.Kind == ElementKind.ObjectNode && i.Subtype == Subtypes.DataStore && i.Title == "Data Store");
        Assert.Contains(act, i => i.Kind == ElementKind.ObjectNode && i.Subtype == Subtypes.CentralBuffer && i.Title == "Central Buffer");
    }

    [Fact]
    public void Entry_And_Exit_Points_Of_A_State_Sit_On_Its_Border_And_Render_With_Their_Notation()
    {
        var m = new ModelRepository("States");
        var sm = m.Create(m.Root, ElementKind.StateMachine, "Door");
        var s = m.Create(sm, ElementKind.State, "Open");
        var entry = m.Create(s, ElementKind.Pseudostate, "in");
        m.Set(entry, FieldKeys.Subtype, Pseudostates.EntryPoint);
        var exit = m.Create(s, ElementKind.Pseudostate, "out");
        m.Set(exit, FieldKeys.Subtype, Pseudostates.ExitPoint);
        var deep = m.Create(sm, ElementKind.Pseudostate);
        m.Set(deep, FieldKeys.Subtype, Pseudostates.DeepHistory);
        var stop = m.Create(sm, ElementKind.Pseudostate);
        m.Set(stop, FieldKeys.Subtype, Pseudostates.Terminate);
        Assert.True(Notation.IsBorderShape(entry));
        Assert.False(Notation.IsBorderShape(deep));
        var d = m.CreateDiagram(sm, DiagramType.StateMachine);
        var sn = m.AddNode(d, s, new Bounds(100, 100, 200, 120));
        m.AddNode(d, entry, new Bounds(91, 150, 18, 18), parent: sn);
        m.AddNode(d, exit, new Bounds(291, 150, 18, 18), parent: sn);
        m.AddNode(d, deep, new Bounds(20, 20, 28, 28));
        m.AddNode(d, stop, new Bounds(400, 20, 22, 22));
        m.Connect(ElementKind.Transition, exit, stop);
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains(">H*</text>", svg);
        Assert.Contains(">in</text>", svg);
        Assert.Contains(">out</text>", svg);
    }

    [Fact]
    public void Datastore_And_Central_Buffer_Nodes_Show_Their_Keyword()
    {
        var m = new ModelRepository("Act");
        var act = m.Create(m.Root, ElementKind.Activity, "Order");
        var store = m.Create(act, ElementKind.ObjectNode, "Orders");
        m.Set(store, FieldKeys.Subtype, Subtypes.DataStore);
        var buffer = m.Create(act, ElementKind.ObjectNode, "Queue");
        m.Set(buffer, FieldKeys.Subtype, Subtypes.CentralBuffer);
        var plain = m.Create(act, ElementKind.ObjectNode, "Order");
        Assert.Equal("«datastore»", Notation.Content(m, store, DiagramType.Activity).Keyword);
        Assert.Equal("«centralBuffer»", Notation.Content(m, buffer, DiagramType.Activity).Keyword);
        Assert.Null(Notation.Content(m, plain, DiagramType.Activity).Keyword);
        Assert.Contains(FieldKeys.Subtype, Metamodel.EditableFields(ElementKind.ObjectNode));
    }

    [Fact]
    public void Source_Multiplicity_Is_Drawn_At_The_Source_End()
    {
        var m = new ModelRepository("Fleet");
        var a = m.Create(m.Root, ElementKind.Block, "Fleet");
        var b = m.Create(m.Root, ElementKind.Block, "Car");
        var r = m.Connect(ElementKind.Association, a, b);
        m.Set(r, FieldKeys.Multiplicity, "1..*");
        m.Set(r, FieldKeys.SourceMultiplicity, "0..1");
        var d = m.CreateDiagram(m.Root, DiagramType.BlockDefinition);
        m.AddNode(d, a, new Bounds(0, 0, 120, 60));
        m.AddNode(d, b, new Bounds(300, 0, 120, 60));
        var svg = SvgRenderer.Render(m, d);
        Assert.Contains(">0..1</text>", svg);
        Assert.Contains(">1..*</text>", svg);
        Assert.Contains(FieldKeys.SourceMultiplicity, Metamodel.EditableFields(ElementKind.Association));
    }

    // ================================================================== persistence

    private static ModelRepository Everything()
    {
        var m = SampleModels.Vehicle();
        var opt = m.OfKind(ElementKind.CombinedFragment).Single();
        m.Set(opt, FieldKeys.Operator, "alt");
        m.AddOperand(opt, "engine warm");
        var sm = m.OfKind(ElementKind.StateMachine).First();
        foreach (var k in Subtypes.PseudostateKinds.Except([Pseudostates.EntryPoint, Pseudostates.ExitPoint]))
        {
            var p = m.Create(sm, ElementKind.Pseudostate, k == Pseudostates.Choice ? "check" : null);
            m.Set(p, FieldKeys.Subtype, k);
        }
        var composite = sm.OwnedElements.First(e => e.Kind == ElementKind.State);
        foreach (var k in new[] { Pseudostates.EntryPoint, Pseudostates.ExitPoint })
        {
            var p = m.Create(composite, ElementKind.Pseudostate, k);
            m.Set(p, FieldKeys.Subtype, k);
        }
        var act = m.OfKind(ElementKind.Activity).First();
        var store = m.Create(act, ElementKind.ObjectNode, "Log");
        m.Set(store, FieldKeys.Subtype, Subtypes.DataStore);
        var buffer = m.Create(act, ElementKind.ObjectNode, "Parts");
        m.Set(buffer, FieldKeys.Subtype, Subtypes.CentralBuffer);
        m.Create(act, ElementKind.ObjectNode, "Order");
        var assoc = m.Relationships.First(r => r.Kind == ElementKind.Composition);
        m.Set(assoc, FieldKeys.SourceMultiplicity, "0..1");
        return m;
    }

    [Fact]
    public void Project_Files_Keep_Operands_Pseudostates_Object_Node_Kinds_And_Separators()
    {
        var m = Everything();
        var json = ProjectFile.Serialize(m);
        var back = ProjectFile.Deserialize(json);
        Assert.Equal(json, ProjectFile.Serialize(back));
        var f = back.OfKind(ElementKind.CombinedFragment).Single();
        Assert.Equal(["engine cold", "engine warm"], InteractionOperands.Guards(f));
        var node = back.Diagrams.SelectMany(d => d.Nodes).Single(n => ReferenceEquals(n.Element, f));
        Assert.Equal(2, InteractionOperands.Layout(node, node.Bounds).Count);
        Assert.Equal(9, back.OfKind(ElementKind.Pseudostate).Count());
        Assert.Contains(back.OfKind(ElementKind.ObjectNode), o => o.Get(FieldKeys.Subtype) == Subtypes.DataStore);
        Assert.Equal("0..1", back.Relationships.First(r => r.Kind == ElementKind.Composition).Get(FieldKeys.SourceMultiplicity));
    }

    [Fact]
    public void Old_Project_Files_Without_Operands_Still_Load()
    {
        // Written before operands and pseudostates existed: a fragment with a merged guard, an object node.
        const string old = """
            {"format":"meridian","version":1,"root":{"id":"r","kind":"Model","fields":{"name":"Old"},"children":[
              {"id":"ia","kind":"Interaction","fields":{"name":"Seq"},"children":[
                {"id":"f","kind":"CombinedFragment","fields":{"guard":"a | b","operator":"alt"}},
                {"id":"d","kind":"Diagram","diagramType":"Sequence","fields":{"name":"Seq"},
                 "nodes":[{"id":"n","element":"f","x":10,"y":20,"w":300,"h":100,"style":null}],"edges":[]}]},
              {"id":"act","kind":"Activity","fields":{"name":"Act"},"children":[{"id":"o","kind":"ObjectNode","fields":{"name":"Stuff"}}]}]}}
            """;
        var m = ProjectFile.Deserialize(old);
        var f = m.Find("f")!;
        Assert.Empty(InteractionOperands.Of(f));
        Assert.Equal(["a | b"], InteractionOperands.Guards(f));
        Assert.Equal("", m.Find("o")!.Get(FieldKeys.Subtype));
        var d = m.Diagrams.Single();
        Assert.Single(InteractionOperands.Layout(d.Nodes.Single(), d.Nodes.Single().Bounds));
    }

    [Fact]
    public void Xmi_Writes_Operands_Pseudostate_Kinds_Datastores_And_Both_Association_Ends()
    {
        var m = Everything();
        var xml = XmiExchange.ToXml(m);
        var text = xml.ToString();
        Assert.Contains("uml:InteractionOperand", text);
        Assert.Contains("uml:InteractionConstraint", text);
        Assert.Contains("kind=\"deepHistory\"", text);
        Assert.Contains("<connectionPoint", text);
        Assert.Contains("uml:DataStoreNode", text);
        var (back, report) = XmiExchange.Import(xml);
        Assert.Empty(report.Warnings);
        var f = back.OfKind(ElementKind.CombinedFragment).Single();
        Assert.Equal(["engine cold", "engine warm"], InteractionOperands.Guards(f));
        Assert.Equal(Subtypes.PseudostateKinds.Order(), back.OfKind(ElementKind.Pseudostate).Select(p => Pseudostates.KindOf(p)!).Order());
        Assert.Equal(2, back.OfKind(ElementKind.Pseudostate).Count(p => p.Owner!.Kind == ElementKind.State));
        Assert.Equal(["", Subtypes.CentralBuffer, Subtypes.DataStore], back.OfKind(ElementKind.ObjectNode).Where(o => o.Name is "Log" or "Parts" or "Order")
            .Select(o => o.Get(FieldKeys.Subtype)).Order());
        Assert.Equal("0..1", back.Relationships.First(r => r.Kind == ElementKind.Composition).Get(FieldKeys.SourceMultiplicity));
    }

    [Fact]
    public void Xmi_From_Another_Tool_Reads_Operands_And_Pseudostates_Without_The_Meridian_Extension()
    {
        const string xmi = """
            <xmi:XMI xmlns:xmi="http://www.omg.org/spec/XMI/20131001" xmlns:uml="http://www.omg.org/spec/UML/20131001">
              <uml:Model xmi:type="uml:Model" xmi:id="m" name="Tool">
                <packagedElement xmi:type="uml:Interaction" xmi:id="ia" name="Pay">
                  <fragment xmi:type="uml:CombinedFragment" xmi:id="cf" interactionOperator="alt">
                    <operand xmi:type="uml:InteractionOperand" xmi:id="o1">
                      <guard xmi:type="uml:InteractionConstraint" xmi:id="g1"><specification xmi:type="uml:OpaqueExpression" xmi:id="s1"><body>paid</body></specification></guard>
                    </operand>
                    <operand xmi:type="uml:InteractionOperand" xmi:id="o2">
                      <guard xmi:type="uml:InteractionConstraint" xmi:id="g2"><specification xmi:type="uml:OpaqueExpression" xmi:id="s2"><body>else</body></specification></guard>
                    </operand>
                  </fragment>
                </packagedElement>
                <packagedElement xmi:type="uml:StateMachine" xmi:id="sm" name="Door">
                  <region xmi:type="uml:Region" xmi:id="r">
                    <subvertex xmi:type="uml:Pseudostate" xmi:id="c" kind="choice"/>
                    <subvertex xmi:type="uml:Pseudostate" xmi:id="j" kind="junction"/>
                  </region>
                </packagedElement>
                <packagedElement xmi:type="uml:Activity" xmi:id="a" name="Act">
                  <node xmi:type="uml:DataStoreNode" xmi:id="ds" name="Db"/>
                  <node xmi:type="uml:CentralBufferNode" xmi:id="cb" name="Buf"/>
                </packagedElement>
              </uml:Model>
            </xmi:XMI>
            """;
        var (m, _) = XmiExchange.Import(System.Xml.Linq.XDocument.Parse(xmi));
        Assert.Equal(["paid", "else"], InteractionOperands.Guards(m.Find("cf")!));
        Assert.Equal("", m.Find("cf")!.Get(FieldKeys.Guard));
        Assert.Equal(Pseudostates.Junction, Pseudostates.KindOf(m.Find("j")!));
        Assert.Equal(Subtypes.DataStore, m.Find("ds")!.Get(FieldKeys.Subtype));
        Assert.Equal(Subtypes.CentralBuffer, m.Find("cb")!.Get(FieldKeys.Subtype));
    }
}
