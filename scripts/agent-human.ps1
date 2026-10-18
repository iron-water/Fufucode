# Persistent human requirements; importing this file never invokes a model.
function Get-HumanRequirements($Repository,$TaskId){
 $path="$Repository/tmp/agent-flow/human/task-$TaskId.json"
 if(Test-Path $path){
  $current=Read-AgentJson $path
  $superseded=@($current.requests|ForEach-Object {$_.record.request.supersedes}|Where-Object {$_})
  $current.effectiveRequests=@($current.requests|Where-Object {$_.commandId -notin $superseded})
  $current.ready=@($current.effectiveRequests|Where-Object {$ack="$Repository/tmp/agent-flow/human/ack-$($_.commandId).json";-not (Test-Path $ack) -or (Read-AgentJson $ack).decision -ne 'applied'}).Count -eq 0
  return $current
 }
 return @{revision=0;requests=@();effectiveRequests=@();ready=$true}
}
function Assert-HumanReview($Repository,$TaskId,$Review){
 $current=Get-HumanRequirements $Repository $TaskId
 if($current.revision -gt 0 -and (-not $current.ready -or $Review.requirementRevision -ne $current.revision)){throw 'Human requirements pending or review revision stale; no acceptance/integration'}
}
function Open-HumanLease($Repository){
 $path="$Repository/tmp/agent-flow/human";[IO.Directory]::CreateDirectory($path)|Out-Null
 return [IO.File]::Open("$path/requirements.lock",'OpenOrCreate','ReadWrite','None')
}
