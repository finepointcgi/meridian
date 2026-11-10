using Meridian.Core.Language;
using Meridian.Core.Libraries;
using Meridian.Core.Model;

namespace Meridian.Serialization;

/// <summary>How exports (XMI, SysML v2, reports) treat the content of used projects (docs/LIBRARIES.md).</summary>
public enum LibraryExportMode
{
    /// <summary>Library packages are exported with the project, as if they were part of it.</summary>
    Inline,
    /// <summary>Only the project is exported; elements that refer to library elements refer to them by id and qualified name.</summary>
    Reference,
}

public static class LibraryExport
{
    /// <summary>Field set on reference stand-ins: "Standard Parts (v3)".</summary>
    public const string ReferenceField = "libraryRef";

    /// <summary>
    /// The model to hand to an exporter. <see cref="LibraryExportMode.Inline"/> returns <paramref name="model"/> (library packages are
    /// in its tree). <see cref="LibraryExportMode.Reference"/> returns a copy without them, where each referenced library element is a
    /// stand-in named by its qualified name (not in the tree, so not exported itself) and features typed by library elements are
    /// typed by that qualified name.
    /// </summary>
    public static ModelRepository Prepare(ModelRepository model, LibraryExportMode mode)
    {
        if (!model.HasLibraries || mode == LibraryExportMode.Inline) return model;
        var copy = ProjectFile.Deserialize(ProjectFile.Serialize(model));
        foreach (var stub in copy.MissingReferences)
            if (model.Find(stub.Id) is { } original && model.LibraryOf(original) is { } lib)
            {
                ModelRepository.SetFieldForLoad(stub, FieldKeys.Name, original.QualifiedName);
                ModelRepository.SetFieldForLoad(stub, MissingReference.Field, null);
                ModelRepository.SetFieldForLoad(stub, ReferenceField, $"{lib.Info.SourceProjectName} ({lib.Info.PinLabel})");
            }
        foreach (var e in copy.AllElements)
            if (Metamodel.IsTypedFeature(e.Kind) && e.TypeRef.Length > 0 && copy.Find(e.TypeRef) is null && model.Find(e.TypeRef) is { } t && model.IsLibraryElement(t))
                ModelRepository.SetFieldForLoad(e, FieldKeys.Type, t.QualifiedName);
        return copy;
    }
}
