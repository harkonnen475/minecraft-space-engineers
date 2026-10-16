param(
    [int]$Operation=0,[int]$X=0,[int]$Y=0,[int]$Z=0,[int]$Item=0,[int]$Face=1,[int]$Flags=0,
    [float]$HitX=0.5,[float]$HitY=0.5,[float]$HitZ=0.5,
    [double]$PlayerX=0.5,[double]$PlayerY=241,[double]$PlayerZ=0.5,[float]$Yaw=0,[float]$Pitch=0
)
$ErrorActionPreference='Stop'
$labDirectory=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$file=Join-Path $labDirectory 'bridge-v2.bin'
$stream=[IO.FileStream]::new($file,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)
$map=[IO.MemoryMappedFiles.MemoryMappedFile]::CreateFromFile($stream,[NullString]::Value,8388608,[IO.MemoryMappedFiles.MemoryMappedFileAccess]::ReadWrite,[IO.HandleInheritability]::None,$false)
$view=$map.CreateViewAccessor()
$commandMutex=[Threading.Mutex]::new($false,'Local\SEMC-V2-Mailbox')
$mutexHeld=$false
try{
    if($view.ReadInt32(0)-ne 0x32434D53 -or $view.ReadInt32(4)-ne 2){throw 'Unsupported v2 header'}
    $mail=4194304
    if($Operation -ne 0){
        try{$mutexHeld=$commandMutex.WaitOne(8000)}catch [Threading.AbandonedMutexException]{$mutexHeld=$true}
        if(-not $mutexHeld){throw 'SEMC command mailbox is busy'}
        $idleDeadline=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()+8000
        while($view.ReadInt32($mail)-ne $view.ReadInt32(56) -and [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()-lt $idleDeadline){Start-Sleep -Milliseconds 25}
        $previous=$view.ReadInt32($mail)
        if($previous -ne $view.ReadInt32(56)){throw 'Previous v2 command is pending; no overwrite permitted'}
        if($previous -eq [int]::MaxValue){throw 'Command sequence exhausted'}
        $view.Write($mail+4,$Operation);$view.Write($mail+8,$X);$view.Write($mail+12,$Y);$view.Write($mail+16,$Z)
        $view.Write($mail+20,$Item);$view.Write($mail+24,$Face);$view.Write($mail+28,$Flags)
        $view.Write($mail+32,$HitX);$view.Write($mail+36,$HitY);$view.Write($mail+40,$HitZ)
        $view.Write($mail+48,$PlayerX);$view.Write($mail+56,$PlayerY);$view.Write($mail+64,$PlayerZ)
        $view.Write($mail+72,$Yaw);$view.Write($mail+76,$Pitch)
        [Threading.Thread]::MemoryBarrier();$command=$previous+1;$view.Write($mail,[int]$command)
        $deadline=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()+$(if($Operation-eq 11 -or $Operation-eq 12){90000}else{8000})
        while($view.ReadInt32(56)-ne $command -and [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()-lt $deadline){Start-Sleep -Milliseconds 25}
        if($view.ReadInt32(56)-ne $command){throw ('Command '+$command+' timed out; it may still complete. Do not reuse sequence.')}
    }
    $bytes=$null
    $stable=$false
    for($attempt=0;$attempt-lt 30;$attempt++){
        $before=$view.ReadInt32(8);if(($before-band 1)-ne 0){continue}
        $recordCount=$view.ReadInt32(52);$recordBytes=$view.ReadInt32(104)
        if($recordCount-lt 1 -or $recordCount-gt 262144 -or $recordBytes-ne 12){throw 'Unsupported v2 capture dimensions'}
        $required=4096+$recordCount*$recordBytes
        if(!$bytes -or $bytes.Length-ne $required){$bytes=New-Object byte[] $required}
        [Threading.Thread]::MemoryBarrier();[void]$view.ReadArray(0,$bytes,0,$bytes.Length);[Threading.Thread]::MemoryBarrier()
        if($before-eq $view.ReadInt32(8)){$stable=$true;break}
    }
    if(-not $stable){throw 'No consistent snapshot'}
    $ox=[BitConverter]::ToInt32($bytes,36);$oy=[BitConverter]::ToInt32($bytes,40);$oz=[BitConverter]::ToInt32($bytes,44)
    $sx=[BitConverter]::ToInt32($bytes,24);$sy=[BitConverter]::ToInt32($bytes,28);$sz=[BitConverter]::ToInt32($bytes,32)
    $state=$null;$cellFlags=$null
    if($X-ge $ox -and $X-lt $ox+$sx -and $Y-ge $oy -and $Y-lt $oy+$sy -and $Z-ge $oz -and $Z-lt $oz+$sz){
        $index=($X-$ox)+$sx*(($Z-$oz)+$sz*($Y-$oy));$state=[BitConverter]::ToInt32($bytes,4096+$index*12);$cellFlags=[BitConverter]::ToInt32($bytes,4104+$index*12)
    }
    $entry=$null
    if($null-ne $state -and $state-gt 0){$palette=Get-Content -Raw -LiteralPath (Join-Path $labDirectory 'palette-v2.json')|ConvertFrom-Json;$entry=$palette.states|Where-Object {$_.stateId-eq $state}|Select-Object -First 1}
    [pscustomobject]@{
        Sequence=$before;Flags=[BitConverter]::ToInt32($bytes,12);AgeMs=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()-[BitConverter]::ToInt64($bytes,16)
        Origin=@($ox,$oy,$oz);Size=@($sx,$sy,$sz);Nonair=[BitConverter]::ToInt32($bytes,108);PaletteRevision=[BitConverter]::ToInt32($bytes,48)
        Ack=[BitConverter]::ToInt32($bytes,56);Result=[BitConverter]::ToInt32($bytes,60);ServerTick=[BitConverter]::ToInt64($bytes,64)
        Player=@([BitConverter]::ToDouble($bytes,72),[BitConverter]::ToDouble($bytes,80),[BitConverter]::ToDouble($bytes,88))
        Cell=@($X,$Y,$Z);State=$state;CellFlags=$cellFlags;RegistryName=$entry.registryName;Properties=$entry.properties
    }|ConvertTo-Json -Compress -Depth 6
}finally{if($mutexHeld){$commandMutex.ReleaseMutex()};$commandMutex.Dispose();$view.Dispose();$map.Dispose();$stream.Dispose()}
