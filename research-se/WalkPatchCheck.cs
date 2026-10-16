using System;
using System.IO;
using System.Reflection;
class WalkPatchCheck {
 static string root=@"C:\Users\konra\Documents\SEMC-Lab";
 static string bin=@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
 static void Main(){
  AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{string name=new AssemblyName(e.Name).Name+".dll";foreach(string dir in new[]{bin,Path.Combine(root,"tools","Pulsar","Libraries","Legacy"),Path.Combine(root,"research-se")}){string file=Path.Combine(dir,name);if(File.Exists(file))return Assembly.LoadFrom(file);}return null;};
  Assembly a=Assembly.LoadFrom(Path.Combine(root,"research-se","WalkOverride.check.dll"));
  Type type=a.GetType("SEMC.V2.RuntimeControl",true);
  object instance=Activator.CreateInstance(type,new object[]{null,null,new Action<string>(Console.WriteLine)});
  try{type.GetMethod("EnsureCharacterMotionOverride",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(instance,null);Console.WriteLine("PASS native MyCharacter.MoveAndRotate Harmony prefix installation");}
  finally{((IDisposable)instance).Dispose();Console.WriteLine("PASS scoped patch cleanup");}
 }
}