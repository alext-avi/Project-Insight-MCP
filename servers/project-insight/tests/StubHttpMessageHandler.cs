using System.Net;

namespace ProjectInsightMCP.Tests;

public sealed record RecordedRequest(HttpMethod Method, Uri Uri, string Body);

public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses;

    public List<RecordedRequest> Requests { get; } = new();

    public StubHttpMessageHandler(IEnumerable<HttpResponseMessage> responses)
        => _responses = new Queue<HttpResponseMessage>(responses);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, body));

        return _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.OK)
              { Content = new StringContent("{\"success\":true}") };
    }
}
