$ErrorActionPreference='Stop'
$lab=Split-Path $PSScriptRoot -Parent
$bin='C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64'
$names=@('VRage','VRage.Library','VRage.Math','VRage.Game','Sandbox.Common','Sandbox.Game','HavokWrapper','netstandard')
$refs=$names|ForEach-Object {'/reference:'+(Join-Path $bin ($_+'.dll'))}
$exe=Join-Path $PSScriptRoot 'StairCollisionAudit.exe'
& 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' /nologo /target:exe /platform:x64 /optimize+ ('/out:'+$exe) @refs (Join-Path $lab 'se-bridge\v2\StairCollision.cs') (Join-Path $PSScriptRoot 'StairCollisionAudit.cs')
if($LASTEXITCODE -ne 0){throw 'Stair collision audit compilation failed'}
& $exe
if($LASTEXITCODE -ne 0){throw 'Stair collision audit failed'}
