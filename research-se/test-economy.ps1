param([string]$SpaceEngineersBin='C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$names=@('VRage.dll','VRage.Library.dll','VRage.Math.dll','VRage.Game.dll','VRage.Render.dll','VRage.Render11.dll','Sandbox.Common.dll','Sandbox.Game.dll','Sandbox.Graphics.dll','VRage.Input.dll','SpaceEngineers.Game.dll','SpaceEngineers.ObjectBuilders.dll','HavokWrapper.dll','netstandard.dll')
$references=@($names | ForEach-Object { '/reference:'+(Join-Path $SpaceEngineersBin $_) })
$references+='/reference:'+(Join-Path $projectRoot 'tools\Pulsar\Libraries\Legacy\Newtonsoft.Json.dll')
$library=Join-Path $PSScriptRoot 'UnifiedInventory.check.dll'
$sources=@('ItemCatalog.cs','RecipeBridge.cs','UnifiedInventory.cs','BridgeIO.cs') | ForEach-Object { Join-Path $projectRoot ('se-bridge\v2\'+$_) }
& $compiler /nologo /target:library /platform:x64 /optimize+ ('/out:'+$library) @references @sources
if($LASTEXITCODE -ne 0){throw 'Economy module compilation failed'}
$test=Join-Path $PSScriptRoot 'EconomyPlannerCheck.exe'
& $compiler /nologo /platform:x64 ('/out:'+$test) @references ('/reference:'+$library) (Join-Path $PSScriptRoot 'EconomyPlannerCheck.cs')
if($LASTEXITCODE -ne 0){throw 'Economy test compilation failed'}
& $test $SpaceEngineersBin
if($LASTEXITCODE -ne 0){throw 'Economy planner assertions failed'}
