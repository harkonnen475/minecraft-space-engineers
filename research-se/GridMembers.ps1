$seBin='C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64'
[AppDomain]::CurrentDomain.add_AssemblyResolve({param($sender,$event) $p=Join-Path $seBin ((New-Object Reflection.AssemblyName($event.Name)).Name + '.dll'); if(Test-Path -LiteralPath $p){return [Reflection.Assembly]::LoadFrom($p)} })
$a=[Reflection.Assembly]::LoadFrom((Join-Path $seBin 'Sandbox.Game.dll'))
$t=$a.GetType('Sandbox.Game.Entities.MyCubeGrid')
$t.GetMembers([Reflection.BindingFlags]'Instance,Static,Public,NonPublic') | Where-Object {$_.Name -match 'Split|Connection|Deformation|Dynamic|Destruction|Editable'} | ForEach-Object { $_.ToString() }
