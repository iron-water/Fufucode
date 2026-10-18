# Fufucode

Windows-first desktop client for AI coding agents, built in C# on [Avalonia](https://avaloniaui.net/). Default backend is **FUMO**, a pure C# agent kernel with zero external runtime dependencies. Pi, OMP, Claude Code, Codex and DeepSeek Harness are optional backend plugins.

Fufucode renders the session: streaming timeline, tool cards, approval gate, built-in browser, terminal, file preview and Git panel.

## Layout

| Path | Contents |
|---|---|
| `src/Fufucode.Core` | Sessions, persistence, trust, diffs, updates, plugin host, toolchain catalog |
| `src/Fufucode.App` | Avalonia UI shell |
| `src/Fufucode.Cli` | Headless CLI |
| `src/Fufucode.Plugins.*` | Backend and feature plugins (Pi, OMP, Claude, Codex, DSH, browser, computer, speech, remote control, cron, auto-dev, skins) |
| `src/Fufucode.Pi` / `src/Fufucode.Claude` | Protocol adapters carried by their backend plugins |
| `tools/` | Native launcher and CLI forwarder |
| `scripts/packaging/` | Package, installer, toolchain and runtime packaging |
| `tests/` | Executable verification suites |

## Requirements

- Windows 10+ (x64)
- .NET 10 SDK to build. End users need nothing with the self-contained installer; the slim installer requires the **.NET 10 Runtime** (`Microsoft.NETCore.App 10.x`).
- Optional: Node, Bun, Python, Git Bash, or any backend CLI. Fufucode uses what is already installed and only offers a download for what is missing.

## Build and run

```bash
dotnet build Fufucode.sln -c Release
```

Local development build and deploy:

```bash
pwsh fucode/publish-local.ps1
```

## CLI

```bash
fufucode "<prompt>"                  # one-shot chat
fufucode sessions list
fufucode settings get --json
fufucode runtime status              # node / bun / python / git-bash / backends
fufucode plugins status              # installed plugin payloads
```

Runtime and plugin payloads install from a local zip or URL with SHA-256 verification:

```bash
fufucode runtime install pi --from Fufucode-Pi-Runtime.zip --sha256 <hex>
fufucode plugins install backend.claude --from backend.claude-1.0.0.zip
```

## Packaging

```bash
pwsh scripts/packaging/package-native.ps1 -AppVersion 1.4.0                     # self-contained
pwsh scripts/packaging/package-native.ps1 -AppVersion 1.4.0 -FrameworkDependent # needs .NET 10
pwsh scripts/packaging/package-toolchain.ps1 -Id pi
```

| Installer | Download | Installed | Requires |
|---|---:|---:|---|
| `Fufucode-Native-Setup.exe` | 19.45 MB | 62.18 MB | nothing |
| `Fufucode-Native-Slim-Setup.exe` | 12.2 MB | 37.60 MB | .NET 10 Runtime |

`plugins-dist/` and `runtimes/` are separate download assets. Build output is not committed.

## Verify

```bash
out/build-native/app/Fufucode.App.exe --selftest
pwsh scripts/packaging/native-start-smoke.ps1 -PackageRoot out/build-native
pwsh scripts/packaging/test-native-channel.ps1
```

## Status

Actively developed. FUMO is the default kernel; backend plugins are installed on demand. Mobile remote control and the plugin marketplace are implemented but under active iteration. `THIRD-PARTY-NOTICES.md` records the licence position; the application code has no licence file yet.
