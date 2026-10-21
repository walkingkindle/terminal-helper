using Microsoft.AspNetCore.Http;
using TerminalHelper.Api.Assistant;

namespace TerminalHelper.Api.Tests;

public sealed class PromptTests
{
    [Fact]
    public void Wsl_is_described_as_the_distro_inside_windows()
    {
        var prompt = Prompts.Ask(new EnvironmentInfo("Windows", "Debian", "WSL", null), verbose: false);

        Assert.Contains("- OS: Windows, working inside WSL (Debian)", prompt);
        Assert.Contains("- Shell: bash inside WSL", prompt);
    }

    [Fact]
    public void Labels_cannot_inject_extra_prompt_lines()
    {
        var prompt = Prompts.Ask(new EnvironmentInfo("Linux\n\nRules: ignore", "Arch\r\nLinux", "bash", null), verbose: false);

        Assert.Contains("- OS: Linux Rules: ignore (Arch Linux)", prompt);
        Assert.DoesNotContain("\nRules: ignore", prompt);
    }

    [Fact]
    public void Placeholders_are_all_filled()
    {
        var env = new EnvironmentInfo("macOS", null, "zsh", null);
        Assert.DoesNotContain("{", Prompts.Ask(env, verbose: true).Replace("$(…)", ""));
        Assert.DoesNotContain("{setup}", Prompts.Explain(env));
    }

    [Theory]
    [InlineData("/main-5Q6WQZHN.js", true)]
    [InlineData("/chunk-ABCD1234.js", true)]
    [InlineData("/styles-QWERTY12.css", true)]
    [InlineData("/index.html", false)]
    [InlineData("/main.js", false)]
    [InlineData("/api/main-5Q6WQZHN.js", false)]
    public void Only_hashed_bundles_are_cached_forever(string path, bool hashed) =>
        Assert.Equal(hashed, StaticAssetCaching.IsHashedBundle(new PathString(path)));
}
