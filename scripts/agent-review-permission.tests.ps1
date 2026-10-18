# Verify the managed-review launch policy against the real repository without touching user files.
param([string]$Repository=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$Repository=(Resolve-Path -LiteralPath $Repository).Path
. "$PSScriptRoot/agent-events.ps1"
$root=Join-Path ([IO.Path]::GetTempPath()) ('fufucode-review-permission-'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root)|Out-Null
$target=Join-Path $Repository ('tmp/agent-gate/permission-model-probe-'+[guid]::NewGuid().ToString('N')+'/result.txt')
if(Test-Path $target){throw 'Never overwrite probe'}
$psi=[Diagnostics.ProcessStartInfo]::new()
$psi.FileName='C:/Users/mio/AppData/Roaming/npm/node_modules/@openai/codex/node_modules/@openai/codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe'
$psi.WorkingDirectory=$Repository;$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true
$psi.RedirectStandardInput=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
foreach($arg in @('--no-daemon','exec','-m','devin/swe-2','-C',$Repository,'-s','danger-full-access','-c','approval_policy="never"','--json','-')){$psi.ArgumentList.Add($arg)}
$p=[Diagnostics.Process]::Start($psi)
$out=[IO.File]::Open("$root/stdout.jsonl",'CreateNew','Write','Read');$err=[IO.File]::Open("$root/stderr.log",'CreateNew','Write','Read')
try {
    $a=$p.StandardOutput.BaseStream.CopyToAsync($out);$b=$p.StandardError.BaseStream.CopyToAsync($err)
    $p.StandardInput.Write("L1机制测试，模型devin/swe-2。仅用apply_patch创建你自己的探针文件$target，内容恰为REVIEW_WRITE_OK并读回确认。此文件是本次独立测试的新临时数据。不要读或改其他文件，不调用子代理，不Git提交，不派工，不读取交付卡，不改变任何配置。所有命令显式workdir=$Repository。最终仅回复REVIEW_WRITE_OK。")
    $p.StandardInput.Close()
    if(-not $p.WaitForExit(600000)){$p.Kill($true);$p.WaitForExit();throw 'Probe timeout; no automatic retry'}
    [void]$a.GetAwaiter().GetResult();[void]$b.GetAwaiter().GetResult();$out.Dispose();$err.Dispose()
    $thread=$null;$terminal=$null;$reply=$null;$patchOk=$false
    foreach($line in [IO.File]::ReadLines("$root/stdout.jsonl")){$e=$line|ConvertFrom-Json -AsHashtable;if($e.type -eq 'thread.started'){$thread=$e.thread_id};if($e.type -eq 'turn.completed'){$terminal=$e.type};if($e.type -eq 'item.completed' -and $e.item.type -eq 'file_change' -and $e.item.status -eq 'completed'){$patchOk=$true};if($e.type -eq 'item.completed' -and $e.item.type -eq 'agent_message'){$reply=$e.item.text}}
    if($p.ExitCode -or -not $terminal -or -not $patchOk -or $reply.Trim() -cne 'REVIEW_WRITE_OK' -or (Get-Content $target -Raw).Trim() -cne 'REVIEW_WRITE_OK'){throw 'Actual model patch/write/read failed'}
    Save-AgentJson "$root/validation.json" @{status='passed';scope='actual-repository-review-write';model='devin/swe-2';sandbox='danger-full-access';repository=$Repository;threadId=$thread;target=$target;applyPatchVerified=$true;verifiedAt=[DateTime]::UtcNow.ToString('o')}
    Write-Output "PASS actual repository model apply_patch/write/read: $root/validation.json"
}finally{$out.Dispose();$err.Dispose();$p.Dispose()}
