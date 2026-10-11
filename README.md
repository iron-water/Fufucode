# Fufucode

A Windows-first desktop client for the [Pi](https://www.npmjs.com/package/@earendil-works/pi-coding-agent) coding agent, written in C# on [Avalonia](https://avaloniaui.net/).

Fufucode does **not** reimplement an agent loop or a model-provider protocol. It drives the official Pi process over its documented JSONL RPC surface and renders what comes back: a streaming message timeline, tool-execution cards, and an explicit approval gate. The desktop shell is deliberately thin — Pi does the work, Fufucode gives it a window.

## What is here

| Path | Contents |
|---|---|
| `src/Fufucode.Core` | Sessions, persistence, project trust, diffs, usage queries, Git worktrees |
| `src/Fufucode.Pi` | Pi RPC protocol: JSONL transport, event decoding, backend identification |
| `src/Fufucode.App` | Avalonia UI: workbench shell, timeline, approvals, settings, extensions, embedded browser |
| `src/Fufucode.Cli` | Headless CLI: `sessions`, `chat`, `approve`, `models`, `settings`, `extensions`, `diag` |
| `extensions/fufucode-bridge` | Pi extension that routes every tool call through a desktop approval dialog |
| `tests/` | Executable verification suites plus `FakePi`, a protocol-conformant stub harness |
| `scripts/` | Runtime setup, packaging, installer drills, and real-Pi verification harnesses |

## Architecture

```
Fufucode.App / Fufucode.Cli
        └── Fufucode.Core        session orchestration, persistence, trust
                └── Fufucode.Pi  JSONL RPC client
                        └── Pi   official agent process (Node.js)
```

Approvals travel the opposite way: Pi emits `extension_ui_request`, `fufucode-bridge` relays it, and the desktop dialog answers `allow` / `deny`. A denial or an unanswered timeout blocks the tool; an approval is bound to the parameter snapshot it was granted for and is re-requested if that snapshot changes.

## Requirements

- Windows 10+ (x64), .NET SDK 10.0
- Node.js v24.15.0 for the managed Pi runtime
- Microsoft Edge WebView2 Runtime for the embedded browser preview (the app reports it and falls back to an external browser when missing)

## Build and run

```powershell
dotnet build Fufucode.sln -c Release
dotnet run --project src/Fufucode.App -c Release -- --project <path-to-your-repo>
```

Prepare the Pi runtime that ships beside the app:

```powershell
npm ci --prefix runtime/pi --ignore-scripts
pwsh scripts/setup-runtime.ps1     # copies node.exe into runtime/node and installs Pi
```

`RealSessionBootstrap` looks for `runtime/node/node.exe` and `runtime/pi/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js`; pass `--pi-node` / `--pi-cli` to point somewhere else.

## Verify

Every claim in this repository is expected to be reproducible, so verification is code, not prose.

```powershell
dotnet run --project src/Fufucode.App -c Release -- --selftest          # headless UI + mock sessions
dotnet run --project src/Fufucode.App -c Release -- --selftest --realpi # adds real Pi integration segment
pwsh scripts/bridge-verify/run.mjs     # approval bridge against a real Pi agent loop
node scripts/pi-probe/probe.mjs        # Pi RPC protocol conformance probe
```

## Packaging

```powershell
pwsh scripts/packaging/package.ps1 -AppVersion <ver>   # self-contained win-x64 bundle -> out/Fufucode.zip
pwsh scripts/packaging/build-installer.ps1             # Inno Setup installer
```

`THIRD-PARTY-NOTICES.md` records the licence position for every distributed component. The application code itself has **no licence file yet**; see the compliance-gap table in that document before redistributing.

## Status

Early-stage, actively developed. The single-session desktop path (open a project, pick a model, submit a task, watch the stream, approve or stop, reopen the session) is the delivered core. Multi-session management, plugin installation, background hosts, and mobile pairing are planned, not shipped. Expect breaking changes and unfinished edges; issues and pull requests are welcome.
