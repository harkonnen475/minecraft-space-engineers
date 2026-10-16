$ErrorActionPreference='Stop'
$labRoot=$PSScriptRoot
$pulsarRoot=Join-Path $labRoot 'tools\Pulsar'
$launcher=Join-Path $pulsarRoot 'Legacy.exe'
$profile=Join-Path $pulsarRoot 'Legacy\Profiles\SEMC-v2.xml'
$plugin=Join-Path $pulsarRoot 'Legacy\Local\SEMCBootstrap.dll'
$runtime=Join-Path $labRoot 'se-bridge\v2\SEMC.Runtime.dll'
$seBin='C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64'
foreach($file in @($launcher,$profile,$plugin,$runtime)){if(!(Test-Path -LiteralPath $file)){throw "Missing file: $file"}}
$running=@(Get-Process -Name SpaceEngineers,Legacy -ErrorAction SilentlyContinue)
if($running.Count -gt 0){throw 'Space Engineers is already running'}
New-Item -ItemType File -Path (Join-Path $labRoot 'load-v2-world.flag') -Force | Out-Null
$arguments='-bin64 "{0}" -profile "{1}" -noUpdate -noStats -noDiscord' -f $seBin,$profile
Start-Process -FilePath $launcher -ArgumentList $arguments -WorkingDirectory $pulsarRoot -WindowStyle Normal -PassThru | Select-Object Id,ProcessName
