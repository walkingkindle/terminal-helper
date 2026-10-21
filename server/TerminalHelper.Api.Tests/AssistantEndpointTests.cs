using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TerminalHelper.Api.Tests;

public sealed class AssistantEndpointTests : IDisposable
{
    static readonly object Arch = new { os = "Linux", distro = "Arch Linux", shell = "bash" };

    readonly ApiFactory _factory = new();
    readonly HttpClient _http;

    public AssistantEndpointTests() => _http = _factory.CreateClient();

    public void Dispose() => _factory.Dispose();

    async Task<List<(string Type, JsonElement Data)>> PostStream(string path, object body)
    {
        var response = await _http.PostAsJsonAsync(path, body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        return Sse.Parse(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ask_streams_the_answer_as_deltas_then_done()
    {
        const string answer = """{"answer":"","commands":[{"command":"pwd","label":"Print working directory","where":"","explanation":"","parts":[]}],"note":""}""";
        _factory.Upstream.Respond = () => FakeAnthropic.Stream(answer);

        var events = await PostStream("/api/ask", new { question = "how do I print the working directory?", env = Arch, verbose = false });

        var text = string.Concat(events.Where(e => e.Type == "delta").Select(e => e.Data.GetProperty("text").GetString()));
        Assert.Equal(answer, text);
        var (type, done) = events[^1];
        Assert.Equal("done", type);
        Assert.Equal("end_turn", done.GetProperty("stopReason").GetString());
        Assert.Equal(321, done.GetProperty("inputTokens").GetInt64());
        Assert.Equal(42, done.GetProperty("outputTokens").GetInt64());
    }

    [Fact]
    public async Task Ask_sends_haiku_a_schema_the_environment_and_follow_up_history()
    {
        await PostStream("/api/ask", new
        {
            question = "and in the background?",
            env = new { os = "Linux", distro = "Arch Linux", shell = "zsh", notes = "I use yay\nfor the AUR" },
            verbose = false,
            history = new[] { new { question = "run a server", answer = """{"answer":"","commands":[],"note":""}""" } },
        });

        var request = Assert.Single(_factory.Upstream.Requests).RootElement;
        Assert.Equal("claude-haiku-5-5", request.GetProperty("model").GetString());
        Assert.True(request.GetProperty("stream").GetBoolean());
        Assert.Equal("disabled", request.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("low", request.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("json_schema", request.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.False(request.TryGetProperty("temperature", out _));

        var system = request.GetProperty("system").GetString()!;
        Assert.Contains("- OS: Linux (Arch Linux)", system);
        Assert.Contains("- Shell: zsh", system);
        Assert.Contains("- Also: I use yay for the AUR", system);
        Assert.Contains("Concise mode", system);

        var messages = request.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "assistant", "user"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.Equal("and in the background?", messages[^1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Verbose_asks_for_a_man_page_style_breakdown()
    {
        await PostStream("/api/ask", new { question = "tar a folder", env = Arch, verbose = true });

        var system = Assert.Single(_factory.Upstream.Requests).RootElement.GetProperty("system").GetString()!;
        Assert.Contains("Verbose mode", system);
        Assert.DoesNotContain("Concise mode", system);
    }

    [Fact]
    public async Task Explain_breaks_down_the_exact_command()
    {
        _factory.Upstream.Respond = () => FakeAnthropic.Stream("""{"explanation":"Lists files.","parts":[{"token":"ls","meaning":"Lists."}]}""");

        var events = await PostStream("/api/explain", new { command = "ls -la", env = new { os = "Windows", shell = "Git Bash" } });

        Assert.Equal("done", events[^1].Type);
        var request = Assert.Single(_factory.Upstream.Requests).RootElement;
        var schema = request.GetProperty("output_config").GetProperty("format").GetProperty("schema");
        Assert.True(schema.GetProperty("properties").TryGetProperty("parts", out _));
        Assert.Contains("ls -la", request.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.Contains("- Shell: Git Bash", request.GetProperty("system").GetString());
    }

    [Theory]
    [InlineData("""{"question":"","env":{"os":"Linux","shell":"bash"}}""", "question")]
    [InlineData("""{"question":"pwd?"}""", "env")]
    [InlineData("""{"question":"pwd?","env":{"os":"Linux"}}""", "env.shell")]
    public async Task Invalid_requests_are_rejected_before_calling_claude(string json, string field)
    {
        var response = await _http.PostAsync("/api/ask", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
        Assert.Empty(_factory.Upstream.Requests);
    }

    [Fact]
    public async Task Junk_history_entries_are_skipped()
    {
        const string json = """{"question":"pwd?","env":{"os":"Linux","shell":"bash"},"history":[null,{"question":"","answer":"x"}]}""";
        var response = await _http.PostAsync("/api/ask", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var messages = Assert.Single(_factory.Upstream.Requests).RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
    }

    [Fact]
    public async Task Overlong_questions_are_rejected()
    {
        var response = await _http.PostAsJsonAsync("/api/ask", new { question = new string('x', 2_001), env = Arch });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_bad_api_key_becomes_an_error_event()
    {
        _factory.Upstream.Respond = () => FakeAnthropic.Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid x-api-key");

        var events = await PostStream("/api/ask", new { question = "pwd?", env = Arch });

        var (type, error) = Assert.Single(events);
        Assert.Equal("error", type);
        Assert.Equal("auth", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Overloaded_upstream_becomes_an_error_event()
    {
        _factory.Upstream.Respond = () => FakeAnthropic.Error((HttpStatusCode)529, "overloaded_error", "Overloaded");

        var events = await PostStream("/api/ask", new { question = "pwd?", env = Arch });

        Assert.Equal("upstream", Assert.Single(events).Data.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Refusals_are_reported_in_done()
    {
        _factory.Upstream.Respond = () => FakeAnthropic.Stream("", stopReason: "refusal");

        var events = await PostStream("/api/ask", new { question = "pwd?", env = Arch });

        Assert.Equal("refusal", events[^1].Data.GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task Health_reports_the_model()
    {
        var health = await _http.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("claude-haiku-5-5", health.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Unknown_api_routes_are_404_not_the_spa()
    {
        var response = await _http.GetAsync("/api/nope");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
