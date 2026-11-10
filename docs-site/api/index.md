# API reference

This reference covers the stable public API in the `Meridian.Scripting` assembly, version 1.0. Scripts and plugins
both program against it.

| Type | Use |
| --- | --- |
| <xref:Meridian.Scripting.IModelApi> | the model: find, create, connect, edit, batch, workbench and output |
| <xref:Meridian.Scripting.IElement>, <xref:Meridian.Scripting.IRelationship>, <xref:Meridian.Scripting.IDiagram> | wrappers over model elements, relationships and diagrams |
| <xref:Meridian.Scripting.Fields> | well-known field keys |
| <xref:Meridian.Scripting.ScriptGlobals> | what a script sees as globals (`Model`, `Selection`, `Log`, …) |
| <xref:Meridian.Scripting.IMeridianPlugin>, <xref:Meridian.Scripting.IPluginHost>, <xref:Meridian.Scripting.PluginIssue> | the plugin contract |
| <xref:Meridian.Scripting.ScriptEngine>, <xref:Meridian.Scripting.ModelApi>, <xref:Meridian.Scripting.HeadlessBridge> | running scripts outside the workbench, for example in tests |

Kinds, relationship kinds and diagram types are passed as strings (case-insensitive), so the API stays stable when
the metamodel grows. Later API versions only add members.

See [Scripting](../docs/scripting.md) and [Plugins](../docs/plugins.md) for guides and examples.
