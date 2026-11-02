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
