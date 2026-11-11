# AI assistants (MCP)

A Meridian team server runs a [Model Context Protocol](https://modelcontextprotocol.io) server at `/api/mcp`. MCP clients
such as Hearth, Claude Code or Claude Desktop can connect to it and answer questions about your models. For example: "which
requirements have no verification?", "what satisfies REQ-012?" or "show me the SysML for the powertrain".

It offers the tools of Meridian's own [AI assistant](ai-assistant.md). Read tools look things up; **write tools change the
model**. Each change is applied at once as one step in the project's history, so anyone can see it in version history and undo
it, and people with the project open see it immediately.

| Tool | What it does |
| --- | --- |
| `list_projects` | The projects you can open. Call this first. |
| `search_elements` | Finds elements with Meridian's query syntax (the same as the Query panel). |
| `get_element` | One element's fields, owner, owned elements, relationships and diagrams. |
| `list_diagrams` | The model's diagrams, optionally inside one package. |
| `get_traceability` | Requirement coverage: what satisfies, verifies and refines what. |
| `validate_model` | Runs the model checks and returns the issues. |
| `get_sysml_text` | SysML v2 text for the model or one package. |
| `plan_diagrams` | Which diagrams the auto-diagrammer would create. |
| `create_element` | **Write.** Adds a block, requirement, part, port, action… under an owner. |
| `update_fields` | **Write.** Renames an element or sets its fields (text, rationale, type, tagged values…). |
| `create_relationship` | **Write.** Adds satisfy, verify, derive, refine, trace, composition… links. |
| `delete_element` | **Write.** Deletes an element with what it owns and its relationships. |
| `create_diagram_with_elements`, `create_diagrams` | **Write.** Adds laid-out diagrams. |
| `apply_sysml_text` | **Write.** Replaces a package's content with edited SysML v2 text (only the differences are applied). |

Every tool except `list_projects` takes a `project` argument: the project's id or its name.

MCP is available on a team server (server mode). The desktop app's private workspace has no server to connect to.

## Connect a client

1. In Meridian, open your avatar menu ▸ **API tokens…** and create a token for the assistant. It is shown once.
2. Add the server to your client with the address `https://<your Meridian>/api/mcp` and the header
   `Authorization: Bearer mrd_…`.

**Hearth:** open **Tools and MCP**, choose **Meridian** under **Add an integration**, and paste the address and token.

**Claude Code:**

```bash
claude mcp add --transport http meridian https://meridian.example.com/api/mcp --header "Authorization: Bearer mrd_…"
```

**Any other client** that supports remote (streamable HTTP) servers:

```json
{
  "mcpServers": {
    "meridian": {
      "type": "http",
      "url": "https://meridian.example.com/api/mcp",
      "headers": { "Authorization": "Bearer mrd_…" }
    }
  }
}
```

## What it can see

- **Your access, no more.** The token acts as you: it sees the projects you are a member of, and packages hidden from you
  stay hidden (see [Administration](administration.md) for package permissions). Write tools need the editor role, and the
  same rules as editing in a browser apply: read-only and hidden packages and other people's locks refuse the change.
- **Changes ask first in the client.** Write tools are marked as not read-only (and `update_fields`, `delete_element` and
  `apply_sysml_text` as replacing what was there), so clients such as Hearth ask you before each one runs.
- **Where the data goes.** Tool results go to the AI client and the model it uses. With a local model (Ollama or LM Studio
  through Hearth, for example), model content stays on your network. With a hosted model, it goes to that provider.
- **Tokens only.** The endpoint doesn't accept your password or browser session. Revoke the token to disconnect the client
  at once. Requests from web pages are refused.
- **Audited.** Each tool call is recorded in the audit log as `mcp.tool`, with the project, the tool and its arguments
  (never the results). Changes also appear in the project's version history under your name.
- **Results are data.** Element names and documentation could contain instructions. The server tells clients that results
  are data, not instructions, but the client and its model are responsible for treating them that way.

## Protocol details

Streamable HTTP, stateless: each JSON-RPC request is a `POST` answered with one JSON response. There are no sessions and no
server-sent events (`GET` and `DELETE` return 405). Supported protocol versions: `2025-11-25`, `2025-06-18` and `2025-03-26`.
Methods: `initialize`, `ping`, `tools/list` and `tools/call`. Results longer than 60,000 characters are cut; narrow the query.

## Linking to Workbench and Keel

Meridian, Workbench and Keel PLM point at each other with ids, so an assistant can follow a question from one app to the next.
On a Meridian element, add these tagged values:

| Tagged value | Holds | Example |
| --- | --- | --- |
| `keel.part` | The Keel part number this block is built as | `P-000123` |
| `keel.spec` | The Keel requirement specification this requirement comes from | `SPEC-0003` |
| `workbench.project` | The Workbench project code that pays for this work | `1001` |

Assistants find linked elements with `search_elements`, for example `kind:Block where tag:keel.part = P-000123`. Hearth's
engineering team uses this convention.
