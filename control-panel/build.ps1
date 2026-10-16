param([string]$OutputPath)
$ErrorActionPreference = 'Stop'
$sourceDir = $PSScriptRoot
$labRoot = Split-Path -Parent $sourceDir
if (!$OutputPath) { $OutputPath = Join-Path $labRoot 'SEMC-Lab.exe' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output /codepage:65001 /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ('/win32manifest:' + (Join-Path $sourceDir 'app.manifest')) ('/out:' + $OutputPath) (Join-Path $sourceDir 'ControlPanel.cs')
if ($LASTEXITCODE -ne 0) { throw 'Control panel compilation failed.' }
