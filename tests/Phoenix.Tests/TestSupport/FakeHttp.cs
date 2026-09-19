using System.Net;

namespace Phoenix.Tests.TestSupport;

/// <summary>An <see cref="HttpMessageHandler"/> driven by a delegate, for tests that must not touch the network.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
    private int _calls;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) =>
        _responder = responder;

    public int Calls => _calls;

    public List<HttpRequestMessage> Requests { get; } = [];

    public static FakeHttpMessageHandler Always(HttpStatusCode status, string content = "") =>
        new((_, _) => new HttpResponseMessage(status) { Content = new StringContent(content) });

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var call = Interlocked.Increment(ref _calls);
        Requests.Add(request);
        return Task.FromResult(_responder(request, call));
    }
}

/// <summary>An <see cref="IHttpClientFactory"/> that hands out clients over a fake handler.</summary>
public sealed class FakeHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public FakeHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

    public HttpClient CreateClient(string name) =>
        new(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
}
