using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using SEMC.V2;
using VRage.Game;
using VRageMath;
class RampContentCheck
{
 static string root=@"C:\Users\konra\Documents\SEMC-Lab";
 static void Main()
 {
  AppDomain.CurrentDomain.AssemblyResolve+=delegate(object sender,ResolveEventArgs e){string name=new AssemblyName(e.Name).Name+".dll";foreach(string dir in new[]{Path.Combine(root,"research-se"),@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64",Path.Combine(root,@"tools\Pulsar\Libraries\Legacy")}){string p=Path.Combine(dir,name);if(File.Exists(p))return Assembly.LoadFrom(p);}return null;};
  Run();
 }
 static void Check(bool value,string message){if(!value)throw new Exception(message);}
 [MethodImpl(MethodImplOptions.NoInlining)] static void Run()
 {
  var make=typeof(RuntimeControl).GetMethod("CreateShowcaseRampBuilder",BindingFlags.Static|BindingFlags.NonPublic);
  MatrixD frame=MatrixD.CreateWorld(new Vector3D(1234,5678,91011),Vector3D.Normalize(new Vector3D(1,-1,-2)),Vector3D.Normalize(new Vector3D(1,1,0)));
  var grid=(MyObjectBuilder_CubeGrid)make.Invoke(null,new object[]{frame,123L});
  Check(grid.GridSizeEnum==MyCubeSize.Large&&grid.IsStatic&&grid.CreatePhysics,"native large static physics grid");
  Check(grid.CubeBlocks.Count==4,"exactly four visible native armor blocks");
  Check(Vector3D.Distance(grid.PositionAndOrientation.Value.Position,Vector3D.Transform(new Vector3D(-8.75,238.75,-3.75),frame))<1e-8,"frame maps native cell center correctly");
  foreach(var block in grid.CubeBlocks)
  {
   Check(block.Min.X>=0&&block.Min.X<=1&&block.Min.Y==0&&block.Min.Z>=0&&block.Min.Z<=1,"2x2 arrangement");
   Check(block.SubtypeName==(block.Min.Z==0?"LargeBlockArmorSlope2Base":"LargeBlockArmorSlope2Tip"),"high base north and low tip south");
   Check(block.BlockOrientation.Forward==Base6Directions.Direction.Forward&&block.BlockOrientation.Up==Base6Directions.Direction.Up,"native high face points north");
   Check(block.Owner==123L&&block.IntegrityPercent==1&&block.BuildPercent==1,"native constructed armor owned by player");
  }
  Console.WriteLine("PASS four native large armor cells; anchor, dimensions, slope base/tip order, orientation, integrity and ownership. No native entity created.");
 }
}
