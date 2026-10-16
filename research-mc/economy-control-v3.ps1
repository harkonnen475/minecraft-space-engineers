param(
    [Parameter(Mandatory=$true)][string]$Operation,
    [hashtable]$Arguments=@{},
    [string]$RequestId=([guid]::NewGuid().ToString('N')),
    [int]$TimeoutSeconds=30
)
$ErrorActionPreference='Stop'
$bridgeRoot=Split-Path -Parent $PSScriptRoot
$state=Get-Content -LiteralPath (Join-Path $bridgeRoot 'economy-state-v3.json') -Raw | ConvertFrom-Json
$world=Get-Content -LiteralPath (Join-Path $bridgeRoot 'world-v2.json') -Raw | ConvertFrom-Json
if(!$state.worldId -or $state.worldId -ne $world.worldId){throw 'World identity mismatch'}
if(([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()-[long]$state.timestamp)-gt 4000){throw 'Minecraft inventory snapshot is stale'}
$request=@{version=3;id=$RequestId;worldId=$state.worldId;operation=$Operation;args=$Arguments}
$target=Join-Path $bridgeRoot 'economy-request-v3.json'
$temporary=$target+'.cli.tmp'
$bytes=[Text.Encoding]::UTF8.GetBytes(($request|ConvertTo-Json -Depth 30 -Compress))
$stream=[IO.File]::Open($temporary,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
if(Test-Path -LiteralPath $target){[IO.File]::Replace($temporary,$target,[NullString]::Value)}else{[IO.File]::Move($temporary,$target)}
$deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
do{
    Start-Sleep -Milliseconds 75
    try{$response=Get-Content -LiteralPath (Join-Path $bridgeRoot 'economy-response-v3.json') -Raw|ConvertFrom-Json}catch{continue}
    if($response.id -eq $RequestId){return $response}
}while([DateTime]::UtcNow -lt $deadline)
throw "RPC timed out; inspect transaction status before retrying. Request ID: $RequestId"
