$out = 'C:\Users\21894\.codex\worktrees\track-f-packaging\Fufucode\labs\packaging\clean-start-report.jsonl'
Get-Content $out | ForEach-Object {
    $o = $_ | ConvertFrom-Json
    $sel = $o | Select-Object phase, pass, success, gotResponse, elapsedMs, startedInMs, readyInMs, json
    $sel | ConvertTo-Json -Compress
}