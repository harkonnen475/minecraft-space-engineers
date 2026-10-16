$ErrorActionPreference='Stop'
$projectDirectory=$PSScriptRoot
$jdkDirectory='C:\Users\konra\AppData\Local\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\runtime\java-runtime-epsilon\windows-x64\java-runtime-epsilon'
$gameDirectory=Join-Path $env:APPDATA '.minecraft'
$version=Get-Content -LiteralPath (Join-Path $gameDirectory 'versions\26.3\26.3.json') -Raw | ConvertFrom-Json
$runtimeJar=(Get-Content -LiteralPath (Join-Path $projectDirectory 'build\latest-jar.txt') -Raw).Trim()
$classpathEntries=@($runtimeJar,(Join-Path $gameDirectory 'versions\26.3\26.3.jar'))
$classpathEntries+=@($version.libraries | ForEach-Object {if($_.downloads.artifact){Join-Path (Join-Path $gameDirectory 'libraries') $_.downloads.artifact.path}} | Where-Object {Test-Path -LiteralPath $_})
$testDirectory=Join-Path $projectDirectory 'build\tests'
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
$argumentFile=Join-Path $projectDirectory 'build\test-javac.args'
$compileArguments=@('-encoding','UTF-8','-classpath',('"'+(($classpathEntries -join ';').Replace('\','/'))+'"'),'-d',('"'+$testDirectory.Replace('\','/')+'"'),('"'+(Join-Path $projectDirectory 'tests\EconomyNativeTest.java').Replace('\','/')+'"'))
[IO.File]::WriteAllLines($argumentFile,$compileArguments,[Text.UTF8Encoding]::new($false))
& (Join-Path $jdkDirectory 'bin\javac.exe') ('@'+$argumentFile)
if($LASTEXITCODE -ne 0){throw 'Economy native test compilation failed'}
$javaArgs=@('-Xmx512m','-Djava.awt.headless=true','-classpath',('"'+((@($testDirectory)+$classpathEntries)-join ';').Replace('\','/')+'"'),'semc.v2.runtime.EconomyNativeTest',('"'+(Join-Path $projectDirectory 'build\native-economy-fixtures.json').Replace('\','/')+'"'))
$runtimeArgs=Join-Path $projectDirectory 'build\test-java.args'
[IO.File]::WriteAllLines($runtimeArgs,$javaArgs,[Text.UTF8Encoding]::new($false))
Push-Location -LiteralPath $testDirectory
try {
    & (Join-Path $jdkDirectory 'bin\java.exe') ('@'+$runtimeArgs)
    if($LASTEXITCODE -ne 0){throw 'Economy native tests failed'}
} finally { Pop-Location }
