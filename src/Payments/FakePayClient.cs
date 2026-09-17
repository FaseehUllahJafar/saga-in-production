using System.Net;
using System.Net.Http.Json;

namespace Payments;

public sealed class FakePaySettings
{
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);
    // The longest FakePay may keep working on a request after we stopped waiting for it.
    public TimeSpan SettleWindow { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed record ProviderAuthorization(string Id, string Status, decimal Amount, string? CaptureId);

public abstract record ProviderResult
{
    public sealed record Ok(ProviderAuthorization Authorization) : ProviderResult;
    // A definitive "no" from the provider (declined, expired, already captured).
    public sealed record Rejected(string Error) : ProviderResult;
    public sealed record NotFound : ProviderResult;
    // No answer, or a 5xx: the effect may or may not have happened.
    public sealed record Unavailable(string Reason) : ProviderResult;
}

// No retry handler on this HttpClient on purpose. Retries belong to the saga (ADR 0003):
// a hidden HTTP retry loop here would stack under the saga's own and multiply the load
// on a provider that is already struggling.
// A singleton over IHttpClientFactory (not a typed client) so Wolverine's generated
// handler code can construct it directly instead of falling back to service location.
public sealed class FakePayClient(IHttpClientFactory httpClientFactory, ILogger<FakePayClient> logger)
{
    public const string HttpClientName = "fakepay";

    private sealed record ErrorBody(string Error);

    public Task<ProviderResult> AuthorizeAsync(Guid idempotencyKey, Guid sagaId, decimal amount, string cardToken, CancellationToken ct) =>
        Send(HttpMethod.Post, "/v1/authorizations", idempotencyKey, sagaId, new { amountMinor = ToMinorUnits(amount), cardToken }, ct);

    public Task<ProviderResult> CaptureAsync(string authorizationId, Guid idempotencyKey, Guid sagaId, decimal amount, CancellationToken ct) =>
        Send(HttpMethod.Post, $"/v1/authorizations/{authorizationId}/capture", idempotencyKey, sagaId, new { amountMinor = ToMinorUnits(amount) }, ct);

    public Task<ProviderResult> VoidAsync(string authorizationId, Guid sagaId, CancellationToken ct) =>
        Send(HttpMethod.Post, $"/v1/authorizations/{authorizationId}/void", null, sagaId, null, ct);

    public Task<ProviderResult> LookupAsync(string operation, Guid idempotencyKey, Guid sagaId, CancellationToken ct) =>
        Send(HttpMethod.Get, $"/v1/lookup/{operation}/{idempotencyKey:D}", null, sagaId, null, ct);

    // One canonical wire value per amount, whatever scale the decimal happens to carry.
    private static long ToMinorUnits(decimal amount) =>
        decimal.ToInt64(decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero));

    private async Task<ProviderResult> Send(HttpMethod method, string path, Guid? idempotencyKey, Guid sagaId, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (idempotencyKey is { } key) request.Headers.Add("Idempotency-Key", key.ToString("D"));
        request.Headers.Add("X-Saga-Id", sagaId.ToString("D"));
        if (body is not null) request.Content = JsonContent.Create(body);

        try
        {
            var http = httpClientFactory.CreateClient(HttpClientName);
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                var authorization = await response.Content.ReadFromJsonAsync<ProviderAuthorization>(ct);
                return new ProviderResult.Ok(authorization!);
            }

            if (response.StatusCode == HttpStatusCode.NotFound) return new ProviderResult.NotFound();
            if ((int)response.StatusCode >= 500) return new ProviderResult.Unavailable($"HTTP {(int)response.StatusCode}");

            var error = await response.Content.ReadFromJsonAsync<ErrorBody>(ct);
            return new ProviderResult.Rejected(error?.Error ?? $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("FakePay {Method} {Path} got no answer: {Error}", method, path, e.Message);
            return new ProviderResult.Unavailable(e.GetType().Name);
        }
    }
}
