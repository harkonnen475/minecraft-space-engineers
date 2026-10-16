param([switch]$StartSE,[switch]$ValidateOnly,[switch]$ValidateColdOnly,[int]$TimeoutSeconds=180)
$ErrorActionPreference='Stop'
if($ValidateColdOnly){$ValidateOnly=$true}
$labDirectory=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$installedDirectory=Join-Path $env:APPDATA '.minecraft'
$backendDirectory=Join-Path $labDirectory 'minecraft-backend'
$worldName='SEMC Backend'
$version='26.3'
$jdkDirectory=Join-Path $env:LOCALAPPDATA 'Packages\Microsoft.4297127D64EC6_8wekyb3d8bbwe\LocalCache\Local\runtime\java-runtime-epsilon\windows-x64\java-runtime-epsilon'
$javaExecutable=Join-Path $jdkDirectory 'bin\javaw.exe'
$agentJar=(Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'build\latest-jar.txt')).Trim()
$launchLog=Join-Path $labDirectory 'backend-launch-v2.log'
$startupMutex=$null;$startupMutexHeld=$false
function Write-LaunchLog([string]$Message){Add-Content -LiteralPath $launchLog -Value ([DateTime]::UtcNow.ToString('O')+' '+$Message)}
function Read-BackendHeader {
    $stream=$null;$reader=$null
    try{
        $stream=[IO.File]::Open((Join-Path $labDirectory 'bridge-v2.bin'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        $reader=[IO.BinaryReader]::new($stream)
        for($attempt=0;$attempt -lt 8;$attempt++){
            $stream.Position=0;$magic=$reader.ReadInt32();$protocol=$reader.ReadInt32();$sequence=$reader.ReadInt32();$flags=$reader.ReadInt32();$timestamp=$reader.ReadInt64()
            $stream.Position=8;$after=$reader.ReadInt32()
            if($magic -eq 0x32434D53 -and $protocol -eq 2 -and $sequence -eq $after -and ($sequence-band 1)-eq 0){
                $age=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()-$timestamp
                return [pscustomobject]@{Fresh=($age-ge 0-and $age-lt 3000);Flags=$flags}
            }
        }
    }catch{}finally{if($reader){$reader.Dispose()}elseif($stream){$stream.Dispose()}}
    return $null
}
function Get-MinecraftProcesses {
    # Inspect the command line only in memory. Never log it: it can include authentication tokens.
    @(Get-CimInstance Win32_Process -Filter "Name='javaw.exe' OR Name='java.exe'" | Where-Object {$_.CommandLine -match '(?:^|\s)net\.minecraft\.client\.main\.Main(?:\s|$)'})
}
function Get-Lifecycle {
    try{
        $state=Get-Content -Raw -LiteralPath (Join-Path $labDirectory 'backend-lifecycle-v2.json') | ConvertFrom-Json
        if(([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()-[long]$state.timestamp)-lt 4000 -and (Get-Process -Id $state.processId -ErrorAction SilentlyContinue)){return $state}
    }catch{}
    return $null
}
function Get-ProductHost {
    $running=@(Get-Process -Name SpaceEngineers,Legacy -ErrorAction SilentlyContinue)
    $knownLauncher=[IO.Path]::GetFullPath((Join-Path $labDirectory 'tools\Pulsar\Legacy.exe'))
    $identity=$null
    try{
        $identityStream=[IO.File]::Open((Join-Path $labDirectory 'host-lifecycle-v2.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite-bor[IO.FileShare]::Delete))
        try{$identityReader=[IO.StreamReader]::new($identityStream);$identity=$identityReader.ReadToEnd()|ConvertFrom-Json}finally{$identityStream.Dispose()}
    }catch{}
    foreach($process in $running){
        $managed=$false
        try{
            if($process.Path -and [string]::Equals([IO.Path]::GetFullPath($process.Path),$knownLauncher,[StringComparison]::OrdinalIgnoreCase)){$managed=$true}
            if($identity -and $identity.version-eq2 -and $identity.processId-eq$process.Id){
                $started=([DateTimeOffset]$process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()
                if([Math]::Abs($started-[long]$identity.processStartedAt)-le3000){$managed=$true}
            }
        }catch{}
        if(!$managed){throw 'Space Engineers działa bez integracji Minecraft. Zapisz świat i zamknij tę grę, a następnie ponownie uruchom skrót Minecraft x Space Engineers.'}
    }
    return [pscustomobject]@{Running=($running.Count-gt0);ProcessIds=@($running|Select-Object -ExpandProperty Id)}
}
function Test-Allowed($Rules){
    if(!$Rules){return $true}
    $allowed=$false
    foreach($rule in $Rules){
        $matches=$true
        if($rule.os){
            if($rule.os.name -and $rule.os.name-ne 'windows'){$matches=$false}
            if($rule.os.arch -and $rule.os.arch-ne 'x86_64' -and $rule.os.arch-ne 'amd64'){$matches=$false}
            if($rule.os.version -and [Environment]::OSVersion.Version.ToString()-notmatch $rule.os.version){$matches=$false}
        }
        if($rule.features){$matches=$false}
        if($matches){$allowed=$rule.action-eq 'allow'}
    }
    return $allowed
}
function Quote-Argument([string]$Value){
    # Windows CommandLineToArgvW quoting; no shell evaluates these arguments.
    if($Value -notmatch '[\s"]' -and $Value.Length -gt 0){return $Value}
    return '"'+([regex]::Replace([regex]::Replace($Value,'(\\*)"','$1$1\"'),'(\\+)$','$1$1'))+'"'
}
try{
    if(!$ValidateOnly){
        $startupMutex=[Threading.Mutex]::new($false,'Local\SEMC-V2-Startup')
        try{$startupMutexHeld=$startupMutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$startupMutexHeld=$true}
        if(!$startupMutexHeld){return}
    }
    foreach($path in @($javaExecutable,$agentJar,(Join-Path $installedDirectory "versions\$version\$version.json"))){if(!(Test-Path -LiteralPath $path)){throw 'Required installed Minecraft 26.3 / Java25 / agent file is missing'}}
    if($StartSE){
        $hostState=Get-ProductHost
        if(!$ValidateOnly){
            New-Item -ItemType File -Path (Join-Path $labDirectory 'mc-v2-capture64.flag') -Force | Out-Null
            New-Item -ItemType File -Path (Join-Path $labDirectory 'mc-v2-streaming.flag') -Force | Out-Null
        }
    }
    $header=Read-BackendHeader
    $lifecycle=Get-Lifecycle
    if(!$ValidateColdOnly -and $header -and $header.Fresh -and ($header.Flags-band 1)-ne 0 -and $lifecycle){
        if($ValidateOnly){[pscustomobject]@{Action='Reuse existing guarded backend';Prepared=(($header.Flags-band 2)-ne 0);Hidden=(($header.Flags-band 4)-ne 0)};return}
        Write-LaunchLog 'Reusing the already running guarded backend.'
        if(($header.Flags-band 2)-eq 0){& (Join-Path $PSScriptRoot 'control.ps1') -Operation 10 | Out-Null}
        if(($header.Flags-band 4)-eq 0){& (Join-Path $PSScriptRoot 'control.ps1') -Operation 7 | Out-Null}
    }else{
        $processes=Get-MinecraftProcesses
        $dedicated=@($processes|Where-Object {$_.CommandLine.Contains($backendDirectory)})
        if($dedicated.Count-gt 0 -and !$ValidateOnly){throw 'A dedicated Minecraft backend is already starting or needs recovery. No second backend was launched.'}
        $versionData=Get-Content -Raw -LiteralPath (Join-Path $installedDirectory "versions\$version\$version.json") | ConvertFrom-Json
        $libraries=@($versionData.libraries|Where-Object {Test-Allowed $_.rules}|ForEach-Object {if($_.downloads.artifact){Join-Path (Join-Path $installedDirectory 'libraries') $_.downloads.artifact.path}})
        $gameJar=Join-Path $installedDirectory "versions\$version\$version.jar"
        foreach($library in @($libraries)+@($gameJar)){if(!(Test-Path -LiteralPath $library)){throw 'An installed Minecraft library is missing; repair this version in the official launcher.'}}
        # Only installed profile identity is used; credential fields are not accessed.
        try{$accounts=Get-Content -Raw -LiteralPath (Join-Path $installedDirectory 'launcher_accounts_microsoft_store.json') | ConvertFrom-Json}
        catch{throw 'The installed Minecraft profile could not be read. Open the official launcher to repair it.'}
        $account=$accounts.accounts.psobject.Properties | Where-Object {$_.Name-eq $accounts.activeAccountLocalId} | Select-Object -First 1
        if(!$account){$account=$accounts.accounts.psobject.Properties | Select-Object -First 1}
        if(!$account.Value.minecraftProfile.id -or !$account.Value.minecraftProfile.name){throw 'No installed Minecraft player profile. Sign in once through the official Minecraft Launcher.'}
        $nativeDirectory=Join-Path $backendDirectory 'natives'
        $replace=@{
            '${natives_directory}'=$nativeDirectory;'${launcher_name}'='SEMC local backend';'${launcher_version}'='2'
            '${classpath}'=((@($libraries)+@($gameJar))-join ';')
        }
        $arguments=[Collections.Generic.List[string]]::new()
        foreach($value in @('-Xms512M','-Xmx2G','-Dsemc.backend.autoStart=true',("-javaagent:{0}={1}|{0}"-f $agentJar,$labDirectory))){$arguments.Add($value)}
        $loggingFile=Join-Path $installedDirectory ('assets\log_configs\'+$versionData.logging.client.file.id)
        if(Test-Path -LiteralPath $loggingFile){$arguments.Add($versionData.logging.client.argument.Replace('${path}',$loggingFile))}
        foreach($entry in $versionData.arguments.jvm){
            if($entry-is [string]){$values=@($entry)}elseif(Test-Allowed $entry.rules){$values=@($entry.value)}else{continue}
            foreach($value in $values){foreach($key in $replace.Keys){$value=$value.Replace($key,$replace[$key])};$arguments.Add($value)}
        }
        $arguments.Add($versionData.mainClass)
        foreach($value in @('--username',[string]$account.Value.minecraftProfile.name,'--uuid',[string]$account.Value.minecraftProfile.id,'--accessToken','0','--version',$version,'--gameDir',$backendDirectory,'--assetsDir',(Join-Path $installedDirectory 'assets'),'--assetIndex',[string]$versionData.assetIndex.id,'--versionType','SEMC local backend','--quickPlaySingleplayer',$worldName,'--width','854','--height','480')){$arguments.Add($value)}
        $sourceWorld=Join-Path $installedDirectory "saves\$worldName"
        $destinationWorld=Join-Path $backendDirectory "saves\$worldName"
        if(!(Test-Path -LiteralPath (Join-Path $sourceWorld 'level.dat')) -and !(Test-Path -LiteralPath (Join-Path $destinationWorld 'level.dat'))){throw 'Dedicated SEMC Backend world is missing; original saves were not modified.'}
        if($ValidateOnly){[pscustomobject]@{Action='Cold local backend launch';Version=$version;JavaMajor=25;LibraryCount=$libraries.Count;BackendDirectory=$backendDirectory;CopiesClone=!(Test-Path -LiteralPath $destinationWorld);NormalMinecraftInstances=$processes.Count;CredentialsCopied=$false};return}
        if($header -and $header.Fresh -and $lifecycle){
            # An agent attached to a different world must stop writing the shared transport.
            # STOP only detaches our runtime, leaving that normal game and world running.
            & (Join-Path $PSScriptRoot 'control.ps1') -Operation 4 | Out-Null
        }
        if(!(Test-Path -LiteralPath (Join-Path $destinationWorld 'level.dat'))){
            $worldLock=$null
            try{
                $worldLock=[IO.File]::Open((Join-Path $sourceWorld 'session.lock'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
                New-Item -ItemType Directory -Path (Split-Path -Parent $destinationWorld) -Force | Out-Null
                $stagingWorld=$destinationWorld+'.preparing-'+[Guid]::NewGuid().ToString('N')
                New-Item -ItemType Directory -Path $stagingWorld -Force | Out-Null
                Get-ChildItem -LiteralPath $sourceWorld -Force | Where-Object {$_.Name-ne 'session.lock'} | Copy-Item -Destination $stagingWorld -Recurse -Force
                $resolvedStaging=[IO.Path]::GetFullPath($stagingWorld);$resolvedDestination=[IO.Path]::GetFullPath($destinationWorld)
                if(!$resolvedStaging.StartsWith($backendDirectory+'\') -or !$resolvedDestination.StartsWith($backendDirectory+'\')){throw 'Clone path escaped isolated backend directory'}
                Move-Item -LiteralPath $resolvedStaging -Destination $resolvedDestination
            }catch{throw 'The dedicated source clone is still open or could not be copied. Close only SEMC Backend before the first isolated launch.'}finally{if($worldLock){$worldLock.Dispose()}}
            Write-LaunchLog 'Copied dedicated clone to isolated game directory; original source clone preserved.'
        }
        New-Item -ItemType Directory -Path $nativeDirectory -Force | Out-Null
        if(!(Test-Path -LiteralPath (Join-Path $backendDirectory 'options.txt'))){
            [IO.File]::WriteAllLines((Join-Path $backendDirectory 'options.txt'),@('pauseOnLostFocus:false','renderDistance:3','simulationDistance:5','maxFps:30','enableVsync:false','soundCategory_master:0.0','fullscreen:false','onboardAccessibility:false'),[Text.UTF8Encoding]::new($false))
        }
        [IO.File]::WriteAllText((Join-Path $labDirectory 'mc-v2-startup.flag'),$worldName,[Text.UTF8Encoding]::new($false))
        New-Item -ItemType File -Path (Join-Path $labDirectory 'enable-v2-edits.flag') -Force | Out-Null
        if(Test-Path -LiteralPath (Join-Path $labDirectory 'mc-v2-stop.flag')){Remove-Item -LiteralPath (Join-Path $labDirectory 'mc-v2-stop.flag')}
        & (Join-Path $PSScriptRoot 'configure-launcher-v2.ps1') -BackendDirectory $backendDirectory | Out-Null
        $argumentLine=($arguments|ForEach-Object {Quote-Argument $_})-join ' '
        $process=Start-Process -FilePath $javaExecutable -ArgumentList $argumentLine -WorkingDirectory $backendDirectory -WindowStyle Hidden -PassThru
        Write-LaunchLog ('Started local offline Minecraft backend PID '+$process.Id+'. No account tokens copied.')
        $deadline=[DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        while([DateTime]::UtcNow-lt $deadline){
            if($process.HasExited){throw 'Minecraft backend exited during startup; see its logs/latest.log and mc-v2.log.'}
            $header=Read-BackendHeader;$state=Get-Lifecycle
            if($header -and $header.Fresh -and ($header.Flags-band 7)-eq 7 -and $state -and $state.processId-eq $process.Id){break}
            Start-Sleep -Milliseconds 250
        }
        if(!$header -or !$header.Fresh -or ($header.Flags-band 7)-ne 7){
            if($header -and $header.Fresh){& (Join-Path $PSScriptRoot 'control.ps1') -Operation 8 | Out-Null}
            throw 'Minecraft startup timed out; recovery requested. See backend-launch-v2.log.'
        }
        Write-LaunchLog 'Dedicated native world prepared and hidden.'
    }
    if($StartSE){
        $hostState=Get-ProductHost
        if(!$hostState.Running){
            & (Join-Path $labDirectory 'start-v2.ps1') | Out-Null
            Write-LaunchLog 'Space Engineers integration launch requested.'
        }else{Write-LaunchLog ('Reusing verified integrated Space Engineers host PID '+($hostState.ProcessIds-join ','))}
    }
}catch{
    # Messages deliberately exclude raw ProcessStartInfo, command lines and account data.
    Write-LaunchLog ('ERROR '+$_.Exception.Message)
    throw
}finally{
    if($startupMutexHeld){$startupMutex.ReleaseMutex()}
    if($startupMutex){$startupMutex.Dispose()}
}
