param([int]$TargetProcessId=0)
$ErrorActionPreference='Stop'
$projectDirectory=$PSScriptRoot
$labDirectory=Split-Path -Parent (Split-Path -Parent $projectDirectory)
$javaExecutable='C:\Users\konra\AppData\Local\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\runtime\java-runtime-epsilon\windows-x64\java-runtime-epsilon\bin\java.exe'
$ids=@(Get-CimInstance Win32_Process -Filter "Name='javaw.exe' OR Name='java.exe'" | Where-Object {$_.CommandLine -match '(?:^|\s)net\.minecraft\.client\.main\.Main(?:\s|$)'} | Select-Object -ExpandProperty ProcessId)
if($TargetProcessId -gt 0){if($ids -notcontains $TargetProcessId){throw 'Specified PID is not a running vanilla Minecraft client'}}elseif($ids.Count -eq 1){$TargetProcessId=$ids[0]}else{throw ('Expected one running Minecraft client; found '+$ids.Count)}
if(Test-Path -LiteralPath (Join-Path $labDirectory 'mc-v2-stop.flag')){throw 'Remove mc-v2-stop.flag explicitly before reattaching'}
$jarPath=(Get-Content -Raw -LiteralPath (Join-Path $projectDirectory 'build\latest-jar.txt')).Trim()
& $javaExecutable --add-modules jdk.attach -cp $jarPath semc.v2.Attach $TargetProcessId $jarPath $labDirectory
if($LASTEXITCODE -ne 0){throw 'v2 attach failed; inspect mc-v2.log'}
