using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Collections.Generic;
class Inspect {
 static string bin = @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
 static Dictionary<short,OpCode> ops = new Dictionary<short,OpCode>();
 static void Main() {
  AppDomain.CurrentDomain.AssemblyResolve += (o,e) => {string p=Path.Combine(bin,new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};
  foreach(var f in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static)) if(f.FieldType==typeof(OpCode)){var x=(OpCode)f.GetValue(null);ops[x.Value]=x;}
  var a=Assembly.LoadFrom(Path.Combine(bin,"VRage.dll"));
  foreach(string tn in new[]{"VRage.Plugins.MyPlugins","VRage.Plugins.IPlugin","VRage.Plugins.IHandleInputPlugin"}){
   var t=a.GetType(tn);Console.WriteLine("TYPE "+tn);if(t==null)continue;
   foreach(var m in t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly)) {
    Console.WriteLine(m.ToString());if(m.Name=="RegisterFromArgs"||m.Name=="RegisterUserAssemblyFiles"||m.Name=="Load"||m.Name=="LoadPlugins")Dump(m);
   }
  }
 }
 static void Dump(MethodInfo m){var body=m.GetMethodBody();if(body==null)return;var il=body.GetILAsByteArray();int i=0;while(i<il.Length){int off=i;short c=il[i++];if(c==254)c=(short)(0xfe00|il[i++]);OpCode op;if(!ops.TryGetValue(c,out op))break;string val="";int n=0;switch(op.OperandType){case OperandType.InlineNone:break;case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:n=1;val=il[i].ToString();break;case OperandType.InlineVar:n=2;val=BitConverter.ToInt16(il,i).ToString();break;case OperandType.InlineI8:case OperandType.InlineR:n=8;val=BitConverter.ToInt64(il,i).ToString();break;case OperandType.InlineSwitch:n=4+4*BitConverter.ToInt32(il,i);break;default:n=4;int tok=BitConverter.ToInt32(il,i);try{if(op.OperandType==OperandType.InlineString)val=m.Module.ResolveString(tok);else if(op.OperandType==OperandType.InlineMethod||op.OperandType==OperandType.InlineField||op.OperandType==OperandType.InlineType||op.OperandType==OperandType.InlineTok)val=m.Module.ResolveMember(tok).ToString();else val=tok.ToString();}catch{val=tok.ToString();}break;}Console.WriteLine("  "+off.ToString("x4")+" "+op.Name+" "+val);i+=n;}}
}
