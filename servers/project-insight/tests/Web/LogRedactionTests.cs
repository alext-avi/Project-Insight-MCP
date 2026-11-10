using System.IO.Compression;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using ProjectInsightMCP.Web;
using ProjectInsightMCP.Web.Security;
using Xunit;

namespace ProjectInsightMCP.Tests.Web;

/// <summary>The scrubber as a logging processor: the request's token never leaves in an exported log record.</summary>
public class LogRedactionTests
{
    private const string Token = "log+tok/en=123";

    private static IHttpContextAccessor RequestWith(string token)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer " + token;
        return new HttpContextAccessor { HttpContext = context };
    }

    [Fact]
    public void Message_arguments_and_exception_lose_the_requests_token()
    {
        var exported = new List<(string? Message, string Attributes, string? Exception)>();
        using (var loggers = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.AddProcessor(new LogRedactionProcessor(RequestWith(Token)));
            o.AddProcessor(new SimpleLogRecordExportProcessor(new CapturingExporter(exported)));
        })))
        {
            var logger = loggers.CreateLogger("test");
            logger.LogWarning(new InvalidOperationException("PI said " + Token), "Call failed: {Body}", "echo " + Token);
        }

        var (message, attributes, exception) = Assert.Single(exported);
        Assert.Equal("Call failed: echo [redacted]", message);
        Assert.DoesNotContain(Token, attributes);
        Assert.Contains("echo [redacted]", attributes);
        Assert.Contains("InvalidOperationException: PI said [redacted]", exception);
        Assert.DoesNotContain(Token, exception);
    }

    [Fact]
    public void Records_logged_outside_a_request_pass_unchanged()
    {
        var exported = new List<(string? Message, string Attributes, string? Exception)>();
        using (var loggers = LoggerFactory.Create(b => b.AddOpenTelemetry(o =>
        {
            o.IncludeFormattedMessage = true;
            o.AddProcessor(new LogRedactionProcessor(new HttpContextAccessor()));
            o.AddProcessor(new SimpleLogRecordExportProcessor(new CapturingExporter(exported)));
        })))
            loggers.CreateLogger("test").LogWarning("Pruning {What} failed", "drafts");

        Assert.Equal("Pruning drafts failed", Assert.Single(exported).Message);
    }

    /// <summary>
    /// The wiring: what the Azure Monitor exporter actually sends. Its endpoint is a local server, so a record
    /// that reached the exporter before the redaction processor would show the token in the captured payload.
    /// </summary>
    [Fact]
    public async Task The_azure_monitor_export_carries_no_token()
    {
        var payloads = new List<string>();
        await using var ingestion = await StartIngestionAsync(payloads);
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddLogging();
        services.AddTelemetry();
        services.Configure<AzureMonitorOptions>(o =>
        {
            o.ConnectionString = $"InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint={ingestion.Url};LiveEndpoint={ingestion.Url}";
            o.DisableOfflineStorage = true;
            o.EnableLiveMetrics = false;
        });
        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IHttpContextAccessor>().HttpContext = RequestWith(Token).HttpContext;
        foreach (var hosted in provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
            await hosted.StartAsync(default);

        provider.GetRequiredService<ILoggerFactory>().CreateLogger("test").LogWarning("PI answered {Body}", "bad token " + Token);
        await provider.DisposeAsync(); // flushes the exporter

        var sent = string.Join("\n", payloads);
        Assert.Contains("bad token [redacted]", sent); // the record was exported
        foreach (var form in SecretForms.Of(Token))
            Assert.DoesNotContain(form, sent);
    }

    private sealed class CapturingExporter(List<(string?, string, string?)> into) : BaseExporter<LogRecord>
    {
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var r in batch)
                into.Add((r.FormattedMessage, string.Join(";", r.Attributes?.Select(a => $"{a.Key}={a.Value}") ?? []), r.Exception?.ToString()));
            return ExportResult.Success;
        }
    }

    private static async Task<Ingestion> StartIngestionAsync(List<string> payloads)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.MapPost("/{**path}", async (HttpContext http) =>
        {
            Stream body = http.Request.Body;
            if (http.Request.Headers.ContentEncoding == "gzip")
                body = new GZipStream(body, CompressionMode.Decompress);
            var text = await new StreamReader(body).ReadToEndAsync();
            lock (payloads)
                payloads.Add(text);
            return Results.Json(new { itemsReceived = 1, itemsAccepted = 1, errors = Array.Empty<object>() });
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Ingestion(app, address.TrimEnd('/') + "/");
    }

    private sealed record Ingestion(WebApplication App, string Url) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => App.DisposeAsync();
    }
}
