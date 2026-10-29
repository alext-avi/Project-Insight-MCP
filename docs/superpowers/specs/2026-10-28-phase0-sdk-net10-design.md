# Phase 0: local server on .NET 10 + official MCP C# SDK

**Status:** implemented on branch `phase0-sdk-net10` · **Release:** 2.0.0
**Follow-ups:** #3 (Phase 1, MCP Apps review card), #4 (Phase 2, remote host), #5 (Phase 3, attribute-style tools decision)

## Goal

Move the local stdio server (`projectinsight-mcp`, which runs the Claude Desktop `.mcpb` bundle and the `ProjectInsight.MCP` NuGet tool) to .NET 10, with protocol handling delegated to the official `ModelContextProtocol` SDK. Tool names, descriptions, input schemas and results stay unchanged. This gives Phase 1 a single place to add resources, `_meta.ui` and structured results.

## Layout

```
servers/project-insight/
  core/      ProjectInsightMCP.Core   net8.0;net10.0  Services, Models, Shared/McpModels.cs
  src-stdio/ projectinsight-mcp       net10.0         SDK host (hand-rolled JSON-RPC loop removed)
  src/       Functions host           net8.0          unchanged behaviour; references Core
  tests/                              net10.0         references Core + stdio host
```

- Core multi-targets so the in-process Functions host keeps building until Phase 2.
- Linked `<Compile Include>` copies are replaced by project references.
- Unused `shared/McpConfiguration.cs`, `shared/McpUtilities.cs` and `src/Configuration/ProjectInsightConfiguration.cs` are deleted.
- OAuth bridge pieces (`AuthCodeStore`, `PkceVerifier`, Functions) stay in `src/`.

## SDK wiring

`ProjectInsightServer` (stdio host) registers the services as singletons. One process serves one user, and device activation keeps state between calls. It plugs the SDK's low-level `WithListToolsHandler` / `WithCallToolHandler` into the existing `McpServerService.InitializeTools()` / `ExecuteToolAsync()`, now public as Core's host API. A thin adapter maps `McpTool` → `Tool` and `McpToolResult` → `CallToolResult`, and passes arguments as boxed `JsonElement`s, the way the old transport did. No tool handler changed.

Four things in the adapter and `Program` keep 1.3.5's runtime behaviour, which the SDK alone would change:

- **Tool calls run one at a time** (`ToolCallGate`). The SDK dispatches requests concurrently, but the old loop was sequential and the handlers rely on that. Without the gate, parallel calls on a fresh install each start a device activation: several browser tabs open, and completion is tied to whichever request finished last.
- **A started call runs to completion.** `notifications/cancelled` only stops a call that is still queued at the gate. Once a call has started, it isn't aborted, because stopping midway could leave a multi-step write (e.g. `add_task_resource` ensuring membership, then assigning) half-applied.
- **stdin EOF answers what was already read** (`DrainingStdio`). The SDK's transport stops sending as soon as its input ends, so a one-shot `printf … | projectinsight-mcp` got no responses at all. The wrapper tracks request ids read from stdin until their response is flushed, and only then reports end of input. A cancelled request is crossed off too. Anything the SDK never answers is given up on after 5 s with no tool call running.
- **Startup fails fast.** The tool stack is resolved right after `Build()`, so bad configuration (e.g. a malformed `BaseUrl`) prints `Fatal error: …` and exits 1, as before. Otherwise every call would fail with an opaque error.

What clients see differently:

| | 1.3.5 | 2.0.0 |
|---|---|---|
| protocolVersion | fixed `2025-03-26` | negotiated (SDK) |
| capabilities | `tools`, `resources` (resources unimplemented) | `tools`, `logging` |
| serverInfo.version | hard-coded `1.1.0` | assembly version |
| `notifications/cancelled` | ignored; the call completes and is answered | a queued call is dropped and never reaches PI; a running call completes (not aborted) but, per spec, isn't answered |
| stdin EOF | answers every request already read, then exits | same (via `DrainingStdio`) |
| malformed requests | specific -32602/-32603/-32700 errors, some with exception text | `tools/call` without params: -32602 as before. Rejected by the SDK before our handler runs, so not changeable here: no `name`, or non-object `arguments`, give -32603 "An error occurred." (no exception text); no reply to unparseable JSON without a recoverable id, batch arrays or invalid id types; `jsonrpc` must be `"2.0"` |
| `params` field names | case-sensitive (`"Name"` → unknown tool) | case-insensitive (`"Name"` / `"Arguments"` bind) |
| `logging/setLevel` | method not found | supported |

## Packaging and CI

- NuGet tool targets net10.0, so `dotnet tool install` needs the .NET 10 runtime. That's a breaking install requirement, hence 2.0.0.
- The `.mcpb` bundle stays self-contained per platform. The manifest version is 2.0.0.
- Core emits no symbols in Release, matching the stdio host's "no path leakage" setting.
- `publish-nuget.yml` uses SDK `10.0.x` and watches `core/**` instead of `src/**` and `shared/**`.
- `deploy.yml` is manual-only (`workflow_dispatch`) until Phase 2. Each deploy job runs only for its own selected environment; previously a dispatch from `main` also deployed staging. Before this change, any push to `main` touching `src/`, `shared/` or workflows redeployed infra and the Function app to staging. It uses SDK `10.0.x` too, because restoring Core needs it; the app still targets net8.0.
- New `ci.yml` builds both hosts and runs the tests on every PR and every push to `main`. That replaces the build coverage the old `deploy.yml` triggers gave.

## Verification

- **Snapshot:** `tests/Snapshots/tools-list.json` was captured from 1.3.5 **before** any code moved. `ToolListSnapshotTests` pins the definitions. `SdkServerTests.Tools_list_over_the_wire_matches_snapshot` compares each tool's whole wire object, so an added field (title, annotations, outputSchema, `_meta`) fails it too.
- **`SdkServerTests`:** a real `McpClient` talks to the server through the same `AddProjectInsightServices` / `AddProjectInsightMcp` registration the host uses, over in-memory pipes. The PI client is swapped for a stub. It doesn't cover `Program.Main` itself (config files, BaseUrl check, stdio transport); the live runs below cover that. Cases:
  - handshake, `tools/list`
  - a successful call, asserting the outgoing PI request
  - a missing argument, an unknown tool
  - concurrent calls are serialised
- **`RawStdioTests`:** raw JSON-RPC lines, with stdin closed straight after, the way a stdio client or one-shot pipeline sends them. The SDK's own `McpClient` can't drive these: it adds `params` to every request, and on the newer protocol version it negotiates it never sends `notifications/cancelled`. Cases:
  - everything read before EOF is answered
  - cancelling a running call doesn't abort it
  - a cancelled queued call never runs and doesn't delay exit
  - `tools/call` without params gives -32602
  - an unanswerable request is given up on after the quiet period
  - the id/envelope reader
  Each test was checked to fail with its fix reverted.
- **Live A/B, read-only:** 1.3.5 (built from `main`) vs 2.0.0, both framework-dependent and as the self-contained osx-arm64 single-file binary. 27 calls; every response byte-identical.
- **Deep A/B: 749 calls,** replayed against 1.3.5, 2.0.0 and 1.3.5 again. They ran through a local proxy that forwards reads to live PI but blocks every write (only `…/search` POSTs pass through) and answers writes with a canned reply. Upstream responses were recorded once and replayed, so every run saw identical data. Each run used a throwaway HOME.
  - **633 read calls:** 11 projects, 21 tasks, 6 users, timesheets, PTO and rate cards, across every field preset, filter combination, date range, and wrong-type, unicode, injection-style and oversized inputs.
  - **116 write calls:** every write tool with valid, missing-argument, wrong-type and API-failure inputs, plus the auth-completion tools.
  - **Result:** responses **and** the 1,046 outgoing HTTP requests (method, path, body) were identical for 748 of 749 calls. The exception: when an object is passed where an id string belongs, both versions put the object's JSON in the URL and get the same 404. The SDK compacts that JSON's whitespace.
  - Numbers in arguments (`1.50`, `1e0`, `2.250000`, `-0`, `1e400`) reach PI identically.
- **46 raw protocol edge cases:** malformed and duplicate-key JSON, odd ids, batches, NUL characters and lone surrogates, 200 KB and deeply nested arguments, calls before `initialize`, and more. The differences are the expected ones in the table above.
- **Adversarial review** (separate agent) found eight issues. Its repros were re-run against 1.3.5, the first 2.0.0 and the patched 2.0.0, all against a local stub PI:
  - concurrency / device-activation race: fixed (one activation request, one browser open, completion uses it)
  - bad config: fails fast again
  - stdin EOF: all requests answered (was 0 of 4)
  - deploy conditions and missing PR CI: fixed
  - design-note claims and stale .NET 8 docs: fixed
  - half-staged index: staged
  - malformed calls: `tools/call` without params restored to -32602; the rest are SDK-side (table above)
  - cancellation: running calls are no longer aborted
  After the patches, the 749-call A/B is unchanged (748/749 identical), and the only change in the 46 protocol cases is `tools/call` without params going back to -32602.
- **Not exercised:** `link_pi_account` live (it opens a browser and starts real SSO). `get_resource_types` was tested with invalid ids only, because no resource-type id could be discovered through the API.

## Out of scope

The Functions host migration and its auth problems, plus the in-process end of support on 2026-11-10 (#4). MCP Apps (#3). Attribute-style tools (#5).
