using System;
using System.IO;
using System.Reflection;
class RegistryIconAudit {
 static string root=@"C:\Users\konra\Documents\SEMC-Lab";
 static string bin=@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
 static int Main() {
  AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{string n=new AssemblyName(e.Name).Name+".dll";if(n=="SEMC.Runtime.next.dll")n="SEMC.Runtime.dll";foreach(string d in new[]{Path.Combine(root,"se-bridge","v2"),Path.Combine(root,"tools","Pulsar","Libraries","Legacy"),bin}){string p=Path.Combine(d,n);if(File.Exists(p))return Assembly.LoadFrom(p);}return null;};
  try { return Run(); } catch(Exception e){Console.WriteLine(e);return 1;}
 }
 static int Run(){
  var icons=new SEMC.V2.ItemIcons(root); int resolved=0,fallback=0;
  foreach(string path in Directory.GetFiles(Path.Combine(root,"render-assets","minecraft","models","item"),"*.json")){
   string id="minecraft:"+Path.GetFileNameWithoutExtension(path);string icon=icons.Get(id);
   if(icon==null){fallback++;continue;}
   using(var f=new BinaryReader(File.OpenRead(icon))){if(f.ReadInt32()!=0x20534444)throw new Exception("Invalid DDS "+id);}
   resolved++;
  }
  foreach(string id in new[]{"minecraft:../../outside","minecraft:no_such_item_999","se:MyObjectBuilder_Ingot/Iron"})if(icons.Get(id)!=null)throw new Exception("Unexpected guessed icon "+id);
  if(icons.Get("minecraft:diamond_sword")==null||icons.Get("minecraft:oak_stairs")==null||icons.Get("minecraft:redstone_lamp")==null)throw new Exception("Basic icons missing");
  if(icons.Get("se:MyObjectBuilder_Ingot/Iron",@"Textures\GUI\Icons\ingot\iron_ingot.dds")!=@"Textures\GUI\Icons\ingot\iron_ingot.dds")throw new Exception("Native icon not retained");
  Console.WriteLine("PASS registry textures="+resolved+" explicit text fallbacks="+fallback+"; safe unknown/SE IDs; native definition icon retained.");return 0;
 }
}
