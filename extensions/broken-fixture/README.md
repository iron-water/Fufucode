# broken-fixture

> Fixture for the M5 plugin management page. **Intentionally fails to load.**

Purpose: give track D's plugin management page a reproducible load error so the "最近加载错误" / "加载失败" display path can be tested.

## What it does

`export default function()` throws immediately during extension registration:

```
Error: Failed to load extension "<path>": Failed to load extension: broken-fixture: intentional load failure for management-page acceptance
Hint: Start without extensions using "pi -ne".
```

## Measured behavior (Pi 0.87.1)

- A `-e`/`--extension` extension that throws at load makes **Pi exit with code 1** — it is a *fatal startup failure*, not a soft error that surfaces in `errors` and continues.
- Because the process dies before the RPC loop starts, no `get_commands` probe can run; the observable is the non-zero exit + the stderr diagnostic.
- For the management page: a broken extension is reported as **启动失败** (process exit, stderr line above), and the "approval unavailable" semantics from the bridge still apply — when the bridge is on the same `-e` list, the broken one kills startup entirely.

## Load it

```
pi --extension <repo>/extensions/broken-fixture        # exits with code 1
```
