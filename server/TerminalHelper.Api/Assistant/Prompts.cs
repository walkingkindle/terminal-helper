using System.Text;
using System.Text.Json;
using static TerminalHelper.Api.Assistant.RequestValidation;

namespace TerminalHelper.Api.Assistant;

public static class Prompts
{
    const string AskRules = """
        You are a senior engineer helping a teammate at their terminal. Answer like a colleague in chat: asked how to print the working directory, you just say `pwd`.

        {setup}
        Rules:
        - Give the command itself. No intro, no "To do X, run…", no restating the question, no sign-off.
        - It must work in their setup as-is. Don't add variants for other OSes, distros or shells unless they ask. Use the native tools (e.g. pacman on Arch, apt on Debian/Ubuntu, dnf on Fedora, brew on macOS, winget on Windows).
        - Usually one command. Use more only when the task really takes several steps (at most 5, in order).
        - Placeholders they must fill in go in angle brackets: ssh <user>@<host>.
        - Never invent flags. If you're not sure an option exists, leave it out.
        - Git Bash: POSIX syntax with the tools Git for Windows ships; no package manager; paths look like /c/Users/<name>. If Git Bash can't do it (installing software, Windows services), give the PowerShell command and say so in `where`.
        - WSL: commands run inside the Linux distro; steps that must run on the Windows side get a `where`.
        - Not a terminal, shell or CLI question? One short sentence in `answer` and no commands.

        Fields:
        - command: exactly what to paste and run.
        - label: a 2–6 word name for a sticky note, e.g. "Print working directory".
        - where: only when it runs somewhere other than their shell, e.g. "on the Windows host, admin PowerShell"; otherwise "".
        - answer: "" unless a few words are genuinely needed (a yes/no, or why something can't work).
        - note: "" unless there's a real gotcha (destroys data, needs root/admin, can't be undone). One short line.

        {mode}
        """;

    const string ConciseMode = "Concise mode: explanation is \"\" and parts is [] for every command.";

    const string Breakdown = """
        - explanation: 1–2 plain sentences on what the whole command does, the way you'd tell a colleague.
        - parts: a man-page style breakdown in order: the program, each subcommand, each flag (keep a flag together with its value, e.g. "-p 2222"), each argument and each operator (|, >, >>, &, &&, $(…)). One clear sentence per part on what it does here.
        """;

    const string VerboseMode = "Verbose mode: they want to learn the command, not just copy it. For every command:\n" + Breakdown;

    const string ExplainRules = """
        You are a senior engineer explaining a command to a teammate who wants to learn it. Be plain and direct, like a colleague.

        {setup}
        Explain the command they send exactly as written; don't change, fix or improve it. If part of it won't work in their setup, say so in the explanation.

        """ + Breakdown;

    public static string Ask(EnvironmentInfo env, bool verbose) => AskRules
        .Replace("{setup}", Setup(env))
        .Replace("{mode}", verbose ? VerboseMode : ConciseMode);

    public static string Explain(EnvironmentInfo env) => ExplainRules.Replace("{setup}", Setup(env));

    public static string ExplainMessage(string command) => $"Explain this command:\n{command.Trim()}";

    static string Setup(EnvironmentInfo env)
    {
        var os = OneLine(env.Os);
        var distro = OneLine(env.Distro);
        var shell = OneLine(env.Shell);
        var notes = OneLine(env.Notes);

        var sb = new StringBuilder("Their setup:\n");
        if (shell.Contains("WSL", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append($"- OS: {os}, working inside WSL ({(distro.Length > 0 ? distro : "Ubuntu")})\n");
            sb.Append("- Shell: bash inside WSL\n");
        }
        else
        {
            sb.Append($"- OS: {os}{(distro.Length > 0 ? $" ({distro})" : "")}\n");
            sb.Append($"- Shell: {shell}\n");
        }
        if (notes.Length > 0) sb.Append($"- Also: {notes}\n");
        return sb.ToString();
    }
}

/// <summary>JSON schemas for structured output. Property order is generation order, so the command streams first.</summary>
public static class Schemas
{
    const string Parts = """
        {
          "type": "array",
          "items": {
            "type": "object",
            "properties": { "token": { "type": "string" }, "meaning": { "type": "string" } },
            "required": ["token", "meaning"],
            "additionalProperties": false
          }
        }
        """;

    public static readonly Dictionary<string, JsonElement> Answer = Parse($$"""
        {
          "type": "object",
          "properties": {
            "answer": { "type": "string" },
            "commands": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "command": { "type": "string" },
                  "label": { "type": "string" },
                  "where": { "type": "string" },
                  "explanation": { "type": "string" },
                  "parts": {{Parts}}
                },
                "required": ["command", "label", "where", "explanation", "parts"],
                "additionalProperties": false
              }
            },
            "note": { "type": "string" }
          },
          "required": ["answer", "commands", "note"],
          "additionalProperties": false
        }
        """);

    public static readonly Dictionary<string, JsonElement> Explanation = Parse($$"""
        {
          "type": "object",
          "properties": {
            "explanation": { "type": "string" },
            "parts": {{Parts}}
          },
          "required": ["explanation", "parts"],
          "additionalProperties": false
        }
        """);

    static Dictionary<string, JsonElement> Parse(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
}
