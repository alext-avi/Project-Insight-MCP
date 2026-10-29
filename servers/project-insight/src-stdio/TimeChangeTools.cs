using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using ProjectInsightMCP.TimeChanges;

namespace ProjectInsightMCP.Stdio;

/// <summary>
/// The six time-change tools and the review card as SDK-native primitives. The logic lives in
/// Core (<see cref="TimeChangeService"/>); this file only shapes MCP inputs and results.
/// </summary>
internal static class TimeChangeTools
{
    public const string CardUri = "ui://project-insight/time-changes.html";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // One schema for all six tools: they all return a TimeChangesView.
    private static readonly JsonElement OutputSchema = AIJsonUtilities.CreateJsonSchema(typeof(TimeChangesView), serializerOptions: Json);

    private static readonly string[] Model = [McpUiToolVisibility.Model];
    private static readonly string[] App = [McpUiToolVisibility.App];
    private static readonly string[] ModelAndApp = [McpUiToolVisibility.Model, McpUiToolVisibility.App];

    private const string PlanDescription =
        "Draft changes to Project Insight time entries for the user to review. Writes nothing to Project Insight: " +
        "returns a draft (shown to the user as a review card where supported), or a clarification when a name is " +
        "ambiguous. Each change is one of: create {date, hours, taskId or project + task names, description?, billable?, user?}, " +
        "update {timeEntryId plus only the fields to change}, delete {timeEntryId}. After planning, summarize the draft " +
        "and ask the user to confirm; only commit when they agree.";

    private const string CommitDescription =
        "Apply a draft from plan_time_changes or undo_time_changes to Project Insight, all or nothing. Only call this " +
        "after the user has agreed to the draft. Refused if the affected days changed in Project Insight since the draft " +
        "was made (draft again) or if the draft expired (24 hours).";

    private const string UndoDescription =
        "Draft the reverse of a committed draft (pass its id as receiptId). Returns a new draft that must be confirmed " +
        "or committed like any other. Available for 24 hours after commit.";

    public static IReadOnlyList<McpServerTool> Create() =>
    [
        Tool("plan_time_changes", PlanDescription, Plan, Model, readOnly: true),
        Tool("commit_time_changes", CommitDescription, Commit, Model, destructive: true),
        Tool("confirm_time_changes", "Used by the review card's Confirm button. Agents use commit_time_changes instead.",
            Confirm, App, destructive: true),
        Tool("cancel_time_changes", "Cancel a time-change draft so it can't be committed. Nothing in Project Insight changes.",
            Cancel, ModelAndApp),
        Tool("undo_time_changes", UndoDescription, Undo, ModelAndApp, readOnly: true),
        Tool("get_time_changes", "Show a time-change draft or receipt by id, with its current status.", Get, ModelAndApp, readOnly: true)
    ];

    public static McpServerResource CreateCard() => McpServerResource.Create(
        () => new TextResourceContents
        {
            Uri = CardUri,
            MimeType = McpApps.HtmlMimeType,
            Text = TimeChangeCard.Html,
            // MCP Apps reads UI metadata from the resource contents: no network access for the card.
            Meta = new JsonObject
            {
                ["ui"] = new JsonObject
                {
                    ["csp"] = new JsonObject { ["connectDomains"] = new JsonArray(), ["resourceDomains"] = new JsonArray() },
                    ["prefersBorder"] = true
                }
            }
        },
        new McpServerResourceCreateOptions
        {
            UriTemplate = CardUri,
            Name = "time-changes",
            Title = "Time changes review card",
            Description = "Review card for Project Insight time-entry drafts.",
            MimeType = McpApps.HtmlMimeType
        });

    private static McpServerTool Tool(string name, string description, Delegate handler, string[] visibility,
        bool readOnly = false, bool destructive = false)
    {
        var tool = McpServerTool.Create(handler, new McpServerToolCreateOptions
        {
            Name = name,
            Description = description,
            ReadOnly = readOnly ? true : null,
            Destructive = destructive ? true : null,
            UseStructuredContent = true,
            OutputSchema = OutputSchema,
            SerializerOptions = Json
        });
        McpApps.SetAppUi(tool, new McpUiToolMeta { ResourceUri = CardUri, Visibility = [.. visibility] });
        return tool;
    }

    private static async Task<CallToolResult> Plan(
        [Description("One line saying what these changes are for, e.g. \"Log Tuesday from H4T\".")] string intent,
        [Description("The changes to draft, 1 to 100.")] List<ChangeInput> changes,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).PlanAsync(intent, changes, cancellationToken));

    private static async Task<CallToolResult> Commit(
        [Description("Draft id from plan_time_changes or undo_time_changes.")] string draftId,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).CommitAsync(draftId, cancellationToken));

    private static async Task<CallToolResult> Confirm(
        [Description("Draft id.")] string draftId,
        [Description("The one-time token from the draft's result metadata.")] string confirmToken,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).ConfirmAsync(draftId, confirmToken, cancellationToken));

    private static async Task<CallToolResult> Cancel(
        [Description("Draft id.")] string draftId,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).CancelAsync(draftId, cancellationToken));

    private static async Task<CallToolResult> Undo(
        [Description("Id of the committed draft to reverse.")] string receiptId,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).UndoAsync(receiptId, cancellationToken));

    private static async Task<CallToolResult> Get(
        [Description("Draft or receipt id.")] string id,
        RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
        => ToResult(await Service(context).GetAsync(id, cancellationToken));

    private static TimeChangeService Service(RequestContext<CallToolRequestParams> context)
        => context.Services!.GetRequiredService<TimeChangeService>();

    internal static CallToolResult ToResult(TimeChangeResponse response) => new()
    {
        Content = [new TextContentBlock { Text = response.Text }],
        StructuredContent = JsonSerializer.SerializeToElement(response.View, Json),
        IsError = response.IsError,
        // The card needs the token to confirm; per MCP Apps, _meta is not meant for model context.
        Meta = response.ConfirmToken is null ? null : new JsonObject { ["confirmToken"] = response.ConfirmToken }
    };
}
