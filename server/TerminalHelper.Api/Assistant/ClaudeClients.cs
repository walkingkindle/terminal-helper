using Anthropic;
using Anthropic.Exceptions;
using TerminalHelper.Api.Storage;

namespace TerminalHelper.Api.Assistant;

/// <summary>Creates SDK clients; tests swap this for one that talks to a fake Anthropic.</summary>
public interface IClaudeClientFactory
{
    /// <summary>A null key leaves the SDK to find credentials itself (ANTHROPIC_API_KEY, an `ant` login).</summary>
    AnthropicClient Create(string? apiKey);
}

public sealed class ClaudeClientFactory : IClaudeClientFactory
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    public AnthropicClient Create(string? apiKey) => apiKey is null
        ? new AnthropicClient { Timeout = Timeout }
        : new AnthropicClient { ApiKey = apiKey, Timeout = Timeout };
}

public enum KeyCheck
{
    Valid,
    Rejected,
    /// <summary>Couldn't ask Anthropic (offline, outage); the key may well be fine.</summary>
    Unknown,
}

/// <summary>Hands out a client for whichever key is current, so a key saved in the app applies at once.</summary>
public sealed class ClaudeClients(ApiKeyStore keys, IClaudeClientFactory factory)
{
    readonly Lock _lock = new();
    AnthropicClient? _client;
    string? _clientKey;

    public AnthropicClient Current
    {
        get
        {
            var key = keys.Current.Key;
            lock (_lock)
            {
                if (_client is null || key != _clientKey)
                {
                    _client = factory.Create(key);
                    _clientKey = key;
                }
                return _client;
            }
        }
    }

    /// <summary>Listing models is free, so it's how a new key gets tried before it's saved.</summary>
    public async Task<KeyCheck> CheckAsync(string key, CancellationToken ct)
    {
        try
        {
            await factory.Create(key).Models.List(cancellationToken: ct);
            return KeyCheck.Valid;
        }
        catch (AnthropicUnauthorizedException)
        {
            return KeyCheck.Rejected;
        }
        catch (AnthropicException)
        {
            return KeyCheck.Unknown;
        }
    }
}
