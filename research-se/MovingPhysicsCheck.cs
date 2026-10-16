using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using SEMC.V2;
using VRageMath;
class MovingPhysicsCheck
{
    static readonly string root = @"C:\Users\konra\Documents\SEMC-Lab";
    static void Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
        {
            string name = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in new[] { Path.Combine(root,"research-se"), @"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64", Path.Combine(root,@"tools\Pulsar\Libraries\Legacy") })
            { string file = Path.Combine(dir,name); if (File.Exists(file)) return Assembly.LoadFrom(file); }
            return null;
        };
        Run();
    }
    static void Check(bool value,string message) { if (!value) throw new Exception(message); }
    [MethodImpl(MethodImplOptions.NoInlining)] static void Run()
    {
        MethodInfo parse = typeof(MovingBlockPhysics).GetMethod("ReadSamples",BindingFlags.NonPublic|BindingFlags.Static);
        JArray trace = JArray.Parse(File.ReadAllText(Path.Combine(root,@"research-mc\piston-live-trace-v2.json")));
        int snapshots=0,bodies=0,moving=0,stationary=0;
        JObject example=null;
        foreach (JObject doc in trace)
        {
            IList samples=(IList)parse.Invoke(null,new object[]{doc}); snapshots++;
            foreach(object sample in samples)
            {
                Type t=sample.GetType(); Vector3D center=(Vector3D)t.GetField("Center").GetValue(sample), previous=(Vector3D)t.GetField("Previous").GetValue(sample);
                double distance=Vector3D.Distance(center,previous);
                Check(distance<=1.5,"native motion bound");
                if(distance>0.0001)moving++;else stationary++;
                bodies++;
            }
            if(samples.Count>0)example=doc;
        }
        Check(moving>0&&stationary>0,"recorded native moving parts and stationary base must both appear");
        JObject fallback=(JObject)example.DeepClone(); foreach(JObject piston in (JArray)fallback["movingBlocks"])piston["collisionPartsVerified"]=false;
        foreach(object sample in (IList)parse.Invoke(null,new object[]{fallback}))
        {Type t=sample.GetType();Check((Vector3D)t.GetField("Center").GetValue(sample)==(Vector3D)t.GetField("Previous").GetValue(sample),"unverified geometry must not infer motion");}
        JObject invalid=(JObject)example.DeepClone(); ((JArray)invalid["movingBlocks"])[0]["collisionParts"][0]["collisionBoxes"][0][3]=double.PositiveInfinity;
        bool rejected=false;try{parse.Invoke(null,new object[]{invalid});}catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}Check(rejected,"nonfinite collision must reject before mutation");
        var physics=new PhysicsWorld(8,8,8,Console.WriteLine);
        MethodInfo changed=typeof(PhysicsWorld).GetMethod("CollisionChanged",BindingFlags.Instance|BindingFlags.NonPublic);
        physics.SetDynamicCollisionStates(new[]{2});
        Check((bool)changed.Invoke(physics,new object[]{1,2,7,7}),"static full cube to moving full cube must rebuild");
        Check((bool)changed.Invoke(physics,new object[]{2,1,7,7}),"moving to static must restore collision");
        Check(!(bool)changed.Invoke(physics,new object[]{1,3,7,7}),"ordinary full cube material changes need no physics rebuild");
        Vector3[] vertices;Check(StairCollision.TryGetVertices("minecraft:oak_stairs","bottom","straight","north",out vertices),"stair metadata accepted");
        var stairs=new System.Collections.Generic.Dictionary<int,Vector3[]>();stairs[4]=vertices;physics.SetStairCollisionStates(stairs);vertices[0]=new Vector3(99);
        Check((bool)changed.Invoke(physics,new object[]{4,1,7,7}),"stair state transitions rebuild");
        rejected=false;try{physics.SetStairCollisionStates(stairs);}catch(ArgumentException){rejected=true;}Check(rejected,"invalid hull must reject");physics.Dispose();
        Console.WriteLine("PASS native trace snapshots="+snapshots+" boxSamples="+bodies+" moving="+moving+" stationary="+stationary+"; fallback, bounds, dynamic transitions, stair validation. No native entities created.");
    }
}
