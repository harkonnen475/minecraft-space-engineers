$ErrorActionPreference='Stop'
$compiler='C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$executable=Join-Path $PSScriptRoot 'Minecraft x Space Engineers.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /reference:System.Windows.Forms.dll ('/out:'+$executable) (Join-Path $PSScriptRoot 'Launcher.cs')
if($LASTEXITCODE -ne 0){throw 'Launcher compilation failed'}
$desktop=[Environment]::GetFolderPath('Desktop')
$shell=New-Object -ComObject WScript.Shell
$shortcut=$shell.CreateShortcut((Join-Path $desktop 'Minecraft x Space Engineers.lnk'))
$shortcut.TargetPath=$executable
$shortcut.WorkingDirectory=$PSScriptRoot
$shortcut.Description='Minecraft w świecie Space Engineers — uruchamia obie gry i zapisany świat.'
$shortcut.IconLocation='C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64\SpaceEngineers.exe,0'
$shortcut.Save()
$previous=Join-Path $desktop 'Minecraft x Space Engineers - Lab.lnk'
if(Test-Path -LiteralPath $previous){
  $backup=Join-Path $PSScriptRoot 'backups\Minecraft x Space Engineers - Lab.lnk'
  Copy-Item -LiteralPath $previous -Destination $backup -Force
  Remove-Item -LiteralPath $previous
}
Write-Output (Join-Path $desktop 'Minecraft x Space Engineers.lnk')
