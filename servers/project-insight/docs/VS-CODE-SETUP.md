# VS Code Setup

## Extensions

- **C# Dev Kit** for .NET development and debugging.
- **Bicep**, if you edit `bicep/`.

You need the .NET 10 SDK. No Azure Functions tools are needed.

## Tasks

Press `Cmd+Shift+P` (macOS) or `Ctrl+Shift+P` (Windows, Linux) and run "Tasks: Run Task". The tasks
are in `.vscode/tasks.json`:

| Task | What it does |
|------|--------------|
| `build` | Builds the web host (`src-web`) |
| `build stdio` | Builds the local stdio tool (`src-stdio`) |
| `test` | Runs the test project (`servers/project-insight/tests`) |
| `watch web` | Runs the web host with `dotnet watch` (background) |

From a terminal, the same tests run as `dotnet test servers/project-insight/tests -c Release`.

## Debugging

`.vscode/launch.json` has one configuration, **Debug web host**. It runs the `build` task, then
launches `ProjectInsightMCP.Web.dll` with `ASPNETCORE_ENVIRONMENT=Development`. Set breakpoints and
press `F5`.

The configuration sets the local-run settings itself (`Storage__Mode=Local`, the URLs, the drafts
folder) and takes `ProjectInsight__BaseUrl` from the environment VS Code
was started in. The web host doesn't read user secrets, and these environment values override
`appsettings.json`, so set it in your shell and start VS Code from it (`code .`), as the
[README](../README.md#debugging) describes. Without it the host stops at startup and names the missing
setting. For the full list of settings, see
[Run the web host locally](../../../docs/agents.md#run-the-web-host-locally).

To debug the stdio tool, launch it from your MCP client or from the MCP Inspector
(`npx @modelcontextprotocol/inspector projectinsight-mcp`).

## Troubleshooting

- **Web host exits at startup:** read the message. It names the setting that is missing or unsafe.
- **Build errors:** check the Problems panel (`Cmd+Shift+M`) and that `dotnet --version` is 10.x.
- **Debugger will not attach:** make sure C# Dev Kit is installed and restart VS Code.
