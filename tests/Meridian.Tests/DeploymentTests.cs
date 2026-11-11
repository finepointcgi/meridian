using System.Text;
using Meridian.Core.Analysis;
using Meridian.Core.CodeSync;
using Meridian.Core.Deployment;
using Meridian.Core.Language;
using Meridian.Core.Model;
using Meridian.Core.Validation;
using Meridian.Serialization;
using Meridian.Serialization.CodeSync;
using Meridian.Serialization.Reports;
using Microsoft.Data.Sqlite;

namespace Meridian.Tests;

/// <summary>UML deployment: nodes, artifacts, «deploy», «manifest», communication paths, notation, rules and interchange.</summary>
public class DeploymentTests
{
    private sealed record Shop(ModelRepository M, Element Pkg, Element Server, Element Jvm, Element Db, Element Jar, Element Orders, Element Gateway, Relationship Deploy, Relationship Path);

    /// <summary>A device «server» (cores, RAM) holding a JVM execution environment; an artifact deployed to the JVM that manifests a block.</summary>
    private static Shop Build()
    {
        var m = new ModelRepository("Shop");
        var pkg = m.Create(m.Root, ElementKind.Package, "Deployment");
        var server = m.Create(pkg, ElementKind.Device, "AppServer");
        m.Set(server, FieldKeys.Stereotypes, "server");
        m.Set(m.Create(server, ElementKind.Property, "cores"), FieldKeys.DefaultValue, "8");
        m.Set(m.Create(server, ElementKind.Property, "ramGb"), FieldKeys.DefaultValue, "32");
        var jvm = m.Create(server, ElementKind.ExecutionEnvironment, "JVM");
        var db = m.Create(pkg, ElementKind.Node, "DbServer");
        var gateway = m.Create(pkg, ElementKind.Node, "Gateway");
        var jar = m.Create(pkg, ElementKind.Artifact, "orders.jar");
        m.Set(jar, DeploymentFields.FileName, "orders-service.jar");
        var orders = m.Create(m.Root, ElementKind.Block, "OrderService");
        m.Connect(ElementKind.Manifestation, jar, orders);
        var deploy = m.Connect(ElementKind.Deployment, jar, jvm);
        var path = m.Connect(ElementKind.CommunicationPath, server, db);
        m.Set(path, DeploymentFields.Protocol, "JDBC");
        m.History.Clear();
        return new Shop(m, pkg, server, jvm, db, jar, orders, gateway, deploy, path);
    }

    // ------------------------------------------------------------------ model

    [Fact]
    public void Nodes_Nest_Carry_Stereotypes_And_Value_Properties_And_Artifacts_Deploy_And_Manifest()
    {
        var s = Build();
        Assert.True(Metamodel.CanOwn(ElementKind.Device, ElementKind.ExecutionEnvironment));
        Assert.True(Metamodel.CanOwn(ElementKind.ExecutionEnvironment, ElementKind.ExecutionEnvironment));
        Assert.True(Metamodel.CanOwn(ElementKind.Node, ElementKind.Property));
        Assert.True(Metamodel.CanOwn(ElementKind.Artifact, ElementKind.Artifact));
        Assert.True(Metamodel.CanOwn(ElementKind.Package, ElementKind.Node));
        Assert.False(Metamodel.CanOwn(ElementKind.Node, ElementKind.Artifact));   // deployment is a relationship, never ownership
        Assert.Same(s.Server, s.Jvm.Owner);
        Assert.Equal(["server"], s.Server.Stereotypes);

        Assert.Equal([s.Jvm], DeploymentModel.DeployedTo(s.M, s.Jar));
        Assert.Equal([s.Jar], DeploymentModel.DeployedOn(s.M, s.Jvm));
        Assert.Same(s.Server, DeploymentModel.HostOf(s.Jvm));
        Assert.Same(s.Jvm, DeploymentModel.EnvironmentOf(s.Jvm));
        Assert.Null(DeploymentModel.EnvironmentOf(s.Db));
        Assert.True(DeploymentModel.HostsAnything(s.M, s.Server));   // through its execution environment
        Assert.Equal([s.Orders], DeploymentModel.Manifests(s.M, s.Jar));

        Assert.Null(Metamodel.CheckEnds(ElementKind.Deployment, ElementKind.Package, ElementKind.Node));   // database / schema
        Assert.NotNull(Metamodel.CheckEnds(ElementKind.Deployment, ElementKind.Block, ElementKind.Node));
        Assert.NotNull(Metamodel.CheckEnds(ElementKind.Manifestation, ElementKind.Artifact, ElementKind.Node));
        Assert.NotNull(Metamodel.CheckEnds(ElementKind.CommunicationPath, ElementKind.Node, ElementKind.Artifact));
        Assert.Contains(DeploymentFields.FileName, Metamodel.EditableFields(ElementKind.Artifact));
        Assert.Contains(DeploymentFields.Protocol, Metamodel.EditableFields(ElementKind.CommunicationPath));
        Assert.Equal("deploy", Metamodel.Style(ElementKind.Deployment).Keyword);
        Assert.Equal("manifest", Metamodel.Style(ElementKind.Manifestation).Keyword);
    }

    [Fact]
    public void DeployTo_Deploys_Each_Element_Once_In_One_Undo_Step()
    {
        var s = Build();
        var war = s.M.Create(s.Pkg, ElementKind.Artifact, "web.war");
        var db = s.M.Create(s.M.Root, ElementKind.Package, "Orders DB");
        s.M.Set(db, Core.DataModeling.DbFields.Role, Core.DataModeling.DbFields.RoleDatabase);
        Assert.True(DeploymentModel.CanDeploy(db));
        var created = DeploymentModel.DeployTo(s.M, [war, s.Jar, db, s.Orders], s.Jvm);
        Assert.Equal(2, created.Count);   // the jar was already there; a block can't be deployed
        Assert.Contains(s.Jvm, DeploymentModel.DeployedTo(s.M, db));
        s.M.History.Undo();
        Assert.Empty(DeploymentModel.DeployedTo(s.M, war));
        Assert.Throws<InvalidOperationException>(() => DeploymentModel.DeployTo(s.M, [war], s.Orders));
    }

    [Fact]
    public void Project_File_Round_Trips_Deployment_And_Older_Files_Still_Load()
    {
        var s = Build();
        var d = s.M.CreateDiagram(s.Pkg, DiagramType.Deployment, "Runtime");
        var serverNode = s.M.AddNode(d, s.Server, new Bounds(40, 40, 400, 260));
        var jvmNode = s.M.AddNode(d, s.Jvm, new Bounds(60, 100, 300, 180), parent: serverNode);
        s.M.AddNode(d, s.Jar, new Bounds(80, 160, 170, 76), parent: jvmNode);
        var json = ProjectFile.Serialize(s.M);
        var back = ProjectFile.Deserialize(json);
        var jar = back.Find(s.Jar.Id)!;
        Assert.Equal(ElementKind.Artifact, jar.Kind);
        Assert.Equal("orders-service.jar", jar.Get(DeploymentFields.FileName));
        Assert.Equal(ElementKind.ExecutionEnvironment, back.Find(s.Jvm.Id)!.Kind);
        Assert.Equal("JDBC", back.Find(s.Path.Id)!.Get(DeploymentFields.Protocol));
        var bd = back.Diagrams.Single();
        Assert.Equal(DiagramType.Deployment, bd.DiagramType);
        Assert.Equal(jvmNode.Id, bd.Nodes.Single(n => n.Element.Id == s.Jar.Id).ParentId);
        // Kinds and diagram types are stored by name: files without any deployment content are unaffected.
        Assert.Contains("\"kind\":\"Artifact\"", json.Replace(" ", ""));
        var old = ProjectFile.Deserialize(ProjectFile.Serialize(Core.Samples.SampleModels.Vehicle()));
        Assert.DoesNotContain(old.AllElements, e => DeploymentModel.IsNode(e.Kind));
    }

    // ------------------------------------------------------------------ connector rules

    [Fact]
    public void Lines_Become_Deploy_Manifest_And_Communication_Path_Automatically()
    {
        var dep = DiagramType.Deployment;
        Assert.Equal(new ConnectorChoice(ElementKind.Deployment), ConnectorRules.Default(dep, ElementKind.Artifact, ElementKind.Node));
        Assert.Equal(new ConnectorChoice(ElementKind.Deployment), ConnectorRules.Default(dep, ElementKind.DeploymentSpecification, ElementKind.ExecutionEnvironment));
        Assert.Equal(new ConnectorChoice(ElementKind.Deployment, Reverse: true), ConnectorRules.Default(dep, ElementKind.Device, ElementKind.Artifact));
        Assert.Equal(new ConnectorChoice(ElementKind.Manifestation), ConnectorRules.Default(dep, ElementKind.Artifact, ElementKind.Block));
        Assert.Equal(new ConnectorChoice(ElementKind.Manifestation), ConnectorRules.Default(dep, ElementKind.Artifact, ElementKind.Component));
        Assert.Equal(new ConnectorChoice(ElementKind.Manifestation, Reverse: true), ConnectorRules.Default(dep, ElementKind.Component, ElementKind.Artifact));
        Assert.Equal(new ConnectorChoice(ElementKind.CommunicationPath), ConnectorRules.Default(dep, ElementKind.Node, ElementKind.Device));
        Assert.Equal(new ConnectorChoice(ElementKind.CommunicationPath), ConnectorRules.Default(dep, ElementKind.ExecutionEnvironment, ElementKind.Node));
        Assert.Equal(new ConnectorChoice(ElementKind.Deployment), ConnectorRules.Default(dep, ElementKind.Package, ElementKind.Node));
        // Elsewhere a package line is still a dependency first; deploy stays one click away.
        var onPkg = ConnectorRules.Candidates(DiagramType.Package, ElementKind.Package, ElementKind.Node);
        Assert.Equal(ElementKind.Dependency, onPkg[0].Kind);
        Assert.Contains(onPkg, c => c.Kind == ElementKind.Deployment);
        // Every choice is valid.
        foreach (var (a, b) in new[] { (ElementKind.Artifact, ElementKind.Node), (ElementKind.Node, ElementKind.Node), (ElementKind.Artifact, ElementKind.Package) })
            foreach (var c in ConnectorRules.Candidates(dep, a, b))
                Assert.Null(Metamodel.CheckEnds(c.Kind, c.Reverse ? b : a, c.Reverse ? a : b));
    }

    [Fact]
    public void Deployment_Palette_Offers_Nodes_Artifacts_And_Deployment_Connectors()
    {
        var items = Metamodel.PaletteItems(DiagramType.Deployment).Select(i => i.Kind).ToList();
        Assert.Equal([ElementKind.Node, ElementKind.Device, ElementKind.ExecutionEnvironment, ElementKind.Artifact, ElementKind.DeploymentSpecification], items.Take(5));
        var links = Metamodel.PaletteLinks(DiagramType.Deployment).Select(i => i.Kind).ToList();
        Assert.Equal([ElementKind.Deployment, ElementKind.Manifestation, ElementKind.CommunicationPath], links.Take(3));
        Assert.Equal("Deployment Diagram", Metamodel.DisplayName(DiagramType.Deployment));
        Assert.True(Metamodel.IsValidDiagramContext(DiagramType.Deployment, ElementKind.Package));
    }

    // ------------------------------------------------------------------ validation

    [Fact]
    public void Undeployed_Artifacts_Warn_And_Paths_Between_Empty_Nodes_Inform()
    {
        var s = Build();
        var lonely = s.M.Create(s.Pkg, ElementKind.Artifact, "lonely.jar");
        var nested = s.M.Create(s.Jar, ElementKind.Artifact, "config.yml");   // deployed with the jar it is part of
        var idle = s.M.Connect(ElementKind.CommunicationPath, s.Db, s.Gateway);
        var issues = new ModelValidator().Validate(s.M);
        var dep1 = issues.Where(i => i.RuleId == "DEP-001").ToList();
        Assert.Equal([lonely], dep1.Select(i => i.Element));
        Assert.Equal(Severity.Warning, dep1[0].Severity);
        Assert.DoesNotContain(issues, i => ReferenceEquals(i.Element, nested));
        var dep2 = issues.Where(i => i.RuleId == "DEP-002").ToList();
        Assert.Equal([(Element)idle], dep2.Select(i => i.Element));   // AppServer ↔ DbServer has the jar on one end
        Assert.Equal(Severity.Info, dep2[0].Severity);

        DeploymentModel.DeployTo(s.M, [lonely], s.Gateway);
        Assert.DoesNotContain(new ModelValidator().Validate(s.M), i => i.RuleId.StartsWith("DEP-", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ table, matrix, report

    [Fact]
    public void Deployment_Table_Lists_Artifact_Node_And_Environment()
    {
        var s = Build();
        var bare = s.M.Create(s.Pkg, ElementKind.Artifact, "schema.sql");
        s.M.Connect(ElementKind.Deployment, bare, s.Db);
        var def = DeploymentModel.DeploymentTable();
        Assert.Equal(["Artifact", "Node", "Environment"], def.Columns.Take(3).Select(c => c.Header));
        var rows = ModelTables.Build(s.M, def).ToDictionary(r => r.Element.Name, r => r.Cells);
        Assert.Equal(["orders.jar", "AppServer", "JVM", "orders-service.jar", "OrderService"], rows["orders.jar"]);
        Assert.Equal(["schema.sql", "DbServer", "", "", ""], rows["schema.sql"]);
        Assert.Equal("JVM", ModelValues.Get(s.M, s.Jar, "deployedTo"));
        Assert.False(ModelValues.CanEdit(s.Jar, DeploymentModel.NodeKey));
        Assert.Equal(def.Columns.Select(c => c.Key), TableDefinition.DefaultColumns(ElementKind.Artifact).Select(c => c.Key));
    }

    [Fact]
    public void Artifact_By_Node_Matrix_Shows_Deployments()
    {
        var s = Build();
        var m = Traceability.BuildMatrix(s.M, [ElementKind.Artifact], [ElementKind.Node, ElementKind.Device, ElementKind.ExecutionEnvironment], [ElementKind.Deployment]);
        Assert.False(m.Cell(s.Jar, s.Jvm).IsEmpty);
        Assert.True(m.Cell(s.Jar, s.Db).IsEmpty);
    }

    [Fact]
    public void Sdd_Has_A_Deployment_View_With_The_Diagram_And_The_Deployment_Table()
    {
        var s = Build();
        var d = s.M.CreateDiagram(s.Pkg, DiagramType.Deployment, "Runtime view");
        s.M.AddNode(d, s.Server, new Bounds(40, 40, 300, 200));
        var sdd = ReportTemplates.Sdd();
        var titles = sdd.Sections.Select(x => x.Title).ToList();
        Assert.Equal(titles.IndexOf("Behavior") + 1, titles.IndexOf("Deployment view"));   // added after Behavior, nothing else moved
        Assert.Equal("Requirements allocation", titles[titles.IndexOf("Deployment view") + 1]);
        var html = Encoding.UTF8.GetString(ReportEngine.Generate(s.M, sdd, ReportFormat.Html));
        Assert.Contains("Deployment view", html);
        Assert.Contains("Runtime view", html);
        Assert.Contains("orders.jar", html);
        Assert.Contains("JDBC", html);
    }

    // ------------------------------------------------------------------ notation and auto-diagramming

    [Fact]
    public void Svg_Draws_Node_Boxes_Artifact_Icons_And_Hides_A_Deploy_Shown_By_Nesting()
    {
        var s = Build();
        var d = s.M.CreateDiagram(s.Pkg, DiagramType.Deployment, "Runtime");
        var server = s.M.AddNode(d, s.Server, new Bounds(40, 40, 420, 280));
        var jvm = s.M.AddNode(d, s.Jvm, new Bounds(60, 100, 340, 200), parent: server);
        s.M.AddNode(d, s.Jar, new Bounds(80, 160, 170, 76), parent: jvm);
        s.M.AddNode(d, s.Db, new Bounds(600, 40, 220, 130));
        Assert.NotNull(d.EdgeFor(s.Deploy));
        var svg = SvgRenderer.Render(s.M, d);
        Assert.Contains("node-3d", svg);
        Assert.Contains("artifact-icon", svg);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("«device», «server»"), svg);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("«executionEnvironment»"), svg);
        Assert.DoesNotContain("cores = 8", svg);   // a node holding nested shapes shows only its title
        Assert.DoesNotContain($"data-edge=\"{d.EdgeFor(s.Deploy)!.Id}\"", svg);   // nested: no line
        Assert.Contains($"data-edge=\"{d.EdgeFor(s.Path)!.Id}\"", svg);

        // Drawn side by side, the deployment is a «deploy» line; a node without nested shapes lists its value properties.
        var flat = s.M.CreateDiagram(s.Pkg, DiagramType.Deployment, "Flat");
        s.M.AddNode(flat, s.Jvm, new Bounds(40, 40, 220, 130));
        s.M.AddNode(flat, s.Jar, new Bounds(400, 40, 240, 120));
        s.M.AddNode(flat, s.Server, new Bounds(40, 300, 220, 130));
        var flatSvg = SvgRenderer.Render(s.M, flat);
        Assert.Contains($"data-edge=\"{flat.EdgeFor(s.Deploy)!.Id}\"", flatSvg);
        Assert.Contains(System.Net.WebUtility.HtmlEncode("«deploy»"), flatSvg);
        Assert.Contains("cores = 8", flatSvg);
        Assert.Contains("file = orders-service.jar", flatSvg);
    }

    [Fact]
    public void Auto_Diagrammer_Plans_A_Deployment_Overview_With_Artifacts_Nested_In_Their_Nodes()
    {
        var s = Build();
        var plan = AutoDiagrammer.Plan(s.M);
        var overview = plan.Diagrams.Single(p => p.Type == DiagramType.Deployment);
        Assert.Equal("Deployment overview", overview.Name);
        Assert.Contains(s.Jar, overview.Elements);
        foreach (var p in plan.Diagrams.Where(p => p.Type != DiagramType.Deployment)) p.Selected = false;
        var result = AutoDiagrammer.Apply(s.M, plan);
        var d = result.Created.Single();
        var server = d.NodeFor(s.Server)!;
        var jvm = d.NodeFor(s.Jvm)!;
        Assert.Equal(server.Id, jvm.ParentId);
        Assert.Equal(jvm.Id, d.NodeFor(s.Jar)!.ParentId);
        Assert.NotNull(d.NodeFor(s.Db));
        Assert.True(d.EdgeFor(s.Path) != null);
        Assert.True(DeploymentModel.ShownByNesting(d, d.EdgeFor(s.Deploy)!));
        Assert.DoesNotContain(AutoDiagrammer.Plan(new ModelRepository("Empty")).Diagrams, p => p.Type == DiagramType.Deployment);
    }

    // ------------------------------------------------------------------ interchange

    [Fact]
    public void Xmi_Round_Trips_Nodes_Artifacts_Deployments_Manifestations_And_Paths()
    {
        var s = Build();
        var d = s.M.CreateDiagram(s.Pkg, DiagramType.Deployment, "Runtime");
        var serverNode = s.M.AddNode(d, s.Server, new Bounds(40, 40, 400, 260));
        s.M.AddNode(d, s.Jvm, new Bounds(60, 100, 300, 180), parent: serverNode);
        var xml = XmiExchange.ToXml(s.M);
        var text = xml.ToString();
        Assert.Contains("uml:Device", text);
        Assert.Contains("nestedNode", text);
        Assert.Contains("uml:Deployment", text);
        Assert.Contains("uml:Manifestation", text);
        Assert.Contains("uml:CommunicationPath", text);
        Assert.Contains("fileName=\"orders-service.jar\"", text);

        var (back, report) = XmiExchange.Import(xml);
        Assert.Empty(report.Warnings);
        var server = back.Find(s.Server.Id)!;
        Assert.Equal(ElementKind.Device, server.Kind);
        Assert.Equal(["server"], server.Stereotypes);
        var jvm = back.Find(s.Jvm.Id)!;
        Assert.Equal(ElementKind.ExecutionEnvironment, jvm.Kind);
        Assert.Same(server, jvm.Owner);
        Assert.Equal("8", server.OwnedElements.Single(p => p.Name == "cores").Get(FieldKeys.DefaultValue));
        var jar = back.Find(s.Jar.Id)!;
        Assert.Equal("orders-service.jar", jar.Get(DeploymentFields.FileName));
        var deploy = (Relationship)back.Find(s.Deploy.Id)!;
        Assert.Equal((ElementKind.Deployment, jar, jvm), (deploy.Kind, deploy.Source, deploy.Target));
        Assert.Equal([back.Find(s.Orders.Id)!], DeploymentModel.Manifests(back, jar));
        var path = (Relationship)back.Find(s.Path.Id)!;
        Assert.Equal((ElementKind.CommunicationPath, server, back.Find(s.Db.Id)!), (path.Kind, path.Source, path.Target));
        Assert.Equal("JDBC", path.Get(DeploymentFields.Protocol));
        Assert.Equal(DiagramType.Deployment, back.Diagrams.Single().DiagramType);
    }

    [Fact]
    public void SysML_v2_Text_Round_Trips_Deployment_Elements_And_Relationships()
    {
        var s = Build();
        var text = SysMLv2Text.Export(s.M);
        Assert.Contains("#device #server part def AppServer", text);
        Assert.Contains("#executionEnvironment part def JVM", text);
        Assert.Contains("#deploy dependency from", text);
        Assert.Contains("#manifest dependency from", text);
        Assert.Contains("#communicationPath dependency from", text);

        var (back, report) = SysMLv2Text.Import(text);
        Assert.Empty(report.Warnings);
        var server = back.AllElements.Single(e => e.Name == "AppServer");
        Assert.Equal(ElementKind.Device, server.Kind);
        Assert.Equal(["server"], server.Stereotypes);
        var jvm = back.AllElements.Single(e => e.Name == "JVM");
        Assert.Equal((ElementKind.ExecutionEnvironment, server), (jvm.Kind, jvm.Owner));
        var jar = back.AllElements.Single(e => e.Name == "orders.jar");
        Assert.Equal(ElementKind.Artifact, jar.Kind);
        Assert.Equal([jvm], DeploymentModel.DeployedTo(back, jar));
        Assert.Equal(["OrderService"], DeploymentModel.Manifests(back, jar).Select(e => e.Name));
        Assert.Single(back.OfKind(ElementKind.CommunicationPath));
        Assert.Equal(ElementKind.Node, back.AllElements.Single(e => e.Name == "Gateway").Kind);
    }

    [Fact]
    public void Cameo_Import_Reads_Nodes_Devices_Environments_Artifacts_Deployments_Manifestations_And_Paths()
    {
        var xmi = MagicDrawFixtures.Header + """
              <uml:Model xmi:type='uml:Model' xmi:id='_model' name='Shop'>
                <packagedElement xmi:type='uml:Package' xmi:id='_pkg' name='Deployment'>
                  <packagedElement xmi:type='uml:Class' xmi:id='_svc' name='OrderService'/>
                  <packagedElement xmi:type='uml:Device' xmi:id='_server' name='AppServer'>
                    <nestedNode xmi:type='uml:ExecutionEnvironment' xmi:id='_jvm' name='JVM'>
                      <deployment xmi:type='uml:Deployment' xmi:id='_dep' client='_jvm' supplier='_jar' deployedArtifact='_jar'/>
                    </nestedNode>
                  </packagedElement>
                  <packagedElement xmi:type='uml:Node' xmi:id='_db' name='DbServer'/>
                  <packagedElement xmi:type='uml:Artifact' xmi:id='_jar' name='orders.jar' fileName='orders-service.jar'>
                    <manifestation xmi:type='uml:Manifestation' xmi:id='_man' client='_jar' supplier='_svc' utilizedElement='_svc'/>
                  </packagedElement>
                  <packagedElement xmi:type='uml:CommunicationPath' xmi:id='_path' name='lan' memberEnd='_e1 _e2'>
                    <ownedEnd xmi:type='uml:Property' xmi:id='_e1' type='_server' association='_path'/>
                    <ownedEnd xmi:type='uml:Property' xmi:id='_e2' type='_db' association='_path'/>
                  </packagedElement>
                </packagedElement>
              </uml:Model>
              <sysml:Block xmi:id='_svc_b' base_Class='_svc'/>
            </xmi:XMI>
            """;
        var (m, report) = MagicDrawImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(xmi)), "shop.mdxml");
        var server = m.Find("_server")!;
        Assert.Equal(ElementKind.Device, server.Kind);
        var jvm = m.Find("_jvm")!;
        Assert.Equal((ElementKind.ExecutionEnvironment, server), (jvm.Kind, jvm.Owner));
        Assert.Equal(ElementKind.Node, m.Find("_db")!.Kind);
        var jar = m.Find("_jar")!;
        Assert.Equal((ElementKind.Artifact, "orders-service.jar"), (jar.Kind, jar.Get(DeploymentFields.FileName)));
        Assert.Equal([jvm], DeploymentModel.DeployedTo(m, jar));
        var svc = m.Find("_svc")!;
        Assert.Equal(ElementKind.Block, svc.Kind);
        Assert.Equal([svc], DeploymentModel.Manifests(m, jar));
        var path = m.OfKind(ElementKind.CommunicationPath).OfType<Relationship>().Single();
        Assert.Equal(new[] { server, m.Find("_db")! }.ToHashSet(), new[] { path.Source, path.Target }.ToHashSet());
        Assert.DoesNotContain(report.Skipped, x => x.Key.Contains("Deployment", StringComparison.Ordinal));
        Assert.Equal(DiagramType.Deployment, MagicDrawDiagramTypes("SysML Deployment Diagram", "Deployment Diagram"));
    }

    private static DiagramType? MagicDrawDiagramTypes(string type, string umlType) =>
        (DiagramType?)typeof(MagicDrawImporter).GetMethod("MapDiagramType", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [type, umlType]);

    [Fact]
    public void Enterprise_Architect_Import_Reads_Deployment_Objects_And_Connectors()
    {
        var dir = Directory.CreateTempSubdirectory("meridian-ea-deploy-");
        var path = Path.Combine(dir.FullName, "deploy.qea");
        try
        {
            using (var c = new SqliteConnection($"Data Source={path}"))
            {
                c.Open();
                void Exec(string sql) { using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
                Exec("create table t_package (Package_ID int, Name text, Parent_ID int, Notes text)");
                Exec("create table t_object (Object_ID int, Object_Type text, Name text, Alias text, Note text, Package_ID int, Stereotype text, ParentID int, Classifier int, Abstract text, NType int)");
                Exec("create table t_objectproperties (Object_ID int, Property text, Value text, Notes text)");
                Exec("create table t_attribute (ID int, Object_ID int, Name text, Type text, Classifier text, LowerBound text, UpperBound text, \"Default\" text, Notes text, Scope text, Pos int)");
                Exec("create table t_operation (OperationID int, Object_ID int, Name text, Type text, Notes text, Pos int)");
                Exec("create table t_operationparams (OperationID int, Name text, Type text, Kind text, Pos int)");
                Exec("create table t_connector (Connector_ID int, Connector_Type text, SubType text, Stereotype text, Name text, Start_Object_ID int, End_Object_ID int, DestCard text, PDATA2 text)");
                Exec("create table t_diagram (Diagram_ID int, Package_ID int, ParentID int, Diagram_Type text, Name text, StyleEx text)");
                Exec("create table t_diagramobjects (Diagram_ID int, Object_ID int, RectTop int, RectLeft int, RectRight int, RectBottom int, Sequence int)");
                Exec("create table t_diagramlinks (DiagramID int, ConnectorID int, Hidden text)");
                Exec("insert into t_package values (1,'Model',0,''),(2,'Runtime',1,'')");
                Exec("""
                    insert into t_object values
                    (10,'Device','AppServer','','',2,'server',0,0,'0',0),
                    (11,'ExecutionEnvironment','JVM','','',2,'',10,0,'0',0),
                    (12,'Node','DbServer','','',2,'',0,0,'0',0),
                    (13,'Artifact','orders.jar','','',2,'',0,0,'0',0),
                    (14,'Component','Orders','','',2,'',0,0,'0',0),
                    (15,'Node','Edge','','',2,'device',0,0,'0',0)
                    """);
                Exec("""
                    insert into t_connector values
                    (100,'Deployment','','','',13,11,'',''),
                    (101,'Manifest','','','',13,14,'',''),
                    (102,'CommunicationPath','','','',10,12,'',''),
                    (103,'Dependency','','deploy','',13,12,'','')
                    """);
                Exec("insert into t_diagram values (1,2,0,'Deployment','Runtime','')");
                Exec("insert into t_diagramobjects values (1,10,-50,100,500,-350,1),(1,13,-400,100,270,-480,2)");
            }
            var (m, _) = EaImporter.Import(path);
            var server = m.OfKind(ElementKind.Device).Single(e => e.Name == "AppServer");
            Assert.Equal(["server"], server.Stereotypes);
            var jvm = m.OfKind(ElementKind.ExecutionEnvironment).Single();
            Assert.Same(server, jvm.Owner);
            Assert.Equal(ElementKind.Device, m.AllElements.Single(e => e.Name == "Edge").Kind);   // a node stereotyped «device»
            var jar = m.OfKind(ElementKind.Artifact).Single();
            Assert.Equal(["JVM", "DbServer"], DeploymentModel.DeployedTo(m, jar).Select(e => e.Name).Order(StringComparer.Ordinal).Reverse());
            Assert.Equal(["Orders"], DeploymentModel.Manifests(m, jar).Select(e => e.Name));
            Assert.Single(m.OfKind(ElementKind.CommunicationPath));
            Assert.Equal(DiagramType.Deployment, m.Diagrams.Single().DiagramType);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            dir.Delete(true);
        }
    }

    // ------------------------------------------------------------------ code sync

    private static CodeSyncChanges Sync(ModelRepository m, CodeArtifactMode mode, string commit = "c1") =>
        CodeModelMapper.Apply(m, new CSharpCodeReader().Read(
        [
            new CodeFile("src/Eps.cs", "namespace Sat.Power; public class Eps { public double Watts { get; set; } }"),
            new CodeFile("src/Radio.cs", "namespace Comms; public class Radio { }"),
        ]), new CodeSyncOptions { RepoId = "repo-1", Commit = commit, RootPackage = "Flight Software", Artifacts = mode });

    [Fact]
    public void Code_Sync_Keeps_No_Artifacts_By_Default()
    {
        var m = new ModelRepository("Mission");
        Sync(m, CodeArtifactMode.None);
        Assert.Empty(m.OfKind(ElementKind.Artifact));
    }

    [Fact]
    public void Code_Sync_Can_Keep_An_Artifact_Per_Repository_Or_Per_Top_Level_Package_That_Manifests_It()
    {
        var m = new ModelRepository("Mission");
        Sync(m, CodeArtifactMode.Repository);
        var root = m.Root.OwnedElements.Single(e => e.Name == "Flight Software");
        var artifact = m.OfKind(ElementKind.Artifact).Single();
        Assert.Same(root, artifact.Owner);
        Assert.Equal([root], DeploymentModel.Manifests(m, artifact));
        Assert.Equal("repo-1", artifact.Get(CodeFieldKeys.Repo));
        Assert.True(Sync(m, CodeArtifactMode.Repository, "c2").IsEmpty);   // stable

        Sync(m, CodeArtifactMode.TopLevelPackages, "c3");
        var arts = m.OfKind(ElementKind.Artifact).Where(m.Contains).ToList();
        Assert.Equal(["Comms", "Sat"], arts.Select(a => a.Name).Order(StringComparer.Ordinal));
        foreach (var a in arts) Assert.Equal([a.Owner!], DeploymentModel.Manifests(m, a));
        Assert.DoesNotContain(new ModelValidator().Validate(m), i => i.RuleId == "MER-002");

        // Deployed by hand, then switched off: kept (marked removed) rather than deleted.
        var node = m.Create(m.Root, ElementKind.Node, "OBC");
        var sat = arts.Single(a => a.Name == "Sat");
        DeploymentModel.DeployTo(m, [sat], node);
        Sync(m, CodeArtifactMode.None, "c4");
        Assert.True(m.Contains(sat));
        Assert.Equal("true", sat.Get(CodeModelMapper.RemovedKey));
        Assert.DoesNotContain(m.OfKind(ElementKind.Artifact), a => a.Name == "Comms");
    }
}
