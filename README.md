# Terminal Helper

Ask how to do something in the terminal and get the command for **your** setup, the way a colleague would answer: "how do I print the working directory?" → `pwd`. Pin the commands worth keeping to a wall of sticky notes.

- **One page.** The board fills the top; the ask panel is docked underneath. Drag the divider to resize it (it remembers; double-click resets).
- **Set your machine once.** Linux + distro, macOS, or Windows with PowerShell / cmd / Git Bash / WSL, plus free-text extras ("I use yay for the AUR"). Every answer is written for it, so you never type "on Arch…".
- **Concise by default.** Just the command, a short label only when there are steps. Turn on **Verbose** to learn: a sentence on what the command does, then a man-page style line for every flag, argument and operator. Or hit **Explain** on any single command.
- **Pin → sticky note.** The note lands on the board above the answer. Click a note's command to copy it; click the note to edit it, recolor it, add your own notes, or explain it.
- **Your choice of AI.** Claude or OpenAI with your own API key, or a model running on your own computer (Ollama, LM Studio…). Switch any time in Settings; each keeps its own key and model.
- **Self-hosted.** Everyone runs their own copy on their own computer. No accounts, nothing on the web: notes and keys live in a folder on your machine.

## Stack

| | |
|---|---|
| `client/` | Angular 22: standalone components, signals, zoneless, no UI library. About 63 KB (Brotli) on first load, system fonts, light/dark |
| `server/` | ASP.NET Core 10 minimal API. Holds the keys, builds the prompt, calls the chosen AI and streams the answer to the browser over server-sent events. In production it also serves the built client |
| AI | **Claude** through the official Anthropic C# SDK, `claude-haiku-5-5` by default ($0.10 / $0.50 per million input / output tokens, thinking off, effort `low`). **OpenAI** and **local models** through the chat completions API that OpenAI defined and local servers copy, `gpt-5.4-mini` by default for OpenAI ($0.75 / $4.50). An answer is a few hundred tokens: a tiny fraction of a cent, or free on your own machine |

Every provider is asked for the same structured JSON (`command`, `label`, `where`, `explanation`, `parts`), enforced with a JSON schema. The client parses the stream as it arrives, so commands appear token by token, and pinning keeps the exact command with its breakdown. The system prompt is in [`server/TerminalHelper.Api/Assistant/Prompts.cs`](server/TerminalHelper.Api/Assistant/Prompts.cs).

## Run it as a desktop app

You need the .NET 10 SDK and Node.js 22+ (to build it), Chrome, Chromium, Brave, Edge or Vivaldi (for the window), and an AI to ask: an Anthropic or OpenAI API key, or a local model server.

```bash
./terminal-helper.sh
```

The first start builds the app (about a minute); after that it starts in a second. It runs a private server on `127.0.0.1:4977` and opens it in its own app window, with no tabs or address bar. Close the window to quit. The first time, the window asks you to connect an AI (`···` → Settings later):

| | |
|---|---|
| Claude | An API key from the [Claude Console](https://platform.claude.com/settings/keys) |
| OpenAI | An API key from [platform.openai.com](https://platform.openai.com/api-keys) |
| Local model | The address of a server on your computer and a model it has. With [Ollama](https://ollama.com): `ollama pull qwen2.5-coder`, then use `http://localhost:11434/v1`. LM Studio, llama.cpp and other servers that speak OpenAI's chat API with JSON-schema output work too |

Keys are checked with the provider before they're saved, and a local server is checked as you type its address, with its models offered in the picker.

| | |
|---|---|
| `--api-key` | Paste an Anthropic or OpenAI key at a hidden prompt instead of in the window |
| `--api-key sk-…` | The same, from the command line (it ends up in your shell history) |
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

`notes.json` is the board (with `notes.json.bak`, the version before the last save), and `settings.json` the AI provider settings and keys, readable only by you. To back up or move to another computer, copy the folder, or use Export / Import in the `···` menu. `TERMINAL_HELPER_DATA=/some/folder` uses a different folder, for example one that syncs. Every way of running the app (desktop or `dev.sh`) uses the same folder, so they share one board.

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

In development a key can also come from user-secrets, e.g. `dotnet user-secrets set "OpenAI:ApiKey" "sk-..." --project server/TerminalHelper.Api`.

## Build it by hand

```bash
npm --prefix client ci
npm --prefix client run build        # writes the client into server/TerminalHelper.Api/wwwroot
dotnet publish server/TerminalHelper.Api -c Release -o out
cd out && ASPNETCORE_URLS=http://127.0.0.1:4977 dotnet TerminalHelper.Api.dll
```

This is what `terminal-helper.sh` does. Bundles are pre-compressed (Brotli and gzip) and, since their names are content-hashed, cached for a year; `index.html` is always revalidated, so a new build shows up on the next load.

## Configuration

`server/TerminalHelper.Api/appsettings.json`, or environment variables such as `Anthropic__Effort=medium`. Settings saved in the app win over these; a configured key wins over the provider's usual variable (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`).

| Key | Default | |
|---|---|---|
| `Assistant:MaxTokens` | `4096` | Longest answer, whichever AI answers |
| `Assistant:RequestsPerMinute` | `30` | Per client IP; every request costs money |
| `Anthropic:Model` | `claude-haiku-5-5` | |
| `Anthropic:Effort` | `low` | `low`, `medium` or `high`. Higher is slower and wordier |
| `Anthropic:Thinking` | `false` | Adaptive thinking. Adds a pause before the first token |
| `Anthropic:ApiKey`, `OpenAI:ApiKey` | (none) | |
| `OpenAI:Model` | `gpt-5.4-mini` | |
| `OpenAI:BaseUrl` | `https://api.openai.com/v1` | For example an Azure OpenAI or proxy endpoint |
| `Local:BaseUrl` | `http://localhost:11434/v1` | Ollama's address; LM Studio uses `http://localhost:1234/v1` |
| `Local:Model` | (none) | |

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
dotnet test                               # API: streaming, prompts, providers, settings and notes (the AIs are faked)
npm --prefix client test -- --watch=false # client: stream parser, SSE reader, highlighter, board store
```

## Data and privacy

- Notes and keys stay in the data folder on your computer (see above). Questions go from the local server to the provider you picked and nowhere else; with a local model they never leave your machine. Keys never reach the browser.
- The server only listens on 127.0.0.1 and only answers requests addressed to `localhost` or `127.0.0.1`, so other machines can't reach it and websites can't drive it through DNS rebinding.
- Anyone who can reach the server can spend your API credits, so don't expose it to a network. If you put it on a server for several people, it needs accounts first.
