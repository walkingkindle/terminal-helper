# Terminal Helper

Ask how to do something in the terminal and get the command for **your** setup, the way a colleague would answer: "how do I print the working directory?" → `pwd`. Pin the commands worth keeping to a wall of sticky notes.

- **One page.** The board fills the top; the ask panel is docked underneath. Drag the divider to resize it (it remembers; double-click resets).
- **Set your machine once.** Linux + distro, macOS, or Windows with PowerShell / cmd / Git Bash / WSL, plus free-text extras ("I use yay for the AUR"). Every answer is written for it, so you never type "on Arch…".
- **Concise by default.** Just the command, a short label only when there are steps. Turn on **Verbose** to learn: a sentence on what the command does, then a man-page style line for every flag, argument and operator. Or hit **Explain** on any single command.
- **Pin → sticky note.** The note lands on the board above the answer. Click a note's command to copy it; click the note to edit it, recolor it, add your own notes, or explain it.
- **Self-hosted.** Everyone runs their own copy on their own computer with their own Anthropic API key. No accounts, nothing on the web: notes and the key live in a folder on your machine.

## Stack

| | |
|---|---|
| `client/` | Angular 22: standalone components, signals, zoneless, no UI library. About 63 KB (Brotli) on first load, system fonts, light/dark |
| `server/` | ASP.NET Core 10 minimal API. Holds the API key, builds the prompt, calls Claude with the official Anthropic C# SDK, and streams the answer to the browser over server-sent events. In production it also serves the built client |
| Model | `claude-haiku-5-5` (Claude Haiku 5.5: $0.10 / $0.50 per million input / output tokens), thinking off, effort `low`. An answer is a few hundred tokens, a tiny fraction of a cent |

Answers come back as structured JSON (`command`, `label`, `where`, `explanation`, `parts`). The client parses the stream as it arrives, so commands appear token by token, and pinning keeps the exact command with its breakdown. The system prompt is in [`server/TerminalHelper.Api/Assistant/Prompts.cs`](server/TerminalHelper.Api/Assistant/Prompts.cs).

## Run it as a desktop app

You need the .NET 10 SDK and Node.js 22+ (to build it), Chrome, Chromium, Brave, Edge or Vivaldi (for the window), and an Anthropic API key.

```bash
./terminal-helper.sh
```

The first start builds the app (about a minute); after that it starts in a second. It runs a private server on `127.0.0.1:4977` and opens it in its own app window, with no tabs or address bar. Close the window to quit. The first time, the window asks for your API key (create one in the [Claude Console](https://platform.claude.com/settings/keys) under API keys); it's saved for next time. Change it later under `···` → Settings.

| | |
|---|---|
| `--api-key` | Paste the key at a hidden prompt instead of in the window |
| `--api-key sk-ant-…` | The same, from the command line (it ends up in your shell history) |
| `--browser` | Open a normal browser tab instead of an app window |
| `--port 4978` | If 4977 is taken |
| `--install` | Add Terminal Helper to your desktop's app launcher (Linux) |
| `--rebuild` | Build again even if nothing changed |

It rebuilds by itself whenever the sources change (after a `git pull`, say). Running it again while it's open just opens another window. Without a Chromium-family browser it falls back to a tab in your default browser; then press Ctrl+C in the terminal to quit. It's tested on Linux; macOS and Windows (from Git Bash) are supported by the script but untested.

### Where your data lives

| | |
|---|---|
| Linux | `~/.local/share/terminal-helper` |
| macOS | `~/Library/Application Support/terminal-helper` |
| Windows | `%LOCALAPPDATA%\terminal-helper` |

`notes.json` is the board (with `notes.json.bak`, the version before the last save), and `settings.json` the saved API key, readable only by you. To back up or move to another computer, copy the folder, or use Export / Import in the `···` menu. `TERMINAL_HELPER_DATA=/some/folder` uses a different folder, for example one that syncs. Every way of running the app (desktop or `dev.sh`) uses the same folder, so they share one board.

## Develop

```bash
./dev.sh
```

Open http://localhost:4280. `dev.sh` starts the API on :5273 (`dotnet run`), waits until it answers, then starts the Angular dev server on :4280, which proxies `/api` to it and reloads on every change. Ctrl+C stops both. If either port is still taken by an earlier run, it says which process has it instead of starting. The dev server uses 4280 rather than Angular's usual 4200 so it can run next to other Angular projects.

For C# hot reload, or from PowerShell, use two terminals instead:

```bash
dotnet watch --project server/TerminalHelper.Api
npm --prefix client start
```

In development the key can also come from user-secrets: `dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..." --project server/TerminalHelper.Api`.

## Build it by hand

```bash
npm --prefix client ci
npm --prefix client run build        # writes the client into server/TerminalHelper.Api/wwwroot
dotnet publish server/TerminalHelper.Api -c Release -o out
cd out && ASPNETCORE_URLS=http://127.0.0.1:4977 dotnet TerminalHelper.Api.dll
```

This is what `terminal-helper.sh` does. Bundles are pre-compressed (Brotli and gzip) and, since their names are content-hashed, cached for a year; `index.html` is always revalidated, so a new build shows up on the next load.

## Configuration

`server/TerminalHelper.Api/appsettings.json`, section `Anthropic`, or environment variables such as `Anthropic__Effort=medium`:

| Key | Default | |
|---|---|---|
| `ApiKey` | (none) | A key saved in the app wins over this, and this over `ANTHROPIC_API_KEY` |
| `Model` | `claude-haiku-5-5` | |
| `Effort` | `low` | `low`, `medium` or `high`. Higher is slower and wordier |
| `Thinking` | `false` | Adaptive thinking. Adds a pause before the first token |
| `MaxTokens` | `4096` | |
| `RequestsPerMinute` | `30` | Per client IP; every request costs money |

## Keyboard

| Key | |
|---|---|
| `/` | Ask a question |
| `Ctrl K` / `⌘ K` | Filter notes |
| `Enter`, `Shift Enter` | Send, new line |
| `Esc` | Stop the answer; close a menu or note |
| `↑` `↓` on the divider | Resize the ask panel (double-click resets) |

## Tests

```bash
dotnet test                               # API: streaming, prompts, key and notes storage (Claude is faked)
npm --prefix client test -- --watch=false # client: stream parser, SSE reader, highlighter, board store
```

## Data and privacy

- Notes and the key stay in the data folder on your computer (see above). Questions go from the local server to Anthropic and nowhere else; the key never reaches the browser.
- The server only listens on 127.0.0.1 and only answers requests addressed to `localhost` or `127.0.0.1`, so other machines can't reach it and websites can't drive it through DNS rebinding.
- Anyone who can reach the server can spend your API credits, so don't expose it to a network. If you put it on a server for several people, it needs accounts first.
