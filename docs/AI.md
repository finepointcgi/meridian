# AI assistant

Meridian has a modeling assistant powered by Claude (Anthropic). It answers questions about the open model, reviews it,
and proposes changes. You review every proposed change as a diff and apply it, or discard it. The assistant never edits
the model on its own.

Open it from the **AI ▸ Assistant** ribbon button, the **Assistant** dock tab (dock 13), or with ⌘K → "Ask the assistant".

## Setup

The assistant is **off** until an Anthropic API key is available. You can get a key at <https://console.anthropic.com>.

| Mode | Where the key comes from |
| --- | --- |
| Desktop / local mode | Click the gear icon in the assistant panel and paste the key. It is encrypted with ASP.NET Data Protection and stored in `{DataPath}/assistant.json`; the Data Protection keys are in `{DataPath}/keys`. If no key is saved, `Meridian:AI:ApiKey` or `ANTHROPIC_API_KEY` is used when set. |
| Server mode | `Meridian:AI:ApiKey` (for example the environment variable `Meridian__AI__ApiKey`) or the `ANTHROPIC_API_KEY` environment variable. Users can't enter keys in the browser. |

Settings (configuration keys, all optional):

| Key | Default | Meaning |
| --- | --- | --- |
| `Meridian:AI:Enabled` | `true` | Set to `false` to turn the assistant off for everyone (administrators). |
| `Meridian:AI:ApiKey` | none | The Anthropic API key (server mode). |
| `Meridian:AI:Model` | `claude-opus-5-5` | The Claude model to use. |
| `Meridian:AI:MaxTokens` | `16000` | The output limit per model call. |
| `Meridian:AI:ServerSideFallbacks` | `true` | Server-side refusal fallbacks (see below). |
| `Meridian:AI:Provider` | `Anthropic` | `Anthropic`, `Local` (an OpenAI-compatible server) or `ClaudeCode` (desktop / local mode only). In desktop mode this is the default until the user picks a provider in Settings. |
| `Meridian:AI:Local:BaseUrl` | none | The local server's OpenAI-compatible base URL, such as `http://localhost:11434/v1` (Ollama) or `http://localhost:1234/v1` (LM Studio). |
| `Meridian:AI:Local:Model` | none | The model name, such as `qwen2.5:14b`. |
| `Meridian:AI:Local:ApiKey` | none | Optional key for servers that want one. |
| `Meridian:AI:Local:ContextWindow` | `32768` | The model's context window in tokens. |
| `Meridian:AI:Local:MaxTokens` | `4096` | The output limit per call. |
| `Meridian:AI:Local:Temperature` | `0.2` | Sampling temperature. |
| `Meridian:AI:Local:ToolCalling` | `Auto` | `Auto`, `Native` or `Text` (the JSON-in-text protocol). |
| `Meridian:AI:Local:TimeoutSeconds` | `300` | How long to wait for the first token (a cold model has to load). |
| `Meridian:AI:ClaudeCode:Path` / `:Model` | none | Desktop only: path to `claude` and the model to pass (default: Claude Code's own). |

When the assistant is unavailable, the panel says why and how to turn it on.

The assistant can also run on a **local model** (Ollama, LM Studio and other OpenAI-compatible servers) or, in the
desktop app, through **your own Claude Code login**. See [Using a local model](#using-a-local-model) and
[Using Claude Code](#using-claude-code-desktop). The panel header always shows the active provider and model.

### Roles

Anyone who can open a project can ask the assistant questions. Only users who can edit the project can **apply** a
proposed change set. Viewers see the proposals with the Apply button turned off. A change set that touches an
element locked by a collaborator is refused. On a branch, changes apply to that branch like any other edit.

## Using a local model

Meridian can talk to any server with an OpenAI-compatible `/v1/chat/completions` endpoint: **Ollama**, **LM Studio**,
the **llama.cpp** server, **vLLM** and **LocalAI**. It uses plain HTTP with streaming (server-sent events) and OpenAI
function calling, with no extra SDK. With a local model, **your model content stays on that server**: nothing is sent to
Anthropic, and the privacy notice says so.

### Ollama

1. Install Ollama from <https://ollama.com> and start it (the app, or `ollama serve`).
2. Pull a model that does tool calling, for example `ollama pull qwen2.5:14b`.
3. Ollama's default context window is small. The system prompt and tool list alone take several thousand tokens, so
   start Ollama with a larger window, for example `OLLAMA_CONTEXT_LENGTH=32768 ollama serve`, and set the same
   *context window* in Meridian.
4. In **Settings ▸ AI assistant** choose **Local / OpenAI-compatible**, click **Ollama** (it fills in
   `http://localhost:11434/v1`), click **Find models**, pick the model, then click **Save**. **Test connection** checks
   that the server answers, that the model is installed and whether it supports tool calling.

### LM Studio

1. Install LM Studio, download a model, and start the local server (*Developer ▸ Start server*, default port 1234).
2. In **Settings ▸ AI assistant** choose **Local / OpenAI-compatible**, click **LM Studio** (`http://localhost:1234/v1`),
   then **Find models**, pick the loaded model and **Save**.

Other servers work the same way: enter the base URL (it usually ends in `/v1`). Some servers want an API key; the key is
optional and stored encrypted like the Anthropic key.

### Choosing a model

Quality varies a lot between local models, and the assistant depends on tool calling. Models that are known to handle
tools reasonably well include, for example, **Qwen 2.5 / Qwen 3 Instruct** (14B and larger), **Llama 3.1 / 3.3
Instruct** (70B does much better than 8B) and **Mistral Small**. Small models answer questions fine but often make
mistakes in multi-step actions such as *Diagram my model* or *Generate requirements*. Every proposal still goes through
the usual review card, so nothing reaches your model unchecked.

### Settings

| Setting | Meaning |
| --- | --- |
| Base URL | The OpenAI-compatible endpoint, such as `http://localhost:11434/v1`. A bare `host:port` gets `/v1` added. |
| Model | Pick one from **Find models** (the server's `/v1/models`, plus Ollama's `/api/tags`), or type any name. |
| API key | Optional. Sent as `Authorization: Bearer …` to that server only. |
| Context window | The model's context size in tokens (default 32768). Older tool results are shortened to fit; if the conversation still doesn't fit, you're asked to start a new chat. |
| Max output tokens | Per model call (default 4096). |
| Temperature | Default 0.2. Leave it empty to use the server's default. |
| Model supports tool calling | **Auto-detect** (default), **Yes** or **No**. Auto asks Ollama's `/api/show` for the model's capabilities, and otherwise tries native tool calling and switches to the text protocol if the server refuses tools. |

### How tools work on a local model

- **Native tool calling.** The assistant's tools are sent as OpenAI `tools` (the same JSON schemas Claude gets), and
  results go back as `role: tool` messages. Every tool input is validated against its schema before the tool runs.
- **Text protocol** for models without tool calling. The system prompt lists the tools with their schemas and asks the
  model to reply with a fenced JSON block, `{"tool": "...", "arguments": {...}}` (or an array of them). Meridian parses
  it leniently (fences, `<tool_call>` tags, trailing commas, arguments sent as a string), validates every call against
  the tool's schema, and if a call is malformed or doesn't match the schema, asks the model once to correct it. If the
  second try is still invalid, the panel shows a warning and nothing runs. The JSON blocks are never shown in the chat.
- **Prompts.** The local path uses the same system prompt as Claude, plus a few lines of format guidance.
- **Slow models.** A local model may need to load first, so Meridian waits up to 5 minutes for the first token
  (`Meridian:AI:Local:TimeoutSeconds`) and 2 minutes between tokens. The panel shows what is happening (waiting for the
  model, thinking, preparing a tool call). **Stop** cancels at any time.
- **Errors.** Clear messages for a server that isn't running ("Could not connect to the local model server… Start
  Ollama"), a model that isn't installed ("Model 'x' isn't installed — run `ollama pull x`"), a wrong base URL, a
  rejected key and timeouts.

### On a server

On a team server the administrator configures the local model with configuration keys (see the table above and
`docs/DEPLOYMENT.md`); users can't change the provider or the address, and see the provider and model read-only.
Administrators also see the address and a **Test connection** button, which only ever tests the configured server.
Requests go through an HTTP client that only allows the configured base URL's scheme, host and port, with redirects
turned off, so nothing can make the server call another address. Private and loopback addresses are allowed because the
administrator chose the address explicitly.

## Using Claude Code (desktop)

If Claude Code is installed on the computer that runs Meridian (the desktop app, or Meridian in local mode), the
assistant can use it instead of an API key: choose **Claude Code (your login)** in **Settings ▸ AI assistant**. The
option is offered only when the `claude` command is found (on `PATH`, in `~/.claude/local`, `~/.local/bin`,
`/opt/homebrew/bin`, `/usr/local/bin` or `%APPDATA%\npm`), and you can set the path yourself. **Check installation**
runs `claude --version` and `claude auth status`, neither of which uses tokens. If Claude Code is missing or not signed
in, the panel explains how to install it and sign in (`claude`, or `claude auth login`).

**This uses your own local Claude Code install and your own login**, so usage counts against your Claude plan or your
Claude Code account. Check Anthropic's terms for your plan before using it this way. Model content is sent to Anthropic
through that login, and the privacy notice says so.

How it runs:

- Each question starts `claude -p --output-format stream-json --input-format stream-json --verbose
  --include-partial-messages` in a new, empty temporary folder (deleted afterwards), never in your home or project
  folder. Text streams into the panel as it arrives; **Stop** kills the process tree.
- Claude Code's own tools are switched off: `--tools ""` disables every built-in tool, `--disallowedTools` lists Bash,
  Edit, Write, Read, WebFetch, WebSearch and the other file and shell tools as well, `--permission-mode dontAsk` denies
  anything not explicitly allowed without prompting, and `--allowedTools` lists only Meridian's tools.
- Meridian's tools are served to it as an MCP server on `127.0.0.1` with a random port and a random per-question bearer
  token (`--strict-mcp-config --mcp-config` with a temporary file that is deleted afterwards). The endpoint rejects
  requests without the token, with a non-loopback `Host` header or with a browser `Origin`.
- Tool calls run in Meridian exactly as with the API: inputs are validated against their schemas and write tools only
  propose changes, which arrive as the usual review card.
- `--append-system-prompt` adds Meridian's system prompt, `--max-turns` bounds the tool rounds, and `--model` is passed
  only when you choose a model (by default Claude Code uses its own default). Earlier questions of the chat are sent as
  a short transcript, because each question runs a fresh Claude Code session.

Claude Code is never used on a team server.

## Privacy

**With Claude (the default) or Claude Code, using the assistant sends model content to Anthropic.** With a local model,
the same content goes to the configured model server instead. Each question includes:

- a project summary (element counts, top-level packages, requirement coverage),
- the active diagram (its name and the names and ids of the elements on it),
- the current selection,
- and whatever the assistant looks up with its tools while answering (element fields, relationships, SysML text,
  validation results).

The first time a user opens the assistant, Meridian shows a notice about this and won't send anything until they accept
it. The acknowledgement is stored on the server, once per user.

- The API key stays on the server. Meridian never renders it, logs it or sends it to the browser. The settings dialog only
  says whether a key is saved.
- Meridian never logs prompts or answers. The SDK's OpenTelemetry export is turned off for assistant requests. The
  Output dock records only outcomes, for example "Assistant: Applied 5 changes as one undo step".
- Conversations are kept in server memory per user, project and branch. They are lost on restart, and **New chat**
  clears one. They are never written to the database.
- Anthropic's data handling is covered by your organization's agreement with Anthropic (commercial terms or API terms).

## Using it

- **Chat.** Answers stream in as Markdown. Elements mentioned in an answer show as chips: click a chip to select the
  element in the tree and on the diagram (a diagram chip opens that diagram). **Stop** cancels the current answer;
  **New chat** starts over.
- **Proposed changes.** The write tools collect changes into a card that lists each added (+), changed (~) and
  removed (−) item. Field edits show the old and new values. Suggested links show a confidence and a reason. You can
  accept or reject each item; an item that depends on a rejected one (for example, a part of a rejected block) is
  skipped. **Apply** runs the accepted items as **one undoable step** (⌘Z reverts all of them). Applied changes are
  replicated to collaborators like any other edit. If any item fails (for example, because someone deleted its owner in
  the meantime), the whole step is rolled back. **Discard** drops the card. In both cases the assistant is told the
  outcome on your next question.
- **Quick actions** (buttons above the chat; these run at higher effort):
  - *Generate requirements from text…*: paste a specification and pick a package. You get proposed requirements, each
    with an id (continuing your numbering), "shall" text, a rationale and a verification method, plus derive links
    where they apply.
  - *Suggest trace links*: proposes Satisfy, Verify and DeriveReqt links for uncovered requirements in the selected
    package (or the whole model). Each link has a confidence and a reason, and you accept or reject each one.
  - *Review this model*: looks for unsatisfied or unverified requirements, orphan blocks, vague "shall" wording,
    missing units and missing rationale. The result is a checklist; each item links to its element and can be ticked
    off.
  - *Diagram from description…*: describe a system or process and choose a block definition, internal block,
    activity or package diagram. You get the elements plus an automatically laid-out diagram (a package diagram
    nests sub-packages inside their owners and draws the dependencies between them).
  - *Diagram my model*: Claude looks at the deterministic auto-diagram plan (`plan_diagrams`) and the existing
    diagrams, decides which views tell the story (usually a package overview, the main structure, the internals of the
    important blocks, key behaviors and requirement views), names them, chooses what to include and leave out, and
    writes a one-line purpose for each. The proposal arrives as the usual change card with **one item per diagram**, so
    you accept or reject each view; Apply creates them all as one undo step. The purpose becomes the diagram's
    documentation, which reports print above the figure. Layout is done by Meridian, not by Claude.

### Auto-diagram without AI

The same layout engine works without an API key: **Diagram ▸ Auto-diagram** in the ribbon, ⌘K → "Diagram the whole
model", or right-click a package → "Auto-diagram this package". It plans a package overview, a block definition
diagram per package with blocks, an internal block diagram per block with parts, a requirement diagram per
requirements package, a diagram per activity or state machine that has none, and use case diagrams. Elements already on
a diagram of that kind are skipped unless you tick *Regenerate existing views*; groups bigger than about 25 shapes are
split. The code is `src/Meridian.Core/Language/AutoDiagrammer.cs`.

### AI narrative in generated documents

**Generate ▸ Formats ▸ Generate SDD…** (or ⌘K) first offers to add the missing views (it lists them), then opens the
document dialog with the System Design Description template selected. When the assistant is configured, that dialog
has an **AI narrative** switch: *Draft narrative* sends a summary of the model (names, documentation, structure,
requirements, diagram names; see `NarrativeWriter.Digest`) and the section titles to Claude, which returns an overview
and a rationale paragraph per section through the strict `submit_narrative` tool. The draft is shown in the dialog for
review: edit any paragraph or untick a section. Only the reviewed text is added to the document, at the top of each
section; nothing is added silently. It uses the same model, settings, data notice and refusal handling as the chat.

## Tools

The assistant uses Claude's tool use with these Meridian tools. Every input is checked against the tool's JSON schema
before the tool runs. Invalid input, unknown ids and edits the metamodel doesn't allow are returned to Claude as error
results so it can correct them.

Read-only:

| Tool | What it does |
| --- | --- |
| `search_elements` | Runs a model query in the Query panel syntax (`kind:Requirement where no:verify`, `in:Structure`, `has:satisfy`…), with an optional text filter. |
| `get_element` | All fields of one element, its owner, owned elements, incoming and outgoing relationships, and the diagrams that show it. |
| `list_diagrams` | Diagrams with type, owner and shape count, optionally inside one package. |
| `get_traceability` | What satisfies, verifies, refines, traces to or derives a requirement, plus its sub-requirements, suspect links and coverage. |
| `validate_model` | Runs Meridian's validation rules, optionally for one package. |
| `get_sysml_text` | A package as SysML v2 text, with `@id` comments so that edits keep element identity. |
| `plan_diagrams` | The auto-diagrammer's plan for the model or one package: diagram type, suggested name, owner, element ids, shape count, new or update. |
| `report_findings` | Records review findings for the checklist. It doesn't change the model. |

Write tools (these only propose changes; nothing is applied until you click Apply):

| Tool | What it proposes |
| --- | --- |
| `create_element` | A new element of any kind, with fields (requirement id, text, rationale, verification method…). It returns a `new:N` reference that later calls can use. |
| `update_fields` | New field values, including renames. The card shows old → new. |
| `create_relationship` | A Satisfy, Verify, DeriveReqt, Composition, Allocate or other relationship, optionally with a confidence and a reason. End types are checked against the metamodel. |
| `delete_element` | Deleting an element, along with what it owns, its relationships and its diagram shapes. The card shows the cascade. |
| `create_diagram_with_elements` | A new diagram showing the given elements and the relationships between them, laid out automatically by the auto-diagrammer (nesting, ports, no overlaps). |
| `create_diagrams` | Several diagrams at once, one reviewable item each, with a name, an owner, a one-line purpose and the elements to show (or the owner's default content minus `exclude_ids`). At most 60 shapes per diagram. |
| `apply_sysml_text` | An edited SysML v2 text for a package, applied through the same sync as the SysML text editor. It is dry-run on a copy of the model so the card can list the exact changes. Syntax errors are returned to Claude. |

## How it calls Claude

- It uses the official Anthropic C# SDK (`Anthropic` NuGet package, pinned in `src/Meridian.Assistant`) and the
  Messages API with streaming.
- The default model is `claude-opus-5-5`. Thinking is adaptive (Meridian leaves the setting out; it can't be turned off
  on this model). Effort is `medium` for chat and `high` for quick actions.
- Tools are `strict` and `tool_choice` is `auto`. Forced tool choice isn't supported on this model, so the prompts name
  the tools to use.
- The message loop is in `src/Meridian.Assistant/Engine/AssistantEngine.cs`. It streams text into the transcript, runs
  the requested tools, and sends their results back until Claude finishes. Only completed turns stay in the conversation
  history.
- A `refusal` stop reason is shown as a notice. That turn's tool calls are never run and its proposals are dropped.
  A reply cut off at the output limit in the middle of a tool call isn't run either.
- **Server-side refusal fallbacks** are on: the beta `server-side-fallback-2026-07-01` with `fallbacks: "default"`, which
  the C# SDK supports (`MessageCreateParams.Fallbacks`). A request the model declines for policy reasons is retried on
  the model's default fallback in the same call. Turn this off with `Meridian:AI:ServerSideFallbacks=false`, for example
  behind a proxy that doesn't accept beta features.
- API errors (invalid key, rate limits, overload, network) are shown in the panel. The SDK retries 429 and 5xx errors
  twice.

## Cost notes

You pay Anthropic directly for the tokens the assistant uses, at the API price of the configured model. For
`claude-opus-5-5` that is $4 per million input tokens and $20 per million output tokens, with cache reads at $0.20 per
million (check Anthropic's pricing page for current prices). To keep costs down:

- **Prompt caching.** The system prompt and the tool list are identical in every request and cached, as is the growing
  conversation, so each tool round re-reads the earlier rounds from cache. Cached input costs a fraction of the normal
  price. The panel footer shows the input (and cached), output tokens and model of the current chat.
- **What drives cost.** A plain question usually takes a few thousand input tokens. The quick actions run at `high`
  effort and call many tools, so a model review or trace-link pass on a large model can use far more, especially when
  they read SysML text or validation results. Scope them by selecting a package first.
- **Start fresh.** Long conversations resend their whole history on every question. Use **New chat** when you change
  topic.
- **Hard caps.** The output limit per call (`Meridian:AI:MaxTokens`) and 24 tool rounds per question bound a single
  answer. Set spending limits for the key in the Anthropic Console.

## Tests

`tests/Meridian.Tests/AssistantTests.cs` covers the tools, schema validation, change-set apply, undo, replication and
rollback, and the message loop against a fake HTTP handler (`AssistantFakes.cs`).
`tests/Meridian.Tests/AutoDiagramAssistantTests.cs` covers `plan_diagrams`, `create_diagrams`, the "Diagram my model"
turn, the SDD figures and the narrative writer, with the same fake handler.
`tests/Meridian.Web.Tests/AssistantPanelTests.cs` covers the panel. The tests never call the real API.
`tests/Meridian.Tests/LocalModelProviderTests.cs` covers the OpenAI-compatible provider against a fake server
(`LocalModelFakes.cs`: SSE streams with tool calls, the multi-turn tool loop, the text protocol with retry and schema
errors, model discovery, unreachable servers, missing models, timeouts and the host allow-list).
`tests/Meridian.Tests/ClaudeCodeProviderTests.cs` runs the Claude Code provider against a fake `claude` shell script
that records its arguments and emits recorded stream-json, including an MCP tool call round trip; the real CLI is never
run. `tests/Meridian.Web.Tests/LocalAssistantSettingsTests.cs` covers the settings and panel for the local provider.

## Code layout

- `src/Meridian.Assistant/Providers/ChatModels.cs`: the provider abstraction (`IChatProvider`, provider-neutral
  messages, tool definitions and streaming callbacks).
- `AnthropicChatProvider.cs`: Claude through the Anthropic SDK (the original request, unchanged).
- `OpenAiCompatibleProvider.cs`, `TextToolProtocol.cs`, `LocalModelHttp.cs`: local models, the JSON-in-text tool
  protocol and the host-restricted HTTP client.
- `ClaudeCodeProvider.cs`, `LoopbackMcpServer.cs`: Claude Code and the loopback MCP server for Meridian's tools.
