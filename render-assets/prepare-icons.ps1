$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem
$jar=Join-Path $env:APPDATA '.minecraft\versions\26.3\26.3.jar'
$archive=[IO.Compression.ZipFile]::OpenRead($jar)
try {
    foreach($itemName in @('flint_and_steel','oak_door','redstone')) {
        $entry=$archive.GetEntry(('assets/minecraft/textures/item/'+$itemName+'.png'))
        $target=Join-Path $PSScriptRoot ('minecraft\textures\item\'+$itemName+'.png')
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))|Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry,$target,$true)
    }
}
finally {$archive.Dispose()}
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.IO;
using System.Drawing;
public static class SEMCIconDds {
    public static void Convert(string source,string destination) {
        using(var image=new Bitmap(source)) using(var file=File.Create(destination)) using(var w=new BinaryWriter(file)) {
            const int size=128;
            w.Write(0x20534444);w.Write(124);w.Write(0x100F);w.Write(size);w.Write(size);w.Write(size*4);w.Write(0);w.Write(1);
            for(int i=0;i<11;i++)w.Write(0);
            w.Write(32);w.Write(0x41);w.Write(0);w.Write(32);w.Write(0xff);w.Write(0xff00);w.Write(0xff0000);w.Write(unchecked((int)0xff000000));
            w.Write(0x1000);for(int i=0;i<4;i++)w.Write(0);
            int height=Math.Min(image.Width,image.Height);
            for(int y=0;y<size;y++)for(int x=0;x<size;x++) {
                Color p=image.GetPixel(x*image.Width/size,y*height/size);
                w.Write((uint)(p.R|(p.G<<8)|(p.B<<16)|(p.A<<24)));
            }
        }
    }
}
'@
$icons=Join-Path $PSScriptRoot 'icons'
[IO.Directory]::CreateDirectory($icons)|Out-Null
$names=@('dirt','stone','oak_planks','oak_log','glass','cobblestone','bricks','gold_block','redstone_block','redstone_lamp','redstone_dust_dot','lever','tnt_side','crafting_table_front','piston_top_sticky','piston_top','observer_front','white_wool','torch')
foreach($name in $names) {
    [SEMCIconDds]::Convert((Join-Path $PSScriptRoot ('minecraft\textures\block\'+$name+'.png')),(Join-Path $icons ($name+'.dds')))
}
[SEMCIconDds]::Convert((Join-Path $PSScriptRoot 'minecraft\textures\item\flint_and_steel.png'),(Join-Path $icons 'flint_and_steel.dds'))
[SEMCIconDds]::Convert((Join-Path $PSScriptRoot 'minecraft\textures\item\oak_door.png'),(Join-Path $icons 'oak_door.dds'))
[SEMCIconDds]::Convert((Join-Path $PSScriptRoot 'minecraft\textures\item\redstone.png'),(Join-Path $icons 'redstone.dds'))
Write-Output 'Prepared 22 alpha-preserving native GUI DDS textures.'
