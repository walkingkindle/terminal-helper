namespace TerminalHelper.Api.Storage;

/// <summary>
/// The one folder where this app keeps what it saves on the user's machine: the notes and the API key.
/// Self-hosted means one person per install, so there are no accounts, just this folder.
/// </summary>
public sealed class DataFolder
{
    public DataFolder(IConfiguration configuration)
    {
        Path = FirstNonEmpty(
            configuration["Storage:DataDir"],
            configuration["TERMINAL_HELPER_DATA"],
            // ~/.local/share on Linux, ~/Library/Application Support on macOS, %LOCALAPPDATA% on Windows.
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "terminal-helper"));

        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Path);
        else Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    static string FirstNonEmpty(params string?[] values) => values.First(v => !string.IsNullOrWhiteSpace(v))!;
}

public static class AtomicFile
{
    /// <summary>
    /// Writes through a temp file and a rename, so a crash or a full disk never leaves a half-written file.
    /// <paramref name="secret"/> files are readable by their owner only (on Linux and macOS).
    /// </summary>
    public static async Task WriteAsync(string path, byte[] content, bool secret, CancellationToken ct)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (secret && !OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            await using (var stream = new FileStream(temp, options))
            {
                await stream.WriteAsync(content, ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
