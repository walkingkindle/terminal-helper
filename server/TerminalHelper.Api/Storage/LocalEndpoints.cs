using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using TerminalHelper.Api.Providers;

namespace TerminalHelper.Api.Storage;

public sealed record KeyState(bool Set, string? Hint, string? Source);

public sealed record ProviderState(
    string Id,
    string Name,
    bool Active,
    bool Ready,
    string Model,
    string? BaseUrl,
    bool RequiresKey,
    bool EditableBaseUrl,
    KeyState ApiKey);

public sealed record SettingsResponse(string Active, IReadOnlyList<ProviderState> Providers, string DataDir);

public sealed record ProviderSaved(ProviderState Provider, bool Verified, IReadOnlyList<string> Models);

public sealed record ModelsResponse(IReadOnlyList<string> Models);

public sealed record NotesResponse(long Revision, JsonElement Notes);

public sealed record SaveNotesRequest(long Revision, JsonElement Notes);

public sealed record NotesSaved(long Revision);

/// <summary>What a self-hosted install keeps locally: the board, and which AI to use with which key.</summary>
public static class LocalEndpoints
{
    public static void MapLocalEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/settings", (SettingsStore settings, DataFolder folder) => new SettingsResponse(
            settings.ActiveId,
            ProviderCatalog.All.Select(p => StateOf(settings, p.Id)).ToList(),
            folder.Path));

        // Check the new values with the provider first, so a typo shows up here instead of on the
        // first question; then save them and make this provider the one that answers.
        api.MapPut("/settings/providers/{id}", async Task<Results<Ok<ProviderSaved>, NotFound, ValidationProblem, ProblemHttpResult>> (
            string id, ProviderUpdate update, SettingsStore settings, IEnumerable<IModelProvider> providers, CancellationToken ct) =>
        {
            if (ProviderCatalog.Find(id) is not { } info) return TypedResults.NotFound();
            update = Clean(update, info);
            var errors = Validate(update);
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

            var candidate = Merge(settings.Get(id), update);
            if (!SettingsStore.IsReady(candidate))
                return Problem(info.RequiresKey && candidate.ApiKey is null ? "Paste an API key first." : "Pick a model first.");

            var check = await providers.Single(p => p.Id == id).CheckAsync(candidate, ct);
            switch (check.Outcome)
            {
                case CheckOutcome.Rejected:
                    return Problem(check.Message!);
                // A local server has to be running to be any use; a cloud API may just be unreachable for a moment.
                case CheckOutcome.Unreachable when info == ProviderCatalog.Local:
                    return Problem(check.Message!);
                case CheckOutcome.Valid when info != ProviderCatalog.Anthropic && check.Models.Count > 0 && !check.Models.Contains(candidate.Model):
                    return Problem($"{candidate.BaseUrl} has no model called “{candidate.Model}”. It has: {string.Join(", ", check.Models.Take(12))}.");
            }

            await settings.SaveAsync(id, update, ct);
            return TypedResults.Ok(new ProviderSaved(StateOf(settings, id), check.Outcome == CheckOutcome.Valid, check.Models));
        });

        api.MapDelete("/settings/providers/{id}/api-key", async Task<Results<Ok<ProviderState>, NotFound>> (
            string id, SettingsStore settings, CancellationToken ct) =>
        {
            if (ProviderCatalog.Find(id) is null) return TypedResults.NotFound();
            await settings.RemoveKeyAsync(id, ct);
            return TypedResults.Ok(StateOf(settings, id));
        });

        // The models a server offers, for the model picker; nothing is saved.
        api.MapPost("/settings/providers/{id}/models", async Task<Results<Ok<ModelsResponse>, NotFound, ValidationProblem, ProblemHttpResult>> (
            string id, ProviderUpdate probe, SettingsStore settings, IEnumerable<IModelProvider> providers, CancellationToken ct) =>
        {
            if (ProviderCatalog.Find(id) is not { } info) return TypedResults.NotFound();
            probe = Clean(probe, info);
            var errors = Validate(probe);
            if (errors.Count > 0) return TypedResults.ValidationProblem(errors);

            var check = await providers.Single(p => p.Id == id).CheckAsync(Merge(settings.Get(id), probe), ct);
            return check.Outcome is CheckOutcome.Valid or CheckOutcome.Unknown
                ? TypedResults.Ok(new ModelsResponse(check.Models))
                : Problem(check.Message ?? "Couldn't list the models.");
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

    public static ProviderState StateOf(SettingsStore settings, string id)
    {
        var current = settings.Get(id);
        var key = current.ApiKey is null
            ? new KeyState(false, null, null)
            : new KeyState(true, SettingsStore.Hint(current.ApiKey), current.KeySource.ToString().ToLowerInvariant());
        return new ProviderState(
            id, current.Info.Name, settings.ActiveId == id, SettingsStore.IsReady(current), current.Model, current.BaseUrl,
            current.Info.RequiresKey, current.Info.EditableBaseUrl, key);
    }

    /// <summary>Trims the values, treats blanks as "unchanged", and ignores a server URL that can't be edited.</summary>
    static ProviderUpdate Clean(ProviderUpdate update, ProviderInfo info) => new(
        Blank(update.ApiKey),
        Blank(update.Model),
        info.EditableBaseUrl ? Blank(update.BaseUrl) : null);

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    static ProviderSettings Merge(ProviderSettings current, ProviderUpdate update) => current with
    {
        ApiKey = update.ApiKey ?? current.ApiKey,
        Model = update.Model ?? current.Model,
        BaseUrl = update.BaseUrl ?? current.BaseUrl,
    };

    static Dictionary<string, string[]> Validate(ProviderUpdate update)
    {
        var errors = new Dictionary<string, string[]>();
        if (update.ApiKey is { } key && (key.Length is < 10 or > 300 || key.Any(char.IsWhiteSpace)))
            errors["apiKey"] = ["That doesn't look like an API key."];
        if (update.Model is { } model && (model.Length > 200 || model.Any(char.IsWhiteSpace)))
            errors["model"] = ["That doesn't look like a model name."];
        if (update.BaseUrl is { } url && (url.Length > 300
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            errors["baseUrl"] = ["Use a full http:// or https:// address, like http://localhost:11434/v1."];
        return errors;
    }

    static ProblemHttpResult Problem(string message) =>
        TypedResults.Problem(message, statusCode: StatusCodes.Status422UnprocessableEntity);

    /// <summary>Never overwrite a notes file that couldn't be parsed; say where it is so it can be fixed.</summary>
    static ProblemHttpResult Unreadable(NotesStore store) => TypedResults.Problem(
        $"{store.FilePath} isn't valid JSON, so it was left untouched. Fix it, or move it away to start a new board (the previous version is notes.json.bak).",
        title: "Your notes file can't be read",
        statusCode: StatusCodes.Status500InternalServerError);
}
