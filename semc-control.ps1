param([string]$Action='inspect',[double]$X,[double]$Y,[double]$Z,[int]$Item=-1,[switch]$FirstPerson,[switch]$DisableJetpack,[switch]$Enabled,[hashtable]$Extra=@{})
$ErrorActionPreference='Stop'
$request=[ordered]@{id=[guid]::NewGuid().ToString('N');action=$Action}
foreach($axis in @('X','Y','Z')) { if($PSBoundParameters.ContainsKey($axis)){$request[$axis.ToLowerInvariant()]=$PSBoundParameters[$axis]} }
if($Item -ge 0){$request.item=$Item}
if($FirstPerson){$request.firstPerson=$true}
if($DisableJetpack){$request.disableJetpack=$true}
if($Action -eq 'setmode'){$request.enabled=[bool]$Enabled}
foreach($entry in $Extra.GetEnumerator()){if($entry.Key -ne 'id' -and $entry.Key -ne 'action'){$request[$entry.Key]=$entry.Value}}
$target=Join-Path $PSScriptRoot 'runtime-control-v2.json'
$temporary=$target+'.write'
[IO.File]::WriteAllText($temporary,($request|ConvertTo-Json -Compress))
Move-Item -LiteralPath $temporary -Destination $target -Force
$deadline=[DateTime]::UtcNow.AddSeconds(8)
do {
  Start-Sleep -Milliseconds 100
  try {$state=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'runtime-state-v2.json') -Raw | ConvertFrom-Json} catch {continue}
  if($state.id -eq $request.id){$state|ConvertTo-Json -Depth 12; if($state.result -eq 'error'){throw $state.error}; return}
} while([DateTime]::UtcNow -lt $deadline)
throw 'SE did not acknowledge command within 8 seconds.'
