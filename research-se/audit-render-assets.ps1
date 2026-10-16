$ErrorActionPreference='Stop'
$seBin='C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64'
$labRoot=Split-Path $PSScriptRoot -Parent
$names=@('VRage.dll','VRage.Library.dll','VRage.Math.dll','VRage.Game.dll','VRage.Render.dll','Sandbox.Common.dll','Sandbox.Game.dll','Sandbox.Graphics.dll','VRage.Input.dll','netstandard.dll')
$arguments=@('/nologo','/target:exe','/platform:x64','/optimize+','/reference:System.Drawing.dll','/reference:System.Web.Extensions.dll',('/out:'+(Join-Path $PSScriptRoot 'RenderAssetAudit.exe')))
$arguments+=$names | ForEach-Object {'/reference:'+(Join-Path $seBin $_)}
$arguments+=@('Rendering.cs','EntityRendering.cs','BlockLighting.cs','TerrainCache.cs','RetainedWorldStore.cs') | ForEach-Object {Join-Path $labRoot ('se-bridge\v2\'+$_)}
$arguments+=Join-Path $PSScriptRoot 'RenderAssetAudit.cs'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' @arguments
if($LASTEXITCODE -ne 0){throw 'Renderer asset audit compilation failed.'}
& (Join-Path $PSScriptRoot 'RenderAssetAudit.exe')
if($LASTEXITCODE -ne 0){throw 'Renderer asset audit failed.'}
