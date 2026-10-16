using System;
using System.IO;
using System.Reflection;
using VRageMath;
namespace SEMC.V2 {
    static class StairCollisionAudit {
        static void Main() {
            AppDomain.CurrentDomain.AssemblyResolve+=delegate(object s,ResolveEventArgs e){string p=Path.Combine(@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;}; Run();
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Run() {
            foreach(string facing in new[]{"north","south","east","west"}) {
                Vector3[] v;if(!StairCollision.TryGetVertices("minecraft:oak_stairs","bottom","straight",facing,out v)||v.Length!=8)throw new Exception("Missing ramp facing");
                float min=1,max=-1;
                for(int i=0;i<4;i++) {
                    if(v[i].Y!=-.5f||Math.Abs(v[i].X)!=.5f||Math.Abs(v[i].Z)!=.5f)throw new Exception("Hull bottom escaped cell bounds");
                    Vector3 top=v[i+4];float expected=.25f+(facing=="north"?-.5f*top.Z:facing=="south"?.5f*top.Z:facing=="east"?.5f*top.X:-.5f*top.X);
                    if(Math.Abs(top.Y-expected)>1e-6||top.X!=v[i].X||top.Z!=v[i].Z)throw new Exception("Native stair facing or convex slope mismatch");
                    min=Math.Min(min,top.Y+.5f);max=Math.Max(max,top.Y+.5f);
                }
                if(min!=.5f||max!=1)throw new Exception("Ramp did not meet slab and full floor levels");
            }
            Vector3[] ignored;
            if(StairCollision.TryGetVertices("minecraft:oak_stairs","top","straight","north",out ignored)||
               StairCollision.TryGetVertices("minecraft:oak_stairs","bottom","outer_left","north",out ignored)||
               StairCollision.TryGetVertices("minecraft:oak_slab","bottom","straight","north",out ignored))throw new Exception("Unsupported state received approximate ramp");
            Console.WriteLine("STAIR CPU AUDIT all4 native facings,8-vertex convex bounds,0.5-to1m slope26.565degrees and unsupported-state exclusions passed; native walking remains live-test dependent");
        }
    }
}
