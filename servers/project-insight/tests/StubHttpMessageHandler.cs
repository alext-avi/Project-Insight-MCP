using System.Net;

namespace ProjectInsightMCP.Tests;

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;

    public List<RecordedRequest> Requests { get; } = new();

    /// <summary>Artificial latency per request, to make overlapping calls observable.</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.Zero;

    /// <summary>Highest number of requests that were in flight at the same time.</summary>
    public int MaxConcurrent => _maxConcurrent;

    private int _inFlight;
    private int _maxConcurrent;

    public StubHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
        => _responses = new Queue<HttpResponseMessage>(responses);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var inFlight = Interlocked.Increment(ref _inFlight);
        int seen;
        while (inFlight > (seen = _maxConcurrent))
            Interlocked.CompareExchange(ref _maxConcurrent, inFlight, seen);
        try
        {
            if (Delay > TimeSpan.Zero)
                await Task.Delay(Delay, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));

        lock (_responses)
            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                  { Content = new StringContent("{\"success\":true}") };
    }
}
