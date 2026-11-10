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

When the assistant is unavailable, the panel says why and how to turn it on.

### Roles

Anyone who can open a project can ask the assistant questions. Only users who can edit the project can **apply** a
proposed change set. Viewers see the proposals with the Apply button turned off. A change set that touches an
element locked by a collaborator is refused. On a branch, changes apply to that branch like any other edit.

## Privacy

**Using the assistant sends model content to Anthropic.** Each question includes:

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
  - *Diagram from description…*: describe a system or process and choose a block definition, internal block or
    activity diagram. You get the elements plus an automatically laid-out diagram.

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
| `report_findings` | Records review findings for the checklist. It doesn't change the model. |

Write tools (these only propose changes; nothing is applied until you click Apply):

| Tool | What it proposes |
| --- | --- |
| `create_element` | A new element of any kind, with fields (requirement id, text, rationale, verification method…). It returns a `new:N` reference that later calls can use. |
| `update_fields` | New field values, including renames. The card shows old → new. |
| `create_relationship` | A Satisfy, Verify, DeriveReqt, Composition, Allocate or other relationship, optionally with a confidence and a reason. End types are checked against the metamodel. |
| `delete_element` | Deleting an element, along with what it owns, its relationships and its diagram shapes. The card shows the cascade. |
| `create_diagram_with_elements` | A new diagram showing the given elements and the relationships between them, laid out automatically. |
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
`tests/Meridian.Web.Tests/AssistantPanelTests.cs` covers the panel. The tests never call the real API.
