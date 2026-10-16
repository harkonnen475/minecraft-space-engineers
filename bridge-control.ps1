param(
    [ValidateSet('status','recenter','gold','remove','stop')][string]$Action = 'status',
    [int]$X = 0, [int]$Y = 0, [int]$Z = 0
)
$ErrorActionPreference = 'Stop'
$labRoot = $PSScriptRoot
$file = Join-Path $labRoot 'bridge.bin'
$stream = [IO.File]::Open($file,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)
$mapping = [IO.MemoryMappedFiles.MemoryMappedFile]::CreateFromFile($stream,[NullString]::Value,65536,[IO.MemoryMappedFiles.MemoryMappedFileAccess]::ReadWrite,[IO.HandleInheritability]::None,$false)
$view = $mapping.CreateViewAccessor()
try {
    if ($view.ReadInt32(0) -ne 0x434D4553 -or $view.ReadInt32(4) -ne 1) { throw 'Unsupported bridge header.' }
    $snapshot = New-Object byte[] 32768
    $valid = $false
    for($attempt=0;$attempt -lt 20;$attempt++) {
        $before=$view.ReadInt32(8)
        if(($before -band 1) -eq 0) {
            [void]$view.ReadArray(0,$snapshot,0,$snapshot.Length)
            if($view.ReadInt32(8) -eq $before) { $valid=$true; break }
        }
        Start-Sleep -Milliseconds 10
    }
    if(-not $valid) { throw 'No coherent snapshot.' }
    $sx=[BitConverter]::ToInt32($snapshot,12)
    $sy=[BitConverter]::ToInt32($snapshot,16)
    $sz=[BitConverter]::ToInt32($snapshot,20)
    $ox=[BitConverter]::ToInt32($snapshot,24)
    $oy=[BitConverter]::ToInt32($snapshot,28)
    $oz=[BitConverter]::ToInt32($snapshot,32)
    if($sx -ne 12 -or $sy -ne 8 -or $sz -ne 12) { throw 'Unexpected dimensions.' }
    if($Action -ne 'status') {
        if($Action -in @('gold','remove')) {
            if(-not (Test-Path -LiteralPath (Join-Path $labRoot 'enable-edits.flag'))) { throw 'Edits are disabled.' }
            if($X -lt $ox -or $X -ge $ox+$sx -or $Y -lt $oy -or $Y -ge $oy+$sy -or $Z -lt $oz -or $Z -ge $oz+$sz) { throw 'Coordinate is outside the captured patch.' }
        }
        $last=$view.ReadInt32(32768)
        if($last -ne 0 -and $view.ReadInt32(84) -ne $last) { throw 'Previous command is still pending.' }
        $operation=@{remove=1;gold=2;recenter=3;stop=4}[$Action]
        $view.Write(32772,[int]$operation)
        $view.Write(32776,$X)
        $view.Write(32780,$Y)
        $view.Write(32784,$Z)
        [Threading.Thread]::MemoryBarrier()
        $sequence=$last+1
        $view.Write(32768,[int]$sequence)
        $deadline=[DateTime]::UtcNow.AddSeconds(8)
        while($view.ReadInt32(84) -ne $sequence -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
        [pscustomobject]@{Action=$Action;Sequence=$sequence;Acknowledged=($view.ReadInt32(84) -eq $sequence);Result=$view.ReadInt32(88)} | ConvertTo-Json -Compress
    } else {
        $solid=0
        for($i=0;$i -lt $sx*$sy*$sz;$i++) { if([BitConverter]::ToInt32($snapshot,128+$i*8) -ne 0) { $solid++ } }
        [pscustomobject]@{
            Sequence=$before;Active=([BitConverter]::ToInt32($snapshot,36) -band 1);Capture=@($ox,$oy,$oz);Size=@($sx,$sy,$sz)
            SolidBlocks=$solid;Player=@([BitConverter]::ToDouble($snapshot,48),[BitConverter]::ToDouble($snapshot,56),[BitConverter]::ToDouble($snapshot,64))
            SnapshotAgeMs=([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()-[BitConverter]::ToInt64($snapshot,40))
            SEStatus=$view.ReadInt32(32804);SEBlocks=$view.ReadInt32(32800);SEAgeMs=([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()-$view.ReadInt64(32792))
            Command=$view.ReadInt32(32768);Acknowledgement=$view.ReadInt32(84);Result=$view.ReadInt32(88)
        } | ConvertTo-Json -Compress
    }
} finally { $view.Dispose();$mapping.Dispose();$stream.Dispose() }
