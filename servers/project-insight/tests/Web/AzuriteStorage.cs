namespace ProjectInsightMCP.Tests.Web;

/// <summary>Azure-backed store tests run only when Azurite is available (CI sets the variable).</summary>
public static class AzuriteStorage
{
    public static string? ConnectionString => Environment.GetEnvironmentVariable("AZURITE_CONNECTION_STRING");
    public static bool Available => !string.IsNullOrEmpty(ConnectionString);
    public static string UniqueName(string prefix) => prefix + Guid.NewGuid().ToString("N")[..12];
}
