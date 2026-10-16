$ErrorActionPreference='Stop'
$errorFile=Join-Path $PSScriptRoot 'launcher-error.txt'
[IO.File]::WriteAllText($errorFile,'',[Text.UTF8Encoding]::new($false))
try {
    & (Join-Path $PSScriptRoot 'minecraft-bridge\v2\silent-launch-v2.ps1') -StartSE
} catch {
    [IO.File]::WriteAllText($errorFile,$_.Exception.Message,[Text.UTF8Encoding]::new($false))
    throw
}
