using System;
using System.Reflection;
using Havok;
using Sandbox.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace SEMC.V2
{
    // SE's character cannot step onto a Minecraft 0.5 m riser. Straight bottom stairs use
    // a native convex walking surface from their lower .5 m edge to their upper 1 m edge.
    // This changes only the collision approximation, never the native Minecraft state/model.
    public static class StairCollision
    {
        static object boundApi;
        static Action<HkShape,PhysicsSettings> create;
        public static bool TryGetVertices(string registryName, string half, string shape, string facing, out Vector3[] centeredVertices)
        {
            centeredVertices = null;
            if (registryName == null || !registryName.EndsWith("_stairs",StringComparison.Ordinal) || half != "bottom" || shape != "straight") return false;
            if (facing != "north" && facing != "south" && facing != "east" && facing != "west") return false;
            var points = new Vector3[8];
            // Havok computes the hull; no hand-authored triangle winding or thin faces are required.
            int[] xs = { 0,1,1,0 }, zs = { 0,0,1,1 };
            for (int i=0;i<4;i++)
            {
                float progress = facing == "north" ? 1-zs[i] : facing == "south" ? zs[i] : facing == "east" ? xs[i] : 1-xs[i];
                points[i] = new Vector3(xs[i]-.5f,-.5f,zs[i]-.5f);
                points[i+4] = new Vector3(xs[i]-.5f,.5f*progress,zs[i]-.5f);
            }
            centeredVertices = points; return true;
        }

        // Settings.WorldMatrix must be centered at the absolute cell minimum + (.5,.5,.5),
        // with LocalCenter=Zero and LocalAABB=(-.5..+.5), as in the normal 1 m collider path.
        // Do NOT also retain the stair's native boxes: their vertical riser would still block walking.
        public static void CreatePhysics(PhysicsSettings settings, Vector3[] centeredVertices)
        {
            if (centeredVertices == null || centeredVertices.Length != 8) throw new ArgumentException("A stair hull requires eight centered vertices.");
            for (int i=0;i<centeredVertices.Length;i++)
            {
                Vector3 p=centeredVertices[i];
                if (Single.IsNaN(p.X)||Single.IsNaN(p.Y)||Single.IsNaN(p.Z)||Single.IsInfinity(p.X)||Single.IsInfinity(p.Y)||Single.IsInfinity(p.Z))
                    throw new ArgumentException("A stair hull cannot contain non-finite vertices.");
            }
            object api = MyAPIGateway.Physics;
            if (api == null) throw new InvalidOperationException("The installed native SE physics API is unavailable.");
            if (!ReferenceEquals(api,boundApi))
            {
                // The concrete ModAPI class is internal, although this method is public.
                MethodInfo method=api.GetType().GetMethod("CreatePhysics",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,
                    null,new[]{typeof(HkShape),typeof(PhysicsSettings)},null);
                if (method==null) throw new MissingMethodException("Native CreatePhysics(HkShape, PhysicsSettings) is unavailable.");
                create=(Action<HkShape,PhysicsSettings>)Delegate.CreateDelegate(typeof(Action<HkShape,PhysicsSettings>),api,method);
                boundApi=api;
            }
            // Verified installed SE1.210 API: CreatePhysics is the same implementation called by
            // CreateBoxPhysics. It releases the input shape reference after the body owns it.
            // Calling RemoveReference here a second time would corrupt that ownership.
            HkConvexVerticesShape hull = new HkConvexVerticesShape(centeredVertices,centeredVertices.Length,true,.01f);
            create(hull.Base,settings);
        }
    }
}
