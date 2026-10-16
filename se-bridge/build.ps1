$ErrorActionPreference = 'Stop'
$seBin = 'C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64'
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$referenceNames = @('VRage.dll', 'VRage.Library.dll', 'VRage.Math.dll', 'VRage.Game.dll', 'VRage.Render.dll', 'Sandbox.Common.dll', 'Sandbox.Game.dll', 'Sandbox.Graphics.dll', 'VRage.Input.dll', 'netstandard.dll')
$compileArgs = @('/nologo', '/target:library', '/platform:x64', '/optimize+', ('/out:' + (Join-Path $PSScriptRoot 'SEMCBridge.dll')))
$compileArgs += $referenceNames | ForEach-Object { '/reference:' + (Join-Path $seBin $_) }
$compileArgs += Join-Path $PSScriptRoot 'SEMCBridge.cs'
& $compiler @compileArgs
if ($LASTEXITCODE -ne 0) { throw 'C# compiler failed.' }
Get-Item -LiteralPath (Join-Path $PSScriptRoot 'SEMCBridge.dll') | Select-Object FullName, Length, LastWriteTime
