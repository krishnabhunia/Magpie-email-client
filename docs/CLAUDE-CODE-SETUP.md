# Working on Magpie with Claude Code (Windows laptop)

One-time setup, then the daily loop.

## 1. Install (once)

| Tool | How | Check |
|---|---|---|
| Git | https://git-scm.com/download/win | `git --version` |
| .NET 8 SDK (x64) | https://dotnet.microsoft.com/download/dotnet/8.0 → "SDK 8.0.x" Windows x64 installer | `dotnet --version` → 8.0.x |
| Python 3 | https://www.python.org/downloads/windows/ — tick **Add python.exe to PATH** | `python --version` |
| Node.js LTS | https://nodejs.org (Claude Code needs it) | `node --version` |
| Claude Code | `npm install -g @anthropic-ai/claude-code` | `claude --version` |
| Inno Setup 6 (optional) | https://jrsoftware.org/isdl.php — only if you want to build the installer locally; CI builds it anyway | — |
| WebView2 runtime | Already on Windows 11 / most Windows 10 | — |

## 2. Get the code

```powershell
cd C:\Dev
git clone https://github.com/krishnabhunia/Magpie-email-client.git Magpie
cd Magpie
git config user.name "Krishna Bhunia"
git config user.email "krishnabhunia@gmail.com"
dotnet test common/tests/Magpie.Core.Tests -c Release --filter "Category!=Integration"   # expect 151 passed
```

## 3. Start Claude Code

```powershell
cd C:\Dev\Magpie
claude
```

Claude Code reads `CLAUDE.md` automatically (windows/build/test/release rules, the way you work). Paste this as the first message:

> Read CLAUDE.md and docs/ROADMAP-1.2.0.md. First run the "Smoke test first" table in the roadmap on this laptop (build with windows/build/publish.ps1, run publish/Magpie.exe, use my real accounts) and report anything that fails as a table. Then start queue item #6 (dark theme, design B1): show me the plan and a screenshot/mock-up before writing code, wait for my "approved", build it, run tests + the XAML check, and stop before pushing — I will say "deploy".

## 4. Daily loop

| Step | Command / word |
|---|---|
| Build + tests + XAML check + EXE | `windows/build/publish.ps1` |
| Run the app | `publish\Magpie.exe` (quit the installed Magpie first — single instance) |
| Approve a design | write **approved** |
| Release | write **deploy** → Claude bumps the version + CHANGELOG, commits, pushes → GitHub Actions publishes `v<version>` → your installed Magpie offers the update |
| See what's queued | `docs/ROADMAP-1.2.0.md` |

## 5. Things Claude Code cannot do here

| Thing | Where it lives |
|---|---|
| Integration tests (Dovecot / test SMTP) | Linux only — they self-skip on Windows |
| Design canvas (the HTML boards) | claude.ai — the text specs are in `docs/ROADMAP-1.2.0.md` |
| Project status doc (`claude/magpie-status.md`) | claude.ai Project "Apps or Software" — update it there when you're back in the app, or keep `docs/ROADMAP-1.2.0.md` as the source of truth from now on |

## 6. Before 1.2.0 calendar / contacts (queue #7, #9)

In the Google Cloud project used for Gmail sign-in (the client ID in Settings → Accounts): APIs & Services → Library → enable **Google Calendar API** and **People API**. Claude will add the scopes; you sign in again once.
