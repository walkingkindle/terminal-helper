using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using TerminalHelper.Api.Providers;

namespace TerminalHelper.Api.Storage;

/// <summary>What the user changes in Settings for one provider; null leaves a value as it is.</summary>
public sealed record ProviderUpdate(string? ApiKey, string? Model, string? BaseUrl);

/// <summary>
/// Which AI provider answers, and each provider's key, model and server, kept in settings.json in the data
/// folder (readable by the owner only). Per value, what's saved in the app wins over configuration, which
/// wins over the provider's usual environment variable, which wins over the built-in default.
/// </summary>
public sealed class SettingsStore
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Used when neither the app nor configuration names a model or server.</summary>
    static readonly Dictionary<string, (string Model, string BaseUrl)> Defaults = new()
    {
        [ProviderCatalog.OpenAI.Id] = ("gpt-5.4-mini", "https://api.openai.com/v1"),
        [ProviderCatalog.Local.Id] = ("", "http://localhost:11434/v1"),
    };

    readonly string _path;
    readonly IConfiguration _configuration;
    readonly AnthropicOptions _anthropic;
    readonly Lock _lock = new();
    Stored _stored;

    public SettingsStore(DataFolder folder, IConfiguration configuration, IOptions<AnthropicOptions> anthropic)
    {
        _path = folder.File("settings.json");
        _configuration = configuration;
        _anthropic = anthropic.Value;
        _stored = Read(_path);
    }

    public ProviderSettings Active => Get(ActiveId);

    public string ActiveId
    {
        get
        {
            lock (_lock) return ProviderCatalog.Find(_stored.Provider)?.Id ?? ProviderCatalog.Anthropic.Id;
        }
    }

    public ProviderSettings Get(string id)
    {
        var info = ProviderCatalog.Find(id) ?? throw new ArgumentException($"Unknown provider '{id}'.");
        StoredProvider? saved;
        lock (_lock) saved = _stored.Providers?.GetValueOrDefault(id);

        var (configuredKey, configuredModel, configuredUrl) = info == ProviderCatalog.Anthropic
            ? (_anthropic.ApiKey, _anthropic.Model, null)
            : (_configuration[$"{info.ConfigSection}:ApiKey"], _configuration[$"{info.ConfigSection}:Model"], _configuration[$"{info.ConfigSection}:BaseUrl"]);
        var environmentKey = info.KeyEnvironmentVariable is { } variable ? _configuration[variable] : null;

        var (key, source) = FirstSet(saved?.ApiKey) is { } savedKey ? (savedKey, KeySource.Saved)
            : FirstSet(configuredKey) is { } configKey ? (configKey, KeySource.Config)
            : FirstSet(environmentKey) is { } envKey ? (envKey, KeySource.Environment)
            : (null, KeySource.None);
        var defaults = Defaults.GetValueOrDefault(id);
        var model = FirstSet(saved?.Model, configuredModel, defaults.Model) ?? "";
        var baseUrl = info == ProviderCatalog.Anthropic ? null : FirstSet(saved?.BaseUrl, configuredUrl, defaults.BaseUrl);
        return new ProviderSettings(info, key, source, model, baseUrl);
    }

    /// <summary>Enough to ask: a key where one is needed, a model and a server where one is chosen.</summary>
    public static bool IsReady(ProviderSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.Model)
        && (!settings.Info.RequiresKey || settings.ApiKey is not null)
        && (settings.Info == ProviderCatalog.Anthropic || !string.IsNullOrWhiteSpace(settings.BaseUrl));

    /// <summary>Saves what changed for a provider and makes it the one that answers.</summary>
    public async Task SaveAsync(string id, ProviderUpdate update, CancellationToken ct)
    {
        Stored next;
        lock (_lock)
        {
            var providers = new Dictionary<string, StoredProvider>(_stored.Providers ?? []);
            var current = providers.GetValueOrDefault(id) ?? new StoredProvider(null, null, null);
            providers[id] = new StoredProvider(
                update.ApiKey ?? current.ApiKey,
                update.Model ?? current.Model,
                update.BaseUrl ?? current.BaseUrl);
            next = new Stored(id, null, providers);
        }
        await WriteAsync(next, ct);
    }

    public async Task RemoveKeyAsync(string id, CancellationToken ct)
    {
        Stored next;
        lock (_lock)
        {
            var providers = new Dictionary<string, StoredProvider>(_stored.Providers ?? []);
            if (providers.TryGetValue(id, out var current)) providers[id] = current with { ApiKey = null };
            next = _stored with { ApiKey = null, Providers = providers };
        }
        await WriteAsync(next, ct);
    }

    /// <summary>Enough to recognise a key without revealing it: "…3fQx".</summary>
    public static string Hint(string key) => "…" + key[^Math.Min(4, key.Length)..];

    async Task WriteAsync(Stored next, CancellationToken ct)
    {
        await AtomicFile.WriteAsync(_path, JsonSerializer.SerializeToUtf8Bytes(next, Json), secret: true, ct);
        lock (_lock) _stored = next;
    }

    /// <summary>
    /// The first version stored only an Anthropic key, as { "apiKey": … }; it becomes that provider's key.
    /// A damaged file means "nothing saved" (the app asks again), not a crash.
    /// </summary>
    static Stored Read(string path)
    {
        try
        {
            var stored = File.Exists(path) ? JsonSerializer.Deserialize<Stored>(File.ReadAllText(path), Json) : null;
            if (stored is null) return new Stored(null, null, null);
            if (string.IsNullOrWhiteSpace(stored.ApiKey)) return stored;
            var providers = new Dictionary<string, StoredProvider>(stored.Providers ?? []);
            providers.TryAdd(ProviderCatalog.Anthropic.Id, new StoredProvider(stored.ApiKey, null, null));
            return stored with { ApiKey = null, Providers = providers };
        }
        catch (JsonException)
        {
            return new Stored(null, null, null);
        }
    }

    static string? FirstSet(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    sealed record Stored(string? Provider, string? ApiKey, Dictionary<string, StoredProvider>? Providers);

    sealed record StoredProvider(string? ApiKey, string? Model, string? BaseUrl);
}
