using Meridian.Core.Language;
using Meridian.Core.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Meridian.Serialization;

/// <summary>
/// Reverse engineering: parses C# sources with Roslyn into packages (namespaces), classes, interfaces, records,
/// structs and enums with their properties/fields, methods, inheritance and interface realization.
/// </summary>
public static class CSharpReverse
{
    public sealed record Result(int Types, int Relationships, Diagram? Diagram);

    public static Result Import(ModelRepository m, Element target, IEnumerable<(string Path, string Source)> files, bool createDiagram = true)
    {
        var trees = files.Select(f => CSharpSyntaxTree.ParseText(f.Source, path: f.Path)).ToList();
        var types = new Dictionary<string, Element>(StringComparer.Ordinal);       // simple name -> element
        var pending = new List<(Element Type, BaseTypeDeclarationSyntax Syntax)>();
        var relationships = 0;
        Diagram? diagram = null;

        using (m.History.Transaction("Reverse engineer C#"))
        {
            Element PackageFor(string ns)
            {
                var owner = target;
                foreach (var part in ns.Split('.', StringSplitOptions.RemoveEmptyEntries))
                    owner = owner.OwnedElements.FirstOrDefault(c => c.Kind == ElementKind.Package && c.Name == part) ?? m.Create(owner, ElementKind.Package, part);
                return owner;
            }

            foreach (var tree in trees)
                foreach (var decl in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                {
                    if (decl.Parent is BaseTypeDeclarationSyntax) continue;   // nested types: keep it simple, skip
                    var ns = decl.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Select(n => n.Name.ToString()).Reverse().ToList();
                    var pkg = PackageFor(string.Join(".", ns));
                    var kind = decl switch
                    {
                        InterfaceDeclarationSyntax => ElementKind.Interface,
                        EnumDeclarationSyntax => ElementKind.Enumeration,
                        StructDeclarationSyntax or RecordDeclarationSyntax { ClassOrStructKeyword.Text: "struct" } => ElementKind.DataType,
                        _ => ElementKind.Class,
                    };
                    var name = decl.Identifier.Text;
                    if (types.ContainsKey(name)) continue;
                    var e = m.Create(pkg, kind, name);
                    if (decl.Modifiers.Any(SyntaxKind.AbstractKeyword)) m.Set(e, FieldKeys.IsAbstract, "true");
                    if (decl is RecordDeclarationSyntax) m.Set(e, FieldKeys.Stereotypes, "record");
                    m.Set(e, FieldKeys.Documentation, Doc(decl));
                    types[name] = e;
                    pending.Add((e, decl));
                }

            foreach (var (type, decl) in pending)
            {
                if (decl is EnumDeclarationSyntax en)
                {
                    foreach (var mem in en.Members) m.Create(type, ElementKind.EnumerationLiteral, mem.Identifier.Text);
                    continue;
                }
                var tdecl = (TypeDeclarationSyntax)decl;
                // Primary-constructor records: parameters are properties.
                if (tdecl is RecordDeclarationSyntax { ParameterList: { } pl })
                    foreach (var p in pl.Parameters) AddProperty(type, p.Identifier.Text, p.Type, null, "public");
                foreach (var member in tdecl.Members)
                {
                    switch (member)
                    {
                        case PropertyDeclarationSyntax p:
                            AddProperty(type, p.Identifier.Text, p.Type, p.Initializer?.Value.ToString(), Visibility(p.Modifiers), Doc(p));
                            break;
                        case FieldDeclarationSyntax f when !f.Modifiers.Any(SyntaxKind.ConstKeyword):
                            foreach (var v in f.Declaration.Variables)
                                AddProperty(type, v.Identifier.Text, f.Declaration.Type, v.Initializer?.Value.ToString(), Visibility(f.Modifiers), Doc(f));
                            break;
                        case MethodDeclarationSyntax md:
                        {
                            var op = m.Create(type, ElementKind.Operation, md.Identifier.Text);
                            var ps = md.ParameterList.Parameters.Select(p => $"{p.Identifier.Text} : {TypeName(p.Type)}");
                            var ret = md.ReturnType.ToString() == "void" ? "" : $" : {TypeName(md.ReturnType)}";
                            m.Set(op, FieldKeys.Signature, $"({string.Join(", ", ps)}){ret}");
                            m.Set(op, FieldKeys.Visibility, Visibility(md.Modifiers));
                            m.Set(op, FieldKeys.Documentation, Doc(md));
                            break;
                        }
                    }
                }
                foreach (var bt in tdecl.BaseList?.Types ?? default)
                {
                    var baseName = Simple(bt.Type.ToString());
                    if (!types.TryGetValue(baseName, out var b)) continue;
                    var kind = b.Kind == ElementKind.Interface && type.Kind != ElementKind.Interface ? ElementKind.Realization : ElementKind.Generalization;
                    if (Metamodel.CheckEnds(kind, type.Kind, b.Kind) != null) continue;
                    m.Connect(kind, type, b);
                    relationships++;
                }
            }

            void AddProperty(Element owner, string name, TypeSyntax? typeSyntax, string? defaultValue, string visibility, string? doc = null)
            {
                if (!Metamodel.CanOwn(owner.Kind, ElementKind.Property)) return;
                var (elementType, many) = Unwrap(typeSyntax);
                var p = m.Create(owner, ElementKind.Property, name);
                m.Set(p, FieldKeys.Type, types.TryGetValue(elementType, out var t) ? t.Id : MapPrimitive(elementType));
                m.Set(p, FieldKeys.Multiplicity, many ? "*" : typeSyntax is NullableTypeSyntax ? "0..1" : "1");
                m.Set(p, FieldKeys.DefaultValue, defaultValue);
                m.Set(p, FieldKeys.Visibility, visibility);
                m.Set(p, FieldKeys.Documentation, doc);
            }

            if (createDiagram && types.Count > 0)
            {
                diagram = m.CreateDiagram(target, DiagramType.Class, "Code structure");
                var i = 0;
                foreach (var t in types.Values)
                {
                    var features = t.OwnedElements.Count(c => c.Kind is ElementKind.Property or ElementKind.Operation or ElementKind.EnumerationLiteral);
                    m.AddNode(diagram, t, new Bounds(40 + (i % 5) * 240, 40 + (i / 5) * 280, 210, Math.Min(260, 70 + features * 16)));
                    i++;
                }
                m.SetBounds(diagram, AutoLayout.Layered(diagram), "Arrange");
            }
        }
        return new Result(types.Count, relationships, diagram);
    }

    private static string Simple(string name)
    {
        var lt = name.IndexOf('<');
        if (lt >= 0) name = name[..lt];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    private static (string Name, bool Many) Unwrap(TypeSyntax? t)
    {
        switch (t)
        {
            case null: return ("", false);
            case NullableTypeSyntax n: return (Unwrap(n.ElementType).Name, false);
            case ArrayTypeSyntax a: return (Unwrap(a.ElementType).Name, true);
            case GenericNameSyntax g when g.Identifier.Text is "List" or "IList" or "IEnumerable" or "ICollection" or "IReadOnlyList" or "IReadOnlyCollection" or "HashSet" or "ISet" or "Collection"
                                          && g.TypeArgumentList.Arguments.Count == 1:
                return (Unwrap(g.TypeArgumentList.Arguments[0]).Name, true);
            case QualifiedNameSyntax q: return Unwrap(q.Right);
            default: return (Simple(t.ToString()), false);
        }
    }

    private static string TypeName(TypeSyntax t)
    {
        var (n, many) = Unwrap(t);
        var p = MapPrimitive(n);
        return many ? p + "[*]" : p;
    }

    private static string MapPrimitive(string t) => t switch
    {
        "int" or "long" or "short" or "byte" or "Int32" or "Int64" => "Integer",
        "double" or "float" or "decimal" or "Double" or "Single" or "Decimal" => "Real",
        "bool" or "Boolean" => "Boolean",
        "string" or "String" or "char" or "Guid" or "DateTime" or "DateTimeOffset" => "String",
        _ => t,
    };

    private static string Visibility(SyntaxTokenList mods) =>
        mods.Any(SyntaxKind.PublicKeyword) ? "public" : mods.Any(SyntaxKind.ProtectedKeyword) ? "protected" : mods.Any(SyntaxKind.InternalKeyword) ? "package" : "private";

    private static string? Doc(SyntaxNode node)
    {
        var trivia = node.GetLeadingTrivia().Select(t => t.GetStructure()).OfType<DocumentationCommentTriviaSyntax>().FirstOrDefault();
        var summary = trivia?.Content.OfType<XmlElementSyntax>().FirstOrDefault(x => x.StartTag.Name.ToString() == "summary");
        if (summary == null) return null;
        var text = string.Concat(summary.Content.Select(c => c.ToString())).Replace("///", "");
        return string.Join(" ", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();
    }
}
