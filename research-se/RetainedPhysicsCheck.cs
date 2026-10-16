using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using SEMC.V2;
using VRageMath;
class RetainedPhysicsCheck {
 static string root=@"C:\Users\konra\Documents\SEMC-Lab\research-se";
 static string bin=@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64";
 static void Main() {
  AppDomain.CurrentDomain.AssemblyResolve+=(o,e)=>{ string name=new AssemblyName(e.Name).Name+".dll";string file=Path.Combine(root,name);if(!File.Exists(file))file=Path.Combine(bin,name);return File.Exists(file)?Assembly.LoadFrom(file):null;};
  Run();
 }
 static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
 [MethodImpl(MethodImplOptions.NoInlining)] static void Run(){
  var physics=new PhysicsWorld(8,8,8,Console.WriteLine);
  var palette=new Dictionary<int,BoundingBox[]>();
  Action<int,int[]> update=(origin, positions)=>{int[] states=new int[512],flags=new int[512];foreach(int absoluteX in positions){int i=absoluteX-origin;states[i]=1;flags[i]=1;}MatrixD frame=MatrixD.Identity;frame.Translation=new Vector3D(origin,0,0);physics.Update(states,flags,palette,frame,new Vector3I(origin,0,0));};
  update(-8,new[]{-6});Check(physics.CachedCellCount==1 && physics.RetainedChunkCount==1,"negative chunk");
  update(-4,new[]{-4,1});Check(physics.CachedCellCount==3 && physics.RetainedChunkCount==2,"partial window must retain unseen -6");
  Check(physics.IsOccupiedAbsolute(new Vector3I(-6,0,0)),"negative retained cell");
  physics.ApplyConfirmedRemoval(new Vector3I(-6,0,0));Check(physics.CachedCellCount==2,"confirmed off-window removal");
  update(100,new int[0]);Check(physics.CachedCellCount==2 && physics.RetainedChunkCount==2,"distant window must retain");
  Check(!physics.IsOccupied(Vector3I.Zero),"active local state must update independently");
  Check(physics.WorldPoint(Vector3D.Zero).X==100,"active ray frame follows origin");
  update(-8,new int[0]);Check(physics.CachedCellCount==1 && physics.RetainedChunkCount==1,"authoritative air should remove only observed negative chunk");
  Check(physics.IsOccupiedAbsolute(new Vector3I(1,0,0)),"unseen positive cell must survive");
  physics.ApplyConfirmedRemoval(new Vector3I(1,0,0));Check(physics.CachedCellCount==0 && physics.RetainedChunkCount==0,"last nonempty chunk should be evicted");
  update(-8,new[]{-6});MatrixD moved=MatrixD.Identity;moved.Translation=new Vector3D(500,0,0);physics.Update(new int[512],new int[512],palette,moved,Vector3I.Zero);Check(physics.CachedCellCount==0,"actual anchor change must reset old world");
  physics.Dispose();Console.WriteLine("PASS: negative coordinates, partial-window merge, distant retention, authoritative air, confirmed removal, local-frame update, anchor reset. No native entities created.");
 }
}