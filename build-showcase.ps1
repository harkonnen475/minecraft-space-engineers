param([switch]$PlanOnly)
$ErrorActionPreference='Stop'
$terrain=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'terrain-v2.json') -Raw|ConvertFrom-Json
$cells=[ordered]@{}
function Block([int]$x,[int]$y,[int]$z,[int]$item,[int]$face=1,[double]$yaw=0){
  $cells["$x,$y,$z"]=[ordered]@{x=$x;y=$y;z=$z;item=$item;face=$face;yaw=$yaw}
}
# Solid foundation follows the real planet instead of leaving a floating platform.
foreach($x in -12..-4){foreach($z in -12..-6){
  $height=$terrain.heights[($x-$terrain.originX)+$terrain.sizeX*($z-$terrain.originZ)]
  if($null -eq $height){throw 'Missing native terrain sample under the workshop'}
  foreach($y in ([int]$height)..238){Block $x $y $z 23}
  Block $x 239 $z 2
}}
# Oak frame, glass windows, five-metre open entrance, and a wooden roof.
foreach($y in 240..244){foreach($x in -12..-4){foreach($z in -12..-6){
  if($x -ne -12 -and $x -ne -4 -and $z -ne -12 -and $z -ne -6){continue}
  if($z -eq -6 -and $x -ge -11 -and $x -le -5 -and $y -le 243){continue}
  $corner=($x -eq -12 -or $x -eq -4) -and ($z -eq -12 -or $z -eq -6)
  $item=if($corner){3}elseif($y -eq 241 -or $y -eq 242){4}else{2}
  Block $x $y $z $item
}}}
foreach($x in -13..-3){foreach($z in -13..-5){Block $x 245 $z 2}}
# Exterior lamp is powered by its own real Minecraft lever on the front face.
Block -8 244 -6 9
Block -8 244 -5 11 3
# Functional items remain native blocks on the hidden server.
Block -11 240 -11 13
Block -10 240 -11 14
Block -5 240 -11 15 1 -90
Block -6 240 -11 2
Block -4 240 -11 11 5
# The five-metre approach is a native SE steel ramp, built by showcaseramp.
$plan=@($cells.Values)
$plan|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'showcase-plan.json') -Encoding utf8
if($PlanOnly){[pscustomobject]@{blocks=$plan.Count;bounds='x -13..-3, z -13..-5, terrain..245'}|ConvertTo-Json;return}
$control=Join-Path $PSScriptRoot 'minecraft-bridge\v2\control.ps1'
$completed=0
foreach($cell in $plan){
  $response=& $control -Operation 2 -X $cell.x -Y $cell.y -Z $cell.z -Item $cell.item -Face $cell.face -Flags 1 -PlayerX ($cell.x+0.5) -PlayerY ($cell.y+1.0) -PlayerZ ($cell.z+3.0) -Yaw $cell.yaw | ConvertFrom-Json
  if($response.Result -ne 1){throw "Native placement failed at $($cell.x),$($cell.y),$($cell.z), item $($cell.item)"}
  $completed++
  if($completed%50 -eq 0){Write-Output "Placed $completed / $($plan.Count) native blocks"}
}
[pscustomobject]@{placed=$completed;timestamp=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()}|ConvertTo-Json
