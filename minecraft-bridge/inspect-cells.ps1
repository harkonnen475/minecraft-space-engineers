param(
    [int]$Limit = 12,
    [switch]$AllLayers,
    [Nullable[int]]$X = $null,
    [Nullable[int]]$Y = $null,
    [Nullable[int]]$Z = $null
)
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
    if ([BitConverter]::ToInt32($snapshot, 0) -ne 0x434D4553 -or [BitConverter]::ToInt32($snapshot, 4) -ne 1) { throw 'Unsupported snapshot' }
    $age = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() - [BitConverter]::ToInt64($snapshot, 40)
    if ($age -gt 2000 -or ([BitConverter]::ToInt32($snapshot, 36) -band 1) -eq 0) { throw 'Snapshot is stale or has no active world' }
    $ox = [BitConverter]::ToInt32($snapshot,24); $oy = [BitConverter]::ToInt32($snapshot,28); $oz = [BitConverter]::ToInt32($snapshot,32)
    $px = [BitConverter]::ToDouble($snapshot,48); $py = [BitConverter]::ToDouble($snapshot,56); $pz = [BitConverter]::ToDouble($snapshot,64)
    $explicitCell = $null -ne $X -or $null -ne $Y -or $null -ne $Z
    if ($explicitCell -and ($null -eq $X -or $null -eq $Y -or $null -eq $Z)) { throw 'Supply X, Y and Z together' }
    if ($explicitCell -and ($X -lt $ox -or $X -ge $ox+12 -or $Y -lt $oy -or $Y -ge $oy+8 -or $Z -lt $oz -or $Z -ge $oz+12)) { throw 'Cell lies outside the current capture' }
    $cells = for ($cy = 0; $cy -lt 8; $cy++) { for ($cz = 0; $cz -lt 12; $cz++) { for ($cx = 0; $cx -lt 12; $cx++) {
        $wx=$ox+$cx; $wy=$oy+$cy; $wz=$oz+$cz
        if ($explicitCell) { if ($wx -ne $X -or $wy -ne $Y -or $wz -ne $Z) { continue } }
        elseif (-not $AllLayers -and $cy -ne 7) { continue }
        $index=$cx+12*($cz+12*$cy)
        $stateId=[BitConverter]::ToInt32($snapshot,128+8*$index)
        if (-not $explicitCell -and $stateId -ne 0) { continue }
        [pscustomobject]@{
            X=$wx; Y=$wy; Z=$wz; LocalX=$cx; LocalY=$cy; LocalZ=$cz; Index=$index
            StateId=$stateId; RGB=('{0:X6}' -f [BitConverter]::ToInt32($snapshot,132+8*$index))
            Distance=[Math]::Round([Math]::Sqrt(($wx+0.5-$px)*($wx+0.5-$px)+($wy+0.5-$py)*($wy+0.5-$py)+($wz+0.5-$pz)*($wz+0.5-$pz)),2)
            Classification=$(if ($stateId -eq 0) {'Exported air: actual block must be checked'} else {'Solid exported block'})
            SnapshotSequence=$firstSequence
        }
    } } }
    $cells | Sort-Object Distance | Select-Object -First $Limit
} finally { $view.Dispose(); $mapping.Dispose(); $stream.Dispose() }
