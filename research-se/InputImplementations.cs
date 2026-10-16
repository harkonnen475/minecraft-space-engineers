using System;using System.IO;using System.Reflection;
class InputImplementations{
 static string bin=@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
 static void Main(){AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{string p=Path.Combine(bin,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};foreach(string name in new[]{"VRage.Input","VRage.Platform.Windows","Sandbox.Game","Sandbox.Graphics","SpaceEngineers.Game"}){var a=Assembly.LoadFrom(Path.Combine(bin,name+".dll"));foreach(Type t in a.GetTypes()){foreach(MethodInfo m in t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly)){if(m.Name=="IsNewKeyPressed"||m.Name=="SetControlBlock"||m.Name=="IsKeyPress")Console.WriteLine(name+" "+t.FullName+" "+m.ToString());}}}}
}
