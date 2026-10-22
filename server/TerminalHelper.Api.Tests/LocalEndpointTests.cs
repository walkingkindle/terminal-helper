using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace TerminalHelper.Api.Tests;

public sealed class LocalEndpointTests : IDisposable
{
    readonly ApiFactory _factory;
    readonly HttpClient _http;

    public LocalEndpointTests()
    {
        _factory = new ApiFactory();
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
    public async Task Only_loopback_host_names_are_served()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = "attacker.example";

        var response = await _http.SendAsync(request);

        // Blocks DNS-rebinding pages from driving the local server (and spending the API key).
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
