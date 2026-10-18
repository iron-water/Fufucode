# Data-only protocol helpers. Dot-source from the supervisor or its tests.
function New-AgentFileWatch([string]$Directory,[string]$Prefix) {
    $watcher=[IO.FileSystemWatcher]::new($Directory,'*.json')
    $watcher.IncludeSubdirectories=$true
    $sources=@()
    foreach($name in @('Created','Changed','Renamed','Deleted','Error')) {
        $id="$Prefix.$name"
        [void](Register-ObjectEvent -InputObject $watcher -EventName $name -SourceIdentifier $id)
        $sources+=$id
    }
    $watcher.EnableRaisingEvents=$true
    return @{watcher=$watcher;sources=$sources;prefix=$Prefix;processes=@{}}
}
function Test-AgentProcessIdentity($State,[ValidateSet('worker','agent')][string]$Role) {
    $id=$State[($Role+'Pid')];if(-not $id){return $false}
    $process=Get-Process -Id $id -ErrorAction SilentlyContinue;if(-not $process){return $false}
    try{
        $actual=$process.StartTime.ToUniversalTime()
        $recorded=$State[($Role+'Started')]
        if($recorded){$expected=if($recorded -is [DateTime]){$recorded}else{[DateTime]::Parse($recorded)};return $actual -eq $expected.ToUniversalTime()}
        # Legacy state lacks exact creation time. A process born after the recorded
        # worker finished cannot be that writer; all other uncertainty stays blocked.
        if($State.finished){$finished=if($State.finished -is [DateTime]){$State.finished}else{[DateTime]::Parse($State.finished)};if($actual -gt $finished.ToUniversalTime()){return $false}}
        return $true
    }catch{return $true}
}
function Watch-AgentProcessExit($Watch,[int]$ProcessId) {
    try{$process=[Diagnostics.Process]::GetProcessById($ProcessId)}catch [ArgumentException]{return $false}
    $key="$ProcessId-$($process.StartTime.ToUniversalTime().Ticks)"
    if($Watch.processes.ContainsKey($key)){$process.Dispose();return (-not $Watch.processes[$key].HasExited)}
    $id="$($Watch.prefix).process-$key"
    [void](Register-ObjectEvent -InputObject $process -EventName Exited -SourceIdentifier $id)
    $Watch.sources+= $id;$Watch.processes[$key]=$process
    $process.EnableRaisingEvents=$true
    return (-not $process.HasExited)
}
function Wait-AgentFileWatch($Watch,[ValidateRange(1,60)][int]$TimeoutSeconds=30) {
    # Event-first, with a pure-local reconciliation sweep for missed notifications.
    # The timeout never sends a model request or authorizes a duplicate claim.
    $event=Wait-Event -Timeout $TimeoutSeconds
    foreach($pending in @(Get-Event|Where-Object {$_.SourceIdentifier -in $Watch.sources})){Remove-Event -EventIdentifier $pending.EventIdentifier}
    if(-not $event){return 'local.reconcile'}
    return $event.SourceIdentifier
}
function Close-AgentFileWatch($Watch) {
    foreach($id in $Watch.sources){Unregister-Event -SourceIdentifier $id -ErrorAction SilentlyContinue}
    foreach($pending in @(Get-Event|Where-Object {$_.SourceIdentifier -in $Watch.sources})){Remove-Event -EventIdentifier $pending.EventIdentifier}
    $Watch.watcher.Dispose()
    foreach($process in $Watch.processes.Values){$process.Dispose()}
}
function Read-AgentJson([string]$Path) {
    $deadline=[DateTime]::UtcNow.AddSeconds(2)
    do {
        $stream=$null;$reader=$null
        try {
            $stream=[IO.File]::Open($Path,'Open','Read',([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
            $reader=[IO.StreamReader]::new($stream)
            return ($reader.ReadToEnd()|ConvertFrom-Json -AsHashtable)
        }catch {
            $native=$_.Exception.InnerException
            $code=if($native){$native.HResult -band 0xffff}else{$_.Exception.HResult -band 0xffff}
            if($code -notin @(5,32,33) -or [DateTime]::UtcNow -ge $deadline){throw}
            Start-Sleep -Milliseconds 10
        }finally{if($reader){$reader.Dispose()}elseif($stream){$stream.Dispose()}}
    }while($true)
}
function ConvertTo-AgentOrdered($Value) {
    if ($Value -is [System.Collections.IDictionary]) {
        $ordered = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object)) { $ordered[$key] = ConvertTo-AgentOrdered $Value[$key] }
        return $ordered
    }
    if ($Value -is [array]) {
        $items = @($Value | ForEach-Object { ConvertTo-AgentOrdered $_ })
        return ,$items
    }
    return $Value
}
function Save-AgentJson($Path, $Value) {
    $temporary = "$Path.$PID.$([guid]::NewGuid().ToString('N')).tmp"
    $Value | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath $temporary -Encoding utf8
    # Move-Item -Force can remove the destination before moving, exposing a missing-file gap.
    $deadline=[DateTime]::UtcNow.AddSeconds(2)
    do {
        try {[IO.File]::Move($temporary,$Path,$true);return}
        catch {
            $native=$_.Exception.InnerException
            $code=if($native){$native.HResult -band 0xffff}else{$_.Exception.HResult -band 0xffff}
            if($code -notin @(5,32,33) -or [DateTime]::UtcNow -ge $deadline){throw}
            # Windows readers without FILE_SHARE_DELETE can briefly block an atomic rename.
            # Retry the local rename only; the destination always remains intact.
            Start-Sleep -Milliseconds 10
        }
    }while($true)
}
function Write-AgentEvent($Directory, $Manifest, [string]$Type, $Payload) {
    $slots = @{'job.created'=1;'agent.started'=2;'agent.exited'=3;'delivery.received'=4;'delivery.invalid'=4;'review.decided'=5;'supervisor.failed'=7}
    if (-not $slots.ContainsKey($Type)) { throw "Unknown event type: $Type" }
    $events = Join-Path $Directory 'events'
    [IO.Directory]::CreateDirectory($events) | Out-Null
    $path = Join-Path $events ('{0:D2}.json' -f [int]$slots[$Type])
    if($Type -eq 'review.decided' -and $Payload.requirementRevision -gt 0){$path=Join-Path $events "05-r$($Payload.requirementRevision).json"}
    $event = [ordered]@{schemaVersion=1;eventId="$($Manifest.runId)/$Type";runId=$Manifest.runId;taskId=$Manifest.task;sequence=$slots[$Type];type=$Type;occurredAt=[DateTime]::UtcNow.ToString('o');payload=$Payload}
    $lock = [IO.File]::Open((Join-Path $Directory 'events.lock'), 'OpenOrCreate','ReadWrite','None')
    try {
        if (Test-Path -LiteralPath $path) {
            $prior = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
            $oldPayload = ConvertTo-AgentOrdered $prior.payload | ConvertTo-Json -Depth 40 -Compress
            $newPayload = ConvertTo-AgentOrdered $Payload | ConvertTo-Json -Depth 40 -Compress
            if ($prior.eventId -ne $event.eventId -or $oldPayload -cne $newPayload) { throw 'Conflicting event replay' }
            return
        }
        Save-AgentJson $path $event
    } finally { $lock.Dispose() }
}
function Get-OmpFinalReply([string]$Path) {
    $sessionId = $null
    $terminal = $null
    foreach ($line in [IO.File]::ReadLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $entry = ConvertFrom-Json -InputObject $line -AsHashtable -ErrorAction Stop
        if ($entry.type -eq 'session') { $sessionId = $entry.id }
        if ($entry.type -eq 'agent_start') { $terminal = $null }
        if ($entry.type -eq 'agent_end') { $terminal = $entry }
    }
    if (-not $sessionId -or -not $terminal -or $terminal.isTerminal -ne $true) { throw 'Missing session identity or terminal agent_end' }
    $last = @($terminal.messages)[-1]
    if (-not $last -or $last.role -ne 'assistant' -or $last.stopReason -ne 'stop') { throw 'Final assistant did not complete normally' }
    if (@($last.content | Where-Object { $_.type -eq 'toolCall' }).Count) { throw 'Final assistant still requests tools' }
    $reply = (@($last.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join "`n").Trim()
    return @{sessionId=$sessionId;text=$reply}
}
function Read-AgentCard([string]$Text, [string]$TaskId) {
    $json = $Text.Trim()
    # A single fenced JSON document is allowed; prose or other JSON is not a verdict.
    if ($json.StartsWith('```json') -and $json.EndsWith('```')) { $json = $json.Substring(7, $json.Length - 10).Trim() }
    $card = ConvertFrom-Json -InputObject $json -AsHashtable -ErrorAction Stop
    if ($card -isnot [System.Collections.IDictionary]) { throw 'Card must be an object' }
    foreach ($key in @('schemaVersion','type','taskId','status','summary','commits','changedFiles','verification','evidence','integrationNeeds','blockers','writerStopped')) {
        if (-not $card.Contains($key)) { throw "Missing card field: $key" }
    }
    if ($card.schemaVersion -ne 1 -or $card.type -ne 'delivery.card' -or $card.taskId -cne $TaskId) { throw 'Card schema/type/task mismatch' }
    if ($card.status -cnotin @('module_ready','submitted','blocked','incomplete')) { throw 'Invalid delivery status' }
    if ($card.writerStopped -isnot [bool] -or -not $card.writerStopped) { throw 'Writer has not declared it stopped' }
    if ($card.summary -isnot [string] -or [string]::IsNullOrWhiteSpace($card.summary)) { throw 'Summary required' }
    foreach ($key in @('commits','changedFiles','evidence','integrationNeeds','blockers')) { if ($card[$key] -isnot [array]) { throw "$key must be an array" } }
    foreach ($commit in $card.commits) { if ($commit -isnot [string] -or $commit -notmatch '^[a-fA-F0-9]{40}$') { throw 'Commit must be a full SHA' } }
    if ($card.verification -isnot [System.Collections.IDictionary]) { throw 'Verification object required' }
    foreach ($key in @('passed','failed','notRun')) { if ($card.verification[$key] -isnot [array]) { throw "verification.$key must be an array" } }
    if ($card.status -eq 'blocked' -and $card.blockers.Count -eq 0) { throw 'Blocked card must identify blockers' }
    return $card
}
function Publish-AgentDelivery($Directory, $Manifest, $State, [string]$ProtocolPath) {
    if ($State.status -eq 'running' -or -not $State.finished) { throw 'Cannot publish before process exit' }
    $reason = $null
    try {
        if ($State.exitCode -ne 0 -or $State.watchdogTimeout) { throw 'Process failed or timed out' }
        $reply = Get-OmpFinalReply $ProtocolPath
        $card = Read-AgentCard $reply.text $Manifest.task
        $commits = @($State.commits -split "`n" | Where-Object { $_ } | ForEach-Object { ($_ -split ' ')[0] })
        foreach ($commit in $card.commits) { if ($commit -notin $commits) { throw 'Card cites a commit outside this run' } }
        $delivery = [ordered]@{schemaVersion=1;runId=$Manifest.runId;taskId=$Manifest.task;sessionId=$reply.sessionId;base=$Manifest.base;head=$State.head;commits=$commits;dirty=$State.dirty;card=$card;status='unreviewed';finished=$State.finished}
        Save-AgentJson (Join-Path $Directory 'delivery.json') $delivery
        Write-AgentEvent $Directory $Manifest 'delivery.received' @{head=$State.head;sessionId=$reply.sessionId;cardStatus=$card.status;receipt='delivery.json';legacy=$false}
        return 'completed_pending_review'
    } catch { $reason = $_.Exception.Message }
    Write-AgentEvent $Directory $Manifest 'delivery.invalid' @{reason=$reason;exitCode=$State.exitCode;head=$State.head}
    return 'failed_or_missing_card'
}
function Publish-AgentReview($Directory, $Manifest, $State, $Review) {
    if ($State.status -ne 'completed_pending_review' -or -not $State.finished) { throw 'No completed delivery to review' }
    $delivery = Get-Content -LiteralPath (Join-Path $Directory 'delivery.json') -Raw | ConvertFrom-Json -AsHashtable
    $received = Get-Content -LiteralPath (Join-Path $Directory 'events/04.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($received.type -cne 'delivery.received' -or $received.runId -cne $Manifest.runId -or $received.taskId -cne $Manifest.task -or
        $received.payload.head -cne $State.head -or $delivery.head -cne $State.head -or $delivery.runId -cne $Manifest.runId -or $delivery.taskId -cne $Manifest.task) { throw 'Delivery event/receipt/state identity mismatch' }
    if ($Review.schemaVersion -ne 1 -or $Review.type -ne 'review.decision' -or $Review.runId -cne $Manifest.runId -or $Review.taskId -cne $Manifest.task -or $Review.reviewedHead -cne $delivery.head) { throw 'Review identity or HEAD mismatch' }
    if ($Review.decision -cnotin @('accept','rework','blocked','defer')) { throw 'Invalid decision enum' }
    if ($Review.decision -eq 'accept' -and $delivery.card.status -in @('blocked','incomplete')) { throw 'Blocked or incomplete delivery cannot be accepted' }
    foreach ($key in @('evidence','findings')) { if ($Review[$key] -isnot [array]) { throw "$key must be an array" } }
    if ($Review.evidence.Count -eq 0 -or [string]::IsNullOrWhiteSpace($Review.nextAction)) { throw 'Review evidence and nextAction required' }
    if ($Review.decision -eq 'rework' -and $Review.findings.Count -eq 0) { throw 'Rework requires findings' }
    if ($Review.integrationStatus -cnotin @('not_integrated','pending_wiring','integrated')) { throw 'Explicit integration status required' }
    if ($Review.decision -ne 'accept' -and $Review.integrationStatus -eq 'integrated') { throw 'Non-accept cannot be integrated' }
    if ($Review.integrationStatus -eq 'integrated' -and $Review.mainSha -notmatch '^[a-fA-F0-9]{40}$') { throw 'Integrated review requires main SHA' }
    Write-AgentEvent $Directory $Manifest 'review.decided' $Review
    if($Review.requirementRevision -gt 0){Save-AgentJson (Join-Path $Directory "review-r$($Review.requirementRevision).json") $Review}
    Save-AgentJson (Join-Path $Directory 'review.json') $Review
}
