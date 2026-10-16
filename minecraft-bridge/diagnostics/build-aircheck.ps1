$ErrorActionPreference = 'Stop'
$diagnosticsDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectDirectory = Split-Path -Parent $diagnosticsDirectory
$jdkDirectory = 'C:\Users\konra\AppData\Local\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\runtime\java-runtime-epsilon\windows-x64\java-runtime-epsilon'
$gameDirectory = 'C:\Users\konra\AppData\Roaming\.minecraft'
$classpathEntries = @((Join-Path $gameDirectory 'versions\26.3\26.3.jar'))
$classpathEntries += Get-ChildItem -LiteralPath (Join-Path $gameDirectory 'libraries') -Recurse -Filter '*.jar' -File | Select-Object -ExpandProperty FullName
$classesDirectory = Join-Path $diagnosticsDirectory 'classes'
New-Item -ItemType Directory -Path $classesDirectory -Force | Out-Null
$compileArguments = @('-encoding','UTF-8','-classpath',('"'+(($classpathEntries -join ';').Replace('\','/'))+'"'),'-d',('"'+$classesDirectory.Replace('\','/')+'"'),('"'+(Join-Path $diagnosticsDirectory 'AirCheckAgent.java').Replace('\','/')+'"'))
$argumentFile = Join-Path $diagnosticsDirectory 'javac.args'
[System.IO.File]::WriteAllLines($argumentFile,$compileArguments,[System.Text.UTF8Encoding]::new($false))
& (Join-Path $jdkDirectory 'bin\javac.exe') ('@'+$argumentFile)
if ($LASTEXITCODE -ne 0) { throw 'Air check compilation failed' }
& (Join-Path $jdkDirectory 'bin\jar.exe') cfm (Join-Path $diagnosticsDirectory 'aircheck-agent.jar') (Join-Path $diagnosticsDirectory 'aircheck-manifest.mf') -C $classesDirectory .
if ($LASTEXITCODE -ne 0) { throw 'Air check packaging failed' }
Write-Output (Join-Path $diagnosticsDirectory 'aircheck-agent.jar')
