$ErrorActionPreference='Stop'
$projectDirectory=$PSScriptRoot
$jdkDirectory='C:\Users\konra\AppData\Local\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\runtime\java-runtime-epsilon\windows-x64\java-runtime-epsilon'
$gameDirectory='C:\Users\konra\AppData\Roaming\.minecraft'
$classpathEntries=@((Join-Path $gameDirectory 'versions\26.3\26.3.jar'))
$classpathEntries+=Get-ChildItem -LiteralPath (Join-Path $gameDirectory 'libraries') -Recurse -Filter '*.jar' -File | Select-Object -ExpandProperty FullName
$classesDirectory=Join-Path $projectDirectory 'build\classes'
New-Item -ItemType Directory -Path $classesDirectory -Force | Out-Null
$sourceFiles=Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'src') -Recurse -Filter '*.java' -File | Select-Object -ExpandProperty FullName
$compileArguments=@('--add-modules','jdk.attach','-encoding','UTF-8','-classpath',('"'+(($classpathEntries -join ';').Replace('\','/'))+'"'),'-d',('"'+$classesDirectory.Replace('\','/')+'"'))
$compileArguments+=$sourceFiles | ForEach-Object {'"'+$_.Replace('\','/')+'"'}
$argumentFile=Join-Path $projectDirectory 'build\javac.args'
[IO.File]::WriteAllLines($argumentFile,$compileArguments,[Text.UTF8Encoding]::new($false))
& (Join-Path $jdkDirectory 'bin\javac.exe') ('@'+$argumentFile)
if($LASTEXITCODE -ne 0){throw 'v2 compilation failed'}
$jarPath=Join-Path $projectDirectory ('build\semc-v2-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')+'.jar')
& (Join-Path $jdkDirectory 'bin\jar.exe') cfm $jarPath (Join-Path $projectDirectory 'manifest.mf') -C $classesDirectory .
if($LASTEXITCODE -ne 0){throw 'v2 packaging failed'}
[IO.File]::WriteAllText((Join-Path $projectDirectory 'build\latest-jar.txt'),$jarPath,[Text.UTF8Encoding]::new($false))
Write-Output $jarPath
