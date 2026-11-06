# Phase 2 Remote Host Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the in-process Azure Functions host with an ASP.NET Core app on .NET 10. It serves the same tools and card as the stdio server, signs users in through a pass-through OAuth front over PI device activation, and deploys from CI with GitHub OIDC.

**Architecture:**
- **`server/` (new):** holds the MCP SDK wiring shared by both hosts.
- **`src-web/` (new):** the ASP.NET Core host.
  - The SDK's stateless HTTP transport serves `/mcp`, behind a PI bearer authentication handler.
  - OAuth endpoints sit alongside: metadata, register, authorize, token.
  - Per-user state lives in Table storage (sign-in records) and Blob storage (drafts).
- **`bicep/`:** describes the Azure resources.
- **`deploy.yml`:** an unprivileged build job, then an OIDC deploy job with no build step.

**Tech Stack:** .NET 10, ModelContextProtocol 2.3.0 (+ AspNetCore, Extensions.Apps), Azure.Data.Tables 12.13.0, Azure.Storage.Blobs 12.30.1, Azure.Identity 1.21.0, Azure.Monitor.OpenTelemetry.AspNetCore 1.6.0, xUnit 2.9.3, Microsoft.AspNetCore.Mvc.Testing 10.0.14, Xunit.SkippableFact 1.5.23, Bicep, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-30-phase2-remote-host-design.md`. Read it before any task. It is the authority; this plan argues from it.

## Global Constraints

- **The repo is public.**
  - Never write a real PI tenant URL, Azure tenant, subscription or client id, app name, or person's email into any file.
  - Tests use `https://pi.test/api/` for PI and `https://mcp.test` for the server.
  - Docs use placeholders: `your-tenant.projectinsight.net`, `<app>`.
- **Commits:** conventional messages (`feat(web): …`, `refactor(server): …`, `test(web): …`, `ci: …`, `docs: …`). **No `Co-Authored-By` trailer.** Don't push or open PRs without asking the owner.
- **Don't read `~/.projectinsight/credentials.json`.** No test calls a live PI or Azure service.
- **Versions:**
  - target framework `net10.0`;
  - `ModelContextProtocol*` `2.3.0`;
  - `Microsoft.Extensions.*` `10.0.14`.
- **Build and test:**
  - Every project builds with **0 warnings** in Release.
  - Tests run with `dotnet test servers/project-insight/tests -c Release`, from the repo root.
- **Style:** match the surrounding code. Short "why" comments only, xUnit `[Fact]` and `[Theory]`, sentence-style test names (`Rejected_token_gets_401_…`).
- **Configuration:**
  - The web host never reads configuration before `builder.Build()`. Use `IOptions<T>` and factory lambdas; `WebApplicationFactory` overrides only reach values read after `Build()`.
  - Bearer tokens, authorization codes and activation codes never appear in a response body, log line or telemetry item, except the access token in a `/token` success body.
- **Pass-through:** the access token *is* the PI token. Lifetime: `Auth:PiTokenLifetimeDays`, default `90`, allowed `1..90`.
- **Default allowlists:**
  - Redirect URIs: `https://claude.ai/api/mcp/auth_callback` and `https://chatgpt.com/connector_platform_oauth_redirect`.
  - Loopback: `http` on `127.0.0.1`, `localhost` or `[::1]`, any port, path `/callback`.
  - CIMD hosts: `claude.ai` and `chatgpt.com`.
- **Hidden web tools:** `complete_authentication`, `link_pi_account`, `complete_pi_link`.

## Review Focus

- **PI user ids differ only in case** from the `Auth:AllowedPiUserIds` entry (GUIDs come back upper- or lower-case). The user must still be allowed. Pinned in Task B3.
- **A client sends `resource` with a trailing slash or upper-case host** (`https://MCP.test/mcp/`). It must be accepted at `/authorize` and `/token`. Pinned in Task B8.
- **PI returns the token expiry without a UTC marker** (`DateTimeKind.Unspecified`). `expires_in` must still be the true seconds to expiry, not off by the server's offset. Pinned in Task B8.
- **A user double-clicks Continue,** so two `/authorize/complete` posts race. Exactly one redirect with a code; the other gets a plain "already finished" page, not a 500. Pinned in Task B7.
- **A client opens `GET /mcp`** (legacy SSE probe) on the stateless endpoint. It must get 405, not a 500 or a hang. Pinned in Task B4.

## Delivery

| Part | Branch | Pull request |
| - | - | - |
| A: Tasks A1–A2 | `phase2-remote-host` (already holds the spec and this plan) | PR 1 "Phase 2a: shared MCP wiring" |
| B: Tasks B1–B11 | `phase2-web-host`, from `main` after PR 1 merges | PR 2 "Phase 2b: web host" |
| C: Tasks C1–C3 | `phase2-deploy`, from `main` after PR 2 merges | PR 3 "Phase 2c: infrastructure and deploy" |
| C4 | none | done by the owner, after PR 3 merges |

**Rulings against the spec:**

| Ruling | Why | Cost if wrong |
| - | - | - |
| Core's `net8.0` target is dropped in PR 2 (Task B11), not PR 1 | the Functions host in `src/` still targets net8.0 until PR 2 deletes it | none; one PR later |
| The web host uses its own `PiIdentityClient` and `PiActivationClient`, not Core's `GetCurrentUserAsync` and `DeviceActivationService` | the spec's intent is a typed status result and no shared device-activation state. Core's `GetCurrentUserAsync` folds every failure into `Success=false`, and `DeviceActivationService` carries pending state and writes credentials to disk | a little duplicated HTTP code |
| The activation code stays in the `complete-request` query string, which is the only form PI's endpoint takes. Telemetry strips query strings from outbound URLs instead (Task B9) | PI defines the endpoint | none if the telemetry processor is tested, which it is |
| `AllowedHosts` is `<app host>;localhost` | App Service health probes may use `localhost` | none |

---

## Part A: Shared MCP wiring (PR 1)

### Task A1: Move the SDK wiring into `ProjectInsightMCP.Server`

**Files:**
- Create: `servers/project-insight/server/ProjectInsightMCP.Server.csproj`
- Create: `servers/project-insight/server/ProjectInsightServer.cs` (SDK parts of `src-stdio/ProjectInsightServer.cs`, plus the options and the keyed gate)
- Move: `servers/project-insight/src-stdio/TimeChangeTools.cs` → `servers/project-insight/server/TimeChangeTools.cs`
- Create: `servers/project-insight/src-stdio/StdioHost.cs` (stdio-only parts of `src-stdio/ProjectInsightServer.cs`)
- Delete: `servers/project-insight/src-stdio/ProjectInsightServer.cs`
- Modify: `servers/project-insight/src-stdio/Program.cs`, `servers/project-insight/src-stdio/ProjectInsightMCP.Stdio.csproj`, `servers/project-insight/tests/ProjectInsightMCP.Tests.csproj`
- Modify (usings only): `tests/SdkServerTests.cs`, `tests/RawStdioTests.cs`, `tests/StartupPruneTests.cs`, `tests/TimeChangeToolsTests.cs`, and any other test that fails to compile
- Create: `servers/project-insight/tests/ToolCallGateTests.cs`, `servers/project-insight/tests/HiddenToolsTests.cs`

**Interfaces:**
- Produces, in namespace `ProjectInsightMCP.Server`:
  - `public static class ProjectInsightServer`:
    - `const string ServerName`
    - `IMcpServerBuilder AddProjectInsightMcp(this IServiceCollection services, ProjectInsightMcpOptions? options = null)`
    - `string VersionOf(Assembly assembly)`
    - `internal static Tool ToSdkTool(McpTool)`
    - `internal static CallToolResult ToSdkResult(McpToolResult)`
    - `internal static Dictionary<string, object> ToLegacyArguments(IDictionary<string, JsonElement>?)`
  - `public sealed class ProjectInsightMcpOptions { string? ServerVersion { get; init; } Func<string, bool> ExposeTool { get; init; } }`
  - `public interface IToolCallKey { string Key { get; } }`
  - `internal sealed class ToolCallGate { bool IsBusy; Task<T> RunAsync<T>(string key, Func<Task<T>> call, CancellationToken whileQueued); }`
  - `TimeChangeTools`: unchanged API, new namespace.
- Produces, in namespace `ProjectInsightMCP.Stdio`: `public static class StdioHost` with `AddProjectInsightServices`, `PruneDraftsAtStartupAsync` and `WithDrainingStreamServerTransport`, all unchanged signatures.

- [ ] **Step 1: Write the failing tests**

`servers/project-insight/tests/ToolCallGateTests.cs`:

```csharp
using ProjectInsightMCP.Server;
using Xunit;

namespace ProjectInsightMCP.Tests;

public class ToolCallGateTests
{
    [Fact]
    public async Task Calls_with_the_same_key_run_one_at_a_time()
    {
        var gate = new ToolCallGate();
        var running = 0;
        var maxRunning = 0;

        async Task<int> Call()
        {
            var now = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, now);
            await Task.Delay(50);
            Interlocked.Decrement(ref running);
            return 0;
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => gate.RunAsync("user-a", Call, CancellationToken.None)));
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task Calls_with_different_keys_run_at_the_same_time()
    {
        var gate = new ToolCallGate();
        var bothStarted = new TaskCompletionSource();
        var started = 0;

        async Task<int> Call()
        {
            if (Interlocked.Increment(ref started) == 2)
                bothStarted.SetResult();
            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return 0;
        }

        await Task.WhenAll(gate.RunAsync("user-a", Call, CancellationToken.None),
                           gate.RunAsync("user-b", Call, CancellationToken.None));
        Assert.True(bothStarted.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public void Version_drops_the_source_revision()
        => Assert.Equal("2.1.0", ProjectInsightServer.VersionOf("2.1.0+abc123"));
}
```

`VersionOf` gets a string overload for this test: `internal static string VersionOf(string informationalVersion)`. The `Assembly` overload calls it.

`servers/project-insight/tests/HiddenToolsTests.cs`, modelled on `SdkServerTests` (same in-memory pipes):

```csharp
using System.IO.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ProjectInsightMCP.Server;
using ProjectInsightMCP.Stdio;
using Xunit;

namespace ProjectInsightMCP.Tests;

/// <summary>A host can hide tools: hidden tools leave tools/list and calls to them are refused.</summary>
public sealed class HiddenToolsTests : IAsyncLifetime
{
    private static readonly string[] Hidden = ["link_pi_account", "get_time_changes"];
    private IHost _host = null!;
    private McpClient _client = null!;

    public async Task InitializeAsync()
    {
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ProjectInsight:BaseUrl"] = "https://pi.test",
            ["ProjectInsight:ApiToken"] = "test-token",
            ["ProjectInsight:DraftsPath"] = Path.Combine(Path.GetTempPath(), "pi-hidden-" + Guid.NewGuid().ToString("N"))
        });
        builder.Services.AddLogging();
        builder.Services.AddProjectInsightServices(builder.Configuration);
        builder.Services.AddProjectInsightMcp(new ProjectInsightMcpOptions { ExposeTool = name => !Hidden.Contains(name) })
            .WithDrainingStreamServerTransport(() => clientToServer.Reader.AsStream(), () => serverToClient.Writer.AsStream());
        _host = builder.Build();
        await _host.StartAsync();
        _client = await McpClient.CreateAsync(new StreamClientTransport(
            clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance));
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task Hidden_tools_are_not_listed()
    {
        var names = (await _client.ListToolsAsync()).Select(t => t.Name).ToList();
        Assert.DoesNotContain("link_pi_account", names);   // a Core tool
        Assert.DoesNotContain("get_time_changes", names);  // an SDK-native tool
        Assert.Contains("search_projects", names);
    }

    [Theory]
    [InlineData("link_pi_account")]
    [InlineData("get_time_changes")]
    public async Task Hidden_tools_cannot_be_called(string name)
        => await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await _client.CallToolAsync(name, new Dictionary<string, object?> { ["id"] = "d_x" }));
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~ToolCallGateTests|FullyQualifiedName~HiddenToolsTests"`
Expected: build failure. `ProjectInsightMCP.Server` doesn't exist.

- [ ] **Step 3: Create the project and move the code**

`servers/project-insight/server/ProjectInsightMCP.Server.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    MCP SDK wiring shared by the stdio and web hosts: tool and resource registration, and the
    adapters between Core's tool model and the SDK's. Core itself stays free of the SDK.
  -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>ProjectInsightMCP.Server</RootNamespace>
    <AssemblyName>ProjectInsightMCP.Server</AssemblyName>
    <IsPackable>false</IsPackable>
    <!-- ModelContextProtocol.Extensions.Apps is marked experimental (MCPEXP003). -->
    <NoWarn>$(NoWarn);MCPEXP003</NoWarn>
    <DebugType Condition="'$(Configuration)' == 'Release'">none</DebugType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol" Version="2.3.0" />
    <PackageReference Include="ModelContextProtocol.Extensions.Apps" Version="2.3.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../core/ProjectInsightMCP.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="projectinsight-mcp" />
    <InternalsVisibleTo Include="ProjectInsightMCP.Web" />
    <InternalsVisibleTo Include="ProjectInsightMCP.Tests" />
  </ItemGroup>

</Project>
```

Run `git mv servers/project-insight/src-stdio/TimeChangeTools.cs servers/project-insight/server/TimeChangeTools.cs`, then change its namespace line to `namespace ProjectInsightMCP.Server;`. Its references to `ProjectInsightServer.ToSdkResult` keep working, because the class moves to the same namespace.

`servers/project-insight/server/ProjectInsightServer.cs`:
- Take the whole current `src-stdio/ProjectInsightServer.cs`.
- Change the namespace to `ProjectInsightMCP.Server`.
- **Remove** `AddProjectInsightServices`, `PruneDraftsAtStartupAsync` and `WithDrainingStreamServerTransport`; they go to `StdioHost`.
- Make these changes:

```csharp
/// <summary>Which tools a host exposes, and the version it reports.</summary>
public sealed class ProjectInsightMcpOptions
{
    /// <summary>Reported in serverInfo. Hosts pass their own assembly's version.</summary>
    public string? ServerVersion { get; init; }

    /// <summary>False hides a tool from tools/list and refuses calls to it.</summary>
    public Func<string, bool> ExposeTool { get; init; } = _ => true;
}

/// <summary>Which <see cref="ToolCallGate"/> queue a tool call joins: one queue per user.</summary>
public interface IToolCallKey
{
    string Key { get; }
}

internal sealed class SingleUserToolCallKey : IToolCallKey
{
    public static readonly SingleUserToolCallKey Instance = new();
    public string Key => "";
}
```

```csharp
    public static IMcpServerBuilder AddProjectInsightMcp(this IServiceCollection services, ProjectInsightMcpOptions? options = null)
    {
        options ??= new ProjectInsightMcpOptions();
        services.TryAddSingleton<ToolCallGate>();
        // One queue for the whole process unless the host registers a per-user key first.
        services.TryAddSingleton<IToolCallKey>(SingleUserToolCallKey.Instance);

        return services
            .AddMcpServer(o =>
            {
                o.ServerInfo = new Implementation
                {
                    Name = ServerName,
                    Version = options.ServerVersion ?? VersionOf(typeof(ProjectInsightServer).Assembly),
                    Description = ServerDescription
                };
            })
            .WithListToolsHandler((ctx, ct) =>
            {
                var tools = McpServerService.InitializeTools()
                    .Where(t => options.ExposeTool(t.Name))
                    .Select(ToSdkTool)
                    .ToList();
                return ValueTask.FromResult(new ListToolsResult { Tools = tools });
            })
            .WithCallToolHandler(async (ctx, ct) =>
            {
                // (body unchanged)
            })
            .WithTools(TimeChangeTools.Create().Where(t => options.ExposeTool(t.ProtocolTool.Name)))
            .WithResources([TimeChangeTools.CreateCard()])
            // Every tool call, adapter or SDK-native, goes through the gate, in its caller's queue.
            // (keep the existing comment about cancellation and half-applied writes)
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => (context, cancellationToken) =>
            {
                var name = context.Params?.Name ?? "";
                if (!options.ExposeTool(name))
                    throw new McpProtocolException($"Unknown tool: '{name}'", McpErrorCode.InvalidParams);
                var services = context.Services!;
                return new ValueTask<CallToolResult>(services.GetRequiredService<ToolCallGate>().RunAsync(
                    services.GetRequiredService<IToolCallKey>().Key,
                    () => next(context, CancellationToken.None).AsTask(), cancellationToken));
            }));
    }

    public static string VersionOf(Assembly assembly)
        => VersionOf(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    internal static string VersionOf(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        return plus >= 0 ? informationalVersion[..plus] : informationalVersion;
    }
```

Move `ToolCallGate` out of the static class to a top-level `internal sealed class ToolCallGate` in the same file, keyed:

```csharp
/// <summary>
/// The SDK dispatches requests concurrently; the handlers depend on one call at a time per user
/// (e.g. device activation's check-then-start of a pending sign-in). One queue per key.
/// </summary>
internal sealed class ToolCallGate
{
    // Bounded by the number of users allowed on a server; entries are never removed.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _queues = new();
    private int _active;

    /// <summary>True while any tool call, in any queue, is queued or running.</summary>
    public bool IsBusy => Volatile.Read(ref _active) > 0;

    public async Task<T> RunAsync<T>(string key, Func<Task<T>> call, CancellationToken whileQueued)
    {
        var queue = _queues.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        Interlocked.Increment(ref _active);
        try
        {
            await queue.WaitAsync(whileQueued);
            try
            {
                return await call();
            }
            finally
            {
                queue.Release();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }
}
```

Delete the old `ServerVersion()` private method.

`servers/project-insight/src-stdio/StdioHost.cs`: `namespace ProjectInsightMCP.Stdio; public static class StdioHost`. It contains the three moved methods with their bodies and doc comments unchanged, except:
- `AddProjectInsightServices` no longer registers `ToolCallGate`, because `AddProjectInsightMcp` does;
- `WithDrainingStreamServerTransport` needs `using ProjectInsightMCP.Server;` for `ToolCallGate`.

Delete `src-stdio/ProjectInsightServer.cs`.

`src-stdio/Program.cs`: add `using ProjectInsightMCP.Server;`. Replace the registration line with:

```csharp
        builder.Services.AddProjectInsightMcp(new ProjectInsightMcpOptions
        {
            ServerVersion = ProjectInsightServer.VersionOf(typeof(Program).Assembly)
        }).WithDrainingStreamServerTransport(
            Console.OpenStandardInput, () => new BufferedStream(Console.OpenStandardOutput()));
```

Change `ProjectInsightServer.PruneDraftsAtStartupAsync` to `StdioHost.PruneDraftsAtStartupAsync`.

`src-stdio/ProjectInsightMCP.Stdio.csproj`:
- remove the two `ModelContextProtocol*` package references (they now come through `server`);
- replace the Core project reference with `<ProjectReference Include="../server/ProjectInsightMCP.Server.csproj" />`.

`tests/ProjectInsightMCP.Tests.csproj`: add `<ProjectReference Include="../server/ProjectInsightMCP.Server.csproj" />`.

Tests:
- Add `using ProjectInsightMCP.Server;` wherever `ProjectInsightServer` or `TimeChangeTools` is used.
- In `StartupPruneTests`, change `ProjectInsightServer.PruneDraftsAtStartupAsync` to `StdioHost.PruneDraftsAtStartupAsync`.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet build servers/project-insight/src-stdio -c Release && dotnet test servers/project-insight/tests -c Release`
Expected: 0 warnings, and all tests pass: the 237 existing ones plus the 6 new ones (3 gate cases, 1 listing test, 2 hidden-call cases).
- `Tool_definitions_match_snapshot` and `Tools_list_over_the_wire_matches_snapshot` must pass **without** regenerating the snapshots.
- If either snapshot test fails, the move changed behaviour: fix the code, never the snapshot.

- [ ] **Step 5: Check the packed tool still contains everything**

Run: `dotnet pack servers/project-insight/src-stdio -c Release -o /tmp/pi-pack && unzip -l /tmp/pi-pack/ProjectInsight.MCP.2.1.0.nupkg | grep -E "ProjectInsightMCP\.(Core|Server)\.dll"`
Expected: both DLLs are listed under `tools/net10.0/any/`.

- [ ] **Step 6: Commit**

```bash
git add -A servers/project-insight/server servers/project-insight/src-stdio servers/project-insight/tests
git commit -m "refactor(server): move the MCP SDK wiring into a shared project used by the stdio host"
```

### Task A2: Keep Dependabot and CI aware of the new project

**Files:**
- Modify: `.github/dependabot.yml` (first nuget block)
- Modify: `.github/workflows/ci.yml` (header comment only)

- [ ] **Step 1: Edit Dependabot**

In the first `nuget` block, add `- "/servers/project-insight/server"` to `directories`, and change its comment to `# Local stdio server (.NET 10), the shared MCP wiring, and the tests.`

- [ ] **Step 2: Edit the CI comment**

In `ci.yml`, change the header comment to `# Builds the hosts (the shared server project builds with them) and runs the tests. Deployment is separate (deploy.yml, manual-only).` No step changes: `server` builds as a dependency of `src-stdio`.

- [ ] **Step 3: Validate and commit**

Run: `python3 -c "import yaml,sys; [yaml.safe_load(open(f)) for f in sys.argv[1:]]" .github/dependabot.yml .github/workflows/ci.yml`
Expected: no output.

```bash
git add .github/dependabot.yml .github/workflows/ci.yml
git commit -m "ci: include the shared server project in Dependabot"
```

- [ ] **Step 4: Ask the owner, then open PR 1**

With the owner's go-ahead, push `phase2-remote-host` and open PR 1, "Phase 2a: shared MCP wiring". The body says:
- no behaviour change for the published tool;
- it contains the Phase 2 spec and plan;
- it ends with the attribution line from the session.

Wait for CI, gitleaks and CodeRabbit, address their comments, and let the owner merge.

---
## Part B: Web host (PR 2)

Start from `main` after PR 1 merges: `git switch -c phase2-web-host origin/main`.

All web code lives in `servers/project-insight/src-web/` under namespace `ProjectInsightMCP.Web`. All web tests live in `servers/project-insight/tests/Web/` under namespace `ProjectInsightMCP.Tests.Web`.

### Task B1: Web host skeleton, options, startup checks, health, test fixture

**Files:**
- Create: `src-web/ProjectInsightMCP.Web.csproj`, `src-web/Program.cs`, `src-web/WebHost.cs`, `src-web/Options.cs`, `src-web/StartupChecks.cs`, `src-web/TokenHash.cs`, `src-web/appsettings.json`
- Modify: `tests/ProjectInsightMCP.Tests.csproj`
- Create: `tests/Web/WebFactory.cs`, `tests/Web/PiStub.cs`, `tests/Web/HostTests.cs`

**Interfaces:**
- Produces:
  - `WebHost.AddProjectInsightWeb(this WebApplicationBuilder)` and `WebHost.UseProjectInsightWeb(this WebApplication)`. Later tasks add registrations and endpoints inside these two methods.
  - `WebOptions` (section `Web`): `string PublicBaseUrl`, `string Issuer`, `string Resource`, `string ResourceMetadataUrl`, `bool IsResource(string? candidate)`.
  - `AuthOptions` (section `Auth`): `string AllowedPiUserIds`, `int PiTokenLifetimeDays`, `string[]? AllowedRedirectUris`, `string[]? AllowedCimdHosts`, `string[]? AllowedOrigins`. Derived: `IReadOnlySet<string> PiUserIds` (case-insensitive), `IReadOnlyList<string> RedirectUris`, `IReadOnlySet<string> CimdHosts`, `IReadOnlySet<string> Origins`.
  - `TokenHash.Of(string) → string`: lower-case hex SHA-256.
  - Test fixture: `WebFactory : WebApplicationFactory<Program>`, with `Settings`, `Pi` (a `PiStub`) and `ExtraServices`. `PiStub` has constants `TokenA`, `TokenB`, `UserA`, `UserB`, `RejectedToken`, `BrokenToken`, `ActivationCode`.

- [ ] **Step 1: Test project references**

In `tests/ProjectInsightMCP.Tests.csproj`, add:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.14" />
    <PackageReference Include="Xunit.SkippableFact" Version="1.5.23" />
```

```xml
    <ProjectReference Include="../src-web/ProjectInsightMCP.Web.csproj" />
```

- [ ] **Step 2: Write the test fixture and the failing tests**

`tests/Web/PiStub.cs`: a fake Project Insight for the web host. It records each request together with its `Authorization` header.

```csharp
using System.Net;
using System.Text;
using System.Text.Json;

namespace ProjectInsightMCP.Tests.Web;

public sealed record PiRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body);

/// <summary>
/// A fake Project Insight. Tokens map to users; a few magic tokens and paths simulate failures.
/// Device activation succeeds for <see cref="ActivationCode"/> and fails for anything else.
/// </summary>
public sealed class PiStub : HttpMessageHandler
{
    public const string TokenA = "pi-token-for-user-a";
    public const string TokenB = "pi-token-for-user-b";
    public const string UserA = "0f0e2a4c-1111-4a4a-9b9b-aaaaaaaaaaaa";
    public const string UserB = "0f0e2a4c-2222-4a4a-9b9b-bbbbbbbbbbbb";
    public const string StrangerToken = "pi-token-for-a-stranger";
    public const string RejectedToken = "pi-token-pi-rejects";
    public const string BrokenToken = "pi-token-pi-is-down";
    public const string ActivationCode = "GOOD-CODE";
    public const string ActivationId = "act-1";

    private int _toolCallsInFlight;
    private int _maxToolCallsInFlight;

    public List<PiRequest> Requests { get; } = new();
    public TimeSpan ToolCallDelay { get; set; } = TimeSpan.Zero;
    public int MaxToolCallsInFlight => _maxToolCallsInFlight;
    /// <summary>Activation completions return this token and user.</summary>
    public (string Token, string UserId, string Expiry) ActivationResult { get; set; } = (TokenA, UserA, "2027-01-28T12:00:00");
    /// <summary>Extra canned responses by exact absolute URI (e.g. a client metadata document).</summary>
    public Dictionary<string, Func<HttpResponseMessage>> Fixed { get; } = new();

    public IEnumerable<PiRequest> ToPath(string path) { lock (Requests) return Requests.Where(r => r.Uri.AbsolutePath == path).ToList(); }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var auth = request.Headers.Authorization?.ToString();
        lock (Requests) Requests.Add(new PiRequest(request.Method, request.RequestUri!, auth, body));

        if (Fixed.TryGetValue(request.RequestUri!.AbsoluteUri, out var fixedResponse))
            return fixedResponse();

        var token = request.Headers.Authorization?.Parameter;
        var path = request.RequestUri.AbsolutePath;

        if (token == BrokenToken)
            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        if (path == "/api/user/me")
            return token switch
            {
                TokenA => Json(new { id = UserA, name = "User A" }),
                TokenB => Json(new { id = UserB.ToUpperInvariant(), name = "User B" }), // PI may change GUID case
                StrangerToken => Json(new { id = "0f0e2a4c-9999-4a4a-9b9b-cccccccccccc", name = "Stranger" }),
                _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            };
        if (path == "/api/device-activation/create-request")
            return Json(new { DeviceActivationId = ActivationId, DeviceActivationRequestUrl = "https://pi.test/device/activate/" + ActivationId });
        if (path == "/api/device-activation/complete-request")
            return request.RequestUri.Query.Contains("activationCode=" + ActivationCode)
                ? Json(new { ApiToken = ActivationResult.Token, UserId = ActivationResult.UserId, ApiTokenExpirationDateTimeUTC = ActivationResult.Expiry })
                : new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Invalid activation code") };
        if (path.Contains("echo"))
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($"bad token {token} rejected") };

        var now = Interlocked.Increment(ref _toolCallsInFlight);
        int seen;
        while (now > (seen = _maxToolCallsInFlight))
            Interlocked.CompareExchange(ref _maxToolCallsInFlight, now, seen);
        try
        {
            if (ToolCallDelay > TimeSpan.Zero)
                await Task.Delay(ToolCallDelay, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _toolCallsInFlight);
        }
        return Json(new { success = true });
    }

    public static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
}
```

`tests/Web/WebFactory.cs`:

```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>The web host with in-memory stores, a temp drafts folder and every outbound call sent to <see cref="Pi"/>.</summary>
public sealed class WebFactory : WebApplicationFactory<Program>
{
    public static readonly Uri BaseAddress = new("https://mcp.test/");
    private readonly string _drafts = Path.Combine(Path.GetTempPath(), "pi-web-drafts-" + Guid.NewGuid().ToString("N"));

    public PiStub Pi { get; } = new();

    public Dictionary<string, string?> Settings { get; } = new()
    {
        ["Web:PublicBaseUrl"] = "https://mcp.test",
        ["ProjectInsight:BaseUrl"] = "https://pi.test/api/",
        ["Auth:AllowedPiUserIds"] = $"{PiStub.UserA},{PiStub.UserB}",
        ["Storage:Mode"] = "Local",
        ["AllowedHosts"] = "*"
    };

    public Action<IServiceCollection>? ExtraServices { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Settings["ProjectInsight:DraftsPath"] = _drafts;
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
        builder.ConfigureTestServices(services =>
        {
            // Every HttpClient the app creates talks to the stub, never the network.
            services.ConfigureAll<HttpClientFactoryOptions>(o =>
                o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = Pi));
            ExtraServices?.Invoke(services);
        });
    }

    public HttpClient Client() => CreateDefaultClient(BaseAddress);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (Directory.Exists(_drafts))
            Directory.Delete(_drafts, recursive: true);
    }
}
```

`tests/Web/HostTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.AspNetCore;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class HostTests
{
    [Fact]
    public async Task Health_reports_status_and_version_only()
    {
        using var factory = new WebFactory();
        var json = await factory.Client().GetFromJsonAsync<JsonElement>("/healthz");

        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("version").GetString()));
        Assert.Equal(2, json.EnumerateObject().Count());
    }

    [Theory]
    [InlineData("ProjectInsight:ApiToken", "static-token", "ProjectInsight:ApiToken")]
    [InlineData("Web:PublicBaseUrl", "http://mcp.test", "Web:PublicBaseUrl")]
    [InlineData("Web:PublicBaseUrl", "https://mcp.test/sub", "Web:PublicBaseUrl")]
    [InlineData("ProjectInsight:BaseUrl", "", "ProjectInsight:BaseUrl")]
    [InlineData("Auth:AllowedPiUserIds", "", "Auth:AllowedPiUserIds")]
    [InlineData("Auth:PiTokenLifetimeDays", "91", "Auth:PiTokenLifetimeDays")]
    public void Startup_refuses_unsafe_configuration(string key, string value, string named)
    {
        using var factory = new WebFactory();
        factory.Settings[key] = value;

        var ex = Record.Exception(() => factory.Client());

        Assert.NotNull(ex);
        Assert.Contains(named, ex!.ToString());
    }

    [Fact]
    public void Startup_refuses_a_stateful_mcp_endpoint()
    {
        using var factory = new WebFactory
        {
            ExtraServices = s => s.Configure<HttpServerTransportOptions>(o => o.Stateless = false)
        };

        var ex = Record.Exception(() => factory.Client());

        Assert.Contains("stateless", ex?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
```

`Startup_refuses_a_stateful_mcp_endpoint` compiles once B3 registers the MCP server. **Add it in B3.** In B1, write only the first two tests.

- [ ] **Step 3: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web.HostTests"`
Expected: build failure (`Program` not found).

- [ ] **Step 4: Implement**

`src-web/ProjectInsightMCP.Web.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <!-- Remote MCP server: the shared tools over Streamable HTTP, behind a pass-through OAuth front for PI. -->
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <RootNamespace>ProjectInsightMCP.Web</RootNamespace>
    <AssemblyName>ProjectInsightMCP.Web</AssemblyName>
    <IsPackable>false</IsPackable>
    <NoWarn>$(NoWarn);MCPEXP003</NoWarn>
    <DebugType Condition="'$(Configuration)' == 'Release'">none</DebugType>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="2.3.0" />
    <PackageReference Include="Azure.Data.Tables" Version="12.13.0" />
    <PackageReference Include="Azure.Storage.Blobs" Version="12.30.1" />
    <PackageReference Include="Azure.Identity" Version="1.21.0" />
    <PackageReference Include="Azure.Monitor.OpenTelemetry.AspNetCore" Version="1.6.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../server/ProjectInsightMCP.Server.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="ProjectInsightMCP.Tests" />
  </ItemGroup>

</Project>
```

If `Azure.Storage.Blobs 12.30.1` or `Azure.Monitor.OpenTelemetry.AspNetCore 1.6.0` raises NU1605 against `Azure.Core` or `Microsoft.Extensions.*` 10.0.14, use the newest version that restores cleanly and record it in the commit message.

`src-web/Program.cs`:

```csharp
using ProjectInsightMCP.Web;

var builder = WebApplication.CreateBuilder(args);
builder.AddProjectInsightWeb();

var app = builder.Build();
app.UseProjectInsightWeb();
app.Run();

/// <summary>Entry point; partial so tests can host it with WebApplicationFactory.</summary>
public partial class Program;
```

`src-web/Options.cs`:

```csharp
namespace ProjectInsightMCP.Web;

/// <summary>Section "Web". The public base URL is configuration, never the request's Host header.</summary>
public sealed class WebOptions
{
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>The OAuth issuer: the base URL without a trailing slash, byte-identical everywhere.</summary>
    public string Issuer => PublicBaseUrl.TrimEnd('/');
    public string Resource => Issuer + "/mcp";
    public string ResourceMetadataUrl => Issuer + "/.well-known/oauth-protected-resource/mcp";

    /// <summary>
    /// Whether a client's resource parameter names this server. Scheme and host compare
    /// case-insensitively and a trailing slash is ignored, as the MCP spec asks.
    /// </summary>
    public bool IsResource(string? candidate)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var given) || !Uri.TryCreate(Resource, UriKind.Absolute, out var ours))
            return false;
        return string.Equals(given.Scheme, ours.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(given.Host, ours.Host, StringComparison.OrdinalIgnoreCase)
            && given.Port == ours.Port
            && given.AbsolutePath.TrimEnd('/') == ours.AbsolutePath
            && given.Query.Length == 0 && given.Fragment.Length == 0;
    }
}

/// <summary>Section "Auth".</summary>
public sealed class AuthOptions
{
    public static readonly string[] DefaultRedirectUris =
        ["https://claude.ai/api/mcp/auth_callback", "https://chatgpt.com/connector_platform_oauth_redirect"];
    public static readonly string[] DefaultCimdHosts = ["claude.ai", "chatgpt.com"];

    /// <summary>Comma-separated PI user ids allowed to use the server.</summary>
    public string AllowedPiUserIds { get; set; } = "";
    public int PiTokenLifetimeDays { get; set; } = 90;
    // Nullable so configuration replaces the defaults instead of appending to them.
    public string[]? AllowedRedirectUris { get; set; }
    public string[]? AllowedCimdHosts { get; set; }
    public string[]? AllowedOrigins { get; set; }

    public IReadOnlySet<string> PiUserIds => AllowedPiUserIds
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<string> RedirectUris => AllowedRedirectUris ?? DefaultRedirectUris;
    public IReadOnlySet<string> CimdHosts => (AllowedCimdHosts ?? DefaultCimdHosts).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> Origins => (AllowedOrigins ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
```

`src-web/TokenHash.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.Web;

/// <summary>Keys for caches and stores that must never hold a token or code itself.</summary>
public static class TokenHash
{
    public static string Of(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
```

`src-web/StartupChecks.cs`:

```csharp
namespace ProjectInsightMCP.Web;

/// <summary>Refuses to start with configuration that would be unsafe on a shared server.</summary>
public static class StartupChecks
{
    public static void Validate(IConfiguration config, WebOptions web, AuthOptions auth)
    {
        if (!string.IsNullOrEmpty(config["ProjectInsight:ApiToken"]))
            throw new InvalidOperationException("ProjectInsight:ApiToken must not be set on the web host: it would override every user's own token.");
        if (!IsHttpsOrigin(config["ProjectInsight:BaseUrl"], allowPath: true))
            throw new InvalidOperationException("ProjectInsight:BaseUrl must be an absolute https URL.");
        if (!IsHttpsOrigin(web.PublicBaseUrl, allowPath: false))
            throw new InvalidOperationException("Web:PublicBaseUrl must be an https origin with no path, such as https://host.");
        if (auth.PiTokenLifetimeDays is < 1 or > 90)
            throw new InvalidOperationException("Auth:PiTokenLifetimeDays must be between 1 and 90.");
        if (auth.PiUserIds.Count == 0)
            throw new InvalidOperationException("Auth:AllowedPiUserIds must list at least one PI user id.");
    }

    private static bool IsHttpsOrigin(string? value, bool allowPath)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && uri.Scheme == Uri.UriSchemeHttps
           && uri.Query.Length == 0 && uri.Fragment.Length == 0 && uri.UserInfo.Length == 0
           && (allowPath || uri.AbsolutePath == "/");
}
```

`src-web/WebHost.cs`:

```csharp
using System.Reflection;
using Microsoft.Extensions.Options;

namespace ProjectInsightMCP.Web;

public static class WebHost
{
    public static void AddProjectInsightWeb(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        services.AddOptions<WebOptions>().BindConfiguration("Web");
        services.AddOptions<AuthOptions>().BindConfiguration("Auth");
        services.AddSingleton(TimeProvider.System);
    }

    public static void UseProjectInsightWeb(this WebApplication app)
    {
        // Configuration is complete only after Build(), so validation runs here.
        StartupChecks.Validate(app.Configuration,
            app.Services.GetRequiredService<IOptions<WebOptions>>().Value,
            app.Services.GetRequiredService<IOptions<AuthOptions>>().Value);

        app.MapGet("/healthz", () => Results.Json(new { status = "ok", version = Version }));
    }

    // The deploy sets InformationalVersion to the commit SHA; reported as-is so the smoke test can match it.
    private static readonly string Version =
        typeof(WebHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
}
```

`src-web/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "ModelContextProtocol": "Warning",
      "System.Net.Http.HttpClient": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web.HostTests"`
Expected: PASS (7 cases). Also run `dotnet build servers/project-insight/src-web -c Release`: 0 warnings.

- [ ] **Step 6: Commit**

```bash
git add servers/project-insight/src-web servers/project-insight/tests
git commit -m "feat(web): ASP.NET Core host skeleton with startup checks and health endpoint"
```

### Task B2: Stores for sign-in records and drafts (memory, Table, Blob)

**Files:**
- Create: `src-web/Storage/OAuthStores.cs` (interfaces and records), `src-web/Storage/MemoryStores.cs`, `src-web/Storage/TableStores.cs`, `src-web/Storage/BlobDraftStore.cs`, `src-web/Storage/WebStorage.cs`
- Modify: `src-web/WebHost.cs` (call `AddWebStorage`)
- Create: `tests/Web/AzuriteStorage.cs`, `tests/Web/OAuthStoreContractTests.cs`, `tests/Web/DraftStoreContractTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record RegisteredClient(string ClientId, string? ClientName, IReadOnlyList<string> RedirectUris, DateTimeOffset LastUsed);
public interface IClientStore
{
    Task<RegisteredClient?> GetAsync(string clientId, CancellationToken ct);
    /// <summary>False when the store already holds <paramref name="max"/> clients.</summary>
    Task<bool> TryAddAsync(RegisteredClient client, int max, CancellationToken ct);
    Task TouchAsync(string clientId, DateTimeOffset at, CancellationToken ct);
    Task PruneAsync(DateTimeOffset unusedSince, CancellationToken ct);
}

public static class SessionStatus { public const string Pending = "pending", Completing = "completing"; }
public sealed record SignInSession
{
    public required string Id { get; init; }
    public required string ClientId { get; init; }
    public required string RedirectUri { get; init; }
    public string? State { get; init; }
    public required string CodeChallenge { get; init; }
    public string? Resource { get; init; }
    public required string PiActivationId { get; init; }
    public required string PiActivationUrl { get; init; }
    public required string CookieHash { get; init; }
    public int Attempts { get; init; }
    public string Status { get; init; } = SessionStatus.Pending;
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Concurrency token set by the store on read; ignored on create.</summary>
    public string? Version { get; init; }
}
public interface ISessionStore
{
    /// <summary>False when <paramref name="maxOutstanding"/> unexpired sessions already exist.</summary>
    Task<bool> TryCreateAsync(SignInSession session, int maxOutstanding, DateTimeOffset now, CancellationToken ct);
    Task<SignInSession?> GetAsync(string id, CancellationToken ct);
    /// <summary>Saves <paramref name="updated"/> only if the stored session still has updated.Version.</summary>
    Task<bool> TryReplaceAsync(SignInSession updated, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
    Task PruneAsync(DateTimeOffset now, CancellationToken ct);
}

public sealed record StoredCode(string CodeHash, byte[] Payload, DateTimeOffset ExpiresAt);
public interface ICodeStore
{
    Task SaveAsync(StoredCode code, CancellationToken ct);
    /// <summary>Removes and returns the code; null if missing or another caller took it first.</summary>
    Task<StoredCode?> TryTakeAsync(string codeHash, CancellationToken ct);
    Task PruneAsync(DateTimeOffset now, CancellationToken ct);
}
```

- `WebStorage.AddWebStorage(this IServiceCollection)` registers `IClientStore`, `ISessionStore`, `ICodeStore` and `IDraftStore` as singletons, chosen at resolution time:
  - `Storage:Mode` = `Local` → memory OAuth stores, plus `FileDraftStore` at `ProjectInsight:DraftsPath`.
  - `Azure` (the default) → Table and Blob stores. They use `Storage:ConnectionString` if set (Azurite); otherwise `Storage:TableEndpoint` / `Storage:BlobEndpoint` with `DefaultAzureCredential`.
- Table names `OAuthClients`, `OAuthSessions`, `OAuthCodes`; blob container `drafts`. The app never creates them; Bicep does, and tests do.

- [ ] **Step 1: Write the Azurite helper and contract tests**

`tests/Web/AzuriteStorage.cs`:

```csharp
namespace ProjectInsightMCP.Tests.Web;

/// <summary>Azure-backed store tests run only when Azurite is available (CI sets the variable).</summary>
public static class AzuriteStorage
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("AZURITE_CONNECTION_STRING");
    public static bool Available => !string.IsNullOrEmpty(ConnectionString);
    public static string UniqueName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..12];
}
```

`tests/Web/OAuthStoreContractTests.cs`: an abstract contract with two concrete classes per store kind, memory and Table. The pattern, shown for the code store (repeat it for clients and sessions with the cases listed below):

```csharp
using Azure.Data.Tables;
using ProjectInsightMCP.Web.Storage;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public abstract class CodeStoreContract
{
    protected abstract bool Available { get; }
    protected abstract Task<ICodeStore> CreateAsync();
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public async Task A_code_can_be_taken_once()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.SaveAsync(new StoredCode("h1", [1, 2, 3], Now.AddMinutes(5)), default);

        Assert.Equal(new byte[] { 1, 2, 3 }, (await store.TryTakeAsync("h1", default))!.Payload);
        Assert.Null(await store.TryTakeAsync("h1", default));
    }

    [SkippableFact]
    public async Task Concurrent_takes_give_the_code_to_exactly_one_caller()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.SaveAsync(new StoredCode("h2", [9], Now.AddMinutes(5)), default);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.TryTakeAsync("h2", default)));

        Assert.Single(results, r => r is not null);
    }

    [SkippableFact]
    public async Task Prune_removes_only_expired_codes()
    {
        Skip.IfNot(Available);
        var store = await CreateAsync();
        await store.SaveAsync(new StoredCode("old", [1], Now.AddMinutes(-1)), default);
        await store.SaveAsync(new StoredCode("new", [2], Now.AddMinutes(5)), default);

        await store.PruneAsync(Now, default);

        Assert.Null(await store.TryTakeAsync("old", default));
        Assert.NotNull(await store.TryTakeAsync("new", default));
    }
}

public sealed class MemoryCodeStoreTests : CodeStoreContract
{
    protected override bool Available => true;
    protected override Task<ICodeStore> CreateAsync() => Task.FromResult<ICodeStore>(new MemoryCodeStore());
}

public sealed class TableCodeStoreTests : CodeStoreContract
{
    protected override bool Available => AzuriteStorage.Available;
    protected override async Task<ICodeStore> CreateAsync()
    {
        var table = new TableClient(AzuriteStorage.ConnectionString, AzuriteStorage.UniqueName("codes"));
        await table.CreateIfNotExistsAsync();
        return new TableCodeStore(table);
    }
}
```

Client store contract cases:
- **Add, then get:** returns the same redirect URIs.
- **The cap:** `TryAddAsync` returns false once `max` clients exist.
- **Touch and prune:** `TouchAsync` updates `LastUsed`; prune removes a client unused since before the cutoff and keeps one touched after it.

Session store contract cases:
- **Create, then get:** round-trips every field, and `Version` is non-null.
- **The cap:** `TryCreateAsync` returns false at `maxOutstanding` unexpired sessions; expired ones don't count.
- **Compare-and-set:**
  - `TryReplaceAsync` with the version just read succeeds, and the next read returns a new `Version`;
  - replacing again with the **old** version returns false;
  - two concurrent replaces from the same read: exactly one wins.
- **Delete and prune:** delete removes the session; prune removes expired ones only.

`tests/Web/DraftStoreContractTests.cs`: the same pattern over `IDraftStore`, for `FileDraftStore` (temp folder, always available) and `BlobDraftStore` (Azurite). Use `Drafts.New(owner, id)` and `ManualTimeProvider` from `tests/TimeChanges`. Cases:
- **Owner isolation:** a created draft reads back for its owner and is null for another owner.
- **Creating an existing id** throws `InvalidOperationException`.
- **Transitions:** `TryTransitionAsync` applies the update only from the expected status, and returns null from any other.
- **Concurrency:** eight concurrent `TryTransitionAsync(…, DraftStatus.Draft, d => d.Status = DraftStatus.Committing)` calls succeed exactly once.
- **Pruning:** `PruneAsync(owner)` deletes drafts not updated for 7 days (advance the `ManualTimeProvider`) and keeps newer ones. `PruneAllAsync` does the same across owners.
- **Path characters:** ids or owners with path characters (`"../u-2/d_abc"`, `""`) are not found.

`FileDraftStore` already passes most of these through its own tests. Running them through the contract proves `BlobDraftStore` behaves the same.

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~StoreContract|FullyQualifiedName~StoreTests"`
Expected: build failure (types missing).

- [ ] **Step 3: Implement**

`src-web/Storage/OAuthStores.cs`: the interfaces and records above, in namespace `ProjectInsightMCP.Web.Storage`.

`src-web/Storage/MemoryStores.cs`: `MemoryClientStore`, `MemorySessionStore` and `MemoryCodeStore`, using `ConcurrentDictionary`.
- **Sessions:** `Version` is a counter string; `TryReplaceAsync` uses `TryUpdate` with the stored instance as the comparand.
- **Codes:** `TryTakeAsync` is `TryRemove`.
- **Purpose:** they're for tests and local runs only. Say so in each class's doc comment.

`src-web/Storage/TableStores.cs`: every entity stores the record as JSON in a `Json` string property, plus an `ExpiresAt` (or `LastUsed`) property for filtering. All three use `PartitionKey` = `"p"` and `RowKey` = the id or hash.

```csharp
public sealed class TableCodeStore(TableClient table) : ICodeStore
{
    private const string Partition = "p";

    public Task SaveAsync(StoredCode code, CancellationToken ct) =>
        table.AddEntityAsync(new TableEntity(Partition, code.CodeHash)
        {
            ["Payload"] = code.Payload,
            ["ExpiresAt"] = code.ExpiresAt
        }, ct);

    public async Task<StoredCode?> TryTakeAsync(string codeHash, CancellationToken ct)
    {
        var found = await table.GetEntityIfExistsAsync<TableEntity>(Partition, codeHash, cancellationToken: ct);
        if (!found.HasValue)
            return null;
        var entity = found.Value!;
        try
        {
            // ETag-conditional delete: of two concurrent takers, only one deletes.
            await table.DeleteEntityAsync(Partition, codeHash, entity.ETag, ct);
        }
        catch (RequestFailedException ex) when (ex.Status is 404 or 412)
        {
            return null;
        }
        return new StoredCode(codeHash, entity.GetBinary("Payload"), entity.GetDateTimeOffset("ExpiresAt")!.Value);
    }

    public async Task PruneAsync(DateTimeOffset now, CancellationToken ct)
    {
        await foreach (var e in table.QueryAsync<TableEntity>(x => x.PartitionKey == Partition && x.GetDateTimeOffset("ExpiresAt") < now,
                           select: ["PartitionKey", "RowKey"], cancellationToken: ct))
            await table.DeleteEntityAsync(Partition, e.RowKey, ETag.All, ct);
    }
}
```

`TableSessionStore`:
- `Version` is the entity `ETag` string.
- `TryReplaceAsync` calls `UpdateEntityAsync(entity, new ETag(updated.Version), TableUpdateMode.Replace)` and returns false on 412.
- `TryCreateAsync` counts unexpired rows with a query (at most a few hundred), then calls `AddEntityAsync`.

`TableClientStore`: the same shape. `TouchAsync` is a merge of `LastUsed`.

**Don't** use `Azure.Data.Tables` LINQ on `GetDateTimeOffset` if it doesn't translate. If it fails, use the OData string filter `$"PartitionKey eq 'p' and ExpiresAt lt datetime'{now.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}'"`.

`src-web/Storage/BlobDraftStore.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using ProjectInsightMCP.TimeChanges;

namespace ProjectInsightMCP.Web.Storage;

/// <summary>
/// Drafts as blobs at &lt;ownerId&gt;/&lt;id&gt;.json. Status changes are compare-and-set on the blob's
/// ETag, the same guarantee FileDraftStore gets from its lock file.
/// </summary>
public sealed partial class BlobDraftStore(BlobContainerClient container, TimeProvider time) : IDraftStore
{
    private static readonly TimeSpan Keep = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxRetries = 5;

    [GeneratedRegex("^d_[a-z0-9]{1,40}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    private static partial Regex OwnerPattern();

    public async Task<Draft?> GetAsync(string ownerId, string id, CancellationToken ct = default)
        => BlobFor(ownerId, id) is { } blob ? (await ReadAsync(blob, ct))?.Draft : null;

    public async Task CreateAsync(Draft draft, CancellationToken ct = default)
    {
        var blob = BlobFor(draft.ProposedBy, draft.Id) ?? throw new ArgumentException($"Invalid draft id or owner: {draft.Id}");
        draft.UpdatedAt = time.GetUtcNow();
        try
        {
            await blob.UploadAsync(BinaryData.FromObjectAsJson(draft, Json),
                new BlobUploadOptions { Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All } }, ct);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            throw new InvalidOperationException($"Draft {draft.Id} already exists.");
        }
    }

    public async Task<Draft?> TryTransitionAsync(string ownerId, string id, string expectedStatus, Action<Draft> update, CancellationToken ct = default)
    {
        if (BlobFor(ownerId, id) is not { } blob)
            return null;
        for (var attempt = 0; attempt < MaxRetries; attempt++)
        {
            if (await ReadAsync(blob, ct) is not var (draft, etag) || draft.Status != expectedStatus)
                return null;
            update(draft);
            draft.UpdatedAt = time.GetUtcNow();
            try
            {
                await blob.UploadAsync(BinaryData.FromObjectAsJson(draft, Json),
                    new BlobUploadOptions { Conditions = new BlobRequestConditions { IfMatch = etag } }, ct);
                return draft;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Someone else changed it; re-read and re-check the status.
            }
        }
        return null;
    }

    public async Task PruneAsync(string ownerId, CancellationToken ct = default)
    {
        if (!OwnerPattern().IsMatch(ownerId))
            return;
        var cutoff = time.GetUtcNow() - Keep;
        await foreach (var item in container.GetBlobsAsync(prefix: ownerId + "/", cancellationToken: ct))
        {
            var blob = container.GetBlobClient(item.Name);
            if (await ReadAsync(blob, ct) is var (draft, etag) && draft.UpdatedAt < cutoff)
                await blob.DeleteIfExistsAsync(conditions: new BlobRequestConditions { IfMatch = etag }, cancellationToken: ct);
        }
    }

    public async Task PruneAllAsync(CancellationToken ct = default)
    {
        await foreach (var item in container.GetBlobsByHierarchyAsync(delimiter: "/", cancellationToken: ct))
            if (item.IsPrefix && OwnerPattern().IsMatch(item.Prefix.TrimEnd('/')))
                await PruneAsync(item.Prefix.TrimEnd('/'), ct);
    }

    private BlobClient? BlobFor(string ownerId, string id)
        => OwnerPattern().IsMatch(ownerId) && IdPattern().IsMatch(id) ? container.GetBlobClient($"{ownerId}/{id}.json") : null;

    private static async Task<(Draft Draft, ETag ETag)?> ReadAsync(BlobClient blob, CancellationToken ct)
    {
        try
        {
            var result = await blob.DownloadContentAsync(ct);
            return result.Value.Content.ToObjectFromJson<Draft>(Json) is { } draft ? (draft, result.Value.Details.ETag) : null;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
```

The SDK method names (`GetBlobsAsync` and `GetBlobsByHierarchyAsync` parameters) may differ slightly in 12.30. Adjust to the installed API, keeping the behaviour.

`src-web/Storage/WebStorage.cs`:

```csharp
using Azure.Data.Tables;
using Azure.Identity;
using Azure.Storage.Blobs;
using ProjectInsightMCP.TimeChanges;

namespace ProjectInsightMCP.Web.Storage;

public static class WebStorage
{
    public static IServiceCollection AddWebStorage(this IServiceCollection services)
    {
        services.AddSingleton<IClientStore>(sp => Local(sp) ? new MemoryClientStore() : new TableClientStore(Table(sp, "OAuthClients")));
        services.AddSingleton<ISessionStore>(sp => Local(sp) ? new MemorySessionStore() : new TableSessionStore(Table(sp, "OAuthSessions")));
        services.AddSingleton<ICodeStore>(sp => Local(sp) ? new MemoryCodeStore() : new TableCodeStore(Table(sp, "OAuthCodes")));
        services.AddSingleton<IDraftStore>(sp => Local(sp)
            ? new FileDraftStore(Config(sp)["ProjectInsight:DraftsPath"] ?? throw new InvalidOperationException("Storage:Mode Local needs ProjectInsight:DraftsPath."), sp.GetRequiredService<TimeProvider>())
            : new BlobDraftStore(Blobs(sp).GetBlobContainerClient("drafts"), sp.GetRequiredService<TimeProvider>()));
        return services;
    }

    private static IConfiguration Config(IServiceProvider sp) => sp.GetRequiredService<IConfiguration>();
    private static bool Local(IServiceProvider sp) => string.Equals(Config(sp)["Storage:Mode"], "Local", StringComparison.OrdinalIgnoreCase);

    private static TableClient Table(IServiceProvider sp, string name)
    {
        var config = Config(sp);
        return config["Storage:ConnectionString"] is { Length: > 0 } cs
            ? new TableClient(cs, name)
            : new TableServiceClient(new Uri(config["Storage:TableEndpoint"]!), new DefaultAzureCredential()).GetTableClient(name);
    }

    private static BlobServiceClient Blobs(IServiceProvider sp)
    {
        var config = Config(sp);
        return config["Storage:ConnectionString"] is { Length: > 0 } cs
            ? new BlobServiceClient(cs)
            : new BlobServiceClient(new Uri(config["Storage:BlobEndpoint"]!), new DefaultAzureCredential());
    }
}
```

In `WebHost.AddProjectInsightWeb`, add `services.AddWebStorage();`.

- [ ] **Step 4: Run the tests, with Azurite and without it**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Store"`
Expected: memory and file cases pass; Table and Blob cases are skipped.

Then:

```bash
docker run -d --rm --name azurite -p 10000-10002:10000-10002 mcr.microsoft.com/azure-storage/azurite
AZURITE_CONNECTION_STRING=UseDevelopmentStorage=true dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Store"
docker stop azurite
```

Expected: every case passes, none skipped.

- [ ] **Step 5: Commit**

```bash
git add servers/project-insight/src-web servers/project-insight/tests
git commit -m "feat(web): Table and Blob stores for sign-in records and drafts, with shared contract tests"
```

### Task B3: PI bearer authentication, OAuth metadata, `Origin` guard, and the MCP endpoint

**Files:**
- Create: `src-web/Auth/PiCaller.cs`, `src-web/Auth/PiIdentityClient.cs`, `src-web/Auth/PiBearerHandler.cs`, `src-web/Auth/OAuthMetadata.cs`, `src-web/Auth/OriginGuard.cs`
- Modify: `src-web/WebHost.cs`, `src-web/StartupChecks.cs`
- Create: `tests/Web/McpConnect.cs`, `tests/Web/BearerAuthTests.cs`, `tests/Web/MetadataTests.cs`
- Modify: `tests/Web/HostTests.cs` (add `Startup_refuses_a_stateful_mcp_endpoint` from B1)

**Interfaces:**
- Consumes: `WebOptions`, `AuthOptions`, `TokenHash.Of` (B1).
- Produces:
  - `PiCaller : IToolCallKey`: a scoped service with `string? UserId`; `Key` throws if `UserId` is null.
  - `PiIdentityClient.ResolveAsync(string token, CancellationToken) → Task<PiIdentity>`, where `PiIdentity(PiIdentityStatus Status, string? UserId)` and `PiIdentityStatus { Ok, Rejected, Unavailable }`.
  - `PiBearerHandler.SchemeName = "PiBearer"`.
  - `OAuthMetadata.MapOAuthMetadata(this WebApplication)`.
  - `OriginGuard`: middleware.
  - Named HttpClient `PiIdentityClient.HttpClientName = "pi-identity"`.

- [ ] **Step 1: Write the failing tests**

`tests/Web/McpConnect.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace ProjectInsightMCP.Tests.Web;

public static class McpConnect
{
    /// <summary>An MCP client for the in-memory web host, sending <paramref name="token"/> as the bearer token.</summary>
    public static Task<McpClient> ConnectAsync(this WebFactory factory, string token)
        => McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(WebFactory.BaseAddress, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" }
        }, factory.Client(), NullLoggerFactory.Instance, ownsHttpClient: true));
}
```

`tests/Web/MetadataTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class MetadataTests
{
    [Theory]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    [InlineData("/.well-known/oauth-protected-resource")]
    public async Task Protected_resource_metadata_matches_the_spec(string path)
    {
        using var factory = new WebFactory();
        var json = await factory.Client().GetFromJsonAsync<JsonNode>(path);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "resource": "https://mcp.test/mcp",
              "authorization_servers": ["https://mcp.test"],
              "scopes_supported": ["pi"],
              "bearer_methods_supported": ["header"]
            }
            """), json), json!.ToJsonString());
    }

    [Fact]
    public async Task Authorization_server_metadata_matches_the_spec()
    {
        using var factory = new WebFactory();
        var json = await factory.Client().GetFromJsonAsync<JsonNode>("/.well-known/oauth-authorization-server");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "issuer": "https://mcp.test",
              "authorization_endpoint": "https://mcp.test/authorize",
              "token_endpoint": "https://mcp.test/token",
              "registration_endpoint": "https://mcp.test/register",
              "response_types_supported": ["code"],
              "grant_types_supported": ["authorization_code"],
              "code_challenge_methods_supported": ["S256"],
              "token_endpoint_auth_methods_supported": ["none"],
              "client_id_metadata_document_supported": true,
              "authorization_response_iss_parameter_supported": true,
              "scopes_supported": ["pi"]
            }
            """), json), json!.ToJsonString());
    }
}
```

`tests/Web/BearerAuthTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class BearerAuthTests
{
    private static HttpRequestMessage McpPost(string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (origin is not null)
            request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public async Task Unauthenticated_call_gets_401_pointing_at_resource_metadata()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(token: null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer resource_metadata=\"https://mcp.test/.well-known/oauth-protected-resource/mcp\", scope=\"pi\"",
            response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Token_pi_rejects_gets_401_and_is_remembered_for_a_minute()
    {
        using var factory = new WebFactory();
        var client = factory.Client();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(McpPost(PiStub.RejectedToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(McpPost(PiStub.RejectedToken))).StatusCode);

        Assert.Single(factory.Pi.ToPath("/api/user/me"));
    }

    [Fact]
    public async Task Pi_outage_gets_503_with_retry_after_and_no_sign_in_challenge()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(PiStub.BrokenToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("quote\"inside")]
    [InlineData("comma,inside")]
    public async Task Malformed_bearer_is_refused_without_calling_pi(string token)
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.Pi.ToPath("/api/user/me"));
    }

    [Fact]
    public async Task Pi_user_not_on_the_allowlist_gets_403()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(PiStub.StrangerToken));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Allowlist_ignores_the_case_of_pi_user_ids()
    {
        // The stub returns user B's id in upper case; the allowlist has it in lower case.
        using var factory = new WebFactory();
        await using var mcp = await factory.ConnectAsync(PiStub.TokenB);

        Assert.NotEmpty(await mcp.ListToolsAsync());
    }

    [Fact]
    public async Task Valid_token_reaches_the_mcp_server()
    {
        using var factory = new WebFactory();
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);

        Assert.Contains(await mcp.ListToolsAsync(), t => t.Name == "search_projects");
    }

    [Fact]
    public async Task Request_from_an_unknown_origin_gets_403()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().SendAsync(McpPost(PiStub.TokenA, origin: "https://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(factory.Pi.ToPath("/api/user/me"));
    }

    [Fact]
    public async Task Request_from_an_allowed_origin_is_served()
    {
        using var factory = new WebFactory();
        factory.Settings["Auth:AllowedOrigins:0"] = "https://claude.ai";
        var response = await factory.Client().SendAsync(McpPost(PiStub.TokenA, origin: "https://claude.ai"));

        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Token_in_the_query_string_is_not_accepted()
    {
        using var factory = new WebFactory();
        var request = McpPost(token: null);
        request.RequestUri = new Uri("/mcp?access_token=" + PiStub.TokenA, UriKind.Relative);

        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.Client().SendAsync(request)).StatusCode);
    }
}
```

Move `Startup_refuses_a_stateful_mcp_endpoint` into `HostTests` now.

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web.BearerAuthTests|FullyQualifiedName~Web.MetadataTests|FullyQualifiedName~Web.HostTests"`
Expected: FAIL (404s or missing types).

- [ ] **Step 3: Implement**

`src-web/Auth/PiCaller.cs`:

```csharp
using ProjectInsightMCP.Server;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>The signed-in PI user for this request. Set by <see cref="PiBearerHandler"/>; per-user tool queue key.</summary>
public sealed class PiCaller : IToolCallKey
{
    public string? UserId { get; set; }
    public string Key => UserId ?? throw new InvalidOperationException("No signed-in PI user on this request.");
}
```

`src-web/Auth/PiIdentityClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using ProjectInsightMCP.Models;

namespace ProjectInsightMCP.Web.Auth;

public enum PiIdentityStatus { Ok, Rejected, Unavailable }

public sealed record PiIdentity(PiIdentityStatus Status, string? UserId);

/// <summary>
/// Asks PI who a bearer token belongs to. PI is the only judge of a pass-through token, so this is
/// the validation. Results are cached under the token's hash, never the token.
/// </summary>
public sealed class PiIdentityClient(IHttpClientFactory http, IMemoryCache cache, IConfiguration config, ILogger<PiIdentityClient> logger)
{
    public const string HttpClientName = "pi-identity";
    private static readonly TimeSpan OkFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RejectedFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueueFor = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    // Caps how hard random bearer tokens can push on PI.
    private readonly SemaphoreSlim _calls = new(8, 8);

    public async Task<PiIdentity> ResolveAsync(string token, CancellationToken ct)
    {
        var key = "pi-identity:" + TokenHash.Of(token);
        if (cache.TryGetValue(key, out PiIdentity? known))
            return known!;
        if (!await _calls.WaitAsync(QueueFor, ct))
            return new PiIdentity(PiIdentityStatus.Unavailable, null);
        try
        {
            var identity = await FetchAsync(token, ct);
            if (identity.Status == PiIdentityStatus.Ok)
                cache.Set(key, identity, OkFor);
            else if (identity.Status == PiIdentityStatus.Rejected)
                cache.Set(key, identity, RejectedFor);
            return identity;
        }
        finally
        {
            _calls.Release();
        }
    }

    private async Task<PiIdentity> FetchAsync(string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(config["ProjectInsight:BaseUrl"]!), "/api/user/me"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            var status = (int)response.StatusCode;
            if (status is >= 400 and < 500 and not 408 and not 429)
                return new PiIdentity(PiIdentityStatus.Rejected, null);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("PI identity check failed with HTTP {Status}", status);
                return new PiIdentity(PiIdentityStatus.Unavailable, null);
            }
            var user = await response.Content.ReadFromJsonAsync<User>(Json, ct);
            return string.IsNullOrEmpty(user?.Id)
                ? new PiIdentity(PiIdentityStatus.Unavailable, null)
                : new PiIdentity(PiIdentityStatus.Ok, user.Id);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("PI identity check failed: {Error}", ex.GetType().Name);
            return new PiIdentity(PiIdentityStatus.Unavailable, null);
        }
    }
}
```

`src-web/Auth/PiBearerHandler.cs`:

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ProjectInsightMCP.Services;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// Pass-through bearer authentication: the bearer token is a PI token. PI says whose it is;
/// the allowlist says whether they may use this server.
/// </summary>
public sealed partial class PiBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    PiIdentityClient identity, IOptions<AuthOptions> auth, IOptions<WebOptions> web)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "PiBearer";
    private const string Outcome = "pi.auth.outcome";

    // RFC 6750 b64token. Anything else never reaches a header parser that could echo it in an exception.
    [GeneratedRegex(@"^[A-Za-z0-9\-._~+/]+=*$")]
    private static partial Regex Token68();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = header["Bearer ".Length..].Trim();
        if (token.Length is 0 or > 4096 || !Token68().IsMatch(token))
            return AuthenticateResult.Fail("Malformed bearer token.");

        var who = await identity.ResolveAsync(token, Context.RequestAborted);
        switch (who.Status)
        {
            case PiIdentityStatus.Unavailable:
                Context.Items[Outcome] = "unavailable";
                return AuthenticateResult.Fail("Project Insight is unavailable.");
            case PiIdentityStatus.Rejected:
                return AuthenticateResult.Fail("Project Insight rejected the token.");
        }
        if (!auth.Value.PiUserIds.Contains(who.UserId!))
        {
            Context.Items[Outcome] = "forbidden";
            return AuthenticateResult.Fail("This PI user is not allowed on this server.");
        }

        // The MCP endpoint is stateless, so tools resolve services from this request's scope.
        var services = Context.RequestServices;
        services.GetRequiredService<ApiTokenAccessor>().ApiToken = token;
        services.GetRequiredService<PiCaller>().UserId = who.UserId;

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", who.UserId!)], SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        switch (Context.Items[Outcome] as string)
        {
            case "unavailable":
                Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                Response.Headers.RetryAfter = "30";
                return Response.WriteAsync("Project Insight is not responding. Try again shortly.");
            case "forbidden":
                Response.StatusCode = StatusCodes.Status403Forbidden;
                return Response.WriteAsync("This server isn't open to your Project Insight account.");
            default:
                Response.StatusCode = StatusCodes.Status401Unauthorized;
                Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{web.Value.ResourceMetadataUrl}\", scope=\"pi\"";
                return Task.CompletedTask;
        }
    }
}
```

`src-web/Auth/OAuthMetadata.cs`:

```csharp
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>RFC 9728 protected-resource metadata and RFC 8414 authorization-server metadata.</summary>
public static class OAuthMetadata
{
    public static void MapOAuthMetadata(this WebApplication app)
    {
        IResult Resource(IOptions<WebOptions> web) => Results.Json(new JsonObject
        {
            ["resource"] = web.Value.Resource,
            ["authorization_servers"] = new JsonArray(web.Value.Issuer),
            ["scopes_supported"] = new JsonArray("pi"),
            ["bearer_methods_supported"] = new JsonArray("header")
        });

        app.MapGet("/.well-known/oauth-protected-resource/mcp", Resource);
        app.MapGet("/.well-known/oauth-protected-resource", Resource);

        app.MapGet("/.well-known/oauth-authorization-server", (IOptions<WebOptions> web) =>
        {
            var issuer = web.Value.Issuer;
            return Results.Json(new JsonObject
            {
                ["issuer"] = issuer,
                ["authorization_endpoint"] = issuer + "/authorize",
                ["token_endpoint"] = issuer + "/token",
                ["registration_endpoint"] = issuer + "/register",
                ["response_types_supported"] = new JsonArray("code"),
                ["grant_types_supported"] = new JsonArray("authorization_code"),
                ["code_challenge_methods_supported"] = new JsonArray("S256"),
                ["token_endpoint_auth_methods_supported"] = new JsonArray("none"),
                ["client_id_metadata_document_supported"] = true,
                ["authorization_response_iss_parameter_supported"] = true,
                // offline_access is deliberately absent: pass-through issues no refresh tokens.
                ["scopes_supported"] = new JsonArray("pi")
            });
        });
    }
}
```

`src-web/Auth/OriginGuard.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// MCP transports must refuse a present-but-unknown Origin (DNS rebinding). Connector backends send
/// none, so the allowlist is empty by default.
/// </summary>
public sealed class OriginGuard(RequestDelegate next, IOptions<AuthOptions> auth)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/mcp")
            && context.Request.Headers.Origin is { Count: > 0 } origin
            && !auth.Value.Origins.Contains(origin.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
        return next(context);
    }
}
```

`src-web/WebHost.cs`: add the registrations to `AddProjectInsightWeb`:

```csharp
        services.AddMemoryCache();
        services.AddHttpClient(PiIdentityClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<PiIdentityClient>();
        services.AddScoped<ApiTokenAccessor>();
        services.AddScoped<PiCaller>();
        services.AddAuthentication(PiBearerHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, PiBearerHandler>(PiBearerHandler.SchemeName, null);
        services.AddAuthorization();

        services.AddProjectInsightMcp(new ProjectInsightMcpOptions
        {
            ServerVersion = ProjectInsightServer.VersionOf(typeof(WebHost).Assembly),
            ExposeTool = name => !HiddenTools.Contains(name)
        }).WithHttpTransport(o => o.Stateless = true);
        // Per-user tool queue instead of the single-user default.
        services.Replace(ServiceDescriptor.Scoped<IToolCallKey>(sp => sp.GetRequiredService<PiCaller>()));
```

with:

```csharp
    /// <summary>Device-activation sign-in tools: the connector's OAuth sign-in replaces them.</summary>
    public static readonly IReadOnlySet<string> HiddenTools =
        new HashSet<string> { "complete_authentication", "link_pi_account", "complete_pi_link" };
```

Add to `UseProjectInsightWeb`, after the checks:

```csharp
        if (!app.Services.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value.Stateless)
            throw new InvalidOperationException("The MCP endpoint must run stateless: per-request PI services depend on it.");

        app.UseMiddleware<OriginGuard>();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapOAuthMetadata();
        app.MapMcp("/mcp").RequireAuthorization();
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web."`
Expected: PASS.
- If `Valid_token_reaches_the_mcp_server` fails because the SDK resolves tool services from a different scope than `HttpContext.RequestServices`, **stop and report**. The isolation design depends on it.
- Task B4's isolation test is the full check.

- [ ] **Step 5: Commit**

```bash
git add servers/project-insight/src-web servers/project-insight/tests
git commit -m "feat(web): pass-through PI bearer auth, OAuth metadata, Origin guard and the MCP endpoint"
```

### Task B4: Per-request PI services, hidden tools, per-user queue, and isolation

**Files:**
- Modify: `src-web/WebHost.cs`
- Create: `tests/Web/McpEndpointTests.cs`

**Interfaces:**
- Consumes: `PiCaller`, `ApiTokenAccessor` set by `PiBearerHandler` (B3); `IDraftStore` from `AddWebStorage` (B2); `HiddenTools` (B3).
- Produces: the tool services, registered per request.

- [ ] **Step 1: Write the failing tests**

`tests/Web/McpEndpointTests.cs`:

```csharp
using System.Net;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Tests.TimeChanges;
using ProjectInsightMCP.TimeChanges;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class McpEndpointTests
{
    private static readonly string[] SignInTools = ["complete_authentication", "link_pi_account", "complete_pi_link"];

    [Fact]
    public async Task Web_tools_are_the_stdio_tools_without_the_sign_in_tools()
    {
        using var factory = new WebFactory();
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);

        var expected = ToolListSnapshotTests.LoadSnapshot().Select(t => t!["name"]!.GetValue<string>())
            .Concat(TimeChangeToolsTests.ToolNames)
            .Except(SignInTools)
            .Order();
        Assert.Equal(expected, (await mcp.ListToolsAsync()).Select(t => t.Name).Order());
        Assert.Contains(await mcp.ListResourcesAsync(), r => r.Uri == "ui://project-insight/time-changes.html");
    }

    [Theory]
    [InlineData("link_pi_account")]
    [InlineData("complete_pi_link")]
    [InlineData("complete_authentication")]
    public async Task Sign_in_tools_cannot_be_called(string name)
    {
        using var factory = new WebFactory();
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);

        await Assert.ThrowsAsync<McpProtocolException>(async () =>
            await mcp.CallToolAsync(name, new Dictionary<string, object?> { ["activationCode"] = "x" }));
    }

    [Fact]
    public async Task Each_users_calls_carry_their_own_token()
    {
        using var factory = new WebFactory();
        factory.Pi.ToolCallDelay = TimeSpan.FromMilliseconds(100);
        await using var a = await factory.ConnectAsync(PiStub.TokenA);
        await using var b = await factory.ConnectAsync(PiStub.TokenB);

        await Task.WhenAll(Enumerable.Range(1, 3).SelectMany(i => new[]
        {
            a.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = $"te-a-{i}" }).AsTask(),
            b.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = $"te-b-{i}" }).AsTask()
        }));

        var deletes = factory.Pi.Requests.Where(r => r.Method == HttpMethod.Delete).ToList();
        Assert.Equal(6, deletes.Count);
        Assert.All(deletes, r => Assert.Equal(
            r.Uri.AbsolutePath.Contains("te-a-") ? $"Bearer {PiStub.TokenA}" : $"Bearer {PiStub.TokenB}",
            r.Authorization));
    }

    [Fact]
    public async Task One_users_calls_queue_while_another_users_run()
    {
        using var factory = new WebFactory();
        factory.Pi.ToolCallDelay = TimeSpan.FromMilliseconds(150);
        await using var a = await factory.ConnectAsync(PiStub.TokenA);
        await using var b = await factory.ConnectAsync(PiStub.TokenB);

        await Task.WhenAll(
            a.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = "te-a-1" }).AsTask(),
            a.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = "te-a-2" }).AsTask(),
            b.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = "te-b-1" }).AsTask(),
            b.CallToolAsync("delete_time_entry", new Dictionary<string, object?> { ["timeEntryId"] = "te-b-2" }).AsTask());

        Assert.Equal(2, factory.Pi.MaxToolCallsInFlight);
    }

    [Fact]
    public async Task Drafts_are_visible_only_to_their_owner()
    {
        using var factory = new WebFactory();
        await using var a = await factory.ConnectAsync(PiStub.TokenA);
        await using var b = await factory.ConnectAsync(PiStub.TokenB);
        var draft = Drafts.New(owner: PiStub.UserA);
        await factory.Services.GetRequiredService<IDraftStore>().CreateAsync(draft);

        var mine = await a.CallToolAsync("get_time_changes", new Dictionary<string, object?> { ["id"] = draft.Id });
        var theirs = await b.CallToolAsync("get_time_changes", new Dictionary<string, object?> { ["id"] = draft.Id });

        Assert.NotEqual(true, mine.IsError);
        Assert.Equal(true, theirs.IsError);
    }

    [Fact]
    public async Task Get_on_the_stateless_endpoint_is_405()
    {
        using var factory = new WebFactory();
        var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + PiStub.TokenA);
        request.Headers.Accept.ParseAdd("text/event-stream");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await factory.Client().SendAsync(request)).StatusCode);
    }
}
```

`Drafts_are_visible_only_to_their_owner` depends on how `TimeChangeService.GetAsync` reports a missing draft (`IsError` or a "not found" text). Read `core/TimeChanges/TimeChangeService.cs` and assert whichever it does. Keep the assertion that user B does **not** get user A's draft content: assert `theirs` contains no text with `draft.Intent`.

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web.McpEndpointTests"`
Expected: tool calls fail, because the PI services aren't registered.

- [ ] **Step 3: Register the PI services per request**

In `WebHost.AddProjectInsightWeb`:

```csharp
        // Per-request PI services. Nothing here may be a singleton: each request carries its own user's token.
        services.AddHttpClient<ProjectInsightApiService>();
        services.AddScoped(sp => new McpServerService(
            sp.GetRequiredService<ProjectInsightApiService>(),
            sp.GetRequiredService<ILogger<McpServerService>>(),
            sp.GetRequiredService<ApiTokenAccessor>(),
            deviceActivation: null,       // no device-activation sign-in, pending state or credential file on a shared server
            keyVaultTokenService: null));
        services.AddScoped<ITimeEntryGateway, ProjectInsightTimeEntryGateway>();
        services.AddScoped<TimeChangePlanner>();
        services.AddScoped<TimeChangeCommitter>();
        services.AddScoped<TimeChangeService>();
```

**Do not** register `DeviceActivationService`, `CredentialStorageService` or `KeyVaultTokenService` anywhere in the web host.

- [ ] **Step 4: Run the tests**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web."`
Expected: PASS.

If `Get_on_the_stateless_endpoint_is_405` gets 401 instead, authorization runs before routing rejects the method. Accept 405 **or** 401: neither is a 500 and neither hangs. Change the assertion to `Assert.Contains(status, new[] { 405, 401 })`, with a comment saying why.

- [ ] **Step 5: Commit**

```bash
git add servers/project-insight/src-web servers/project-insight/tests
git commit -m "feat(web): per-request PI services with per-user tool queues and isolation tests"
```

### Task B5: OAuth primitives: redirect policy, PKCE, sealed codes

**Files:**
- Create: `src-web/Auth/RedirectPolicy.cs`, `src-web/Auth/Pkce.cs`, `src-web/Auth/CodeProtector.cs`
- Create: `tests/Web/RedirectPolicyTests.cs`, `tests/Web/PkceTests.cs`, `tests/Web/CodeProtectorTests.cs`

**Interfaces:**
- Produces:
  - `RedirectPolicy(IOptions<AuthOptions>)`:
    - `bool IsAllowed(string? uri)`;
    - `static bool Matches(string candidate, IEnumerable<string> registered)`;
    - `static bool IsLoopback(Uri uri)`;
    - `static Uri? Parse(string? uri)`.
  - `Pkce`: `static bool IsValidChallenge(string?)`, `static bool Verify(string verifier, string challenge)`.
  - `CodeProtector`:
    - `static string NewCode()`;
    - `static string Hash(string code)`;
    - `static byte[] Seal(string code, byte[] plaintext)`;
    - `static byte[]? Open(string code, byte[] sealedPayload)`.

- [ ] **Step 1: Write the failing tests**

`tests/Web/RedirectPolicyTests.cs`:

```csharp
using Microsoft.Extensions.Options;
using ProjectInsightMCP.Web;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class RedirectPolicyTests
{
    private static readonly RedirectPolicy Policy = new(Options.Create(new AuthOptions()));

    [Theory]
    [InlineData("https://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://CLAUDE.AI/api/mcp/auth_callback")]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect")]
    [InlineData("http://localhost:3118/callback")]
    [InlineData("http://127.0.0.1:50000/callback")]
    [InlineData("http://[::1]:8080/callback")]
    [InlineData("http://localhost/callback")]
    public void Allowed(string uri) => Assert.True(Policy.IsAllowed(uri));

    [Theory]
    [InlineData("https://claude.ai/other")]
    [InlineData("https://claude.ai/api/mcp/auth_callback/")]
    [InlineData("https://claude.ai/API/mcp/auth_callback")]
    [InlineData("https://claude.ai.evil.com/api/mcp/auth_callback")]
    [InlineData("https://claude.ai@evil.com/api/mcp/auth_callback")]
    [InlineData("https://evil.com@claude.ai/api/mcp/auth_callback")]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect/../x")]
    [InlineData("https://chatgpt.com/connector_platform_oauth_redirect%2F..%2Fx")]
    [InlineData("https://claude.ai/api/mcp/auth_callback#frag")]
    [InlineData("https://claude.ai/api/mcp/auth_callback?x=1")]
    [InlineData("https://claude.ai:8443/api/mcp/auth_callback")]
    [InlineData("http://claude.ai/api/mcp/auth_callback")]
    [InlineData("https://localhost:3118/callback")]
    [InlineData("http://localhost:3118/other")]
    [InlineData("http://localhost.evil.com/callback")]
    [InlineData("http://10.0.0.1/callback")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    [InlineData(null)]
    public void Refused(string? uri) => Assert.False(Policy.IsAllowed(uri));

    [Fact]
    public void Loopback_matches_a_registered_loopback_on_any_port()
        => Assert.True(RedirectPolicy.Matches("http://127.0.0.1:61000/callback", ["http://127.0.0.1/callback"]));

    [Fact]
    public void Loopback_does_not_match_a_different_loopback_host()
        => Assert.False(RedirectPolicy.Matches("http://localhost:61000/callback", ["http://127.0.0.1/callback"]));

    [Fact]
    public void Https_must_match_a_registered_uri_exactly()
    {
        Assert.True(RedirectPolicy.Matches("https://claude.ai/api/mcp/auth_callback", ["https://claude.ai/api/mcp/auth_callback"]));
        Assert.False(RedirectPolicy.Matches("https://claude.ai/api/mcp/auth_callback", ["https://chatgpt.com/connector_platform_oauth_redirect"]));
    }
}
```

`tests/Web/PkceTests.cs`: the RFC 7636 appendix B vector. Verifier `dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk`, challenge `E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGF3cMjXmM`:

```csharp
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class PkceTests
{
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGF3cMjXmM";

    [Fact]
    public void Rfc7636_vector_verifies() => Assert.True(Pkce.Verify(Verifier, Challenge));

    [Fact]
    public void Wrong_verifier_fails() => Assert.False(Pkce.Verify(Verifier[..^1] + "A", Challenge));

    [Theory]
    [InlineData("short")]
    [InlineData("has space in it and is long enough to pass the length check ok")]
    public void Malformed_verifier_fails(string verifier) => Assert.False(Pkce.Verify(verifier, Challenge));

    [Theory]
    [InlineData(Challenge, true)]
    [InlineData("tooshort", false)]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGF3cMjXm+", false)]
    [InlineData(null, false)]
    public void Challenge_must_be_43_base64url_characters(string? challenge, bool valid)
        => Assert.Equal(valid, Pkce.IsValidChallenge(challenge));
}
```

`tests/Web/CodeProtectorTests.cs`:

```csharp
using System.Text;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class CodeProtectorTests
{
    [Fact]
    public void Sealed_payload_opens_only_with_its_code()
    {
        var code = CodeProtector.NewCode();
        var other = CodeProtector.NewCode();
        var sealedPayload = CodeProtector.Seal(code, Encoding.UTF8.GetBytes("pi-token-secret"));

        Assert.Equal("pi-token-secret", Encoding.UTF8.GetString(CodeProtector.Open(code, sealedPayload)!));
        Assert.Null(CodeProtector.Open(other, sealedPayload));
        Assert.DoesNotContain("pi-token-secret", Encoding.UTF8.GetString(sealedPayload));
    }

    [Fact]
    public void Tampered_payload_does_not_open()
    {
        var code = CodeProtector.NewCode();
        var sealedPayload = CodeProtector.Seal(code, [1, 2, 3, 4]);
        sealedPayload[^1] ^= 0xFF;

        Assert.Null(CodeProtector.Open(code, sealedPayload));
    }

    [Fact]
    public void Codes_are_256_bit_and_the_hash_is_not_the_code()
    {
        var code = CodeProtector.NewCode();
        Assert.Equal(43, code.Length);
        Assert.NotEqual(code, CodeProtector.Hash(code));
        Assert.Equal(64, CodeProtector.Hash(code).Length);
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~RedirectPolicyTests|FullyQualifiedName~PkceTests|FullyQualifiedName~CodeProtectorTests"`
Expected: build failure.

- [ ] **Step 3: Implement**

`src-web/Auth/RedirectPolicy.cs`:

```csharp
using Microsoft.Extensions.Options;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// Exact redirect URIs only (MCP: "MUST validate exact redirect URIs"), plus the RFC 8252 loopback
/// form native clients use. The allowlist is what keeps a sign-in from reaching anyone else.
/// </summary>
public sealed class RedirectPolicy(IOptions<AuthOptions> auth)
{
    private static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    public bool IsAllowed(string? candidate)
        => Parse(candidate) is { } uri
           && (IsLoopback(uri) || auth.Value.RedirectUris.Select(Parse).Any(allowed => allowed is not null && SameUri(allowed, uri)));

    /// <summary>Whether <paramref name="candidate"/> is one of the client's registered redirect URIs.</summary>
    public static bool Matches(string candidate, IEnumerable<string> registered)
    {
        if (Parse(candidate) is not { } uri)
            return false;
        return registered.Select(Parse).Any(r => r is not null &&
            (IsLoopback(uri) && IsLoopback(r) ? r.Host == uri.Host : SameUri(r, uri)));
    }

    public static bool IsLoopback(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttp && LoopbackHosts.Contains(uri.Host) && uri.AbsolutePath == "/callback" && uri.Query.Length == 0;

    public static Uri? Parse(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048)
            return null;
        // Encoded characters, backslashes and dot segments have no place in a callback URL.
        if (value.Contains('%') || value.Contains('\\') || value.Contains("/./") || value.Contains("/../") || value.EndsWith("/.") || value.EndsWith("/.."))
            return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            return null;
        if (uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || value.Contains('#'))
            return null;
        if (uri.Scheme == Uri.UriSchemeHttps && !uri.IsDefaultPort)
            return null;
        if (uri.Scheme == Uri.UriSchemeHttp && !LoopbackHosts.Contains(uri.Host))
            return null;
        return uri;
    }

    // Host compares case-insensitively (Uri lower-cases it); path and query exactly.
    private static bool SameUri(Uri a, Uri b)
        => a.Scheme == b.Scheme && a.Host == b.Host && a.Port == b.Port && a.AbsolutePath == b.AbsolutePath && a.Query == b.Query;
}
```

`src-web/Auth/Pkce.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>RFC 7636, S256 only.</summary>
public static class Pkce
{
    public static bool IsValidChallenge(string? challenge)
        => challenge is { Length: 43 } && challenge.All(IsUnreservedBase64Url);

    public static bool Verify(string verifier, string challenge)
    {
        if (verifier.Length is < 43 or > 128 || !verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~'))
            return false;
        var computed = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }

    private static bool IsUnreservedBase64Url(char c) => char.IsAsciiLetterOrDigit(c) || c is '-' or '_';
}
```

`src-web/Auth/CodeProtector.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// Authorization codes are 256 random bits. Storage keys them by SHA-256 and holds the payload
/// (which contains the PI token) encrypted under a key derived from the code itself, so reading
/// storage never yields a usable token. Only the client that received the code can open it.
/// </summary>
public static class CodeProtector
{
    private static readonly byte[] Info = "pi-mcp authorization code v1"u8.ToArray();
    private const int NonceSize = 12, TagSize = 16;

    public static string NewCode() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string code) => TokenHash.Of(code);

    public static byte[] Seal(string code, byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(Key(code), TagSize))
            aes.Encrypt(nonce, plaintext, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }

    public static byte[]? Open(string code, byte[] sealedPayload)
    {
        if (sealedPayload.Length < NonceSize + TagSize)
            return null;
        var plain = new byte[sealedPayload.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(Key(code), TagSize);
            aes.Decrypt(sealedPayload.AsSpan(0, NonceSize), sealedPayload.AsSpan(NonceSize + TagSize),
                sealedPayload.AsSpan(NonceSize, TagSize), plain);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    private static byte[] Key(string code)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(code), 32, salt: [], info: Info);
}
```

- [ ] **Step 4: Run the tests**

Run: the Step 2 command.
Expected: PASS.

If `https://claude.ai@evil.com/...` or `https://evil.com@claude.ai/...` passes `Parse`, make sure the userinfo check runs; `Uri.UserInfo` is non-empty for both.

- [ ] **Step 5: Commit**

```bash
git add servers/project-insight/src-web servers/project-insight/tests
git commit -m "feat(web): exact redirect policy, PKCE and sealed authorization codes"
```

### Task B6: Clients: DCR endpoint, CIMD fetcher, client resolver

**Files:**
- Create: `src-web/Auth/PublicAddressOnly.cs`, `src-web/Auth/CimdFetcher.cs`, `src-web/Auth/ClientResolver.cs`, `src-web/Auth/ClientRegistration.cs`
- Modify: `src-web/WebHost.cs`
- Create: `tests/Web/PublicAddressOnlyTests.cs`, `tests/Web/CimdFetcherTests.cs`, `tests/Web/ClientRegistrationTests.cs`

**Interfaces:**
- Consumes: `RedirectPolicy` (B5), `IClientStore` (B2), `AuthOptions` (B1).
- Produces:
  - `OAuthClient(string ClientId, string? Name, IReadOnlyList<string> RedirectUris, bool IsMetadataDocument)`.
  - `ClientResolver.ResolveAsync(string clientId, CancellationToken) → Task<OAuthClient?>`.
  - `CimdFetcher.FetchAsync(string clientId, CancellationToken) → Task<ClientMetadataDocument?>`, where `ClientMetadataDocument(string? ClientName, IReadOnlyList<string> RedirectUris)`.
  - `PublicAddressOnly.IsPublic(IPAddress)` and `PublicAddressOnly.ConnectAsync(SocketsHttpConnectionContext, CancellationToken)`.
  - `ClientRegistration.MapClientRegistration(this WebApplication)` → `POST /register`.
  - Constants `ClientRegistration.MaxClients = 500`, `CimdFetcher.MaxBytes = 5120`, `CimdFetcher.HttpClientName = "cimd"`.

- [ ] **Step 1: Write the failing tests**

`tests/Web/PublicAddressOnlyTests.cs`:

```csharp
using System.Net;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class PublicAddressOnlyTests
{
    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("160.79.104.10")]
    [InlineData("2001:4860:4860::8888")]
    public void Public_addresses_are_allowed(string ip) => Assert.True(PublicAddressOnly.IsPublic(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("168.63.129.16")]
    [InlineData("100.64.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("198.18.0.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("64:ff9b::a00:1")]
    [InlineData("2002:a00:1::")]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2")]
    public void Internal_and_tunnelled_addresses_are_refused(string ip) => Assert.False(PublicAddressOnly.IsPublic(IPAddress.Parse(ip)));
}
```

`tests/Web/CimdFetcherTests.cs`: runs through `WebFactory`, so the fetcher's HttpClient goes to `PiStub`. Use `factory.Pi.Fixed[url] = () => …` to serve documents, and resolve `CimdFetcher` from `factory.Services`.

```csharp
using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class CimdFetcherTests
{
    private const string Url = "https://claude.ai/oauth/claude-code-client-metadata";

    private static HttpResponseMessage Doc(string json, string type = "application/json")
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, type) };

    private static (WebFactory, CimdFetcher) Create()
    {
        var factory = new WebFactory();
        _ = factory.Client();
        return (factory, factory.Services.GetRequiredService<CimdFetcher>());
    }

    [Fact]
    public async Task Valid_document_is_read_and_cached()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => Doc($$"""{"client_id":"{{Url}}","client_name":"Claude Code","redirect_uris":["http://localhost/callback","http://127.0.0.1/callback"]}""");

        var doc = await cimd.FetchAsync(Url, default);
        await cimd.FetchAsync(Url, default);

        Assert.Equal("Claude Code", doc!.ClientName);
        Assert.Equal(2, doc.RedirectUris.Count);
        Assert.Single(factory.Pi.Requests, r => r.Uri.AbsoluteUri == Url);
    }

    [Theory]
    [InlineData("https://evil.example/client.json")]       // host not on the allowlist
    [InlineData("http://claude.ai/client.json")]           // not https
    [InlineData("https://claude.ai:8443/client.json")]     // not port 443
    [InlineData("https://claude.ai/")]                     // no path
    [InlineData("https://user@claude.ai/client.json")]     // userinfo
    public async Task Refused_urls_are_never_fetched(string url)
    {
        var (factory, cimd) = Create();
        using var _ = factory;

        Assert.Null(await cimd.FetchAsync(url, default));
        Assert.Empty(factory.Pi.Requests);
    }

    [Fact]
    public async Task Document_naming_another_client_id_is_refused()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => Doc("""{"client_id":"https://claude.ai/other","redirect_uris":["http://localhost/callback"]}""");
        Assert.Null(await cimd.FetchAsync(Url, default));
    }

    [Fact]
    public async Task Oversized_document_is_refused_even_without_content_length()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        var padding = new string(' ', 6000);
        factory.Pi.Fixed[Url] = () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(
                $$"""{"client_id":"{{Url}}","redirect_uris":["http://localhost/callback"]{{padding}}}""")))
            { Headers = { ContentType = new("application/json") } }
        };
        Assert.Null(await cimd.FetchAsync(Url, default));
    }

    [Fact]
    public async Task Wrong_content_type_is_refused()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => Doc($$"""{"client_id":"{{Url}}","redirect_uris":["http://localhost/callback"]}""", "text/html");
        Assert.Null(await cimd.FetchAsync(Url, default));
    }

    [Fact]
    public async Task Redirect_is_not_followed()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://claude.ai/elsewhere") } };
        Assert.Null(await cimd.FetchAsync(Url, default));
        Assert.DoesNotContain(factory.Pi.Requests, r => r.Uri.AbsolutePath == "/elsewhere");
    }

    [Fact]
    public async Task Failures_are_remembered_for_a_while()
    {
        var (factory, cimd) = Create();
        using var _ = factory;
        factory.Pi.Fixed[Url] = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        await cimd.FetchAsync(Url, default);
        await cimd.FetchAsync(Url, default);

        Assert.Single(factory.Pi.Requests, r => r.Uri.AbsoluteUri == Url);
    }
}
```

The test factory replaces the fetcher's primary handler with the stub, so these tests can't see `AllowAutoRedirect=false`. `Redirect_is_not_followed` instead proves the fetcher treats a 3xx as failure.

`tests/Web/ClientRegistrationTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ProjectInsightMCP.Web.Auth;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class ClientRegistrationTests
{
    [Fact]
    public async Task Registration_with_allowed_redirects_returns_a_client_id_that_resolves()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsJsonAsync("/register", new
        {
            client_name = "Codex",
            redirect_uris = new[] { "http://127.0.0.1/callback" },
            token_endpoint_auth_method = "none"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = body.GetProperty("client_id").GetString()!;
        var client = await factory.Services.GetRequiredService<ClientResolver>().ResolveAsync(clientId, default);
        Assert.Equal(["http://127.0.0.1/callback"], client!.RedirectUris);
        Assert.False(client.IsMetadataDocument);
    }

    [Theory]
    [InlineData("https://evil.example/callback")]
    [InlineData("https://claude.ai/other")]
    public async Task Registration_with_a_redirect_off_the_allowlist_is_refused(string redirect)
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsJsonAsync("/register", new { redirect_uris = new[] { redirect } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_redirect_uri", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Confidential_clients_are_refused()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsJsonAsync("/register", new
        {
            redirect_uris = new[] { "https://claude.ai/api/mcp/auth_callback" },
            token_endpoint_auth_method = "client_secret_basic"
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_client_id_does_not_resolve()
    {
        using var factory = new WebFactory();
        _ = factory.Client();
        Assert.Null(await factory.Services.GetRequiredService<ClientResolver>().ResolveAsync("dcr_made_up", default));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~PublicAddressOnlyTests|FullyQualifiedName~CimdFetcherTests|FullyQualifiedName~ClientRegistrationTests"`
Expected: build failure.

- [ ] **Step 3: Implement**

`src-web/Auth/PublicAddressOnly.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>
/// Second line of defence for client-metadata fetches (the host allowlist is the first): connect only
/// to public addresses, so a DNS answer can't steer our server at internal endpoints.
/// </summary>
public static class PublicAddressOnly
{
    private static readonly (IPAddress Network, int Prefix)[] BlockedV4 =
    [
        (IPAddress.Parse("0.0.0.0"), 8), (IPAddress.Parse("10.0.0.0"), 8), (IPAddress.Parse("100.64.0.0"), 10),
        (IPAddress.Parse("127.0.0.0"), 8), (IPAddress.Parse("169.254.0.0"), 16), (IPAddress.Parse("172.16.0.0"), 12),
        (IPAddress.Parse("192.0.0.0"), 24), (IPAddress.Parse("192.168.0.0"), 16), (IPAddress.Parse("198.18.0.0"), 15),
        (IPAddress.Parse("224.0.0.0"), 4), (IPAddress.Parse("240.0.0.0"), 4),
        (IPAddress.Parse("168.63.129.16"), 32) // Azure platform endpoint: public range, internal service
    ];

    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return !BlockedV4.Any(b => InNetwork(address, b.Network, b.Prefix));

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6None) || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal || address.IsIPv6Teredo)
            return false;
        var bytes = address.GetAddressBytes();
        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return false; // 6to4 embeds an IPv4 address
        if (bytes is [0x00, 0x64, 0xff, 0x9b, 0, 0, 0, 0, 0, 0, 0, 0, ..])
            return IsPublic(new IPAddress(bytes[12..])); // NAT64 well-known prefix
        return true;
    }

    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        if (addresses.Length == 0 || addresses.Any(a => !IsPublic(a)))
            throw new HttpRequestException("Client metadata host does not resolve to public addresses only.");
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool InNetwork(IPAddress address, IPAddress network, int prefix)
    {
        var a = address.GetAddressBytes();
        var n = network.GetAddressBytes();
        for (var bit = 0; bit < prefix; bit++)
        {
            var mask = (byte)(0x80 >> (bit % 8));
            if ((a[bit / 8] & mask) != (n[bit / 8] & mask))
                return false;
        }
        return true;
    }
}
```

`IPAddress.IsLoopback(IPAddress.IPv6Any)` is false, so add an explicit `address.Equals(IPAddress.IPv6Any)` check (`::`), and make sure the test for `"::"` passes. `IPAddress.IsIPv6UniqueLocal` exists from .NET 6; if your SDK lacks it, check `(bytes[0] & 0xFE) == 0xFC`.

`src-web/Auth/CimdFetcher.cs`:

```csharp
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace ProjectInsightMCP.Web.Auth;

public sealed record ClientMetadataDocument(string? ClientName, IReadOnlyList<string> RedirectUris);

/// <summary>
/// Client ID Metadata Documents, from allowlisted hosts only. Anything else is refused before a
/// request is made, so an /authorize link can't turn this server into a fetcher for arbitrary URLs.
/// </summary>
public sealed class CimdFetcher(IHttpClientFactory http, IMemoryCache cache, IOptions<AuthOptions> auth, ILogger<CimdFetcher> logger)
{
    public const string HttpClientName = "cimd";
    public const int MaxBytes = 5 * 1024;
    private static readonly TimeSpan KeepFor = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailedFor = TimeSpan.FromMinutes(5);

    public async Task<ClientMetadataDocument?> FetchAsync(string clientId, CancellationToken ct)
    {
        if (!Uri.TryCreate(clientId, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort
            || url.UserInfo.Length > 0 || url.Fragment.Length > 0 || url.AbsolutePath == "/" || !auth.Value.CimdHosts.Contains(url.Host))
            return null;

        var key = "cimd:" + clientId;
        if (cache.TryGetValue(key, out ClientMetadataDocument? known))
            return known;

        var (doc, maxAge) = await DownloadAsync(url, clientId, ct);
        cache.Set(key, doc, doc is null ? FailedFor : maxAge is { } age && age < KeepFor ? age : KeepFor);
        return doc;
    }

    private async Task<(ClientMetadataDocument?, TimeSpan?)> DownloadAsync(Uri url, string clientId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json")
                return (null, null);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[MaxBytes + 1];
            var read = 0;
            int n;
            while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), ct)) > 0)
                read += n;
            if (read > MaxBytes)
                return (null, null);

            using var json = JsonDocument.Parse(buffer.AsMemory(0, read));
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("client_id", out var id) || id.GetString() != clientId
                || !root.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array)
                return (null, null);
            var redirects = uris.EnumerateArray().Where(u => u.ValueKind == JsonValueKind.String).Select(u => u.GetString()!).ToList();
            if (redirects.Count == 0)
                return (null, null);
            var name = root.TryGetProperty("client_name", out var n2) && n2.ValueKind == JsonValueKind.String ? n2.GetString() : null;
            return (new ClientMetadataDocument(name is { Length: <= 100 } ? name : null, redirects), response.Headers.CacheControl?.MaxAge);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Client metadata fetch failed: {Error}", ex.GetType().Name);
            return (null, null);
        }
    }
}
```

`src-web/Auth/ClientResolver.cs`:

```csharp
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Auth;

public sealed record OAuthClient(string ClientId, string? Name, IReadOnlyList<string> RedirectUris, bool IsMetadataDocument);

/// <summary>A client is either a CIMD URL or a registration stored by /register; nothing else exists.</summary>
public sealed class ClientResolver(IClientStore store, CimdFetcher cimd, RedirectPolicy policy)
{
    public async Task<OAuthClient?> ResolveAsync(string clientId, CancellationToken ct)
    {
        if (clientId.StartsWith("https://", StringComparison.Ordinal))
            return await cimd.FetchAsync(clientId, ct) is { } doc
                ? new OAuthClient(clientId, doc.ClientName, doc.RedirectUris.Where(policy.IsAllowed).ToList(), IsMetadataDocument: true)
                : null;
        return await store.GetAsync(clientId, ct) is { } registered
            ? new OAuthClient(registered.ClientId, registered.ClientName, registered.RedirectUris, IsMetadataDocument: false)
            : null;
    }
}
```

`src-web/Auth/ClientRegistration.cs`:

```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>RFC 7591 dynamic registration for public clients whose redirects are on the allowlist.</summary>
public static class ClientRegistration
{
    public const int MaxClients = 500;

    public static void MapClientRegistration(this WebApplication app)
    {
        app.MapPost("/register", async (HttpRequest request, IClientStore store, RedirectPolicy policy, TimeProvider time, CancellationToken ct) =>
        {
            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(request.Body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                return Error("invalid_client_metadata", "Body must be JSON.");
            }
            if (body.ValueKind != JsonValueKind.Object)
                return Error("invalid_client_metadata", "Body must be a JSON object.");

            if (body.TryGetProperty("token_endpoint_auth_method", out var method) && method.GetString() is not (null or "none"))
                return Error("invalid_client_metadata", "Only public clients (token_endpoint_auth_method none) are supported.");
            if (!body.TryGetProperty("redirect_uris", out var uris) || uris.ValueKind != JsonValueKind.Array)
                return Error("invalid_redirect_uri", "redirect_uris is required.");
            var redirects = uris.EnumerateArray().Select(u => u.ValueKind == JsonValueKind.String ? u.GetString() : null).ToList();
            if (redirects.Count is 0 or > 5 || redirects.Any(r => !policy.IsAllowed(r)))
                return Error("invalid_redirect_uri", "Every redirect URI must be an allowed callback.");
            var name = body.TryGetProperty("client_name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (name is { Length: > 100 })
                return Error("invalid_client_metadata", "client_name is too long.");

            var now = time.GetUtcNow();
            var client = new RegisteredClient("dcr_" + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16)), name, redirects!, now);
            if (!await store.TryAddAsync(client, MaxClients, ct))
                return Results.Json(new { error = "temporarily_unavailable", error_description = "Too many registered clients." }, statusCode: 503);

            return Results.Json(new Dictionary<string, object?>
            {
                ["client_id"] = client.ClientId,
                ["client_id_issued_at"] = now.ToUnixTimeSeconds(),
                ["client_name"] = name,
                ["redirect_uris"] = client.RedirectUris,
                ["token_endpoint_auth_method"] = "none",
                ["grant_types"] = new[] { "authorization_code" },
                ["response_types"] = new[] { "code" }
            }, statusCode: 201);
        });
    }

    private static IResult Error(string error, string description)
        => Results.Json(new { error, error_description = description }, statusCode: 400);
}
```

`WebHost.AddProjectInsightWeb` additions:

```csharp
        services.AddSingleton<RedirectPolicy>();
        services.AddSingleton<CimdFetcher>();
        services.AddSingleton<ClientResolver>();
        services.AddHttpClient(CimdFetcher.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(5))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                UseCookies = false,
                ConnectCallback = PublicAddressOnly.ConnectAsync
            });
```

`UseProjectInsightWeb`: `app.MapClientRegistration();`.

- [ ] **Step 4: Run the tests**

Run: the Step 2 command, then the whole `Web.` filter.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add servers/project-insight/src-web servers/project-insight/tests
git commit -m "feat(web): client registration and allowlisted client metadata documents"
```

### Task B7: Authorize flow: consent page, PI activation, completion

**Files:**
- Create: `src-web/Auth/PiActivationClient.cs`, `src-web/Auth/AuthorizeRequest.cs`, `src-web/Auth/AuthorizePages.cs`, `src-web/Auth/AuthorizeEndpoints.cs`, `src-web/Auth/OAuthHeaders.cs`, `src-web/Auth/CodePayload.cs`, `src-web/wwwroot/oauth.css`
- Modify: `src-web/WebHost.cs`
- Create: `tests/Web/AuthorizeFlow.cs` (test helper), `tests/Web/AuthorizeTests.cs`

**Interfaces:**
- Consumes:
  - `ClientResolver`, `OAuthClient` (B6);
  - `RedirectPolicy`, `Pkce`, `CodeProtector` (B5);
  - `ISessionStore`, `ICodeStore`, `SignInSession`, `SessionStatus`, `StoredCode` (B2);
  - `WebOptions.IsResource`, `AuthOptions` (B1).
- Produces:
  - `IPiActivationClient`:
    - `Task<PiActivationStart?> StartAsync(string clientDevice, CancellationToken)`;
    - `Task<PiActivationResult> CompleteAsync(string activationId, string code, int lifetimeMinutes, CancellationToken)`.
  - Records: `PiActivationStart(string ActivationId, Uri ActivationUrl)`; `PiActivationResult(PiActivationOutcome Outcome, string? ApiToken, string? UserId, DateTimeOffset? ExpiresAt)`.
  - Enum `PiActivationOutcome { Ok, WrongCode, Unavailable }`.
  - `CodePayload(string PiToken, DateTimeOffset ExpiresAt, string ClientId, string RedirectUri, string CodeChallenge, string? Resource, string PiUserId)`, serialised as JSON before sealing.
  - Endpoints:
    - `GET /authorize`;
    - `POST /authorize/start` (form, the same fields as the GET query);
    - `POST /authorize/complete` (form fields `session_id`, `activation_code`);
    - `GET /oauth.css`.
  - Cookie `__Host-pi_auth`; `AuthorizeEndpoints.MaxAttempts = 5`; `AuthorizeEndpoints.MaxOutstandingSessions = 200`; session lifetime 15 minutes; code lifetime 5 minutes.
  - `OAuthHeaders.Apply(HttpResponse)` adds `Cache-Control: no-store`, `Pragma: no-cache`, the CSP, `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`.

- [ ] **Step 1: Write a flow helper and the failing tests**

`tests/Web/AuthorizeFlow.cs`: drives the browser side of the flow with a cookie-keeping client.

```csharp
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>Plays the browser: consent page, start, activation page, complete.</summary>
public sealed partial class AuthorizeFlow(WebFactory factory)
{
    public const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    public const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGF3cMjXmM";
    public const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";

    public HttpClient Browser { get; } = factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = WebFactory.BaseAddress,
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    public static Dictionary<string, string?> Query(string clientId, string redirect = ClaudeCallback, string? resource = "https://mcp.test/mcp") => new()
    {
        ["response_type"] = "code",
        ["client_id"] = clientId,
        ["redirect_uri"] = redirect,
        ["state"] = "state-123",
        ["code_challenge"] = Challenge,
        ["code_challenge_method"] = "S256",
        ["scope"] = "pi",
        ["resource"] = resource
    };

    public Task<HttpResponseMessage> GetAuthorizeAsync(Dictionary<string, string?> query)
        => Browser.GetAsync(QueryHelpers.AddQueryString("/authorize", query.Where(kv => kv.Value is not null)!));

    /// <summary>Posts the consent form and returns the activation page's session id.</summary>
    public async Task<string> StartAsync(Dictionary<string, string?> query)
    {
        var response = await Browser.PostAsync("/authorize/start",
            new FormUrlEncodedContent(query.Where(kv => kv.Value is not null)!));
        var html = await response.Content.ReadAsStringAsync();
        return SessionField().Match(html).Groups[1].Value;
    }

    public Task<HttpResponseMessage> CompleteAsync(string sessionId, string code)
        => Browser.PostAsync("/authorize/complete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["session_id"] = sessionId,
            ["activation_code"] = code
        }));

    /// <summary>The whole browser side for a registered client; returns the code from the redirect.</summary>
    public async Task<string> CodeForAsync(string clientId, string redirect = ClaudeCallback)
    {
        var session = await StartAsync(Query(clientId, redirect));
        var response = await CompleteAsync(session, PiStub.ActivationCode);
        if (response.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Expected a redirect, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["code"]!;
    }

    public async Task<string> RegisterAsync(string redirect = ClaudeCallback)
    {
        var response = await Browser.PostAsync("/register", JsonContent.Create(new { redirect_uris = new[] { redirect } }));
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("client_id").GetString()!;
    }

    [GeneratedRegex("name=\"session_id\" value=\"([^\"]+)\"")]
    private static partial Regex SessionField();
}
```

(It needs `using System.Net.Http.Json; using System.Text.Json;`.)

`tests/Web/AuthorizeTests.cs`:

```csharp
using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class AuthorizeTests
{
    [Fact]
    public async Task Consent_page_names_the_redirect_host_and_starts_nothing_at_pi()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();

        var response = await flow.GetAuthorizeAsync(AuthorizeFlow.Query(clientId));
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<strong>claude.ai</strong>", html);
        Assert.Contains("If someone sent you this link, close this page.", html);
        Assert.Empty(factory.Pi.ToPath("/api/device-activation/create-request"));
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Unregistered_redirect_gets_an_error_page_not_a_redirect()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync("http://127.0.0.1/callback");

        var response = await flow.GetAuthorizeAsync(AuthorizeFlow.Query(clientId, redirect: AuthorizeFlow.ClaudeCallback));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Unknown_client_gets_an_error_page_not_a_redirect()
    {
        using var factory = new WebFactory();
        var response = await new AuthorizeFlow(factory).GetAuthorizeAsync(AuthorizeFlow.Query("dcr_made_up"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("code_challenge_method", "plain", "invalid_request")]
    [InlineData("code_challenge", "short", "invalid_request")]
    [InlineData("response_type", "token", "unsupported_response_type")]
    [InlineData("scope", "admin", "invalid_scope")]
    [InlineData("resource", "https://other.test/mcp", "invalid_target")]
    public async Task Bad_parameters_go_back_to_the_client_with_state_and_iss(string key, string value, string error)
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var query = AuthorizeFlow.Query(await flow.RegisterAsync());
        query[key] = value;

        var response = await flow.GetAuthorizeAsync(query);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var back = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.Equal(error, back["error"]);
        Assert.Equal("state-123", back["state"]);
        Assert.Equal("https://mcp.test", back["iss"]);
        Assert.StartsWith(AuthorizeFlow.ClaudeCallback, response.Headers.Location.ToString());
    }

    [Fact]
    public async Task Start_creates_a_pi_activation_labelled_with_the_redirect_host_and_sets_the_cookie()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var response = await flow.Browser.PostAsync("/authorize/start",
            new FormUrlEncodedContent(AuthorizeFlow.Query(await flow.RegisterAsync()).Where(kv => kv.Value is not null)!));

        var create = Assert.Single(factory.Pi.ToPath("/api/device-activation/create-request"));
        Assert.Contains("MCP connector (claude.ai)", create.Body);
        var cookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("__Host-pi_auth=", cookie);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://pi.test/device/activate/act-1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Correct_code_redirects_with_code_state_and_iss_and_asks_pi_for_a_90_day_token()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var response = await flow.CompleteAsync(session, PiStub.ActivationCode);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var back = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.False(string.IsNullOrEmpty(back["code"]));
        Assert.Equal("state-123", back["state"]);
        Assert.Equal("https://mcp.test", back["iss"]);
        Assert.DoesNotContain(PiStub.TokenA, response.Headers.Location.ToString());
        Assert.Contains("timeoutMinutes=129600", Assert.Single(factory.Pi.ToPath("/api/device-activation/complete-request")).Uri.Query);
    }

    [Fact]
    public async Task Wrong_codes_re_render_and_the_fifth_ends_the_session()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        for (var i = 1; i < 5; i++)
        {
            var retry = await flow.CompleteAsync(session, "WRONG");
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.Contains("didn't work", await retry.Content.ReadAsStringAsync());
        }
        var last = await flow.CompleteAsync(session, "WRONG");
        Assert.Contains("Too many incorrect codes", await last.Content.ReadAsStringAsync());

        var after = await flow.CompleteAsync(session, PiStub.ActivationCode);
        Assert.NotEqual(HttpStatusCode.Redirect, after.StatusCode);
    }

    [Fact]
    public async Task Completion_from_another_browser_is_refused()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var other = new AuthorizeFlow(factory); // fresh cookie jar
        var response = await other.CompleteAsync(session, PiStub.ActivationCode);

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(factory.Pi.ToPath("/api/device-activation/complete-request"));
    }

    [Fact]
    public async Task Double_submit_gives_exactly_one_redirect_and_a_plain_page_for_the_other()
    {
        using var factory = new WebFactory();
        factory.Pi.ToolCallDelay = TimeSpan.Zero;
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var responses = await Task.WhenAll(flow.CompleteAsync(session, PiStub.ActivationCode), flow.CompleteAsync(session, PiStub.ActivationCode));

        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Redirect);
        Assert.Single(responses, r => r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.BadRequest);
        Assert.DoesNotContain(responses, r => (int)r.StatusCode >= 500);
    }

    [Fact]
    public async Task User_not_on_the_allowlist_gets_no_code()
    {
        using var factory = new WebFactory();
        factory.Pi.ActivationResult = (PiStub.StrangerToken, "0f0e2a4c-9999-4a4a-9b9b-cccccccccccc", "2027-01-28T12:00:00");
        var flow = new AuthorizeFlow(factory);
        var session = await flow.StartAsync(AuthorizeFlow.Query(await flow.RegisterAsync()));

        var response = await flow.CompleteAsync(session, PiStub.ActivationCode);

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("isn't open to your Project Insight account", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Activation_link_on_another_host_is_not_rendered()
    {
        using var factory = new WebFactory();
        factory.Pi.Fixed["https://pi.test/api/device-activation/create-request"] = () =>
            PiStub.Json(new { DeviceActivationId = "act-2", DeviceActivationRequestUrl = "https://evil.example/activate" });
        var flow = new AuthorizeFlow(factory);
        var response = await flow.Browser.PostAsync("/authorize/start",
            new FormUrlEncodedContent(AuthorizeFlow.Query(await flow.RegisterAsync()).Where(kv => kv.Value is not null)!));

        Assert.DoesNotContain("evil.example", await response.Content.ReadAsStringAsync());
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web.AuthorizeTests"`
Expected: FAIL (404s).

- [ ] **Step 3: Implement the activation client**

`src-web/Auth/PiActivationClient.cs`:

```csharp
using System.Text;
using System.Text.Json;
using ProjectInsightMCP.Services;

namespace ProjectInsightMCP.Web.Auth;

public sealed record PiActivationStart(string ActivationId, Uri ActivationUrl);
public enum PiActivationOutcome { Ok, WrongCode, Unavailable }
public sealed record PiActivationResult(PiActivationOutcome Outcome, string? ApiToken = null, string? UserId = null, DateTimeOffset? ExpiresAt = null);

public interface IPiActivationClient
{
    Task<PiActivationStart?> StartAsync(string clientDevice, CancellationToken ct);
    Task<PiActivationResult> CompleteAsync(string activationId, string code, int lifetimeMinutes, CancellationToken ct);
}

/// <summary>
/// PI device activation for the OAuth front. Stateless, unlike Core's DeviceActivationService: no
/// pending state, no browser, no credential file.
/// </summary>
public sealed class PiActivationClient(IHttpClientFactory http, IConfiguration config, ILogger<PiActivationClient> logger) : IPiActivationClient
{
    public const string HttpClientName = "pi-activation";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private string BaseUrl => config["ProjectInsight:BaseUrl"]!.TrimEnd('/');

    public async Task<PiActivationStart?> StartAsync(string clientDevice, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/device-activation/create-request")
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    clientAgent = "ProjectInsight-MCP-OAuth",
                    clientPlatform = "MCP",
                    clientDevice
                }), Encoding.UTF8, "application/json")
            };
            using var response = await http.CreateClient(HttpClientName).SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("PI create-request failed with HTTP {Status}", (int)response.StatusCode);
                return null;
            }
            var body = JsonSerializer.Deserialize<DeviceActivationResponse>(await response.Content.ReadAsStringAsync(ct), Json);
            return body is { DeviceActivationId: { Length: > 0 } id, DeviceActivationRequestUrl: { } url } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? new PiActivationStart(id, uri)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("PI create-request failed: {Error}", ex.GetType().Name);
            return null;
        }
    }

    public async Task<PiActivationResult> CompleteAsync(string activationId, string code, int lifetimeMinutes, CancellationToken ct)
    {
        try
        {
            // PI takes these only as a query string; telemetry strips query strings (QueryRedactionProcessor).
            var url = $"{BaseUrl}/device-activation/complete-request?activationRequestId={Uri.EscapeDataString(activationId)}" +
                      $"&activationCode={Uri.EscapeDataString(code)}&clientAgent=ProjectInsight-MCP-OAuth&timeoutMinutes={lifetimeMinutes}";
            using var response = await http.CreateClient(HttpClientName).GetAsync(url, ct);
            if ((int)response.StatusCode >= 500 || response.StatusCode is System.Net.HttpStatusCode.RequestTimeout or System.Net.HttpStatusCode.TooManyRequests)
                return new PiActivationResult(PiActivationOutcome.Unavailable);
            if (!response.IsSuccessStatusCode)
                return new PiActivationResult(PiActivationOutcome.WrongCode);
            var body = JsonSerializer.Deserialize<DeviceActivationCompleteResponse>(await response.Content.ReadAsStringAsync(ct), Json);
            if (body is not { ApiToken: { Length: > 0 } token, UserId: { Length: > 0 } userId })
                return new PiActivationResult(PiActivationOutcome.WrongCode);
            // PI's expiry comes without an offset; it is UTC.
            DateTimeOffset? expires = body.ApiTokenExpirationDateTimeUTC is { } at
                ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc))
                : null;
            return new PiActivationResult(PiActivationOutcome.Ok, token, userId, expires);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("PI complete-request failed: {Error}", ex.GetType().Name);
            return new PiActivationResult(PiActivationOutcome.Unavailable);
        }
    }
}
```

- [ ] **Step 4: Implement request validation, pages and endpoints**

`src-web/Auth/CodePayload.cs`:

```csharp
namespace ProjectInsightMCP.Web.Auth;

/// <summary>What an authorization code stands for. Stored only sealed under the code (CodeProtector).</summary>
public sealed record CodePayload(string PiToken, DateTimeOffset ExpiresAt, string ClientId, string RedirectUri,
    string CodeChallenge, string? Resource, string PiUserId);
```

`src-web/Auth/AuthorizeRequest.cs`:
- A record `AuthorizeRequest(OAuthClient Client, string RedirectUri, string? State, string CodeChallenge, string? Resource)`.
- A static `ValidateAsync(IReadOnlyDictionary<string, string?> p, ClientResolver clients, RedirectPolicy policy, WebOptions web, CancellationToken ct)`. It returns `(AuthorizeRequest? Request, IResult? Failure)`, applying these checks **in this order**:

  1. `client_id` is missing, or doesn't resolve → `AuthorizePages.Error(400, "This app isn't known to this server.")`. No redirect.
  2. `redirect_uri` fails `policy.IsAllowed`, or fails `RedirectPolicy.Matches(redirect, client.RedirectUris)` → `AuthorizePages.Error(400, "This app's return address isn't allowed.")`. No redirect.
  3. From here, failures redirect to `redirect_uri` with `error`, `error_description`, `state` and `iss = web.Issuer`:
     - `response_type != "code"` → `unsupported_response_type`;
     - `code_challenge_method != "S256"` or `!Pkce.IsValidChallenge(code_challenge)` → `invalid_request`;
     - `scope` present, with any space-separated value other than `pi` or `offline_access` → `invalid_scope`;
     - `resource` present and `!web.IsResource(resource)` → `invalid_target`.

  Error redirect URL: `redirect + (redirect.Contains('?') ? "&" : "?") + query`, built with `QueryHelpers.AddQueryString`.

`src-web/Auth/AuthorizePages.cs`: static methods returning `IResult` (`Results.Content(html, "text/html; charset=utf-8", statusCode: …)`). Every dynamic value goes through `HtmlEncoder.Default.Encode`. Pages link `/oauth.css` and contain no `<script>` and no inline `style`.
- `Consent(AuthorizeRequest r, IReadOnlyDictionary<string, string?> fields)` contains:
  - the heading "Connect Project Insight";
  - `<p><strong>{redirect host}</strong> is asking to use your Project Insight account.</p>`;
  - for CIMD clients, `<p>App identity: <strong>{CIMD host}</strong></p>`;
  - if a client name is known, `<p>It calls itself “{name}” (unverified).</p>`;
  - for loopback redirects, `<p class="warn">This app runs on your own computer (localhost). Only continue if you started it yourself, for example Claude Code or Codex.</p>`;
  - always, `<p class="warn">Only continue if you just clicked Connect in Claude or ChatGPT yourself. If someone sent you this link, close this page.</p>`;
  - a `<form method="post" action="/authorize/start">` with hidden inputs for `response_type`, `client_id`, `redirect_uri`, `state`, `code_challenge`, `code_challenge_method`, `scope` and `resource` (each only if present), and `<button>Sign in to Project Insight</button>`.
- `Activation(string sessionId, Uri activationUrl, string? error)` contains:
  - "Sign in to Project Insight";
  - an ordered list: `<a href="{url}" target="_blank" rel="noopener noreferrer">Open Project Insight</a> and sign in`, then "Copy the activation code it shows and paste it here.";
  - the error, if any, in `<p class="error">`;
  - `<form method="post" action="/authorize/complete">` with `<input type="hidden" name="session_id" value="{id}">`, `<label>Activation code <input name="activation_code" autocomplete="off" required maxlength="64"></label>` and `<button>Continue</button>`.
- `Error(int status, string message)`: a heading "Couldn't connect", then the message.

`src-web/wwwroot/oauth.css`: about 20 lines of plain styling (a centred card, system font, `.warn` amber, `.error` red). Serve it with `app.MapGet("/oauth.css", …)`, reading the file once at startup, with content type `text/css`. Or use `app.UseStaticFiles()`; either is fine.

`src-web/Auth/OAuthHeaders.cs`:

```csharp
namespace ProjectInsightMCP.Web.Auth;

public static class OAuthHeaders
{
    // No form-action: it would block the redirect that follows the POST to /authorize/complete.
    private const string Csp = "default-src 'self'; frame-ancestors 'none'";

    public static void Apply(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers.Pragma = "no-cache";
        response.Headers.ContentSecurityPolicy = Csp;
        response.Headers.XContentTypeOptions = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
    }

    /// <summary>Endpoint filter for /authorize, /authorize/*, /token and /register.</summary>
    public static RouteHandlerBuilder WithOAuthHeaders(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter(async (context, next) =>
        {
            OAuthHeaders.Apply(context.HttpContext.Response);
            return await next(context);
        });
}
```

Apply `.WithOAuthHeaders()` to `/register` from B6 as well.

`src-web/Auth/AuthorizeEndpoints.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Auth;

public static class AuthorizeEndpoints
{
    public const int MaxAttempts = 5;
    public const int MaxOutstandingSessions = 200;
    public const string CookieName = "__Host-pi_auth";
    private static readonly TimeSpan SessionLife = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CodeLife = TimeSpan.FromMinutes(5);

    public static void MapAuthorize(this WebApplication app)
    {
        app.MapGet("/authorize", async (HttpRequest http, ClientResolver clients, RedirectPolicy policy, IOptions<WebOptions> web, CancellationToken ct) =>
        {
            var fields = http.Query.ToDictionary(q => q.Key, q => (string?)q.Value.ToString());
            var (request, failure) = await AuthorizeRequest.ValidateAsync(fields, clients, policy, web.Value, ct);
            return failure ?? AuthorizePages.Consent(request!, fields);
        }).WithOAuthHeaders();

        app.MapPost("/authorize/start", async (HttpContext http, ClientResolver clients, RedirectPolicy policy, IOptions<WebOptions> web,
            IPiActivationClient pi, ISessionStore sessions, IConfiguration config, TimeProvider time, CancellationToken ct) =>
        {
            var form = await http.Request.ReadFormAsync(ct);
            var fields = form.ToDictionary(f => f.Key, f => (string?)f.Value.ToString());
            var (request, failure) = await AuthorizeRequest.ValidateAsync(fields, clients, policy, web.Value, ct);
            if (failure is not null)
                return failure;

            var redirectHost = new Uri(request!.RedirectUri).Host;
            var activation = await pi.StartAsync($"MCP connector ({redirectHost})", ct);
            var piHost = new Uri(config["ProjectInsight:BaseUrl"]!).Host;
            if (activation is null || activation.ActivationUrl.Scheme != Uri.UriSchemeHttps || activation.ActivationUrl.Host != piHost)
                return AuthorizePages.Error(502, "Project Insight didn't start the sign-in. Try again in a moment.");

            var cookie = CodeProtector.NewCode();
            var now = time.GetUtcNow();
            var session = new SignInSession
            {
                Id = CodeProtector.NewCode(),
                ClientId = request.Client.ClientId,
                RedirectUri = request.RedirectUri,
                State = request.State,
                CodeChallenge = request.CodeChallenge,
                Resource = request.Resource,
                PiActivationId = activation.ActivationId,
                PiActivationUrl = activation.ActivationUrl.AbsoluteUri,
                CookieHash = TokenHash.Of(cookie),
                ExpiresAt = now + SessionLife
            };
            if (!await sessions.TryCreateAsync(session, MaxOutstandingSessions, now, ct))
                return AuthorizePages.Error(503, "Too many sign-ins in progress. Try again in a few minutes.");

            http.Response.Cookies.Append(CookieName, cookie, new CookieOptions
            {
                Secure = true, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/", MaxAge = SessionLife
            });
            return AuthorizePages.Activation(session.Id, activation.ActivationUrl, error: null);
        }).WithOAuthHeaders();

        app.MapPost("/authorize/complete", async (HttpContext http, ISessionStore sessions, ICodeStore codes, IPiActivationClient pi,
            IOptions<WebOptions> web, IOptions<AuthOptions> auth, TimeProvider time, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("ProjectInsightMCP.Web.Authorize");
            var form = await http.Request.ReadFormAsync(ct);
            var sessionId = form["session_id"].ToString();
            var code = form["activation_code"].ToString().Trim();
            var now = time.GetUtcNow();

            var session = sessionId.Length > 0 ? await sessions.GetAsync(sessionId, ct) : null;
            if (session is null || session.ExpiresAt <= now)
                return AuthorizePages.Error(400, "This sign-in has expired. Start again from Claude or ChatGPT.");
            var cookie = http.Request.Cookies[CookieName];
            if (cookie is null || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(TokenHash.Of(cookie)), Encoding.ASCII.GetBytes(session.CookieHash)))
                return AuthorizePages.Error(400, "This sign-in was started in a different browser. Start again from Claude or ChatGPT.");
            if (session.Status != SessionStatus.Pending)
                return AuthorizePages.Error(409, "This sign-in is already finished or in progress. Return to Claude or ChatGPT.");
            var activationUrl = new Uri(session.PiActivationUrl);
            if (code.Length is 0 or > 64)
                return AuthorizePages.Activation(session.Id, activationUrl, "Paste the activation code Project Insight shows.");

            // Claim the session before calling PI, so a double submit completes it exactly once.
            var claimed = session with { Status = SessionStatus.Completing };
            if (!await sessions.TryReplaceAsync(claimed, ct))
                return AuthorizePages.Error(409, "This sign-in is already finished or in progress. Return to Claude or ChatGPT.");
            claimed = await sessions.GetAsync(session.Id, ct) ?? claimed;

            var result = await pi.CompleteAsync(session.PiActivationId, code, auth.Value.PiTokenLifetimeDays * 24 * 60, ct);
            if (result.Outcome != PiActivationOutcome.Ok)
            {
                var attempts = claimed.Attempts + (result.Outcome == PiActivationOutcome.WrongCode ? 1 : 0);
                if (attempts >= MaxAttempts)
                {
                    await sessions.DeleteAsync(session.Id, ct);
                    logger.LogWarning("Sign-in {Session} ended after {Attempts} wrong activation codes", TokenHash.Of(session.Id)[..12], attempts);
                    return AuthorizePages.Error(400, "Too many incorrect codes. Start again from Claude or ChatGPT.");
                }
                await sessions.TryReplaceAsync(claimed with { Status = SessionStatus.Pending, Attempts = attempts }, ct);
                if (result.Outcome == PiActivationOutcome.WrongCode)
                    logger.LogWarning("Wrong activation code for sign-in {Session} (attempt {Attempt})", TokenHash.Of(session.Id)[..12], attempts);
                return AuthorizePages.Activation(session.Id, activationUrl, result.Outcome == PiActivationOutcome.WrongCode
                    ? "That code didn't work. Check it and try again."
                    : "Project Insight didn't respond. Try again in a moment.");
            }

            await sessions.DeleteAsync(session.Id, ct);
            if (!auth.Value.PiUserIds.Contains(result.UserId!))
                return AuthorizePages.Error(403, "This server isn't open to your Project Insight account.");

            var authCode = CodeProtector.NewCode();
            var payload = new CodePayload(result.ApiToken!,
                result.ExpiresAt ?? now.AddDays(auth.Value.PiTokenLifetimeDays),
                session.ClientId, session.RedirectUri, session.CodeChallenge, session.Resource, result.UserId!);
            await codes.SaveAsync(new StoredCode(CodeProtector.Hash(authCode),
                CodeProtector.Seal(authCode, JsonSerializer.SerializeToUtf8Bytes(payload)), now + CodeLife), ct);

            return Results.Redirect(QueryHelpers.AddQueryString(session.RedirectUri, new Dictionary<string, string?>
            {
                ["code"] = authCode,
                ["state"] = session.State,
                ["iss"] = web.Value.Issuer
            }));
        }).WithOAuthHeaders();
    }
}
```

`QueryHelpers.AddQueryString` with a null value: filter the `state` entry out when it's null. The dictionary overload throws on null values in some versions; build the dictionary without it.

`WebHost` additions:
- `AddProjectInsightWeb`:
  - `services.AddHttpClient(PiActivationClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(8));`
  - `services.AddSingleton<IPiActivationClient, PiActivationClient>();`
- `UseProjectInsightWeb`:
  - `app.MapAuthorize();`
  - the stylesheet route.

- [ ] **Step 5: Run the tests**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web."`
Expected: PASS. `timeoutMinutes=129600` is 90 days.

- [ ] **Step 6: Commit**

```bash
git add servers/project-insight/src-web servers/project-insight/tests
git commit -m "feat(web): authorize flow with consent page, browser-bound sessions and PI activation"
```

### Task B8: Token endpoint and end-to-end sign-in

**Files:**
- Create: `src-web/Auth/TokenEndpoint.cs`
- Modify: `src-web/WebHost.cs`
- Create: `tests/Web/TokenTests.cs`, `tests/Web/SignInEndToEndTests.cs`

**Interfaces:**
- Consumes: `ICodeStore`, `CodeProtector`, `CodePayload`, `Pkce`, `WebOptions.IsResource` (B1–B7); `AuthorizeFlow` (B7 test helper).
- Produces: `POST /token`.

- [ ] **Step 1: Write the failing tests**

`tests/Web/TokenTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class TokenTests
{
    private static FormUrlEncodedContent Form(string code, string clientId, string? verifier = AuthorizeFlow.Verifier,
        string redirect = AuthorizeFlow.ClaudeCallback, string? resource = "https://mcp.test/mcp")
        => new(new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = verifier,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect,
            ["resource"] = resource
        }.Where(kv => kv.Value is not null)!);

    private static async Task<(WebFactory, AuthorizeFlow, string ClientId, string Code)> SignedInAsync()
    {
        var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        return (factory, flow, clientId, await flow.CodeForAsync(clientId));
    }

    [Fact]
    public async Task Code_and_verifier_give_the_pi_token_with_its_expiry_and_no_refresh_token()
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var _ = factory;
        var response = await flow.Browser.PostAsync("/token", Form(code, clientId));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(PiStub.TokenA, json.GetProperty("access_token").GetString());
        Assert.Equal("Bearer", json.GetProperty("token_type").GetString());
        Assert.False(json.TryGetProperty("refresh_token", out _));
        var expected = (new DateTimeOffset(2027, 1, 28, 12, 0, 0, TimeSpan.Zero) - DateTimeOffset.UtcNow).TotalSeconds;
        Assert.InRange(json.GetProperty("expires_in").GetInt32(), expected - 60, expected + 60);
    }

    [Fact]
    public async Task A_code_works_once()
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var _ = factory;
        Assert.Equal(HttpStatusCode.OK, (await flow.Browser.PostAsync("/token", Form(code, clientId))).StatusCode);

        var again = await flow.Browser.PostAsync("/token", Form(code, clientId));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_grant", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Wrong_verifier_is_invalid_grant_and_burns_the_code()
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var _ = factory;
        var wrong = await flow.Browser.PostAsync("/token", Form(code, clientId, verifier: new string('a', 43)));
        Assert.Equal("invalid_grant", (await wrong.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await flow.Browser.PostAsync("/token", Form(code, clientId))).StatusCode);
    }

    [Theory]
    [InlineData("client")]
    [InlineData("redirect")]
    public async Task Mismatched_client_or_redirect_is_invalid_grant(string which)
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var _ = factory;
        var response = await flow.Browser.PostAsync("/token", which == "client"
            ? Form(code, "dcr_someone_else")
            : Form(code, clientId, redirect: "https://chatgpt.com/connector_platform_oauth_redirect"));

        Assert.Equal("invalid_grant", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("https://mcp.test/mcp/")]
    [InlineData("https://MCP.TEST/mcp")]
    public async Task Resource_with_a_trailing_slash_or_upper_case_host_is_accepted(string resource)
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var _ = factory;
        Assert.Equal(HttpStatusCode.OK, (await flow.Browser.PostAsync("/token", Form(code, clientId, resource: resource))).StatusCode);
    }

    [Fact]
    public async Task Other_resource_is_invalid_target()
    {
        var (factory, flow, clientId, code) = await SignedInAsync();
        using var _ = factory;
        var response = await flow.Browser.PostAsync("/token", Form(code, clientId, resource: "https://other.test/mcp"));
        Assert.Equal("invalid_target", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Refresh_token_grant_is_unsupported()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = "x"
        }));
        Assert.Equal("unsupported_grant_type", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Pi_expiry_without_a_utc_marker_is_read_as_utc()
    {
        using var factory = new WebFactory();
        var expiry = DateTime.UtcNow.AddDays(90);
        factory.Pi.ActivationResult = (PiStub.TokenA, PiStub.UserA, expiry.ToString("yyyy-MM-ddTHH:mm:ss"));
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        var code = await flow.CodeForAsync(clientId);

        var json = await (await flow.Browser.PostAsync("/token", Form(code, clientId))).Content.ReadFromJsonAsync<JsonElement>();

        Assert.InRange(json.GetProperty("expires_in").GetInt32(), 90 * 86400 - 120, 90 * 86400 + 5);
    }
}
```

`tests/Web/SignInEndToEndTests.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class SignInEndToEndTests
{
    [Fact]
    public async Task Registered_client_signs_in_and_calls_tools_with_the_token()
    {
        using var factory = new WebFactory();
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync("http://127.0.0.1/callback");
        var code = await flow.CodeForAsync(clientId, "http://127.0.0.1:53111/callback");
        var token = await TokenAsync(flow, code, clientId, "http://127.0.0.1:53111/callback");

        await using var mcp = await factory.ConnectAsync(token);
        var me = await mcp.CallToolAsync("get_current_user", new Dictionary<string, object?>());

        Assert.NotEqual(true, me.IsError);
    }

    [Fact]
    public async Task Metadata_document_client_signs_in()
    {
        using var factory = new WebFactory();
        const string clientId = "https://claude.ai/oauth/claude-code-client-metadata";
        factory.Pi.Fixed[clientId] = () => PiStub.Json(new
        {
            client_id = clientId,
            client_name = "Claude Code",
            redirect_uris = new[] { "http://localhost/callback", "http://127.0.0.1/callback" }
        });
        var flow = new AuthorizeFlow(factory);
        var code = await flow.CodeForAsync(clientId, "http://localhost:3118/callback");
        var token = await TokenAsync(flow, code, clientId, "http://localhost:3118/callback");

        await using var mcp = await factory.ConnectAsync(token);
        Assert.NotEmpty(await mcp.ListToolsAsync());
    }

    private static async Task<string> TokenAsync(AuthorizeFlow flow, string code, string clientId, string redirect)
    {
        var response = await flow.Browser.PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["code_verifier"] = AuthorizeFlow.Verifier,
            ["client_id"] = clientId,
            ["redirect_uri"] = redirect
        }));
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
    }
}
```

`PiStub.Json` returns `application/json; charset=utf-8`. The CIMD fetcher compares `MediaType`, so the charset is fine.

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web.TokenTests|FullyQualifiedName~Web.SignInEndToEndTests"`
Expected: FAIL (`/token` is 404).

- [ ] **Step 3: Implement**

`src-web/Auth/TokenEndpoint.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Options;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Auth;

/// <summary>Authorization code → the PI token (pass-through). No refresh tokens.</summary>
public static class TokenEndpoint
{
    public static void MapToken(this WebApplication app)
    {
        app.MapPost("/token", async (HttpRequest request, ICodeStore codes, IOptions<WebOptions> web, IOptions<AuthOptions> auth,
            TimeProvider time, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
                return Error("invalid_request", "Use application/x-www-form-urlencoded.");
            var form = await request.ReadFormAsync(ct);
            if (form["grant_type"] != "authorization_code")
                return Error("unsupported_grant_type", "Only authorization_code is supported.");
            string code = form["code"].ToString(), verifier = form["code_verifier"].ToString(),
                   clientId = form["client_id"].ToString(), redirect = form["redirect_uri"].ToString(), resource = form["resource"].ToString();
            if (code.Length == 0 || verifier.Length == 0 || clientId.Length == 0 || redirect.Length == 0)
                return Error("invalid_request", "code, code_verifier, client_id and redirect_uri are required.");

            // Taken (deleted) before any check, so a wrong verifier burns the code and can't be retried.
            var stored = await codes.TryTakeAsync(CodeProtector.Hash(code), ct);
            var now = time.GetUtcNow();
            if (stored is null || stored.ExpiresAt <= now || CodeProtector.Open(code, stored.Payload) is not { } plain)
                return Error("invalid_grant", "The code is invalid, expired or already used.");
            var payload = JsonSerializer.Deserialize<CodePayload>(plain)!;
            if (payload.ClientId != clientId || payload.RedirectUri != redirect)
                return Error("invalid_grant", "The code was issued to another client or redirect URI.");
            if (resource.Length > 0 && !web.Value.IsResource(resource))
                return Error("invalid_target", "Unknown resource.");
            if (!Pkce.Verify(verifier, payload.CodeChallenge))
                return Error("invalid_grant", "PKCE verification failed.");

            var expiresIn = (long)Math.Max(60, (payload.ExpiresAt - now).TotalSeconds);
            return Results.Json(new Dictionary<string, object>
            {
                ["access_token"] = payload.PiToken,
                ["token_type"] = "Bearer",
                ["expires_in"] = expiresIn
            });
        }).WithOAuthHeaders();
    }

    private static IResult Error(string error, string description)
        => Results.Json(new { error, error_description = description }, statusCode: 400);
}
```

`WebHost.UseProjectInsightWeb`: `app.MapToken();`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web."`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add servers/project-insight/src-web servers/project-insight/tests
git commit -m "feat(web): token endpoint returning the PI token, with end-to-end sign-in tests"
```

### Task B9: No echo: scrub PI errors, tool results, logs and telemetry

**Files:**
- Create: `src-web/Security/PiErrorScrubbingHandler.cs`, `src-web/Security/ToolResultScrubber.cs`, `src-web/Security/QueryRedactionProcessor.cs`
- Modify: `src-web/WebHost.cs`, `tests/Web/PiStub.cs` (two more failure paths)
- Create: `tests/Web/CapturingLoggerProvider.cs`, `tests/Web/NoEchoTests.cs`, `tests/Web/QueryRedactionProcessorTests.cs`

**Interfaces:**
- Produces:
  - `PiErrorScrubbingHandler : DelegatingHandler`, with `static string Scrub(string text, IEnumerable<string> secrets)` and `const int MaxErrorBody = 500`.
  - `ToolResultScrubber.Scrub(CallToolResult, string secret) → CallToolResult`.
  - `QueryRedactionProcessor : BaseProcessor<Activity>`.

- [ ] **Step 1: Extend the stub and write the failing tests**

In `PiStub.SendAsync`, after the `echo` branch, add:

```csharp
        if (path.Contains("boom"))
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent($"server error for {token}") };
        if (path.Contains("slow"))
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.");
```

`tests/Web/CapturingLoggerProvider.cs`:

```csharp
using Microsoft.Extensions.Logging;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>Keeps every formatted log message and exception the app writes.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public List<string> Lines { get; } = new();
    public ILogger CreateLogger(string category) => new Logger(this, category);
    public void Dispose() { }

    private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> format)
        {
            lock (owner.Lines)
                owner.Lines.Add($"{category} {level}: {format(state, ex)} {ex}");
        }
    }
}
```

`IsEnabled` returning true doesn't bypass the configured filters: `LoggerFactory` applies the `appsettings.json` levels before calling the provider. So the test sees what production would log.

`tests/Web/NoEchoTests.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class NoEchoTests
{
    private const string WrongCode = "WRONG-CODE-7731";

    [Fact]
    public async Task Tokens_and_codes_appear_in_no_response_and_no_log()
    {
        var logs = new CapturingLoggerProvider();
        using var factory = new WebFactory { ExtraServices = s => s.AddSingleton<ILoggerProvider>(logs) };
        var bodies = new List<string>();

        // Sign-in with a wrong code first, then the right one.
        var flow = new AuthorizeFlow(factory);
        var clientId = await flow.RegisterAsync();
        var session = await flow.StartAsync(AuthorizeFlow.Query(clientId));
        bodies.Add(await (await flow.CompleteAsync(session, WrongCode)).Content.ReadAsStringAsync());
        var redirect = await flow.CompleteAsync(session, PiStub.ActivationCode);
        bodies.Add(redirect.Headers.Location!.ToString());

        // A failing /token and the health endpoint.
        bodies.Add(await (await factory.Client().PostAsync("/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = "nope", ["code_verifier"] = AuthorizeFlow.Verifier,
            ["client_id"] = clientId, ["redirect_uri"] = AuthorizeFlow.ClaudeCallback
        }))).Content.ReadAsStringAsync());
        bodies.Add(await factory.Client().GetStringAsync("/healthz"));

        // Tools whose PI calls echo the token, fail with it in the body, or time out.
        await using var mcp = await factory.ConnectAsync(PiStub.TokenA);
        foreach (var id in new[] { "echo", "boom", "slow" })
        {
            var result = await mcp.CallToolAsync("get_project", new Dictionary<string, object?> { ["projectId"] = id });
            bodies.AddRange(result.Content.OfType<TextContentBlock>().Select(c => c.Text));
            if (result.StructuredContent is { } structured)
                bodies.Add(structured.ToString());
        }

        foreach (var secret in new[] { PiStub.TokenA, PiStub.ActivationCode, WrongCode })
        {
            Assert.DoesNotContain(bodies, b => b.Contains(secret));
            Assert.DoesNotContain(logs.Lines, l => l.Contains(secret));
        }
    }
}
```

`tests/Web/QueryRedactionProcessorTests.cs`:

```csharp
using System.Diagnostics;
using ProjectInsightMCP.Web.Security;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class QueryRedactionProcessorTests
{
    [Theory]
    [InlineData("url.full")]
    [InlineData("http.url")]
    public void Query_strings_are_removed_from_outbound_urls(string tag)
    {
        using var activity = new Activity("HTTP GET").Start();
        activity.SetTag(tag, "https://pi.test/api/device-activation/complete-request?activationRequestId=a&activationCode=SECRET");

        new QueryRedactionProcessor().OnEnd(activity);

        Assert.Equal("https://pi.test/api/device-activation/complete-request", activity.GetTagItem(tag));
    }
}
```

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web.NoEchoTests|FullyQualifiedName~QueryRedactionProcessorTests"`
Expected: FAIL. `NoEchoTests` finds the token in the `echo` and `boom` tool results; the processor test fails to build.

- [ ] **Step 3: Implement**

`src-web/Security/PiErrorScrubbingHandler.cs`:

```csharp
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace ProjectInsightMCP.Web.Security;

/// <summary>
/// On every outbound PI call: an error body is shortened and stripped of the credentials the
/// request carried, before Core copies it into a tool result or a log line.
/// </summary>
public sealed class PiErrorScrubbingHandler : DelegatingHandler
{
    public const int MaxErrorBody = 500;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
            return response;
        var body = await response.Content.ReadAsStringAsync(ct);
        var scrubbed = Scrub(body, SecretsIn(request));
        if (scrubbed.Length > MaxErrorBody)
            scrubbed = scrubbed[..MaxErrorBody] + "…";
        response.Content = new StringContent(scrubbed, Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "text/plain");
        return response;
    }

    public static string Scrub(string text, IEnumerable<string> secrets)
        => secrets.Where(s => s.Length >= 4).Aggregate(text, (t, s) => t.Replace(s, "[redacted]", StringComparison.Ordinal));

    private static IEnumerable<string> SecretsIn(HttpRequestMessage request)
    {
        if (request.Headers.Authorization?.Parameter is { } bearer)
            yield return bearer;
        if (request.Headers.TryGetValues("api-token", out var apiTokens))
            foreach (var t in apiTokens)
                yield return t;
        if (request.RequestUri?.Query is { Length: > 1 } query)
            foreach (var (key, value) in QueryHelpers.ParseQuery(query))
                if (key is "activationCode" or "activationRequestId")
                    yield return value.ToString();
    }
}
```

`src-web/Security/ToolResultScrubber.cs`:

```csharp
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace ProjectInsightMCP.Web.Security;

/// <summary>Last line of defence: the caller's token never reaches the model through a tool result.</summary>
public static class ToolResultScrubber
{
    public static CallToolResult Scrub(CallToolResult result, string secret)
    {
        foreach (var text in result.Content.OfType<TextContentBlock>())
            text.Text = text.Text.Replace(secret, "[redacted]", StringComparison.Ordinal);
        if (result.StructuredContent is { } structured && structured.GetRawText().Contains(secret, StringComparison.Ordinal))
            result.StructuredContent = JsonDocument.Parse(structured.GetRawText().Replace(secret, "[redacted]", StringComparison.Ordinal)).RootElement.Clone();
        return result;
    }
}
```

If `StructuredContent` is a `JsonNode` rather than a `JsonElement` in SDK 2.3.0, use `ToJsonString()` and `JsonNode.Parse`. Check with the IDE or compiler.

`src-web/Security/QueryRedactionProcessor.cs`:

```csharp
using System.Diagnostics;
using OpenTelemetry;

namespace ProjectInsightMCP.Web.Security;

/// <summary>
/// Outbound URLs lose their query strings before export: PI's device-activation endpoint takes the
/// activation code in the query, and nothing in a query is needed for diagnosis.
/// </summary>
public sealed class QueryRedactionProcessor : BaseProcessor<Activity>
{
    private static readonly string[] UrlTags = ["url.full", "http.url"];

    public override void OnEnd(Activity activity)
    {
        foreach (var tag in UrlTags)
            if (activity.GetTagItem(tag) is string url && url.IndexOf('?') is var q and >= 0)
                activity.SetTag(tag, url[..q]);
    }
}
```

`WebHost.AddProjectInsightWeb`:
- Register `services.AddTransient<PiErrorScrubbingHandler>();`.
- Chain `.AddHttpMessageHandler<PiErrorScrubbingHandler>()` onto the three PI client registrations (`AddHttpClient<ProjectInsightApiService>()`, `pi-identity`, `pi-activation`).
- Add `.RemoveAllLoggers()` to `pi-activation`, whose URLs carry the activation code.
- Chain the result scrubber onto the MCP builder from B3:

```csharp
            .WithRequestFilters(f => f.AddCallToolFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct);
                var token = context.Services!.GetRequiredService<ApiTokenAccessor>().ApiToken;
                return string.IsNullOrEmpty(token) ? result : ToolResultScrubber.Scrub(result, token);
            }));
```

- Add telemetry, only when configured:

```csharp
        if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
            services.AddOpenTelemetry()
                // Added before UseAzureMonitor, so it runs before the exporter sees the activity.
                .WithTracing(t => t.AddProcessor(new QueryRedactionProcessor()))
                .UseAzureMonitor();
```

This one reads configuration before `Build()`. That's acceptable: it's an environment variable on App Service, and tests never set it. Add a comment saying so.

- [ ] **Step 4: Run the tests**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web."`
Expected: PASS.
- If `NoEchoTests` still finds a secret in a log line, read which category wrote it and fix that source. Raising the category's level in `appsettings.json` is acceptable only for framework categories (`Microsoft.*`, `System.*`).
- Never weaken the assertion.

- [ ] **Step 5: Commit**

```bash
git add servers/project-insight/src-web servers/project-insight/tests
git commit -m "feat(web): keep PI tokens and activation codes out of tool results, logs and telemetry"
```

### Task B10: Hardening: rate limits, forwarded headers, HSTS, pruning, shutdown

**Files:**
- Create: `src-web/Hosting/RateLimits.cs`, `src-web/Hosting/PruneService.cs`
- Modify: `src-web/WebHost.cs`, `src-web/Auth/PiBearerHandler.cs`, `src-web/Auth/PiIdentityClient.cs` (add `IsCached`), `src-web/Auth/ClientRegistration.cs`, `src-web/Auth/AuthorizeEndpoints.cs`, `src-web/Auth/TokenEndpoint.cs` (attach policies)
- Create: `tests/Web/HardeningTests.cs`

**Interfaces:**
- Produces:
  - Policy names `RateLimits.Register`, `RateLimits.Authorize`, `RateLimits.Complete`, `RateLimits.Token`, `RateLimits.Mcp`.
  - `RateLimits.ClientKey(HttpContext) → string`: IPv6 grouped by /64.
  - `RateLimits.UnknownTokens`: a singleton `PartitionedRateLimiter<HttpContext>`, 30 per minute per client key.
  - `PiIdentityClient.IsCached(string token) → bool`.
  - `PruneService : BackgroundService`, with `Task PruneOnceAsync(CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

`tests/Web/HardeningTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectInsightMCP.Tests.TimeChanges;
using ProjectInsightMCP.TimeChanges;
using ProjectInsightMCP.Web.Hosting;
using ProjectInsightMCP.Web.Storage;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

public class HardeningTests
{
    [Fact]
    public async Task Eleventh_registration_in_an_hour_is_429()
    {
        using var factory = new WebFactory();
        var client = factory.Client();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/register", new { redirect_uris = new[] { "http://127.0.0.1/callback" } })).StatusCode);

        var eleventh = await client.PostAsJsonAsync("/register", new { redirect_uris = new[] { "http://127.0.0.1/callback" } });
        Assert.Equal(HttpStatusCode.TooManyRequests, eleventh.StatusCode);
    }

    [Fact]
    public async Task Unknown_tokens_are_limited_before_they_reach_pi()
    {
        using var factory = new WebFactory();
        var client = factory.Client();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }) };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer random-token-{i}");
            statuses.Add((await client.SendAsync(request)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.Equal(30, factory.Pi.ToPath("/api/user/me").Count());
    }

    [Fact]
    public async Task Responses_carry_hsts()
    {
        using var factory = new WebFactory();
        var response = await factory.Client().GetAsync("/healthz");
        Assert.True(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task Prune_removes_expired_sessions_and_old_drafts()
    {
        using var factory = new WebFactory();
        _ = factory.Client();
        var sessions = factory.Services.GetRequiredService<ISessionStore>();
        var drafts = factory.Services.GetRequiredService<IDraftStore>();
        var past = DateTimeOffset.UtcNow.AddHours(-1);
        await sessions.TryCreateAsync(new SignInSession
        {
            Id = "s-old", ClientId = "c", RedirectUri = "https://claude.ai/api/mcp/auth_callback", CodeChallenge = AuthorizeFlow.Challenge,
            PiActivationId = "a", PiActivationUrl = "https://pi.test/x", CookieHash = "h", ExpiresAt = past
        }, 200, past.AddMinutes(-20), default);
        var old = Drafts.New(owner: PiStub.UserA, at: DateTimeOffset.UtcNow.AddDays(-30));
        await drafts.CreateAsync(old);

        var prune = factory.Services.GetServices<IHostedService>().OfType<PruneService>().Single();
        await prune.PruneOnceAsync(default);

        Assert.Null(await sessions.GetAsync("s-old", default));
        Assert.Null(await drafts.GetAsync(PiStub.UserA, old.Id));
    }
}
```

`FileDraftStore.CreateAsync` sets `UpdatedAt` to its clock's now. To make a draft old, create it, then use `TryTransitionAsync` with a `TimeProvider` set in the past. Or simpler: swap the factory's `TimeProvider` registration for a `ManualTimeProvider` via `ExtraServices`, create the draft, advance the clock 8 days, then prune. **Use the `ManualTimeProvider` approach**, and drop the `at:` argument above.

- [ ] **Step 2: Run the tests to see them fail**

Run: `dotnet test servers/project-insight/tests -c Release --filter "FullyQualifiedName~Web.HardeningTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

`src-web/Hosting/RateLimits.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace ProjectInsightMCP.Web.Hosting;

public static class RateLimits
{
    public const string Register = "register", Authorize = "authorize", Complete = "complete", Token = "token", Mcp = "mcp";

    public static IServiceCollection AddWebRateLimits(this IServiceCollection services)
    {
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(Register, ctx => Fixed(ctx, 10, TimeSpan.FromHours(1)));
            o.AddPolicy(Authorize, ctx => Fixed(ctx, 30, TimeSpan.FromMinutes(10)));
            o.AddPolicy(Complete, ctx => Fixed(ctx, 20, TimeSpan.FromMinutes(10)));
            o.AddPolicy(Token, ctx => Fixed(ctx, 30, TimeSpan.FromMinutes(10)));
            o.AddPolicy(Mcp, ctx => Fixed(ctx, 300, TimeSpan.FromMinutes(1)));
        });
        // Tokens PI hasn't vouched for yet, each of which costs a PI call.
        services.AddSingleton(PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            })));
        return services;
    }

    /// <summary>The caller's address, with IPv6 grouped by /64 (one subscriber's block).</summary>
    public static string ClientKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null)
            return "unknown";
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return ip.ToString();
        var bytes = ip.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static RateLimitPartition<string> Fixed(HttpContext ctx, int permits, TimeSpan window)
        => RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permits, Window = window, QueueLimit = 0
        });
}
```

Attach the policies with `.RequireRateLimiting(...)`:
- `/register` → `Register`;
- `GET /authorize` and `POST /authorize/start` → `Authorize`;
- `/authorize/complete` → `Complete`;
- `/token` → `Token`;
- `MapMcp("/mcp")` → `Mcp`.

`PiIdentityClient`: add `public bool IsCached(string token) => cache.TryGetValue("pi-identity:" + TokenHash.Of(token), out _);`.

`PiBearerHandler`: inject `PartitionedRateLimiter<HttpContext> unknownTokens`. Before `identity.ResolveAsync`:

```csharp
        if (!identity.IsCached(token))
        {
            using var lease = unknownTokens.AttemptAcquire(Context);
            if (!lease.IsAcquired)
            {
                Context.Items[Outcome] = "limited";
                return AuthenticateResult.Fail("Too many unrecognised tokens.");
            }
        }
```

Add `case "limited": Response.StatusCode = 429; return Task.CompletedTask;` to `HandleChallengeAsync`.

`src-web/Hosting/PruneService.cs`:

```csharp
using ProjectInsightMCP.TimeChanges;
using ProjectInsightMCP.Web.Storage;

namespace ProjectInsightMCP.Web.Hosting;

/// <summary>Deletes expired sign-in records every 5 minutes, and old drafts and unused clients hourly.</summary>
public sealed class PruneService(IServiceProvider services, TimeProvider time, ILogger<PruneService> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ClientUnusedFor = TimeSpan.FromDays(90);
    private int _ticks;

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Every, time);
        do
        {
            await PruneOnceAsync(stop);
        }
        while (await timer.WaitForNextTickAsync(stop));
    }

    public async Task PruneOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        await TryAsync("sign-in sessions", () => services.GetRequiredService<ISessionStore>().PruneAsync(now, ct));
        await TryAsync("authorization codes", () => services.GetRequiredService<ICodeStore>().PruneAsync(now, ct));
        if (_ticks++ % 12 == 0)
        {
            await TryAsync("drafts", () => services.GetRequiredService<IDraftStore>().PruneAllAsync(ct));
            await TryAsync("clients", () => services.GetRequiredService<IClientStore>().PruneAsync(now - ClientUnusedFor, ct));
        }
    }

    // A failed prune is retried next tick; it must never stop the server.
    private async Task TryAsync(string what, Func<Task> prune)
    {
        try
        {
            await prune();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pruning {What} failed", what);
        }
    }
}
```

`WebHost.AddProjectInsightWeb`:

```csharp
        services.AddWebRateLimits();
        services.AddHostedService<PruneService>();
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(60)); // let in-flight commits finish on deploy
        services.AddHsts(o => o.MaxAge = TimeSpan.FromDays(365));
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // App Service's front end is the single proxy hop.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            o.KnownIPNetworks.Clear(); // named KnownNetworks before .NET 10
            o.KnownProxies.Clear();
        });
```

`WebHost.UseProjectInsightWeb`, middleware in this order, before the endpoint maps:

```csharp
        app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment())
            app.UseHsts();
        app.UseMiddleware<OriginGuard>();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
```

When an `ExtraServices` override swaps `TimeProvider`, `PruneService` uses it, because it takes `TimeProvider` from DI.

- [ ] **Step 4: Run all tests**

Run: `dotnet test servers/project-insight/tests -c Release`
Expected: PASS. A test in another class may now hit a rate limit; each test creates its own factory, so none should. If one does, raise its budget through `Settings` only if the limit is configurable. Otherwise restructure the test, and never raise the production limit.

- [ ] **Step 5: Commit**

```bash
git add servers/project-insight/src-web servers/project-insight/tests
git commit -m "feat(web): rate limits, forwarded headers, HSTS, background pruning and graceful shutdown"
```

### Task B11: Remove the Functions host; CI, Dependabot, lock files, Azurite

**Files:**
- Delete (git): `servers/project-insight/src/` (tracked files), `servers/project-insight/deploy/deploy.sh`, `.github/workflows/deploy.yml`
- Modify: `servers/project-insight/core/ProjectInsightMCP.Core.csproj`, `servers/project-insight/server/ProjectInsightMCP.Server.csproj`, `servers/project-insight/src-web/ProjectInsightMCP.Web.csproj`
- Create: `packages.lock.json` in `core/`, `server/`, `src-web/` (generated)
- Modify: `.github/workflows/ci.yml`, `.github/dependabot.yml`
- Modify: any script that builds `servers/project-insight/src` (find them in Step 1)

- [ ] **Step 1: Find everything that still points at the Functions host**

Run: `git grep -nE "project-insight/src([^-]|$)|ProjectInsightMCP\.csproj|func azure|Microsoft\.NET\.Sdk\.Functions|deploy\.sh" -- ':!docs/superpowers'`

- Fix scripts and workflows now.
- **Docs are fixed in Task C3**; list the doc hits in the commit message body so C3 can use them.
- `deploy/` holds untracked local files (`deploy-params.json`, `azure-resources.json`). **Don't delete untracked files.** Tell the owner they remain on disk.

- [ ] **Step 2: Delete and retarget**

```bash
git rm -r --quiet servers/project-insight/src servers/project-insight/deploy/deploy.sh .github/workflows/deploy.yml
```

In `core/ProjectInsightMCP.Core.csproj`:
- change `<TargetFrameworks>net8.0;net10.0</TargetFrameworks>` to `<TargetFramework>net10.0</TargetFramework>`;
- replace the comment with `Transport-agnostic core: Project Insight API client, auth helpers and the MCP tool definitions/handlers, shared by the stdio and web hosts.`

Add `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` to the `PropertyGroup` of `core`, `server` and `src-web`. Then run `dotnet restore servers/project-insight/src-web` and confirm that three `packages.lock.json` files appear.

- [ ] **Step 3: CI with Azurite**

`.github/workflows/ci.yml`:

```yaml
name: CI

# Builds both hosts and runs the tests, including the Table/Blob store tests against Azurite.
# Deployment is separate (deploy.yml).
on:
    pull_request:
    push:
        branches:
            - main

permissions:
    contents: read

jobs:
    build-and-test:
        runs-on: ubuntu-latest
        env:
            AZURITE_CONNECTION_STRING: UseDevelopmentStorage=true
        steps:
            - name: Checkout code
              uses: actions/checkout@v7
              with:
                  persist-credentials: false

            - name: Setup .NET
              uses: actions/setup-dotnet@v6
              with:
                  dotnet-version: "10.0.x"

            # A step, not a service container: Azurite needs --skipApiVersionCheck for current Azure SDKs.
            - name: Start Azurite
              run: docker run -d --name azurite -p 10000-10002:10000-10002 mcr.microsoft.com/azure-storage/azurite azurite --skipApiVersionCheck --loose --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0

            - name: Restore web host (locked)
              run: dotnet restore servers/project-insight/src-web --locked-mode

            - name: Build stdio host
              run: dotnet build servers/project-insight/src-stdio --configuration Release

            - name: Build web host
              run: dotnet build servers/project-insight/src-web --configuration Release --no-restore

            - name: Test
              run: dotnet test servers/project-insight/tests --configuration Release
```

Then pin the Azurite image to a version tag. Run `docker pull mcr.microsoft.com/azure-storage/azurite && docker image inspect mcr.microsoft.com/azure-storage/azurite --format '{{index .Config.Labels "org.opencontainers.image.version"}}'`, or check the tags list on mcr, and use `azurite:<that version>`.

`.github/dependabot.yml`: replace the second `nuget` block (Core plus the Functions host, with its ignores) with:

```yaml
    # Core, the shared MCP wiring's dependencies and the web host. Lock files are updated with them.
    - package-ecosystem: nuget
      directories:
          - "/servers/project-insight/core"
          - "/servers/project-insight/src-web"
      schedule:
          interval: weekly
      groups:
          minor-and-patch:
              update-types: [minor, patch]
```

Keep `/servers/project-insight/server` in the first block (A2).

- [ ] **Step 4: Full build and test locally, with Azurite**

```bash
docker run -d --rm --name azurite -p 10000-10002:10000-10002 mcr.microsoft.com/azure-storage/azurite azurite --skipApiVersionCheck --loose --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0
dotnet restore servers/project-insight/src-web --locked-mode
dotnet build servers/project-insight/src-stdio -c Release
dotnet build servers/project-insight/src-web -c Release
AZURITE_CONNECTION_STRING=UseDevelopmentStorage=true dotnet test servers/project-insight/tests -c Release
docker stop azurite
```

Expected: 0 warnings, all tests pass, none skipped.

- [ ] **Step 5: Commit and open PR 2**

```bash
git add -A
git commit -m "chore: remove the in-process Functions host; Core on net10.0; CI runs store tests on Azurite"
```

With the owner's go-ahead, push `phase2-web-host` and open PR 2, "Phase 2b: web host".
- **Body:** what the host does, how sign-in works (pass-through), the test count, and a note that nothing deploys yet (PR 3).
- **Attribution:** end with the line from the session.

Address CodeRabbit and gitleaks findings. The owner merges.

---

## Part C: Infrastructure, deploy and docs (PR 3)

Start from `main` after PR 2 merges: `git switch -c phase2-deploy origin/main`.

### Task C1: Bicep for App Service, storage, identities, locks and alerts

**Files:**
- Replace: `bicep/main.bicep`
- Create: `bicep/main.bicepparam.example`
- Replace: `bicep/README.md`
- Delete: `bicep/main.json`, `bicep/parameters/` (all three JSON files)
- Modify: `.gitignore`

**Interfaces:**
- Produces:
  - Outputs `webAppName`, `baseUrl` and `deployerClientId`, which the owner copies into GitHub secrets.
  - Parameters `namePrefix`, `location`, `piBaseUrl`, `allowedPiUserIds`, `alertEmail`, `githubRepository`, `deployerRole`, `tags`.

- [ ] **Step 1: Write `bicep/main.bicep`**

```bicep
// Remote MCP server for Project Insight on App Service (see docs/superpowers/specs/2026-10-30-phase2-remote-host-design.md).
// No identifying values live here: the owner supplies them in a gitignored .bicepparam file.
targetScope = 'resourceGroup'

@description('Short prefix for resource names, e.g. pi-mcp. The web app is <prefix>-web.')
@minLength(3)
@maxLength(20)
param namePrefix string

param location string = resourceGroup().location

@description('Project Insight API base URL, e.g. https://your-tenant.projectinsight.net/api/')
param piBaseUrl string

@description('Comma-separated PI user ids allowed to use the server.')
param allowedPiUserIds string

@description('Where Activity Log alerts about risky changes are emailed.')
param alertEmail string

@description('GitHub repository (owner/name) whose deploy.yml on main may publish code.')
param githubRepository string

@description('custom: publish and restart only. websiteContributor: fallback if the custom role cannot deploy.')
@allowed([ 'custom', 'websiteContributor' ])
param deployerRole string = 'custom'

param tags object = {}

var storageName = take('st${uniqueString(resourceGroup().id, namePrefix)}', 24)
var tableDataContributor = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var blobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
var websiteContributor = 'de139f84-1756-47ae-9be6-808fbbe84772'
var customRoleName = guid(resourceGroup().id, namePrefix, 'code-deployer')

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${namePrefix}-logs'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-insights'
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageName
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  tags: tags
  properties: {
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource tables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-05-01' = [for name in [ 'OAuthClients', 'OAuthSessions', 'OAuthCodes' ]: {
  parent: tableService
  name: name
}]

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
}

resource drafts 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'drafts'
  properties: { publicAccess: 'None' }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${namePrefix}-plan'
  location: location
  kind: 'linux'
  tags: tags
  sku: {
    name: 'B1'
    capacity: 1 // in-memory caches and per-user queues assume one instance
  }
  properties: { reserved: true }
}

resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: '${namePrefix}-web'
  location: location
  kind: 'app,linux'
  tags: tags
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientAffinityEnabled: false
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      http20Enabled: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      healthCheckPath: '/healthz'
    }
  }
}

resource ftpCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: site
  name: 'ftp'
  properties: { allow: false }
}

resource scmCredentials 'Microsoft.Web/sites/basicPublishingCredentialsPolicies@2024-04-01' = {
  parent: site
  name: 'scm'
  properties: { allow: false }
}

var host = site.properties.defaultHostName

resource appSettings 'Microsoft.Web/sites/config@2024-04-01' = {
  parent: site
  name: 'appsettings'
  properties: {
    ProjectInsight__BaseUrl: piBaseUrl
    Web__PublicBaseUrl: 'https://${host}'
    AllowedHosts: '${host};localhost'
    Storage__Mode: 'Azure'
    Storage__TableEndpoint: storage.properties.primaryEndpoints.table
    Storage__BlobEndpoint: storage.properties.primaryEndpoints.blob
    Auth__AllowedPiUserIds: allowedPiUserIds
    Auth__PiTokenLifetimeDays: '90'
    APPLICATIONINSIGHTS_CONNECTION_STRING: insights.properties.ConnectionString
  }
}

resource appTables 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, site.id, tableDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', tableDataContributor)
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource appBlobs 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storage
  name: guid(storage.id, site.id, blobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', blobDataContributor)
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource deployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-deployer'
  location: location
  tags: tags
}

// Only deploy.yml on main, in the production environment, can sign in as the deployer. The repository's
// OIDC subject template must include job_workflow_ref (bicep/README.md, step 2).
resource githubFederation 'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials@2023-01-31' = {
  parent: deployer
  name: 'github-deploy'
  properties: {
    issuer: 'https://token.actions.githubusercontent.com'
    subject: 'repo:${githubRepository}:environment:production:job_workflow_ref:${githubRepository}/.github/workflows/deploy.yml@refs/heads/main'
    audiences: [ 'api://AzureADTokenExchange' ]
  }
}

resource codeDeployerRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = if (deployerRole == 'custom') {
  name: customRoleName
  properties: {
    roleName: '${namePrefix} code deployer'
    description: 'Publish code to one web app and restart it. No settings, no credentials policy, no Kudu console.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.Web/sites/read'
          'Microsoft.Web/sites/publish/Action'
          'Microsoft.Web/sites/restart/Action'
          'Microsoft.Web/sites/config/list/Action'
        ]
      }
    ]
    assignableScopes: [ resourceGroup().id ]
  }
}

resource deployerOnSite 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: site
  name: guid(site.id, deployer.id, deployerRole)
  dependsOn: [ codeDeployerRole ]
  properties: {
    roleDefinitionId: deployerRole == 'custom'
      ? resourceId('Microsoft.Authorization/roleDefinitions', customRoleName)
      : subscriptionResourceId('Microsoft.Authorization/roleDefinitions', websiteContributor)
    principalId: deployer.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource siteLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: site
  name: 'do-not-delete'
  properties: {
    level: 'CanNotDelete'
    notes: 'Remote MCP server for Project Insight.'
  }
}

resource storageLock 'Microsoft.Authorization/locks@2020-05-01' = {
  scope: storage
  name: 'do-not-delete'
  properties: {
    level: 'CanNotDelete'
    notes: 'Sign-in records and drafts for the remote MCP server.'
  }
}

resource ownerAlerts 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: '${namePrefix}-owner'
  location: 'global'
  tags: tags
  properties: {
    groupShortName: 'pimcp'
    enabled: true
    emailReceivers: [
      {
        name: 'owner'
        emailAddress: alertEmail
        useCommonAlertSchema: true
      }
    ]
  }
}

// Changes that could expose tokens in flight or weaken the deploy trust, made outside this template.
var watchedOperations = [
  'Microsoft.Storage/storageAccounts/write'
  'Microsoft.Web/sites/config/write'
  'Microsoft.Web/sites/basicPublishingCredentialsPolicies/write'
  'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials/write'
  'Microsoft.Authorization/roleAssignments/write'
]

resource riskyChanges 'Microsoft.Insights/activityLogAlerts@2020-10-01' = {
  name: '${namePrefix}-risky-changes'
  location: 'global'
  tags: tags
  properties: {
    enabled: true
    scopes: [ site.id, storage.id, deployer.id ]
    condition: {
      allOf: [
        { field: 'category', equals: 'Administrative' }
        { anyOf: [for op in watchedOperations: { field: 'operationName', equals: op }] }
      ]
    }
    actions: { actionGroups: [ { actionGroupId: ownerAlerts.id } ] }
  }
}

output webAppName string = site.name
output baseUrl string = 'https://${host}'
output deployerClientId string = deployer.properties.clientId
```

- [ ] **Step 2: Parameters example and gitignore**

`bicep/main.bicepparam.example`:

```bicep
// Copy to main.bicepparam (gitignored) and fill in. Never commit the real file.
using './main.bicep'

param namePrefix = 'pi-mcp'
param location = 'centralus'
param piBaseUrl = 'https://your-tenant.projectinsight.net/api/'
param allowedPiUserIds = '00000000-0000-0000-0000-000000000000'
param alertEmail = 'you@example.com'
param githubRepository = 'alext-avi/Project-Insight-MCP'
```

`.gitignore`: add

```
# Real Bicep parameters hold tenant-specific values (bicep/main.bicepparam.example is the template).
bicep/*.bicepparam
```

- [ ] **Step 3: Validate**

Run:

```bash
git rm -q bicep/main.json bicep/parameters/*.json
az bicep build --file bicep/main.bicep --stdout > /dev/null
az bicep lint --file bicep/main.bicep
cp bicep/main.bicepparam.example /tmp/check.bicepparam && sed -i '' "s#'./main.bicep'#'$PWD/bicep/main.bicep'#" /tmp/check.bicepparam && az bicep build-params --file /tmp/check.bicepparam --stdout > /dev/null
git check-ignore -q bicep/main.bicepparam && echo ignored
```

Expected:
- no errors;
- lint warnings only if they're explained in a comment next to the line;
- the last command prints `ignored`.

Use `sed -i` without `''` on Linux.

- [ ] **Step 4: Write `bicep/README.md`**

It's the owner's one-time setup, matching the spec. Use only placeholders. Sections:

1. **What this creates:** a list of the resources above, in one line each.
2. **Before you start:**
   - be Owner of the resource group;
   - `az login` to the right tenant;
   - `az account set --subscription <subscription-id>`.
3. **Deploy the infrastructure:**

   ```bash
   cp bicep/main.bicepparam.example bicep/main.bicepparam   # fill it in; it is gitignored
   az deployment group create --resource-group <resource-group> --parameters bicep/main.bicepparam
   ```

4. **Restrict the deploy identity to `deploy.yml` on `main`:**

   ```bash
   gh api -X PUT repos/<owner>/<repo>/actions/oidc/customization/sub --input - <<'EOF'
   {"use_default": false, "include_claim_keys": ["repo", "context", "job_workflow_ref"]}
   EOF
   ```

5. **GitHub environment and secrets:**
   - Create the environment `production` (Settings → Environments), with deployment branches limited to `main`.
   - Add the secrets with the outputs and ids:

   ```bash
   gh secret set AZURE_CLIENT_ID --env production        # deployerClientId output
   gh secret set AZURE_TENANT_ID --env production
   gh secret set AZURE_SUBSCRIPTION_ID --env production
   gh secret set AZURE_WEBAPP_NAME --env production      # webAppName output
   gh secret set APP_BASE_URL --env production           # baseUrl output
   ```

   Each prompts for its value, so nothing lands in shell history.
6. **Protect `main`:** branch protection with pull requests required and force-pushes blocked.
7. **First deploy:** Actions → "Deploy remote server" → Run workflow on `main`.
8. **If the deploy step gets 403:**
   - The custom role is too narrow for `azure/webapps-deploy`. Re-run step 3 with `param deployerRole = 'websiteContributor'`, and note it in this README.
   - If `azure/login` can't find the subscription, add `allow-no-subscriptions: true` to the login step.
9. **Who has access:** the app lives in a shared resource group whose contributors the owner reviewed and accepted (2026-10-30). Alerts email the owner on risky changes.

- [ ] **Step 5: Commit**

```bash
git add bicep .gitignore
git commit -m "feat(infra): Bicep for the App Service host with OIDC deploy identity, locks and alerts"
```

### Task C2: Deploy workflow, action pinning, gitleaks rules

**Files:**
- Create: `.github/workflows/deploy.yml`
- Create: `.gitleaks.toml`
- Modify: `.github/workflows/secret-scan.yml` (digest pin)

- [ ] **Step 1: Look up the commit SHAs to pin**

Run, for each action used below:

```bash
for a in actions/checkout@v7 actions/setup-dotnet@v6 actions/upload-artifact@v7 actions/download-artifact@v8 azure/login@v3 azure/webapps-deploy@v3; do
  repo=${a%@*}; tag=${a#*@}
  echo "$a $(gh api repos/$repo/commits/$tag --jq .sha)"
done
docker pull -q ghcr.io/gitleaks/gitleaks:v8.30.1 >/dev/null && docker inspect --format '{{index .RepoDigests 0}}' ghcr.io/gitleaks/gitleaks:v8.30.1
```

If a major tag doesn't exist (`gh api` returns 404), use that action's latest release tag (`gh release view -R <repo> --json tagName`).

- [ ] **Step 2: Write `.github/workflows/deploy.yml`**

Replace each `<sha>` with the SHA from Step 1, keeping the tag as a comment.

```yaml
name: Deploy remote server

# Pushes to main that touch the web host deploy it. The build job has no Azure access; the deploy
# job signs in with GitHub OIDC and runs no restore or build, so no third-party build code runs
# while the Azure token exists. Identifiers are environment secrets, because this repo's logs are public.
on:
    push:
        branches: [main]
        paths:
            - "servers/project-insight/core/**"
            - "servers/project-insight/server/**"
            - "servers/project-insight/src-web/**"
            - ".github/workflows/deploy.yml"
    workflow_dispatch:

permissions:
    contents: read

concurrency:
    group: deploy-production
    cancel-in-progress: false

jobs:
    build:
        runs-on: ubuntu-latest
        env:
            AZURITE_CONNECTION_STRING: UseDevelopmentStorage=true
        steps:
            - uses: actions/checkout@<sha> # v7
              with:
                  persist-credentials: false

            - uses: actions/setup-dotnet@<sha> # v6
              with:
                  dotnet-version: "10.0.x"

            - name: Start Azurite
              run: docker run -d --name azurite -p 10000-10002:10000-10002 mcr.microsoft.com/azure-storage/azurite:<version> azurite --skipApiVersionCheck --loose --blobHost 0.0.0.0 --queueHost 0.0.0.0 --tableHost 0.0.0.0

            - name: Restore (locked)
              run: dotnet restore servers/project-insight/src-web --locked-mode

            - name: Test
              run: dotnet test servers/project-insight/tests --configuration Release

            - name: Publish
              run: >-
                  dotnet publish servers/project-insight/src-web --configuration Release --output publish
                  -p:InformationalVersion=${{ github.sha }} -p:IncludeSourceRevisionInInformationalVersion=false

            - uses: actions/upload-artifact@<sha> # v7
              with:
                  name: web
                  path: publish
                  retention-days: 1
                  if-no-files-found: error

    deploy:
        needs: build
        runs-on: ubuntu-latest
        environment: production
        permissions:
            id-token: write
            contents: read
        steps:
            - name: Mask derived values
              env:
                  BASE_URL: ${{ secrets.APP_BASE_URL }}
              run: echo "::add-mask::${BASE_URL#https://}"

            - uses: actions/download-artifact@<sha> # v8
              with:
                  name: web
                  path: publish

            - uses: azure/login@<sha> # v3
              with:
                  client-id: ${{ secrets.AZURE_CLIENT_ID }}
                  tenant-id: ${{ secrets.AZURE_TENANT_ID }}
                  subscription-id: ${{ secrets.AZURE_SUBSCRIPTION_ID }}

            - uses: azure/webapps-deploy@<sha> # v3
              with:
                  app-name: ${{ secrets.AZURE_WEBAPP_NAME }}
                  package: publish

            - name: Smoke test
              env:
                  BASE_URL: ${{ secrets.APP_BASE_URL }}
                  SHA: ${{ github.sha }}
              run: |
                  set -uo pipefail
                  version=""
                  for _ in $(seq 1 18); do
                    version=$(curl -s --max-time 10 "$BASE_URL/healthz" | jq -r '.version // empty' 2>/dev/null || true)
                    [ "$version" = "$SHA" ] && break
                    sleep 10
                  done
                  if [ "$version" != "$SHA" ]; then echo "The new version was not live within 3 minutes."; exit 1; fi

                  headers=$(mktemp)
                  status=$(curl -s -o /dev/null -D "$headers" -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{}' "$BASE_URL/mcp")
                  echo "Unauthenticated POST /mcp: $status"
                  if [ "$status" != "401" ] || ! grep -qi '^www-authenticate: Bearer resource_metadata=' "$headers"; then
                    echo "Expected a 401 with resource_metadata."; exit 1
                  fi

                  resource=$(curl -s "$BASE_URL/.well-known/oauth-protected-resource/mcp" | jq -r '.resource // empty')
                  if [ "$resource" != "$BASE_URL/mcp" ]; then echo "Protected-resource metadata names the wrong resource."; exit 1; fi
                  echo "Smoke test passed."
```

Pin the Azurite image to the same version tag as `ci.yml` (Task B11).

- [ ] **Step 3: gitleaks rules and digest pin**

`.gitleaks.toml`:

```toml
# Default rules, plus hostnames this public repo must never contain.
[extend]
useDefault = true

[[rules]]
id = "pi-tenant-host"
description = "A Project Insight tenant hostname"
regex = '''[a-z0-9-]+\.projectinsight\.net'''
  [rules.allowlist]
  regexes = ['''\b(yourdomain|your-tenant|your-company|example|api)\.projectinsight\.net''']

[[rules]]
id = "app-service-host"
description = "An Azure App Service or Functions hostname"
regex = '''[a-z0-9-]+\.azurewebsites\.net'''
  [rules.allowlist]
  regexes = ['''\b(your-function-app|your-app|example)\.azurewebsites\.net''']
```

In `secret-scan.yml`, change the image to `ghcr.io/gitleaks/gitleaks:v8.30.1@sha256:<digest>` and update the header comment's version note.

Run the full-history scan locally with the new rules:

```bash
docker run --rm -v "$PWD:/repo" ghcr.io/gitleaks/gitleaks:v8.30.1 git /repo \
  --log-opts="--all --full-history --diff-merges=first-parent" --redact --no-banner --exit-code 1
```

Expected: no leaks. gitleaks picks up `/repo/.gitleaks.toml` automatically.
- **A placeholder hostname** (an obvious example in a doc): add it to the rule's allowlist and re-run.
- **A real tenant or app hostname:** **stop and tell the owner.** It would need a history rewrite, which is the owner's decision. Never put a real name into the allowlist.

- [ ] **Step 4: Validate the workflows**

Run:

```bash
python3 - <<'EOF'
import re, sys, yaml
for f in [".github/workflows/deploy.yml", ".github/workflows/secret-scan.yml", ".github/workflows/ci.yml"]:
    yaml.safe_load(open(f))
bad = [l for l in open(".github/workflows/deploy.yml") if re.search(r"uses:\s*\S+@(?![0-9a-f]{40}\b)", l)]
sys.exit("unpinned in deploy.yml: " + "".join(bad) if bad else 0)
EOF
grep -n "pull_request_target" .github/workflows/*.yml && exit 1 || echo "no pull_request_target"
```

Expected: no output from Python, then `no pull_request_target`.

- [ ] **Step 5: Commit**

```bash
git add .github/workflows/deploy.yml .github/workflows/secret-scan.yml .gitleaks.toml
git commit -m "ci: deploy the web host from main with OIDC; pin actions and gitleaks; flag tenant and app hostnames"
```

### Task C3: Docs

**Files:**
- Create: `docs/REMOTE.md`
- Rewrite: `docs/agents.md` (the architecture, getting-started, deployment and client-integration sections)
- Rewrite: `servers/project-insight/docs/DEPLOYMENT.md` (short, pointing at `bicep/README.md` and `docs/REMOTE.md`)
- Delete: `servers/project-insight/docs/PRE-DEPLOYMENT-CHECKLIST.md`
- Modify: every other doc listed by the `git grep` in Task B11 Step 1: `README.md`, `servers/project-insight/README.md`, `CONFIGURATION.md`, `servers/project-insight/docs/AUTHENTICATION.md`, `servers/project-insight/docs/MCP-CLIENT-SETUP.md`, `.github/copilot-instructions.md`, as applicable.

- [ ] **Step 1: Write `docs/REMOTE.md`**

```markdown
# Connecting to the remote server

The remote server serves the same tools as the local `projectinsight-mcp` tool at `https://<app>/mcp`.
You sign in with your Project Insight account; the connector keeps the resulting token, which lasts
90 days. Only Project Insight users the server's owner has allowed can connect.

## Claude (claude.ai, Desktop, Cowork, mobile)

Settings → Connectors → Add custom connector → URL `https://<app>/mcp` → Connect. A sign-in page
opens: click **Sign in to Project Insight**, sign in, copy the activation code Project Insight shows,
paste it, and press Continue.

## Claude Code

    claude mcp add --transport http project-insight https://<app>/mcp

Then run `/mcp` in Claude Code and choose project-insight to sign in.

## ChatGPT

Settings → Apps & Connectors → Advanced → Developer mode, then Create: URL `https://<app>/mcp`,
authentication OAuth. Sign in as above.

## Codex

In `~/.codex/config.toml`:

    mcp_oauth_credentials_store = "keyring"

    [mcp_servers.project-insight]
    url = "https://<app>/mcp"

Then `codex mcp login project-insight`. The keyring setting keeps the token out of a plain file.

## Where your token lives

The connector holds a Project Insight token with your full Project Insight permissions for 90 days.
For Claude and ChatGPT it stays in the vendor's connector service, out of the model's reach. For
Claude Code and Codex it is in your OS keychain (or a credentials file on Linux and Windows), which
an agent with shell access could read, the same as the local tool's stored credentials.

## If a token leaks

Not yet verified. The server must not be used until the owner has filled in this section with the
Project Insight screen where a user, and an admin, revoke a device-activation token (each one is
labelled "MCP connector (<app host>)"), and confirmed that a revoked token stops working.
```

That last section is deliberately incomplete. Task C4's go-live gate replaces it with the verified steps. Say so in the PR body.

- [ ] **Step 2: Update the other docs**

For each file the B11 grep listed:
- Replace descriptions of the Azure Functions host with:
  - the App Service web host (`servers/project-insight/src-web`);
  - the shared `server/` project;
  - links to `docs/REMOTE.md` and `bicep/README.md`.
- Remove `func` commands, `local.settings.json` instructions and `X-PI-Token` header instructions.
- Keep Copilot Studio material, but mark it "not supported by the current web host (stretch goal)".
- `docs/agents.md`: rewrite "Overview", "Architecture", "Getting Started", "Deployment", "MCP Client Integration" and "Validation Checklist" for the current layout:
  - `core/`, `server/`, `src-stdio/`, `src-web/`, `tests/`;
  - `dotnet test servers/project-insight/tests -c Release`;
  - a local web run with `Storage:Mode=Local`.
  Keep its tool notes. Update "Last Updated".

Docs use placeholders only.

- [ ] **Step 3: Check for stale references and identifiers**

Run:
- `git grep -nE "func (start|azure)|local\.settings\.json|X-PI-Token|in-process" -- '*.md' ':!docs/superpowers'`. Expected: no hits, or only lines that explicitly describe history.
- The gitleaks command from Task C2 Step 3. Expected: no leaks.

- [ ] **Step 4: Commit and open PR 3**

```bash
git add -A docs servers/project-insight/docs README.md CONFIGURATION.md .github/copilot-instructions.md
git commit -m "docs: remote server connection guide; replace Functions-era deployment docs"
```

With the owner's go-ahead, push `phase2-deploy` and open PR 3, "Phase 2c: infrastructure and deploy". The body lists the owner's one-time steps (Task C4), notes that the deploy runs only after setup, and ends with the attribution line.

**The workflow is armed as soon as PR 3 merges.** A push to main touching the web host runs `deploy.yml`. Until the secrets exist, its deploy job fails at `azure/login`. Tell the owner, so a red run after merge isn't a surprise.

### Task C4: Owner's one-time setup, go-live gate and cutover (manual, not for an agent)

The owner does these. The agent prepares commands on request but runs nothing against Azure or GitHub settings without the owner's explicit go-ahead.

- [ ] **Step 1:** Follow `bicep/README.md` steps 2–7:
  - infrastructure;
  - OIDC subject template;
  - the `production` environment and its five secrets;
  - branch protection;
  - the first deploy by hand.
- [ ] **Step 2:** If the deploy step fails with 403, switch `deployerRole` to `websiteContributor` (README step 8), re-deploy, and record which role was needed in `bicep/README.md`.
- [ ] **Step 3 (go-live gate):**
  1. Connect claude.ai to `https://<app>/mcp` and sign in.
  2. In Project Insight, find the new token labelled "MCP connector (claude.ai)".
  3. Revoke it, and confirm the next tool call fails and Claude asks to reconnect.
  4. Write the exact steps into `docs/REMOTE.md` → "If a token leaks", in a small PR.
  5. If PI can't list or revoke these tokens, **stop**: the server-issued-token design in the spec's Out of scope moves into scope before anyone relies on the server.
- [ ] **Step 4 (live tests):** In claude.ai and Cowork, ChatGPT developer mode, Claude Code and Codex:
  - add the connector, sign in, list tools, run a read (`get_current_user`), plan a time change and see the card;
  - commit a time change only in the owner's own test.
- [ ] **Step 5 (cutover):**
  - point existing connectors at the new URL;
  - delete the old Function app, its storage account, its Entra app registration and the old Key Vault;
  - revoke in PI any tokens the old bridge issued;
  - optionally delete the untracked `servers/project-insight/deploy/` and `servers/project-insight/src/` leftovers on disk.
