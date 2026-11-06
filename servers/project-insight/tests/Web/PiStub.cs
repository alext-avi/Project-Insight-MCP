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
    public const string NotFoundToken = "pi-token-pi-answers-404";
    public const string RedirectToken = "pi-token-pi-redirects";
    public const string TimeoutToken = "pi-token-pi-times-out";
    public const string ActivationCode = "GOOD-CODE";
    public const string ActivationId = "act-1";

    private int _toolCallsInFlight;
    private int _maxToolCallsInFlight;

    public List<PiRequest> Requests { get; } = new();
    public TimeSpan ToolCallDelay { get; set; } = TimeSpan.Zero;
    public int MaxToolCallsInFlight => _maxToolCallsInFlight;
    /// <summary>Activation completions return this token and user.</summary>
    public (string Token, string UserId, string? Expiry) ActivationResult { get; set; } = (TokenA, UserA, DefaultExpiry.ToString("yyyy-MM-ddTHH:mm:ss"));
    /// <summary>PI sends the expiry without a UTC marker; relative to now so no test depends on the calendar.</summary>
    public static DateTime DefaultExpiry { get; } = DateTime.UtcNow.AddDays(30);
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
        if (token == TimeoutToken)
            throw new TaskCanceledException("simulated PI timeout");
        if (path == "/api/user/me" && token == NotFoundToken)
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (path == "/api/user/me" && token == RedirectToken)
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://elsewhere.test/me");
            return redirect;
        }
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
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($"bad token {token} rejected {request.RequestUri.Query}") };
        if (request.Method == HttpMethod.Post && path == "/api/project")
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($"cannot create, token {token}") };
        if (path.Contains("leak"))
            return Json(new { id = "leak", name = $"name {token}" });
        if (path.Contains("boom"))
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent($"server error for {token}") };
        if (path.Contains("slow"))
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 30 seconds elapsing.");

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
