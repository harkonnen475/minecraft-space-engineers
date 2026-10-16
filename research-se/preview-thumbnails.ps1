Add-Type -AssemblyName System.Drawing
$names=@('oak_stairs','oak_slab','stone_bricks','oak_door','oak_fence','ladder','grass_block','sand','chest')
$root=Split-Path $PSScriptRoot -Parent
$sheet=New-Object Drawing.Bitmap (1152,128)
$g=[Drawing.Graphics]::FromImage($sheet)
$g.Clear([Drawing.Color]::FromArgb(32,42,50))
for($n=0;$n -lt $names.Length;$n++) {
 $bytes=[IO.File]::ReadAllBytes((Join-Path $root ('render-assets\icons\'+$names[$n]+'.dds')))
 $bitmap=New-Object Drawing.Bitmap (128,128)
 for($y=0;$y -lt 128;$y++){for($x=0;$x -lt 128;$x++){$i=128+4*($x+128*$y);$bitmap.SetPixel($x,$y,[Drawing.Color]::FromArgb($bytes[$i+3],$bytes[$i],$bytes[$i+1],$bytes[$i+2]))}}
 $g.DrawImageUnscaled($bitmap,$n*128,0);$bitmap.Dispose()
}
$g.Dispose()
$sheet.Save((Join-Path $PSScriptRoot 'new-item-thumbnails.png'),[Drawing.Imaging.ImageFormat]::Png)
$sheet.Dispose()
