using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace TerminalHelper.Api.Tests;

/// <summary>Stands in for api.anthropic.com: records each request and replies with a canned response.</summary>
public sealed class FakeAnthropic : HttpMessageHandler
{
    public List<JsonDocument> Requests { get; } = [];

    public Func<HttpResponseMessage> Respond { get; set; } = () => Stream("""{"answer":"","commands":[],"note":""}""");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
        return Respond();
    }

    /// <summary>A Messages API stream that emits <paramref name="text"/> in small text deltas.</summary>
    public static HttpResponseMessage Stream(string text, string stopReason = "end_turn")
    {
        var sse = new StringBuilder();
        void Event(string type, object data) => sse.Append($"event: {type}\ndata: {JsonSerializer.Serialize(data)}\n\n");

        Event("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = "msg_test", type = "message", role = "assistant", model = "claude-haiku-5-5",
                content = Array.Empty<object>(), stop_reason = (string?)null, stop_sequence = (string?)null,
                usage = new { input_tokens = 321, output_tokens = 1 },
            },
        });
        Event("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
        for (var i = 0; i < text.Length; i += 7)
        {
            var chunk = text.Substring(i, Math.Min(7, text.Length - i));
            Event("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = chunk } });
        }
        Event("content_block_stop", new { type = "content_block_stop", index = 0 });
        Event("message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { output_tokens = 42 },
        });
        Event("message_stop", new { type = "message_stop" });

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    public static HttpResponseMessage Error(HttpStatusCode status, string type, string message) => new(status)
    {
        Content = new StringContent(
            JsonSerializer.Serialize(new { type = "error", error = new { type, message } }), Encoding.UTF8, "application/json"),
    };
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public FakeAnthropic Upstream { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services => services.AddSingleton(
            new AnthropicClient { ApiKey = "test-key", HttpClient = new HttpClient(Upstream), MaxRetries = 0 }));
    }
}

/// <summary>Parses the app's own text/event-stream output.</summary>
public static class Sse
{
    public static List<(string Type, JsonElement Data)> Parse(string body) =>
        body.Replace("\r\n", "\n")
            .Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(block =>
            {
                var lines = block.Split('\n');
                var type = lines.FirstOrDefault(l => l.StartsWith("event:"))?["event:".Length..].Trim() ?? "message";
                var data = string.Join("\n", lines.Where(l => l.StartsWith("data:")).Select(l => l["data:".Length..].TrimStart()));
                return (type, JsonDocument.Parse(data).RootElement.Clone());
            })
            .ToList();
}
