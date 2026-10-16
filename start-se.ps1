$ErrorActionPreference = 'Stop'
$labRoot = $PSScriptRoot
$pulsarRoot = Join-Path $labRoot 'tools\Pulsar'
$launcher = Join-Path $pulsarRoot 'Legacy.exe'
$plugin = Join-Path $pulsarRoot 'Legacy\Local\SEMCBridge.dll'
$profile = Join-Path $pulsarRoot 'Legacy\Profiles\SEMC.xml'
$bin64 = 'C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64'
foreach ($file in @($launcher, $plugin, $profile, (Join-Path $bin64 'SpaceEngineers.exe'))) {
    if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Required file missing: $file" }
}
$running = @(Get-Process -Name SpaceEngineers, Legacy -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) { throw 'Space Engineers or Pulsar is already running. Exit it before starting this lab instance.' }
# SE is an interactive application; show a normal window so it can receive input.
$arguments = '-bin64 "{0}" -profile "{1}" -noUpdate -noStats -noDiscord' -f $bin64, $profile
Start-Process -FilePath $launcher -ArgumentList $arguments -WorkingDirectory $pulsarRoot -WindowStyle Normal
