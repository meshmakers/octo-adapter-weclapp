using System.Net;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

/// <summary>Scriptable HttpMessageHandler: answers requests via a callback and records them.</summary>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    public List<(string Url, string? AuthToken)> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.TryGetValues("AuthenticationToken", out var tokens);
        Requests.Add((request.RequestUri!.ToString(), tokens?.FirstOrDefault()));
        return Task.FromResult(responder(request, Requests.Count));
    }

    public static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content) };
}
