param([switch]$BootstrapOnly,[switch]$Reload)
$ErrorActionPreference='Stop'
$seBin='C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64'
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$labRoot=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$names=@('VRage.dll','VRage.Library.dll','VRage.Math.dll','VRage.Game.dll','VRage.Render.dll','VRage.Render11.dll','Sandbox.Common.dll','Sandbox.Game.dll','Sandbox.Graphics.dll','VRage.Input.dll','SpaceEngineers.Game.dll','SpaceEngineers.ObjectBuilders.dll','HavokWrapper.dll','netstandard.dll')
$references=@($names | ForEach-Object { if(Test-Path -LiteralPath (Join-Path $seBin $_)){ '/reference:'+(Join-Path $seBin $_) } })
$references+='/reference:'+(Join-Path $labRoot 'tools\Pulsar\Libraries\Legacy\Newtonsoft.Json.dll')
if($BootstrapOnly){
  & $compiler /nologo /target:library /platform:x64 /optimize+ ('/out:'+(Join-Path $PSScriptRoot 'SEMCBootstrap.dll')) @references (Join-Path $PSScriptRoot 'Bootstrap.cs')
} else {
  $sources=@(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Where-Object Name -ne 'Bootstrap.cs' | ForEach-Object FullName)
  & $compiler /nologo /target:library /platform:x64 /optimize+ ('/out:'+(Join-Path $PSScriptRoot 'SEMC.Runtime.next.dll')) @references @sources
  if($LASTEXITCODE -eq 0){ Move-Item -LiteralPath (Join-Path $PSScriptRoot 'SEMC.Runtime.next.dll') -Destination (Join-Path $PSScriptRoot 'SEMC.Runtime.dll') -Force }
}
if($LASTEXITCODE -ne 0){throw 'Compilation failed'}
if($Reload){New-Item -ItemType File -Path (Join-Path $labRoot 'reload-v2.flag') -Force | Out-Null}
