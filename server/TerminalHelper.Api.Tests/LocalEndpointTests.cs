using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TerminalHelper.Api.Tests;

public sealed class LocalEndpointTests : IDisposable
{
    readonly ApiFactory _factory;
    readonly HttpClient _http;

    public LocalEndpointTests() : this(null) { }

    LocalEndpointTests(Dictionary<string, string?>? settings)
    {
        _factory = new ApiFactory(settings);
        _http = _factory.CreateClient();
    }

    public void Dispose() => _factory.Dispose();

    static object Note(string id, string command) => new { id, label = command, command, color = "yellow" };

    string NotesFile => Path.Combine(_factory.DataDir, "notes.json");

    [Fact]
    public async Task A_new_install_has_an_empty_board()
    {
        var board = await _http.GetFromJsonAsync<JsonElement>("/api/notes");

        Assert.Equal(0, board.GetProperty("revision").GetInt64());
        Assert.Equal(0, board.GetProperty("notes").GetArrayLength());
        Assert.False(File.Exists(NotesFile));
    }

    [Fact]
    public async Task Saving_writes_the_board_to_the_data_folder()
    {
        var saved = await _http.PutAsJsonAsync("/api/notes", new { revision = 0, notes = new[] { Note("a", "pwd") } });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(1, (await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64());
        var file = JsonDocument.Parse(await File.ReadAllTextAsync(NotesFile)).RootElement;
        Assert.Equal("pwd", file.GetProperty("notes")[0].GetProperty("command").GetString());

        var board = await _http.GetFromJsonAsync<JsonElement>("/api/notes");
        Assert.Equal(1, board.GetProperty("revision").GetInt64());
        Assert.Equal("a", board.GetProperty("notes")[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_save_from_a_stale_window_gets_the_newer_board_back()
    {
        await _http.PutAsJsonAsync("/api/notes", new { revision = 0, notes = new[] { Note("a", "pwd") } });

        var stale = await _http.PutAsJsonAsync("/api/notes", new { revision = 0, notes = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var newer = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, newer.GetProperty("revision").GetInt64());
        Assert.Equal(1, newer.GetProperty("notes").GetArrayLength());
    }

    [Fact]
    public async Task The_previous_version_is_kept_as_a_backup()
    {
        await _http.PutAsJsonAsync("/api/notes", new { revision = 0, notes = new[] { Note("a", "pwd") } });
        await _http.PutAsJsonAsync("/api/notes", new { revision = 1, notes = Array.Empty<object>() });

        var backup = JsonDocument.Parse(await File.ReadAllTextAsync(NotesFile + ".bak")).RootElement;
        Assert.Equal(1, backup.GetProperty("notes").GetArrayLength());
    }

    [Fact]
    public async Task Big_boards_fit()
    {
        var notes = Enumerable.Range(0, 3000).Select(i => Note($"n{i}", $"echo {new string('x', 200)} {i}")).ToArray();

        var saved = await _http.PutAsJsonAsync("/api/notes", new { revision = 0, notes });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    }

    [Theory]
    [InlineData("""{"revision":0,"notes":{}}""")]
    [InlineData("""{"revision":0,"notes":[1,2]}""")]
    public async Task Only_lists_of_notes_are_accepted(string body)
    {
        var response = await _http.PutAsync("/api/notes", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_damaged_notes_file_is_reported_and_never_overwritten()
    {
        await File.WriteAllTextAsync(NotesFile, "{ not json");

        var read = await _http.GetAsync("/api/notes");
        var save = await _http.PutAsJsonAsync("/api/notes", new { revision = 0, notes = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.InternalServerError, read.StatusCode);
        Assert.Contains("notes.json", await read.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.InternalServerError, save.StatusCode);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(NotesFile));
    }

    [Fact]
    public async Task Settings_report_the_data_folder_and_a_missing_key()
    {
        var settings = await _http.GetFromJsonAsync<JsonElement>("/api/settings");

        Assert.Equal(_factory.DataDir, settings.GetProperty("dataDir").GetString());
        Assert.False(settings.GetProperty("apiKey").GetProperty("set").GetBoolean());
        Assert.False((await _http.GetFromJsonAsync<JsonElement>("/api/health")).GetProperty("credentials").GetBoolean());
    }

    [Fact]
    public async Task A_key_is_checked_with_anthropic_then_saved_for_the_owner_only()
    {
        var saved = await _http.PutAsJsonAsync("/api/settings/api-key", new { apiKey = " sk-ant-test-key-1234 " });

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var state = await saved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("…1234", state.GetProperty("hint").GetString());
        Assert.Equal("saved", state.GetProperty("source").GetString());
        Assert.True(state.GetProperty("verified").GetBoolean());
        Assert.Equal("sk-ant-test-key-1234", _factory.Upstream.Keys.Single());

        var file = Path.Combine(_factory.DataDir, "settings.json");
        Assert.Contains("sk-ant-test-key-1234", await File.ReadAllTextAsync(file));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        Assert.True((await _http.GetFromJsonAsync<JsonElement>("/api/health")).GetProperty("credentials").GetBoolean());
    }

    [Fact]
    public async Task Questions_use_the_saved_key_right_away()
    {
        await _http.PutAsJsonAsync("/api/settings/api-key", new { apiKey = "sk-ant-first-key-0001" });
        await _http.PutAsJsonAsync("/api/settings/api-key", new { apiKey = "sk-ant-second-key-0002" });

        var response = await _http.PostAsJsonAsync("/api/ask", new { question = "pwd?", env = new { os = "Linux", shell = "bash" } });
        await response.Content.ReadAsStringAsync();

        Assert.Equal("sk-ant-second-key-0002", _factory.Upstream.Keys[^1]);
    }

    [Fact]
    public async Task A_rejected_key_is_not_saved()
    {
        _factory.Upstream.RespondToModels = () => FakeAnthropic.Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid x-api-key");

        var response = await _http.PutAsJsonAsync("/api/settings/api-key", new { apiKey = "sk-ant-wrong-key-9999" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("didn't accept", await response.Content.ReadAsStringAsync());
        Assert.False(File.Exists(Path.Combine(_factory.DataDir, "settings.json")));
    }

    [Fact]
    public async Task A_key_that_cannot_be_checked_offline_is_still_saved()
    {
        _factory.Upstream.RespondToModels = () => throw new HttpRequestException("offline");

        var response = await _http.PutAsJsonAsync("/api/settings/api-key", new { apiKey = "sk-ant-offline-key-5555" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("verified").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("sk-ant-has spaces-in-it")]
    public async Task Things_that_are_not_keys_are_refused(string apiKey)
    {
        var response = await _http.PutAsJsonAsync("/api/settings/api-key", new { apiKey });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Upstream.Keys);
    }

    [Fact]
    public async Task Removing_the_saved_key_falls_back_to_the_configured_one()
    {
        using var configured = new LocalEndpointTests(new() { ["Anthropic:ApiKey"] = "sk-ant-from-config-7777" });
        await configured._http.PutAsJsonAsync("/api/settings/api-key", new { apiKey = "sk-ant-saved-key-1111" });

        var whileSaved = await configured._http.GetFromJsonAsync<JsonElement>("/api/settings");
        var removed = await configured._http.DeleteAsync("/api/settings/api-key");

        Assert.Equal("saved", whileSaved.GetProperty("apiKey").GetProperty("source").GetString());
        var state = await removed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("config", state.GetProperty("source").GetString());
        Assert.Equal("…7777", state.GetProperty("hint").GetString());
    }

    [Fact]
    public async Task Only_loopback_host_names_are_served()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = "attacker.example";

        var response = await _http.SendAsync(request);

        // Blocks DNS-rebinding pages from driving the local server (and spending the API key).
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
