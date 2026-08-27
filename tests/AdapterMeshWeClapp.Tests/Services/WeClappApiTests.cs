using System.Net;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Services;
using Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Nodes;

namespace Meshmakers.Octo.Communication.MeshAdapter.WeClapp.Tests.Services;

public class WeClappApiTests
{
    private static WeClappApi Create(FakeHttpMessageHandler handler, int maxRetries = 4) =>
        new(new HttpClient(handler), "https://demo.weclapp.com/webapp/api/v1", "test-key",
            maxRetries, 0);

    [Fact]
    public async Task Timeout_IsTransient_AndTheNextAttemptSucceeds()
    {
        // HttpClient reports its own timeout as TaskCanceledException, not HttpRequestException.
        // GET is idempotent, so repeating it is free — unlike the POST case below.
        var handler = new FakeHttpMessageHandler((_, attempt) => attempt == 1
            ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")
            : FakeHttpMessageHandler.Json("""{"result":[]}"""));

        var result = await Create(handler).SendAsync(HttpMethod.Get, "article", null);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task TimeoutOnEveryAttempt_FailsAfterTheAttemptsAreSpent()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new TaskCanceledException());

        var ex = await Assert.ThrowsAsync<WeClappPipelineExecutionException>(
            () => Create(handler).SendAsync(HttpMethod.Get, "article", null));

        Assert.Contains("4 attempts", ex.Message);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task PostTimeout_PropagatesInsteadOfBeingRetried()
    {
        // A timed-out POST is indeterminate: the server may have processed it and only the
        // response was lost. Repeating it would create a second shipment / book a stock movement
        // twice, and nothing between two attempts reconciles that. The reconcile lives one level
        // up in the file replay, so the raw failure has to reach the caller.
        var handler = new FakeHttpMessageHandler((_, _) => throw new TaskCanceledException());

        await Assert.ThrowsAsync<TaskCanceledException>(() => Create(handler)
            .SendAsync(HttpMethod.Post, "salesOrder/id/1/createShipment", new JsonObject()));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task PutTimeout_IsTransient_AndTheNextAttemptSucceeds()
    {
        // The shipment PUTs (data and status) are idempotent — a repeat writes the same state,
        // so the timeout retry must stay in place for them.
        var handler = new FakeHttpMessageHandler((_, attempt) => attempt == 1
            ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout")
            : FakeHttpMessageHandler.Json("""{"id":"7"}"""));

        var result = await Create(handler).SendAsync(HttpMethod.Put, "shipment/id/7", new JsonObject());

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesWithoutBeingRetried()
    {
        // A shutdown is not a transient failure: swallowing it would keep an adapter that is
        // being stopped busy for the rest of its retry budget.
        var cts = new CancellationTokenSource();
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAsync<TaskCanceledException>(
            () => Create(handler).SendAsync(HttpMethod.Get, "article", null, cts.Token));

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task NetworkFailure_IsStillTransient()
    {
        // Regression guard: the new catch must sit next to the existing one, not replace it.
        var handler = new FakeHttpMessageHandler((_, attempt) => attempt == 1
            ? throw new HttpRequestException("connection reset")
            : FakeHttpMessageHandler.Json("""{"result":[]}"""));

        var result = await Create(handler).SendAsync(HttpMethod.Get, "article", null);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task NonTransientStatus_IsReturnedAsData_NotThrown()
    {
        // Regression guard for the dead-letter path: a 404 is data the AR write node acts on.
        var handler = new FakeHttpMessageHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });

        var result = await Create(handler).SendAsync(HttpMethod.Get, "salesOrder/id/1", null);

        Assert.False(result.IsSuccess);
        Assert.Equal(404, result.StatusCode);
        Assert.Single(handler.Requests);
    }
}
