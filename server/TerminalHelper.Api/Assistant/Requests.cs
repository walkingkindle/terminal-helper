using System.Text;

namespace TerminalHelper.Api.Assistant;

/// <summary>The user's machine, as picked in the UI, e.g. Linux / Arch Linux / bash.</summary>
public sealed record EnvironmentInfo(string? Os, string? Distro, string? Shell, string? Notes);

/// <summary>A previous question and the raw JSON answer the model gave, for follow-ups.</summary>
public sealed record HistoryTurn(string? Question, string? Answer);

public sealed record AskRequest(string? Question, EnvironmentInfo? Env, bool Verbose, IReadOnlyList<HistoryTurn>? History);

public sealed record ExplainRequest(string? Command, EnvironmentInfo? Env);

public static class RequestLimits
{
    public const int Question = 2_000;
    public const int Command = 4_000;
    public const int Label = 60;
    public const int Notes = 500;
    public const int HistoryAnswer = 12_000;
    public const int HistoryTurns = 4;
}

public static class RequestValidation
{
    public static Dictionary<string, string[]> Validate(AskRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        Require(errors, "question", request.Question, RequestLimits.Question);
        ValidateEnv(errors, request.Env);
        if (request.History is { Count: > RequestLimits.HistoryTurns * 2 })
            errors["history"] = [$"Send at most {RequestLimits.HistoryTurns * 2} previous turns."];
        return errors;
    }

    public static Dictionary<string, string[]> Validate(ExplainRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        Require(errors, "command", request.Command, RequestLimits.Command);
        ValidateEnv(errors, request.Env);
        return errors;
    }

    static void ValidateEnv(Dictionary<string, string[]> errors, EnvironmentInfo? env)
    {
        if (env is null)
        {
            errors["env"] = ["Environment is required."];
            return;
        }
        Require(errors, "env.os", env.Os, RequestLimits.Label);
        Require(errors, "env.shell", env.Shell, RequestLimits.Label);
        if (env.Distro?.Length > RequestLimits.Label) errors["env.distro"] = [$"At most {RequestLimits.Label} characters."];
        if (env.Notes?.Length > RequestLimits.Notes) errors["env.notes"] = [$"At most {RequestLimits.Notes} characters."];
    }

    static void Require(Dictionary<string, string[]> errors, string field, string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) errors[field] = ["Required."];
        else if (value.Length > max) errors[field] = [$"At most {max} characters."];
    }

    /// <summary>Collapses whitespace and strips control characters so a label can't break the prompt layout.</summary>
    public static string OneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            else sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
