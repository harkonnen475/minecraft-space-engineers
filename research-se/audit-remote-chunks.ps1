param([switch]$LiveReadOnly)
$ErrorActionPreference='Stop'
$lab=Split-Path $PSScriptRoot -Parent
$bin='C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64'
$names=@('VRage','VRage.Library','VRage.Math','VRage.Game','VRage.Render','Sandbox.Common','Sandbox.Game','Sandbox.Graphics','VRage.Input','netstandard')
$refs=$names | ForEach-Object {'/reference:'+(Join-Path $bin ($_+'.dll'))}
$sources=@('Rendering','EntityRendering','BlockLighting','TerrainCache','RetainedWorldStore','RemoteChunks','BridgeIO') | ForEach-Object {Join-Path $lab ('se-bridge\v2\'+$_+'.cs')}
$exe=Join-Path $PSScriptRoot 'RemoteChunksAudit.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /optimize+ ('/out:'+$exe) @refs @sources (Join-Path $PSScriptRoot 'RemoteChunksAudit.cs')
if($LASTEXITCODE -ne 0){throw 'Remote reader audit compile failed'}
if($LiveReadOnly){& $exe live}else{& $exe}
if($LASTEXITCODE -ne 0){throw 'Remote reader audit failed'}
