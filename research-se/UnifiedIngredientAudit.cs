using System;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
class UnifiedIngredientAudit {
 static string root=@"C:\Users\konra\Documents\SEMC-Lab",bin=@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
 static int Main(){AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{string n=new AssemblyName(e.Name).Name+".dll";if(n=="SEMC.Runtime.next.dll")n="SEMC.Runtime.dll";foreach(string d in new[]{Path.Combine(root,"se-bridge","v2"),Path.Combine(root,"tools","Pulsar","Libraries","Legacy"),bin}){string p=Path.Combine(d,n);if(File.Exists(p))return Assembly.LoadFrom(p);}return null;};try{return Run();}catch(Exception e){Console.WriteLine(e);return 1;}}
 static int Run(){
  var inputs=new JArray();for(int i=0;i<5;i++)inputs.Add(new JObject{{"alternatives",i%2==0?new JArray("minecraft:acacia_planks","minecraft:oak_planks"):new JArray("minecraft:oak_planks","minecraft:acacia_planks")},{"amount",1},{"available",2}});
  inputs.Add(new JObject{{"alternatives",new JArray("minecraft:stick")},{"amount",2},{"available",4}});
  var type=typeof(SEMC.V2.UnifiedInventoryScreen).GetNestedType("InventoryScreen",BindingFlags.NonPublic);
  var grouped=(JArray)type.GetMethod("GroupIngredients",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{inputs});
  if(grouped.Count!=2||(decimal)grouped[0]["amount"]!=5||(decimal)grouped[0]["available"]!=2||(decimal)grouped[1]["amount"]!=2||(int)inputs[0]["amount"]!=1)throw new Exception("Grouping changed quantities, availability, alternatives or original recipe");
  var selection=type.GetMethod("SelectionReference",BindingFlags.Static|BindingFlags.NonPublic); var one=new JObject{{"id","minecraft:diamond_pickaxe"},{"stackKey","mc:3:enchanted"}}; var two=new JObject{{"id","minecraft:diamond_pickaxe"},{"stackKey","mc:5:worn"}}; if((string)selection.Invoke(null,new object[]{one,true})!="stack:mc:3:enchanted"||(string)selection.Invoke(null,new object[]{two,true})!="stack:mc:5:worn"||(string)selection.Invoke(null,new object[]{one,false})!="minecraft:diamond_pickaxe")throw new Exception("Exact stack reference lost or catalog changed"); Console.WriteLine("PASS exact inventory stack references distinct; catalog keeps stable ID. Repeated ingredients aggregate for display only; equivalent alternatives order-independent; stock not multiplied; source recipe unchanged.");return 0;
 }
}
