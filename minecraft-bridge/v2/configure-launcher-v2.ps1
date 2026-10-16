param([string]$BackendDirectory='', [switch]$ValidateOnly)
$ErrorActionPreference='Stop'
$labDirectory=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if(!$BackendDirectory){$BackendDirectory=Join-Path $labDirectory 'minecraft-backend'}
$profilesPath=Join-Path $env:APPDATA '.minecraft\launcher_profiles.json'
$agentJar=(Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'build\latest-jar.txt')).Trim()
if(!(Test-Path -LiteralPath $agentJar)){throw 'Compiled Minecraft agent is missing'}
$profiles=Get-Content -Raw -LiteralPath $profilesPath | ConvertFrom-Json
$profile=[ordered]@{
    name='SEMC Backend (Space Engineers)';type='custom';lastVersionId='26.3';gameDir=$BackendDirectory
    javaArgs=('-Xms512M -Xmx2G -Dsemc.backend.autoStart=true "-javaagent:{0}={1}|{0}"' -f $agentJar,$labDirectory)
    icon='TNT';created=[DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ss.fffZ')
}
if($ValidateOnly){[pscustomobject]@{ProfileId='semc-backend-v2';Version='26.3';BackendDirectory=$BackendDirectory;AgentExists=$true};return}
$backupDirectory=Join-Path $labDirectory 'backups\launcher'
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
Copy-Item -LiteralPath $profilesPath -Destination (Join-Path $backupDirectory ('launcher_profiles-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')+'.json'))
$profiles.profiles | Add-Member -NotePropertyName 'semc-backend-v2' -NotePropertyValue $profile -Force
$temporary=$profilesPath+'.semc.tmp'
[IO.File]::WriteAllText($temporary,($profiles|ConvertTo-Json -Depth 40),[Text.UTF8Encoding]::new($false))
Move-Item -LiteralPath $temporary -Destination $profilesPath -Force
Write-Output 'Dedicated launcher recovery profile installed; existing profiles and selection preserved.'
