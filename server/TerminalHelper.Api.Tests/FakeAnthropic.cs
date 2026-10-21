using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using TerminalHelper.Api.Assistant;

namespace TerminalHelper.Api.Tests;

/// <summary>Stands in for api.anthropic.com: records each request and replies with a canned response.</summary>
public sealed class FakeAnthropic : HttpMessageHandler
{
    /// <summary>Bodies of Messages API calls.</summary>
    public List<JsonDocument> Requests { get; } = [];

    /// <summary>The credential each request carried (x-api-key or bearer token), in order.</summary>
    public List<string?> Keys { get; } = [];

    public Func<HttpResponseMessage> Respond { get; set; } = () => Stream("""{"answer":"","commands":[],"note":""}""");

    /// <summary>GET /v1/models, which the app calls to check a key before saving it.</summary>
    public Func<HttpResponseMessage> RespondToModels { get; set; } = () => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"data":[],"has_more":false,"first_id":null,"last_id":null}""", Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Keys.Add(request.Headers.TryGetValues("x-api-key", out var key) ? key.First() : request.Headers.Authorization?.Parameter);
        if (request.RequestUri!.AbsolutePath.EndsWith("/models")) return RespondToModels();
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

    sealed class Factory(FakeAnthropic upstream) : IClaudeClientFactory
    {
        public AnthropicClient Create(string? apiKey) =>
            new() { ApiKey = apiKey ?? "no-key", HttpClient = new HttpClient(upstream), MaxRetries = 0 };
    }

    public IClaudeClientFactory ClientFactory() => new Factory(this);
}

/// <summary>The app wired to a fake Anthropic and its own empty data folder.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    readonly Dictionary<string, string?> _settings;

    public ApiFactory(Dictionary<string, string?>? settings = null)
    {
        DataDir = Directory.CreateTempSubdirectory("terminal-helper-tests-").FullName;
        _settings = new(settings ?? []) { ["Storage:DataDir"] = DataDir };
        // Tests must not pick up a key exported in the developer's shell.
        _settings.TryAdd("ANTHROPIC_API_KEY", "");
    }

    public FakeAnthropic Upstream { get; } = new();

    public string DataDir { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in _settings) builder.UseSetting(key, value);
        builder.ConfigureTestServices(services => services.AddSingleton(Upstream.ClientFactory()));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(DataDir)) Directory.Delete(DataDir, recursive: true);
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
