using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
class UnifiedContainerAudit {
 static string root=@"C:\Users\konra\Documents\SEMC-Lab";
 static string bin=@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
 static int Main(){
  AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{string n=new AssemblyName(e.Name).Name+".dll";if(n=="SEMC.Runtime.next.dll")n="SEMC.Runtime.dll";foreach(string d in new[]{Path.Combine(root,"se-bridge","v2"),Path.Combine(root,"tools","Pulsar","Libraries","Legacy"),bin}){string p=Path.Combine(d,n);if(File.Exists(p))return Assembly.LoadFrom(p);}return null;};
  try{return Run();}catch(Exception e){Console.WriteLine(e);return 1;}
 }
 static void Check(bool value,string label){if(!value)throw new Exception(label);}
 static void Menu(string path,bool open,long age){File.WriteAllText(path,new JObject{{"timestamp",SEMC.V2.BridgeIO.Now()-age},{"open",open},{"containerId",73},{"type","minecraft:crafting"}}.ToString());Thread.Sleep(120);}
 static int Run(){
  string dir=Path.Combine(Path.GetTempPath(),"semc-container-audit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);string path=Path.Combine(dir,"menu-v2.json");
  var commands=new List<SEMC.V2.BridgeCommand>();int opened=0;bool busy=false;
  using(var bridge=new SEMC.V2.ContainerBridge(dir,c=>commands.Add(c),s=>{})){
   bridge.OpenUnifiedCrafting=()=>opened++;bridge.EconomyBusy=()=>busy;
   Menu(path,true,0);bridge.Update(true);
   Check(commands.Count==1&&commands[0].Operation==14&&commands[0].X==73,"Native close not sent");
   Check(opened==0&&SEMC.V2.ContainerBridge.IsOpen,"Opened before close acknowledgement or input not gated");
   busy=true;Menu(path,false,0);bridge.Update(true);Check(opened==0&&SEMC.V2.ContainerBridge.IsOpen,"Did not wait for busy transaction");
   busy=false;Menu(path,false,0);bridge.Update(true);Check(opened==1&&!SEMC.V2.ContainerBridge.IsOpen,"Close acknowledgement did not open unified crafting");
   Menu(path,false,0);bridge.Update(true);Check(opened==1,"Opened duplicate screen");
   Menu(path,true,0);bridge.Update(true);Menu(path,false,10000);bridge.Update(true);Check(opened==1&&!SEMC.V2.ContainerBridge.IsOpen,"Stale close acknowledged incorrectly");
   Menu(path,true,0);bridge.Update(true);Thread.Sleep(120);bridge.Update(false);Menu(path,false,0);bridge.Update(true);Check(opened==1&&!SEMC.V2.ContainerBridge.IsOpen,"Disconnected pending menu reopened");
  }
  File.Delete(path);Directory.Delete(dir);Console.WriteLine("PASS native menu close acknowledgement, transaction wait, exactly-once UI open, stale/disconnected cancellation, input gating.");return 0;
 }
}
