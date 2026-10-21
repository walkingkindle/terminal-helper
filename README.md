# Terminal Helper

Ask how to do something in the terminal and get the command for **your** setup, the way a colleague would answer: "how do I print the working directory?" → `pwd`. Pin the commands worth keeping to a wall of sticky notes.

- **One page.** The board fills the top; the ask panel is docked underneath. Drag the divider to resize it (it remembers; double-click resets).
- **Set your machine once.** Linux + distro, macOS, or Windows with PowerShell / cmd / Git Bash / WSL, plus free-text extras ("I use yay for the AUR"). Every answer is written for it, so you never type "on Arch…".
- **Concise by default.** Just the command, a short label only when there are steps. Turn on **Verbose** to learn: a sentence on what the command does, then a man-page style line for every flag, argument and operator. Or hit **Explain** on any single command.
- **Pin → sticky note.** The note lands on the board above the answer. Click a note's command to copy it; click the note to edit it, recolor it, add your own notes, or explain it.
- **Local for now.** Notes live in the browser (localStorage). Export / Import a JSON file from the `···` menu to back them up or move them.

## Stack

| | |
|---|---|
| `client/` | Angular 22: standalone components, signals, zoneless, no UI library. About 63 KB (Brotli) on first load, system fonts, light/dark |
| `server/` | ASP.NET Core 10 minimal API. Holds the API key, builds the prompt, calls Claude with the official Anthropic C# SDK, and streams the answer to the browser over server-sent events. In production it also serves the built client |
| Model | `claude-haiku-5-5` (Claude Haiku 5.5: $0.10 / $0.50 per million input / output tokens), thinking off, effort `low`. An answer is a few hundred tokens, a tiny fraction of a cent |

Answers come back as structured JSON (`command`, `label`, `where`, `explanation`, `parts`). The client parses the stream as it arrives, so commands appear token by token, and pinning keeps the exact command with its breakdown. The system prompt is in [`server/TerminalHelper.Api/Assistant/Prompts.cs`](server/TerminalHelper.Api/Assistant/Prompts.cs).

## Run it

You need the .NET 10 SDK, Node 22+ and an Anthropic API key.

```bash
# The key stays on the server and out of the repo
dotnet user-secrets set "Anthropic:ApiKey" "sk-ant-..." --project server/TerminalHelper.Api
# (or: export ANTHROPIC_API_KEY=sk-ant-...)

./dev.sh
```

Open http://localhost:4280. `dev.sh` starts the API on :5273, waits until it answers, then starts the Angular dev server on :4280, which proxies `/api` to it. Ctrl+C stops both. It refuses to start if either port is still taken by an earlier run (and tells you which process), and warns if no API key is set. The dev server uses 4280 rather than Angular's usual 4200 so it can run next to other Angular projects.

Without `dev.sh` (for example from PowerShell), or to get C# hot reload, use two terminals:

```bash
dotnet watch --project server/TerminalHelper.Api
npm --prefix client start
```

## Production build

```bash
npm --prefix client ci
npm --prefix client run build        # writes the client into server/TerminalHelper.Api/wwwroot
dotnet publish server/TerminalHelper.Api -c Release -o out
ASPNETCORE_URLS=http://0.0.0.0:8080 ANTHROPIC_API_KEY=sk-ant-... dotnet out/TerminalHelper.Api.dll
```

The published app is a single process. Bundles are pre-compressed (Brotli and gzip) and, since their names are content-hashed, cached for a year; `index.html` is always revalidated, so a new deploy shows up on the next load.

## Configuration

`server/TerminalHelper.Api/appsettings.json`, section `Anthropic`, or environment variables such as `Anthropic__Effort=medium`:

| Key | Default | |
|---|---|---|
| `ApiKey` | (none) | Falls back to `ANTHROPIC_API_KEY` |
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
dotnet test                               # API: streaming, prompts, validation, error mapping (Claude is faked)
npm --prefix client test -- --watch=false # client: stream parser, SSE reader, highlighter, board store
```

## Data and privacy

- Notes are stored in this browser only (`localStorage`, key `th.board`, versioned). Clearing site data deletes them, so export now and then. Open tabs stay in sync with each other.
- Questions go to this server and from there to Anthropic. The API key never reaches the browser.

## Next (phase 2)

- Save notes on the server (SQL Server via EF Core) under user accounts (ASP.NET Core Identity). The export file is the migration path from localStorage.
- Don't expose the server publicly before that: anyone who can reach it can spend your API credits. The only protection right now is the per-IP rate limit.
