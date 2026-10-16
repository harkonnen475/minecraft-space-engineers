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
  var saved=new CachedTerrainChunk{Minimum=new Vector3I(-16,0,0),States=new int[512],Colors=new int[512],Flags=new int[512]};
  saved.States[1]=1;saved.Flags[1]=1;saved.States[7]=1;saved.Flags[7]=1;
  physics.RestoreRetained(new[]{saved},palette,MatrixD.Identity);
  saved.Flags[1]=0;Check(physics.IsOccupiedAbsolute(new Vector3I(-15,0,0)),"restored arrays must be copied");
  update(100,new int[0]);Check(physics.CachedCellCount==2,"first distant capture must preserve cold restored structure");
  update(-10,new int[0]);Check(physics.CachedCellCount==1 && physics.IsOccupiedAbsolute(new Vector3I(-15,0,0)),"first intersecting capture replaces only observed restored cells");
  bool rejected=false;try{physics.RestoreRetained(new[]{new CachedTerrainChunk{Minimum=new Vector3I(1,0,0),States=new int[512],Flags=new int[512]}},palette,MatrixD.Identity);}catch(ArgumentException){rejected=true;}
  Check(rejected && physics.CachedCellCount==1,"invalid restore must not clear existing world");
  var partial=new CachedTerrainChunk{Minimum=new Vector3I(-16,0,0),States=new int[512],Flags=new int[512]};
  for(int i=0;i<512;i++){partial.States[i]=-1;partial.Flags[i]=-1;}
  partial.States[1]=0;partial.Flags[1]=0;partial.States[4]=2;partial.Flags[4]=1;partial.States[7]=3;partial.Flags[7]=1;
  var distant=new CachedTerrainChunk{Minimum=new Vector3I(96,0,0),States=new int[512],Flags=new int[512]};distant.States[4]=4;distant.Flags[4]=1;
  physics.ApplyRemoteChunks(new[]{partial,distant},palette,MatrixD.Identity);
  Check(physics.CachedCellCount==3 && !physics.IsOccupiedAbsolute(new Vector3I(-15,0,0)),"remote air removes and remote nonair adds");
  Check(physics.IsOccupied(new Vector3I(1,0,0)),"remote overlap must update active gameplay ray cells");
  Check(physics.WorldPoint(Vector3D.Zero).X==-10,"remote chunks must not move active camera frame");
  var skip=new CachedTerrainChunk{Minimum=new Vector3I(96,0,0),States=new int[512],Flags=new int[512]};for(int i=0;i<512;i++)skip.States[i]=-1;
  skip.States[4]=5;skip.Flags[4]=-1;physics.ApplyRemoteChunk(skip,palette,MatrixD.Identity);
  Check(physics.CachedCellCount==3,"negative state or flags must preserve cells");
  var air=new CachedTerrainChunk{Minimum=new Vector3I(-16,0,0),States=new int[512],Flags=new int[512]};for(int i=0;i<512;i++)air.Flags[i]=1;
  physics.ApplyRemoteChunk(air,palette,MatrixD.Identity);Check(physics.CachedCellCount==1 && !physics.IsOccupied(new Vector3I(1,0,0)),"state zero must remove regardless flags");
  var invalid=new CachedTerrainChunk{Minimum=new Vector3I(8,0,0),States=new int[512],Flags=new int[512]};invalid.States[0]=-2;
  var removeFar=new CachedTerrainChunk{Minimum=new Vector3I(96,0,0),States=new int[512],Flags=new int[512]};
  rejected=false;try{physics.ApplyRemoteChunks(new[]{removeFar,invalid},palette,MatrixD.Identity);}catch(ArgumentException){rejected=true;}
  Check(rejected && physics.CachedCellCount==1,"invalid batch must not partially mutate world");
  rejected=false;MatrixD wrongFrame=MatrixD.Identity;wrongFrame.Translation=new Vector3D(9,0,0);try{physics.ApplyRemoteChunk(removeFar,palette,wrongFrame);}catch(InvalidOperationException){rejected=true;}
  Check(rejected && physics.CachedCellCount==1,"wrong remote world frame must be rejected");
  physics.Dispose();Console.WriteLine("PASS: negative coordinates, partial-window merge, distant retention, authoritative air, confirmed removal, local-frame update, anchor reset, cold restore, copied arrays, partial restored overwrite, invalid record rejection, remote add/remove/skip, active ray updates, frame preservation, invalid-batch atomicity. No native entities created.");
 }
}