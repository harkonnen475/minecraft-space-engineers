$ErrorActionPreference='Stop'
$labRoot=Split-Path $PSScriptRoot -Parent
$seBin='C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64'
$references=@('VRage.dll','VRage.Library.dll','VRage.Math.dll','VRage.Game.dll','VRage.Render.dll','Sandbox.Game.dll','Sandbox.Common.dll','netstandard.dll') | ForEach-Object {'/reference:'+(Join-Path $seBin $_)}
$sources=@('Rendering.cs','EntityRendering.cs','BlockLighting.cs','TerrainCache.cs','RetainedWorldStore.cs') | ForEach-Object {Join-Path $labRoot ('se-bridge\v2\'+$_)}
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /optimize+ ('/out:'+(Join-Path $PSScriptRoot 'GenerateThumbnails.exe')) @references @sources (Join-Path $PSScriptRoot 'GenerateThumbnails.cs')
if($LASTEXITCODE -ne 0){throw 'Thumbnail generator compilation failed.'}
& (Join-Path $PSScriptRoot 'GenerateThumbnails.exe')
if($LASTEXITCODE -ne 0){throw 'Thumbnail generation failed.'}
