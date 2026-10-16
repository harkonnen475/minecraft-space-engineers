using System;
using System.IO;
using System.Reflection;
class Probe {
 static string bin=@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
 static void Main(string[]args){
  AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{var p=Path.Combine(bin,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
  var a=Assembly.LoadFrom(Path.Combine(bin,args[0]+".dll"));
  if(args.Length<2){foreach(var t in a.GetTypes())Console.WriteLine(t.FullName);return;}
  var type=a.GetType(args[1]);if(type==null){Console.WriteLine("MISSING");return;}
  Console.WriteLine(type.FullName+" base "+type.BaseType);
  foreach(var m in type.GetMembers(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly)) if(args.Length<3||m.Name.IndexOf(args[2],StringComparison.OrdinalIgnoreCase)>=0)Console.WriteLine(m.MemberType+" "+m);
 }
}
