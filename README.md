# Fufucode

Windows-first desktop client for AI coding agents, built in C# on [Avalonia](https://avaloniaui.net/). Default backend is **FUMO**, a pure C# agent kernel with zero external runtime dependencies. Pi, OMP, Claude Code, Codex and DeepSeek Harness are optional backend plugins.

Fufucode renders the session: streaming timeline, tool cards, approval gate, built-in browser, terminal, file preview and Git panel.

## Layout

| Path | Contents | Tracked? |
|---|---|---|
| `src/Fufucode.Core` | Sessions, persistence, trust, diffs, updates, plugin host, toolchain catalog | yes |
| `src/Fufucode.App` | Avalonia UI shell | yes |
| `src/Fufucode.Cli` | Headless CLI | yes |
| `src/Fufucode.Plugins.*` | Backend and feature plugins (Pi, OMP, Claude, Codex, DSH, browser, computer, speech, remote control, cron, auto-dev, skins) | yes |
| `src/Fufucode.Pi` / `src/Fufucode.Claude` | Protocol adapters carried by their backend plugins | yes |
| `tools/` | Native launcher and CLI forwarder | yes |
| `scripts/packaging/` | Package, installer, toolchain and runtime packaging | yes |
| `tests/` | Executable verification suites | yes |
| `docs/` | Current authority, design, tasks, records and evidence; start at `docs/README.md` | yes |
| `android/` | WorkBuddy-style mobile client | yes |
| `assets/` | Product visual assets | yes |
| `extensions/` | Optional extension payloads | yes |
| `TestApp/` | Legacy initial Avalonia scaffold; not a product entry; build output ignored | source only |
| `labs/` | Historical packaging experiments and logs | yes |
| `out/` | Generated build/package output; not a source of truth | ignored |
| `output/` | Generated screenshot output; use `out/evidence` instead | ignored |
| `runtime/` | Local Node/Pi payload for optional backends; not shipped by default | ignored (provenance only) |
| `reference/` | Third-party read-only references | ignored |
| `tmp/` | Test artifacts; safe to clean | ignored |

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

### Optional local extensions

A clean checkout builds with no extra packages and no local configuration. A few
development-only enhancements (diagnostic tooling, richer file previews) are maintained
outside version control and compiled out by default.

They are gated behind two compile symbols:

| Symbol | Enables | Scope |
| --- | --- | --- |
| `ENHANCED_PREVIEW` | Extended component set (file preview, later chat widgets) | Any configuration |
| `DEV_TOOLING` | Developer Tools bridge + XAML hot reload | Debug only |

Both are always off unless a local configuration file exists, so you will see them in the
source but they never affect a clean clone:

```csharp
#if ENHANCED_PREVIEW
    plugins.Register(new PdfViewerPreviewPlugin());
#else
    plugins.Register(new PdfPreviewPlugin());   // fallback, always present
#endif
```

To enable them on your own machine, copy the template and set the corresponding flag in a
user-level `Directory.Build.props`:

```bash
cp local.dev.props.example local.dev.props
```

Local-only implementation files use the `.local.cs` suffix and are gitignored. To switch
between the two tracks explicitly:

```bash
pwsh scripts/switch-track.ps1 -Status
pwsh scripts/switch-track.ps1 -Track min     # simulate a clean clone
pwsh scripts/switch-track.ps1 -Track auto    # back to automatic
```

Re-run `dotnet restore` after switching. Full details: [docs/dual-track-workflow.md](docs/dual-track-workflow.md).

### Debugging tools

Development builds wire up two tools. Both are Debug-only — neither ships to users.

| Tool | How to start | Reach for it when |
| --- | --- | --- |
| XAML hot reload | `dotnet watch run` | Iterating on `.axaml` — layout, styles, templates, resources. Edits apply on save; live state (typed text, selection, scroll position, view models, event wiring) survives. |
| Avalonia Developer Tools | press **F12** inside the running app | The UI renders or behaves unexpectedly and the reason is not obvious from the markup. Gives the live visual tree, editable properties, the origin of each effective style, and actual layout bounds. |

#### How to use them while working

These are the fast feedback loop, not an optional extra. Building without ever launching
the app is not sufficient verification for UI work.

- **Changing XAML** — run `dotnet watch run`, edit, save, observe. Do not rebuild the whole
  solution to verify a spacing or colour tweak.
- **A control is missing, mispositioned, or clipped** — press F12 and read the live tree for
  real bounds and visibility, instead of re-deriving it from markup.
- **A style or resource does not take effect** — the properties panel names which style or
  resource currently supplies the value.
- **A binding shows `null` or a stale value** — inspect the value on the live instance first;
  the view model is often correct and the binding path is wrong.
- **Something looks right in code but wrong on screen** — verify against the live tree before
  concluding the code is correct.

If Developer Tools is not installed on the machine, hot reload still works and remains the
default loop.

### Contribution rule

When adding code that depends on a locally maintained extension, keep it in a file ending
with `.local.cs` and wrap it in `#if ENHANCED_PREVIEW`. The calling site must always
provide a working `#else` fallback. Nothing else is required — `.local.cs` files never
reach the remote.

## Parallel feature review

Several models can work at once without sharing a worktree or overwriting `out/app`:

```bash
pwsh fucode/feature-worktree.ps1 -Name review-ui
```

Small fixes can stay on `main`; large or blocking features use a worktree. Publish either lane
with one command:

```bash
pwsh fucode/feature-publish.ps1          # feature worktree
pwsh fucode/feature-publish.ps1 -Main    # small change on main
```

The script refuses `main`, checks that the worktree is clean, generates the
`branch@commit` label, builds into the main worktree's `out/review/<branch>`, verifies the
result, and **launches the new window immediately**. Re-publishing the same feature closes
its previous window first; other features and the user's main instance keep running.

Each build lands in `out/review/<branch>`, has a `branch@commit` label, and uses its own
`--data-root` and window title. The review board is only an inventory/fallback:

```bash
pwsh fucode/review-parallel.ps1 -NoBuild -Open
```

After the user approves a feature, merge it with a guard that only accepts a clean,
main-synchronized branch:

```bash
pwsh fucode/feature-merge.ps1 -Name review-ui
```

Cleanup is refused unless the worktree is clean and merged:

```bash
pwsh fucode/feature-worktree.ps1 -Name review-ui -Remove
```

See [docs/operations/parallel-review.md](docs/operations/parallel-review.md) for the workflow and
[docs/tasks/parallel-feature-brief.md](docs/tasks/parallel-feature-brief.md) for the model-facing rules.

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
pwsh scripts/packaging/package-native.ps1 -AppVersion 1.4.0                     # release channel, self-contained
pwsh scripts/packaging/package-native.ps1 -AppVersion 1.4.0 -FrameworkDependent # release, needs .NET 10
pwsh scripts/packaging/package-test.ps1   -AppVersion 0.1.0-test.1             # experimental test channel
pwsh scripts/packaging/package-toolchain.ps1 -Id pi
```

| Installer | Download | Installed | Requires |
|---|---:|---:|---|
| `Fufucode-Native-Setup.exe` | 50.5 MB | 154 MB | nothing |
| `Fufucode-Native-Slim-Setup.exe` | 12.2 MB | 37.60 MB | .NET 10 Runtime |
| `Fufucode-Test-Setup.exe` | ~50 MB | ~154 MB | nothing |

The test channel is a side-by-side experimental build: its own AppId, install directory
(`%LocalAppData%\Programs\Fufucode Test`), data root (`%LocalAppData%\FufucodeTest`), URL schemes
(`fufucode-test:`) and single-instance lock, so it can run next to the release build. It consumes
GitHub Releases marked pre-release, which the release channel never sees. It is **not trimmed**
(untrimmed payload loads optional plugins reliably; trimming has been observed to break plugin
activation). `.github/workflows/test-release.yml` builds it from a `v*-test.*` tag.

`plugins-dist/` and `runtimes/` are separate download assets. Build output is not committed.

## Performance and memory

Settings → 性能与内存 provides opt-in switches; the default is high performance.

| Switch | Effect | Applies |
|---|---|---|
| 软件渲染 | Skips ANGLE/D3D and the GPU driver stack; ~130–140 MB less working set | after restart |
| 关闭视觉特效 | Stops glass/blur luminance sampling | immediately |
| 减少终端回滚 | Terminal history 1500 → 500 lines | new terminals |
| 浏览器后台降内存 | Background tabs skip early WebView2 creation and use `MemoryUsageTargetLevel.Low`; background scripts keep running | next tab switch |

Measured idle build: 327 MB working set (default) vs 189 MB (low-memory preset).

## Verify

```bash
out/build-native/app/Fufucode.App.exe --selftest
pwsh scripts/packaging/native-start-smoke.ps1 -PackageRoot out/build-native
pwsh scripts/packaging/test-native-channel.ps1
```

## Background mode

Closing the window exits Fufucode by default. Enable **Settings → Other → Background and tray** to keep the process, sessions and backend subprocesses running after the window closes: the tray icon restores the window, and its menu can show the window or exit. Only one Fufucode process runs per login session; launching it again brings the existing window forward (even from the tray) and the second process exits.

## Status

Actively developed. FUMO is the default kernel; backend plugins are installed on demand. Mobile remote control and the plugin marketplace are implemented but under active iteration. `THIRD-PARTY-NOTICES.md` records the licence position; the application code has no licence file yet.
