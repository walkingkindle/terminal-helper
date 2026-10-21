using System.Text.Json;

namespace TerminalHelper.Api.Storage;

/// <summary>The board as the client last saved it; the client owns the note format.</summary>
public sealed record NotesDocument(long Revision, JsonElement Notes);

public sealed record SaveResult(bool Saved, NotesDocument Document);

/// <summary>
/// Keeps the board in <c>notes.json</c> in the data folder. Every save bumps a revision number, and a save
/// based on an old revision (another window saved in between) is refused rather than silently overwriting it.
/// </summary>
public sealed class NotesStore(DataFolder folder)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly JsonElement NoNotes = JsonDocument.Parse("[]").RootElement.Clone();

    readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath => folder.File("notes.json");

    public async Task<NotesDocument> ReadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await LoadAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SaveResult> SaveAsync(long expectedRevision, JsonElement notes, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var current = await LoadAsync(ct);
            if (current.Revision != expectedRevision) return new SaveResult(false, current);

            var next = new NotesDocument(current.Revision + 1, notes);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredNotes(1, next.Revision, notes), Json);
            // One step back, in case a bad import or a bug empties the board.
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + ".bak", overwrite: true);
            await AtomicFile.WriteAsync(FilePath, bytes, secret: false, ct);
            return new SaveResult(true, next);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the file every time, so two copies of the app sharing a folder don't clobber each other.</summary>
    async Task<NotesDocument> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(FilePath)) return new NotesDocument(0, NoNotes);
        await using var stream = File.OpenRead(FilePath);
        var stored = await JsonSerializer.DeserializeAsync<StoredNotes>(stream, Json, ct);
        if (stored is null || stored.Notes.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected an object with a \"notes\" array.");
        return new NotesDocument(stored.Revision, stored.Notes.Clone());
    }

    sealed record StoredNotes(int Version, long Revision, JsonElement Notes);
}
