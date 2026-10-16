param([string]$OutputFile,[int]$Seconds=8)
$ErrorActionPreference='Stop'
$lab=Split-Path -Parent $PSScriptRoot
$path=Join-Path $lab 'entities-v2.json'
$frames=[Collections.Generic.List[object]]::new()
$lastStamp=0
$deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
while([DateTime]::UtcNow-lt$deadline){
    $stream=$null;$reader=$null
    try{
        $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
        $reader=[IO.StreamReader]::new($stream)
        $document=$reader.ReadToEnd()|ConvertFrom-Json
        if($document.timestamp-ne$lastStamp){$lastStamp=$document.timestamp;$frames.Add($document)}
    }catch{}finally{if($reader){$reader.Dispose()}elseif($stream){$stream.Dispose()}}
    Start-Sleep -Milliseconds 5
}
[IO.File]::WriteAllText($OutputFile,($frames.ToArray()|ConvertTo-Json -Depth 40 -Compress),[Text.UTF8Encoding]::new($false))
