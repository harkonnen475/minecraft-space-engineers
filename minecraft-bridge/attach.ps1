param([int]$TargetProcessId = 0)
$ErrorActionPreference = 'Stop'
$projectDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$labDirectory = Split-Path -Parent $projectDirectory
$javaExecutable = 'C:\Users\konra\AppData\Local\Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\runtime\java-runtime-epsilon\windows-x64\java-runtime-epsilon\bin\java.exe'
$agentJar = Join-Path $projectDirectory 'build\semc-agent.jar'
$minecraftMainClassPattern = '(?:^|\s)(?:net\.minecraft\.client\.main\.Main|net\.fabricmc\.loader\.impl\.launch\.knot\.KnotClient|org\.quiltmc\.loader\.impl\.launch\.knot\.KnotClient)(?:\s|$)'
# Command lines may contain authentication tokens. Examine them only in memory;
# never write the process objects or their CommandLine properties to the output.
$candidateProcessIds = @(Get-CimInstance Win32_Process -Filter "Name='javaw.exe' OR Name='java.exe'" |
    Where-Object { $_.CommandLine -match $minecraftMainClassPattern } |
    Select-Object -ExpandProperty ProcessId)
if ($TargetProcessId -gt 0) {
    if ($candidateProcessIds -notcontains $TargetProcessId) {
        throw "PID $TargetProcessId is not a recognized running Minecraft client."
    }
} elseif ($candidateProcessIds.Count -eq 1) {
    $TargetProcessId = $candidateProcessIds[0]
} elseif ($candidateProcessIds.Count -eq 0) {
    throw 'No running Minecraft Java client found. Start Minecraft before attaching.'
} else {
    throw ('Multiple Minecraft clients found (PIDs ' + ($candidateProcessIds -join ', ') + '). Supply -TargetProcessId explicitly.')
}
if (Test-Path -LiteralPath (Join-Path $labDirectory 'mc-stop.flag')) {
    throw 'mc-stop.flag exists. Remove that explicit stop flag before attaching again.'
}
& $javaExecutable --add-modules jdk.attach -cp $agentJar semc.AttachMain $TargetProcessId $agentJar $labDirectory
if ($LASTEXITCODE -ne 0) { throw 'Agent attachment failed; inspect mc-bridge.log' }
