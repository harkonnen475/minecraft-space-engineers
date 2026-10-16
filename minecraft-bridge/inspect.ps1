$ErrorActionPreference = 'Stop'
$labDirectory = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$stream = [System.IO.FileStream]::new((Join-Path $labDirectory 'bridge.bin'), [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
$mapping = [System.IO.MemoryMappedFiles.MemoryMappedFile]::CreateFromFile($stream, [NullString]::Value, 65536, [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read, [System.IO.HandleInheritability]::None, $false)
$view = $mapping.CreateViewAccessor(0, 32768, [System.IO.MemoryMappedFiles.MemoryMappedFileAccess]::Read)
try {
    $snapshot = New-Object byte[] 9344
    $stable = $false
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $firstSequence = $view.ReadInt32(8)
        if (($firstSequence -band 1) -ne 0) { continue }
        [System.Threading.Thread]::MemoryBarrier()
        [void]$view.ReadArray(0, $snapshot, 0, $snapshot.Length)
        [System.Threading.Thread]::MemoryBarrier()
        if ($firstSequence -eq $view.ReadInt32(8)) { $stable = $true; break }
    }
    if (-not $stable) { throw 'No stable snapshot after 20 attempts' }
    $solidCount = 0
    for ($cell = 0; $cell -lt 1152; $cell++) {
        if ([BitConverter]::ToInt32($snapshot, 128 + 8 * $cell) -ne 0) { $solidCount++ }
    }
    [pscustomobject]@{
        Magic = '{0:X8}' -f [BitConverter]::ToInt32($snapshot, 0)
        Version = [BitConverter]::ToInt32($snapshot, 4)
        Sequence = $firstSequence
        Active = ([BitConverter]::ToInt32($snapshot, 36) -band 1) -ne 0
        AgeMilliseconds = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [BitConverter]::ToInt64($snapshot, 40)
        Origin = ((24,28,32 | ForEach-Object { [BitConverter]::ToInt32($snapshot, $_) }) -join ',')
        Player = ((48,56,64 | ForEach-Object { [BitConverter]::ToDouble($snapshot, $_) }) -join ',')
        SolidCells = $solidCount
        CommandAcknowledgement = [BitConverter]::ToInt32($snapshot, 84)
        CommandResult = [BitConverter]::ToInt32($snapshot, 88)
        EditsEnabled = Test-Path -LiteralPath (Join-Path $labDirectory 'enable-edits.flag')
    }
} finally { $view.Dispose(); $mapping.Dispose(); $stream.Dispose() }
