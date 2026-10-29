using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProjectInsightMCP.Services;
using Xunit;

namespace ProjectInsightMCP.Tests;

/// <summary>
/// Pins the tools/list surface (names, descriptions, input schemas) so transport and
/// SDK changes can't silently alter what agents see. Regenerate deliberately with
/// UPDATE_SNAPSHOTS=1 dotnet test.
/// </summary>
public class ToolListSnapshotTests
{
    // Mirrors the wire options the stdio host used before the SDK migration.
    private static readonly JsonSerializerOptions WireOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    internal static string SnapshotPath([CallerFilePath] string thisFile = "")
        => Path.Combine(Path.GetDirectoryName(thisFile)!, "Snapshots", "tools-list.json");

    internal static JsonArray LoadSnapshot()
        => JsonNode.Parse(File.ReadAllText(SnapshotPath()))!.AsArray();

    [Fact]
    public void Tool_definitions_match_snapshot()
    {
        var current = JsonSerializer.SerializeToNode(McpServerService.InitializeTools(), WireOptions)!;

        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath())!);
            File.WriteAllText(SnapshotPath(), current.ToJsonString(WireOptions) + "\n");
        }

        Assert.True(JsonNode.DeepEquals(LoadSnapshot(), current),
            "tools/list changed. If intentional, regenerate with UPDATE_SNAPSHOTS=1.");
    }
}
