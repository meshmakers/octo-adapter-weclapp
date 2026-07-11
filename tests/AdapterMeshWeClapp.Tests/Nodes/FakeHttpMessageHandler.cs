using System.Net;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

/// <summary>Scriptable HttpMessageHandler: answers requests via a callback and records them.</summary>
internal sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    public List<(string Method, string Url, string? AuthToken, string? Body)> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.TryGetValues("AuthenticationToken", out var tokens);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method.Method, request.RequestUri!.ToString(), tokens?.FirstOrDefault(), body));
        return responder(request, Requests.Count);
    }

    public static HttpResponseMessage Json(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(content) };
}
