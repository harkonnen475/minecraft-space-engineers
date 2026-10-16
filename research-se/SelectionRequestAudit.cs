using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
class SelectionRequestAudit {
 static string root=@"C:\Users\konra\Documents\SEMC-Lab";
 static string bin=@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
 static int Main(){
  AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{string n=new AssemblyName(e.Name).Name+".dll";if(n=="SEMC.Runtime.next.dll")n="SEMC.Runtime.dll";foreach(string d in new[]{Path.Combine(root,"se-bridge","v2"),Path.Combine(root,"tools","Pulsar","Libraries","Legacy"),bin}){string p=Path.Combine(d,n);if(File.Exists(p))return Assembly.LoadFrom(p);}return null;};
  try{return Run();}catch(Exception e){Console.WriteLine(e);return 1;}
 }
 static void Check(bool value,string label){if(!value)throw new Exception(label);}
 static int Run(){
  var calls=new List<string>();
  var input=new SEMC.V2.GameplayInput(null,c=>{},s=>{});
  input.SetCatalog(new JArray(new JObject{{"id","audit:item_a"},{"index",9000},{"block",true}},new JObject{{"id","audit:item_b"},{"index",9001},{"block",false}}));
  input.RequestSelection=id=>calls.Add(id);
  // Native state is unknown until acknowledged; loading unrelated metadata must not
  // pretend that the first catalog entry is held or enable placement.
  Check(input.SelectedItem==-1&&!input.SelectedIsBlock&&input.SelectedItemName=="Pusta dłoń","Empty native selection not represented");
  input.SelectItem(9000);
  Check(calls.Count==1&&calls[0]=="audit:item_a","Stable ID not requested");
  Check(input.SelectedItem==-1&&!input.SelectedIsBlock,"Request committed before native acknowledgement");
  input.SelectById("audit:item_b");
  Check(calls.Count==2&&calls[1]=="audit:item_b"&&input.SelectedItem==-1,"ID request changed selection optimistically");
  input.SelectById("audit:unknown");
  Check(calls.Count==3&&input.SelectedItem==-1,"Unknown request changed held item");
  input.SetOwned(new JArray(new JObject{{"id","audit:item_a"},{"amount",2}},new JObject{{"id","audit:item_b"},{"amount",0}},new JObject{{"id","se:tool"},{"amount",1}}));
  var owned=(HashSet<string>)typeof(SEMC.V2.GameplayInput).GetField("ownedItems",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(input);
  Check(owned.Count==1&&owned.Contains("audit:item_a"),"Owned wheel includes empty or native SE entries");
  Console.WriteLine("PASS request-before-ack selection, stable IDs, unknown/empty selection, positive owned MC filtering; no preference mutations.");return 0;
 }
}
