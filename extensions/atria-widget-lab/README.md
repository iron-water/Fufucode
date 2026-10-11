# atria-widget-lab

> Fixture for the M5 plugin management page. **Not a product extension.**

Purpose: give track D's plugin management page a "normal, multi-capability" extension to display and toggle. It is harmless — the only tool is read-only and it never mutates the filesystem outside its own result file.

## What it registers

| Surface | Name | Notes |
|---|---|---|
| Tool | `atria_read_snippet` | Reads the first N lines of a file (read-only, capped at 50 lines) |
| Command | `/atria-lab.tour [ms]` | Walks `select`/`input`/`editor` dialogs + `notify`/`setStatus`/`setWidget`/`setTitle`/`set_editor_text`, writes `atria-tour-result.json` to the session cwd |
| Command | `/atria-lab.status` | Emits notify + `setStatus` — presence in `get_commands` is the "loaded" observable |
| Event | `session_start` | Emits `setStatus("atriaWidgetLab", "ready")` as a load signal |

## Management-page acceptance usage

- **Loaded display**: `get_commands` contains `atria-lab.tour` and `atria-lab.status`; after session start a `setStatus`/`atriaWidgetLab` frame arrives.
- **Tool execution**: `prompt` asking to call `atria_read_snippet` produces a `tool_execution_start`/`tool_execution_end` pair. When `fufucode-bridge` is loaded alongside, the call is gated by a `confirm` dialog (unknown tool → confirm).
- **Enable/disable round-trip**: removing the `-e` flag (or the entry from settings) makes the two commands disappear from `get_commands` and stops the `setStatus` frame.
- **UI round-trip**: `/atria-lab.tour` drives a select → input → editor sequence; `editor` has **no timeout** in RPC mode — the host must answer it.

## Load it

```
pi --extension <repo>/extensions/atria-widget-lab
```
