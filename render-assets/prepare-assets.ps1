param(
    [string]$MinecraftJar = (Join-Path $env:APPDATA '.minecraft\versions\26.3\26.3.jar')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$destinationRoot = Join-Path $PSScriptRoot 'minecraft'
$archive = [IO.Compression.ZipFile]::OpenRead($MinecraftJar)
$count = 0
try {
    foreach ($entry in $archive.Entries) {
        $name = $entry.FullName
        $wanted = $name -match '^assets/minecraft/(blockstates/[^/]+\.json|models/(block|item)/[^/]+\.json|textures/(block|item)/[^/]+\.png|textures/entity/chest/[^/]+\.png)$'
        if (-not $wanted) { continue }
        $relative = $name.Substring('assets/minecraft/'.Length)
        $target = Join-Path $destinationRoot $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        $count++
    }
}
finally { $archive.Dispose() }
Write-Output ('Prepared {0} local Minecraft assets.' -f $count)
