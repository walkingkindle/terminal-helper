using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TerminalHelper.Api.Storage;

public enum KeySource
{
    None,
    /// <summary>Entered in the app or passed to the launcher with --api-key; kept in settings.json.</summary>
    Saved,
    /// <summary>Anthropic:ApiKey from appsettings, user-secrets or the Anthropic__ApiKey env var.</summary>
    Config,
    /// <summary>The ANTHROPIC_API_KEY environment variable.</summary>
    Environment,
}

/// <summary>
/// Which Anthropic API key to use. A key saved in the app wins over configuration, which wins over the
/// ANTHROPIC_API_KEY variable: the more specific to this app, the higher it ranks.
/// </summary>
public sealed class ApiKeyStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    readonly string _path;
    readonly string? _configured;
    readonly string? _fromEnvironment;
    readonly Lock _lock = new();
    string? _saved;

    /// <summary>Configuration includes environment variables, so ANTHROPIC_API_KEY is read from there too.</summary>
    public ApiKeyStore(DataFolder folder, IOptions<AssistantOptions> options, IConfiguration configuration)
    {
        _path = folder.File("settings.json");
        _configured = options.Value.ApiKey;
        _fromEnvironment = configuration["ANTHROPIC_API_KEY"];
        _saved = ReadSaved(_path);
    }

    /// <summary>A damaged settings file means "no saved key" (the app asks again), not a crash.</summary>
    static string? ReadSaved(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<StoredSettings>(File.ReadAllText(path), Json)?.ApiKey : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public (string? Key, KeySource Source) Current
    {
        get
        {
            lock (_lock)
            {
                if (!string.IsNullOrWhiteSpace(_saved)) return (_saved, KeySource.Saved);
            }
            if (!string.IsNullOrWhiteSpace(_configured)) return (_configured, KeySource.Config);
            return string.IsNullOrWhiteSpace(_fromEnvironment) ? (null, KeySource.None) : (_fromEnvironment, KeySource.Environment);
        }
    }

    public async Task SaveAsync(string key, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new StoredSettings(key), Json);
        await AtomicFile.WriteAsync(_path, bytes, secret: true, ct);
        lock (_lock) _saved = key;
    }

    public Task RemoveAsync()
    {
        File.Delete(_path);
        lock (_lock) _saved = null;
        return Task.CompletedTask;
    }

    /// <summary>Enough to recognise a key without revealing it: "…3fQx".</summary>
    public static string Hint(string key) => "…" + key[^Math.Min(4, key.Length)..];

    sealed record StoredSettings(string? ApiKey);
}
