using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TerminalHelper.Api.Tests;

/// <summary>Choosing and setting up an AI provider: Claude, OpenAI or a local model server.</summary>
public sealed class ProviderSettingsTests : IDisposable
{
    static readonly object Arch = new { os = "Linux", distro = "Arch Linux", shell = "bash" };

    readonly ApiFactory _factory;
    readonly HttpClient _http;

    public ProviderSettingsTests() : this(null) { }

    ProviderSettingsTests(Dictionary<string, string?>? settings)
    {
        _factory = new ApiFactory(settings);
        _http = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    Task<HttpResponseMessage> Save(string provider, object update) => _http.PutAsJsonAsync($"/api/settings/providers/{provider}", update);

    async Task<List<(string Type, JsonElement Data)>> Ask()
    {
        var response = await _http.PostAsJsonAsync("/api/ask", new { question = "how do I print the working directory?", env = Arch });
        return Sse.Parse(await response.Content.ReadAsStringAsync());
    }

    async Task<JsonElement> Provider(string id) =>
        (await _http.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("providers").EnumerateArray().Single(p => p.GetProperty("id").GetString() == id);

    [Fact]
    public async Task A_new_install_lists_every_provider_with_claude_selected_but_not_set_up()
    {
        var settings = await _http.GetFromJsonAsync<JsonElement>("/api/settings");

        Assert.Equal("anthropic", settings.GetProperty("active").GetString());
        Assert.Equal(_factory.DataDir, settings.GetProperty("dataDir").GetString());
        var providers = settings.GetProperty("providers").EnumerateArray().ToList();
        Assert.Equal(["anthropic", "openai", "local"], providers.Select(p => p.GetProperty("id").GetString()));
        Assert.All(providers, p => Assert.False(p.GetProperty("ready").GetBoolean()));
        Assert.Equal("gpt-5.4-mini", providers[1].GetProperty("model").GetString());
        Assert.Equal("http://localhost:11434/v1", providers[2].GetProperty("baseUrl").GetString());
        var health = await _http.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.False(health.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task A_claude_key_is_checked_then_saved_for_the_owner_only()
    {
        var saved = await Save("anthropic", new { apiKey = " sk-ant-test-key-1234 " });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var result = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(result.GetProperty("verified").GetBoolean());
        var provider = result.GetProperty("provider");
        Assert.Equal("…1234", provider.GetProperty("apiKey").GetProperty("hint").GetString());
        Assert.Equal("saved", provider.GetProperty("apiKey").GetProperty("source").GetString());
        Assert.True(provider.GetProperty("ready").GetBoolean());
        Assert.Equal("sk-ant-test-key-1234", _factory.Upstream.Keys.Single());

        var file = Path.Combine(_factory.DataDir, "settings.json");
        Assert.Contains("sk-ant-test-key-1234", await File.ReadAllTextAsync(file));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.True((await _http.GetFromJsonAsync<JsonElement>("/api/health")).GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Questions_use_the_newest_saved_key()
    {
        await Save("anthropic", new { apiKey = "sk-ant-first-key-0001" });
        await Save("anthropic", new { apiKey = "sk-ant-second-key-0002" });

        await Ask();

        Assert.Equal("sk-ant-second-key-0002", _factory.Upstream.Keys[^1]);
    }

    [Fact]
    public async Task A_rejected_key_is_not_saved()
    {
        _factory.Upstream.RespondToModels = () => FakeAnthropic.Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid x-api-key");

        var response = await Save("anthropic", new { apiKey = "sk-ant-wrong-key-9999" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("didn't accept", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(Path.Combine(_factory.DataDir, "settings.json")));
    }

    [Fact]
    public async Task A_cloud_key_that_cannot_be_checked_offline_is_still_saved()
    {
        _factory.Upstream.RespondToModels = () => throw new HttpRequestException("offline");

        var response = await Save("anthropic", new { apiKey = "sk-ant-offline-key-5555" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verified").GetBoolean());
    }

    [Theory]
    [InlineData("short")]
    [InlineData("sk-ant-has spaces-in-it")]
    public async Task Things_that_are_not_keys_are_refused(string apiKey)
    {
        var response = await Save("anthropic", new { apiKey });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Upstream.Keys);
    }

    [Fact]
    public async Task Removing_the_saved_key_falls_back_to_the_configured_one()
    {
        using var configured = new ProviderSettingsTests(new() { ["Anthropic:ApiKey"] = "sk-ant-from-config-7777" });
        await configured.Save("anthropic", new { apiKey = "sk-ant-saved-key-1111" });

        var whileSaved = await configured.Provider("anthropic");
        var removed = await configured._http.DeleteAsync("/api/settings/providers/anthropic/api-key");

        Assert.Equal("saved", whileSaved.GetProperty("apiKey").GetProperty("source").GetString());
        var key = (await removed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey");
        Assert.Equal("config", key.GetProperty("source").GetString());
        Assert.Equal("…7777", key.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task A_key_saved_by_the_first_version_still_works()
    {
        await File.WriteAllTextAsync(Path.Combine(_factory.DataDir, "settings.json"), """{ "apiKey": "sk-ant-legacy-key-3333" }""");

        var claude = await Provider("anthropic");

        Assert.Equal("…3333", claude.GetProperty("apiKey").GetProperty("hint").GetString());
        Assert.True(claude.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Saving_an_openai_key_switches_answers_to_openai()
    {
        var saved = await Save("openai", new { apiKey = "sk-proj-openai-key-4321" });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var health = await _http.GetFromJsonAsync<JsonElement>("/api/health");
        Assert.Equal("openai", health.GetProperty("provider").GetString());
        Assert.Equal("gpt-5.4-mini", health.GetProperty("model").GetString());

        var events = await Ask();

        var text = string.Concat(events.Where(e => e.Type == "delta").Select(e => e.Data.GetProperty("text").GetString()));
        Assert.Contains("\"command\":\"pwd\"", text);
        var done = events[^1].Data;
        Assert.Equal("end_turn", done.GetProperty("stopReason").GetString());
        Assert.Equal("openai", done.GetProperty("provider").GetString());
        Assert.Equal("gpt-5.4-mini", done.GetProperty("model").GetString());
        Assert.Equal(111, done.GetProperty("inputTokens").GetInt64());
        Assert.Equal(22, done.GetProperty("outputTokens").GetInt64());
        Assert.Empty(_factory.Upstream.Requests); // Claude wasn't asked

        var request = Assert.Single(_factory.OpenAi.ChatRequests);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.Uri.ToString());
        Assert.Equal("Bearer sk-proj-openai-key-4321", request.Authorization);
        Assert.NotNull(request.ContentLength); // not chunked: some local servers can't read that
        var body = request.Body!.Value;
        Assert.Equal("gpt-5.4-mini", body.GetProperty("model").GetString());
        Assert.True(body.GetProperty("stream").GetBoolean());
        Assert.Equal(4096, body.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(body.TryGetProperty("max_tokens", out _));
        var format = body.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(format.GetProperty("json_schema").GetProperty("strict").GetBoolean());
        var messages = body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("Arch Linux", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[^1].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData("length", null, "max_tokens")]
    [InlineData("stop", "I can't help with that.", "refusal")]
    public async Task Cut_off_and_refused_openai_answers_are_reported(string finishReason, string? refusal, string expected)
    {
        await Save("openai", new { apiKey = "sk-proj-openai-key-4321" });
        _factory.OpenAi.RespondToChat = () => FakeOpenAi.Stream("""{"answer":"","comm""", finishReason, refusal);

        var events = await Ask();

        Assert.Equal(expected, events[^1].Data.GetProperty("stopReason").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "Incorrect API key provided", "invalid_api_key", "auth", "didn't accept")]
    [InlineData(HttpStatusCode.NotFound, "The model `gpt-9` does not exist", "model_not_found", "setup", "no model called")]
    [InlineData(HttpStatusCode.TooManyRequests, "You exceeded your current quota", "insufficient_quota", "rate_limit", "out of credits")]
    public async Task OpenAi_errors_read_like_the_rest_of_the_app(HttpStatusCode status, string message, string code, string expectedCode, string expectedText)
    {
        await Save("openai", new { apiKey = "sk-proj-openai-key-4321" });
        _factory.OpenAi.RespondToChat = () => FakeOpenAi.Error(status, message, code);

        var (type, error) = Assert.Single(await Ask());

        Assert.Equal("error", type);
        Assert.Equal(expectedCode, error.GetProperty("code").GetString());
        Assert.Contains(expectedText, error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task OpenAi_cannot_be_picked_without_a_key()
    {
        var response = await Save("openai", new { });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("API key", await response.Content.ReadAsStringAsync());
        Assert.Equal("anthropic", (await _http.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("active").GetString());
    }

    [Fact]
    public async Task A_local_model_server_answers_without_a_key()
    {
        var saved = await Save("local", new { baseUrl = "http://local.test:11434/v1/", model = "qwen2.5-coder:7b" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var events = await Ask();

        Assert.Equal("done", events[^1].Type);
        Assert.Equal("qwen2.5-coder:7b", events[^1].Data.GetProperty("model").GetString());
        var request = Assert.Single(_factory.OpenAi.ChatRequests);
        Assert.Equal("http://local.test:11434/v1/chat/completions", request.Uri.ToString());
        Assert.Null(request.Authorization);
        Assert.Equal(4096, request.Body!.Value.GetProperty("max_tokens").GetInt32());
        Assert.False(request.Body.Value.TryGetProperty("max_completion_tokens", out _));
    }

    [Fact]
    public async Task A_model_the_local_server_lacks_is_refused_with_what_it_has()
    {
        var response = await Save("local", new { baseUrl = "http://local.test:11434/v1", model = "llama-9000" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var detail = await response.Content.ReadAsStringAsync();
        Assert.Contains("llama-9000", detail);
        Assert.Contains("qwen2.5-coder:7b", detail);
    }

    [Fact]
    public async Task A_local_server_that_is_not_running_is_refused()
    {
        _factory.OpenAi.RespondToModels = () => throw new HttpRequestException("Connection refused");

        var response = await Save("local", new { baseUrl = "http://localhost:11434/v1", model = "qwen2.5-coder:7b" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Can't reach http://localhost:11434/v1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_model_picker_lists_what_a_server_has()
    {
        var response = await _http.PostAsJsonAsync("/api/settings/providers/local/models", new { baseUrl = "http://local.test:11434/v1" });

        var models = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("models");
        Assert.Equal(["gpt-5.4-mini", "gpt-5.4-nano", "qwen2.5-coder:7b"], models.EnumerateArray().Select(m => m.GetString()));
        Assert.False(File.Exists(Path.Combine(_factory.DataDir, "settings.json")));
    }

    [Theory]
    [InlineData("ftp://local.test/v1")]
    [InlineData("localhost:11434")]
    public async Task Server_addresses_must_be_http_urls(string baseUrl)
    {
        var response = await Save("local", new { baseUrl, model = "qwen2.5-coder:7b" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Each_provider_keeps_its_settings_when_switching_back_and_forth()
    {
        await Save("openai", new { apiKey = "sk-proj-openai-key-4321", model = "gpt-5.4-nano" });
        await Save("anthropic", new { apiKey = "sk-ant-test-key-1234" });

        var settings = await _http.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal("anthropic", settings.GetProperty("active").GetString());
        var openai = await Provider("openai");
        Assert.Equal("gpt-5.4-nano", openai.GetProperty("model").GetString());
        Assert.Equal("…4321", openai.GetProperty("apiKey").GetProperty("hint").GetString());

        // Switching back needs no new values: saving nothing just picks it again.
        Assert.Equal(HttpStatusCode.OK, (await Save("openai", new { })).StatusCode);
        Assert.Equal("openai", (await _http.GetFromJsonAsync<JsonElement>("/api/health")).GetProperty("provider").GetString());
    }

    [Fact]
    public async Task Asking_before_the_chosen_provider_is_set_up_says_what_to_do()
    {
        await File.WriteAllTextAsync(Path.Combine(_factory.DataDir, "settings.json"), """{ "provider": "local" }""");

        var (type, error) = Assert.Single(await Ask());

        Assert.Equal("error", type);
        Assert.Equal("setup", error.GetProperty("code").GetString());
        Assert.Contains("Settings", error.GetProperty("message").GetString());
        Assert.Empty(_factory.OpenAi.Requests);
    }

    [Fact]
    public async Task Unknown_providers_are_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Save("gemini", new { apiKey = "sk-whatever-123456" })).StatusCode);
    }
}
