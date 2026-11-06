using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>
/// The primary handler the app configured for a named client. WebFactory replaces primary handlers with
/// the PI stub afterwards, so this replays only the app's own builder actions.
/// </summary>
internal static class ConfiguredHandler
{
    public static T? Of<T>(WebFactory factory, string clientName) where T : HttpMessageHandler
    {
        _ = factory.Client();
        var options = factory.Services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(clientName);
        var builder = new CapturingBuilder(factory.Services);
        T? configured = null;
        foreach (var action in options.HttpMessageHandlerBuilderActions)
        {
            action(builder);
            configured ??= builder.PrimaryHandler as T;
        }
        return configured;
    }

    private sealed class CapturingBuilder(IServiceProvider services) : HttpMessageHandlerBuilder
    {
        public override string? Name { get; set; }
        public override HttpMessageHandler PrimaryHandler { get; set; } = new HttpClientHandler();
        public override IList<DelegatingHandler> AdditionalHandlers { get; } = new List<DelegatingHandler>();
        public override IServiceProvider Services { get; } = services;
        public override HttpMessageHandler Build() => PrimaryHandler;
    }
}
