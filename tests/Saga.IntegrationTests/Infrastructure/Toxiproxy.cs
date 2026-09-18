using System.Net.Http.Json;

namespace Saga.IntegrationTests.Infrastructure;

// Minimal client for Toxiproxy's REST API: just what the failure tests need.
public sealed class Toxiproxy(HttpClient http)
{
    public async Task CreateProxy(string name, string listen, string upstream)
    {
        var response = await http.PostAsJsonAsync("/proxies", new { name, listen, upstream, enabled = true });
        response.EnsureSuccessStatusCode();
    }

    // Disabled = every connection through the proxy is refused and existing ones closed.
    public async Task SetEnabled(string proxy, bool enabled)
    {
        var response = await http.PostAsJsonAsync($"/proxies/{proxy}", new { enabled });
        response.EnsureSuccessStatusCode();
    }

    // "timeout" on the downstream stream: the request reaches the server and the server
    // does its work, but the response is DROPPED and the connection closed after
    // `closeAfterMs`. (timeout=0 would only hold the bytes and flush them when the toxic
    // is removed, so a slow client could still get the answer.)
    public async Task DropResponses(string proxy, int closeAfterMs = 300, string toxicName = "drop-responses")
    {
        var response = await http.PostAsJsonAsync($"/proxies/{proxy}/toxics", new
        {
            name = toxicName,
            type = "timeout",
            stream = "downstream",
            toxicity = 1.0,
            attributes = new { timeout = closeAfterMs }
        });
        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveToxic(string proxy, string toxicName = "drop-responses")
    {
        var response = await http.DeleteAsync($"/proxies/{proxy}/toxics/{toxicName}");
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound) response.EnsureSuccessStatusCode();
    }

    // Back to a clean proxy: enabled, no toxics. Every test starts from here.
    public async Task Reset()
    {
        var response = await http.PostAsync("/reset", null);
        response.EnsureSuccessStatusCode();
    }
}
