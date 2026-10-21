using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TerminalHelper.Api.Assistant;

namespace TerminalHelper.Api.Storage;

public sealed record KeyState(bool Set, string? Hint, string? Source, bool? Verified = null);

public sealed record SettingsResponse(string Model, KeyState ApiKey, string DataDir);

public sealed record ApiKeyRequest(string? ApiKey);

public sealed record NotesResponse(long Revision, JsonElement Notes);

public sealed record SaveNotesRequest(long Revision, JsonElement Notes);

public sealed record NotesSaved(long Revision);

/// <summary>What a self-hosted install keeps locally: the board and the API key.</summary>
public static class LocalEndpoints
{
    public static void MapLocalEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/settings", (ApiKeyStore keys, DataFolder folder, IOptions<AssistantOptions> options) =>
            new SettingsResponse(options.Value.Model, StateOf(keys), folder.Path));

        api.MapPut("/settings/api-key", async Task<Results<Ok<KeyState>, ValidationProblem, ProblemHttpResult>> (
            ApiKeyRequest request, ApiKeyStore keys, ClaudeClients clients, CancellationToken ct) =>
        {
            var key = request.ApiKey?.Trim() ?? "";
            if (key.Length is < 10 or > 300 || key.Any(char.IsWhiteSpace))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["apiKey"] = ["That doesn't look like an API key."] });

            // Try it first, so a typo shows up here instead of on the first question.
            var check = await clients.CheckAsync(key, ct);
            if (check == KeyCheck.Rejected)
                return TypedResults.Problem("Anthropic didn't accept that key. Check it and try again.", statusCode: StatusCodes.Status422UnprocessableEntity);

            await keys.SaveAsync(key, ct);
            return TypedResults.Ok(StateOf(keys) with { Verified = check == KeyCheck.Valid });
        });

        api.MapDelete("/settings/api-key", async (ApiKeyStore keys) =>
        {
            await keys.RemoveAsync();
            return StateOf(keys);
        });

        api.MapGet("/notes", async Task<Results<Ok<NotesResponse>, ProblemHttpResult>> (NotesStore store, CancellationToken ct) =>
        {
            try
            {
                var notes = await store.ReadAsync(ct);
                return TypedResults.Ok(new NotesResponse(notes.Revision, notes.Notes));
            }
            catch (JsonException)
            {
                return Unreadable(store);
            }
        });

        api.MapPut("/notes", async Task<Results<Ok<NotesSaved>, Conflict<NotesResponse>, ValidationProblem, ProblemHttpResult>> (
            SaveNotesRequest request, NotesStore store, CancellationToken ct) =>
        {
            if (request.Notes.ValueKind != JsonValueKind.Array || request.Notes.EnumerateArray().Any(n => n.ValueKind != JsonValueKind.Object))
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["notes"] = ["Expected an array of notes."] });
            try
            {
                var result = await store.SaveAsync(request.Revision, request.Notes, ct);
                return result.Saved
                    ? TypedResults.Ok(new NotesSaved(result.Document.Revision))
                    : TypedResults.Conflict(new NotesResponse(result.Document.Revision, result.Document.Notes));
            }
            catch (JsonException)
            {
                return Unreadable(store);
            }
        }).WithMetadata(new RequestSizeLimitAttribute(32 * 1024 * 1024));
    }

    public static KeyState StateOf(ApiKeyStore keys)
    {
        var (key, source) = keys.Current;
        return key is null
            ? new KeyState(false, null, null)
            : new KeyState(true, ApiKeyStore.Hint(key), source.ToString().ToLowerInvariant());
    }

    /// <summary>Never overwrite a notes file that couldn't be parsed; say where it is so it can be fixed.</summary>
    static ProblemHttpResult Unreadable(NotesStore store) => TypedResults.Problem(
        $"{store.FilePath} isn't valid JSON, so it was left untouched. Fix it, or move it away to start a new board (the previous version is notes.json.bak).",
        title: "Your notes file can't be read",
        statusCode: StatusCodes.Status500InternalServerError);
}
