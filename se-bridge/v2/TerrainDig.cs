using System;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage.Game.ModAPI;
using VRageMath;

namespace SEMC.V2
{
    // Creative terrain excavation only. The caller owns fresh/on-foot/reach checks
    // and invokes this on the game thread. Native voxel storage persists the edit;
    // TerrainBridge's next sweep updates Minecraft's terrain support automatically.
    public static class TerrainDig
    {
        public const double CutEdgeMetres = 1.8;
        public const double SurfaceInsetMetres = .4;

        public static bool CanDig(IHitInfo hit)
        {
            var voxel = hit == null ? null : hit.HitEntity as MyVoxelBase;
            if (voxel == null || voxel.Closed || voxel.MarkedForClose) return false;
            var root = voxel.RootVoxel ?? voxel;
            return !root.Closed && !root.MarkedForClose && root.Storage != null;
        }

        public static bool TryDig(IHitInfo hit, MatrixD frame, Action<string> log)
        {
            if (!CanDig(hit) || MyAPIGateway.Session == null || MyAPIGateway.Session.VoxelMaps == null) return false;
            try
            {
                if (!UnitFrame(frame) || !Finite(hit.Position) || !Finite((Vector3D)hit.Normal)) return false;
                Vector3D normal = hit.Normal;
                if (normal.LengthSquared() < .0001) return false;
                normal.Normalize();
                // SE's one-metre voxel samples form a smooth surface, so a tiny cut
                // snapped to an MC cell can miss the same triangle repeatedly. Centre
                // the cut inside the fresh surface hit; only its orientation follows MC.
                Vector3D cutCentre = hit.Position - normal * SurfaceInsetMetres;
                Vector3D inside = Vector3D.Transform(cutCentre, MatrixD.Invert(frame));
                if (!Finite(inside) || Math.Abs(inside.X) > 30000000 || Math.Abs(inside.Y) > 30000000 || Math.Abs(inside.Z) > 30000000) return false;
                Vector3I cell = Vector3I.Floor(inside);
                MatrixD transform = frame;
                transform.Translation = cutCentre;

                var voxel = (MyVoxelBase)hit.HitEntity;
                var root = voxel.RootVoxel ?? voxel;
                // Installed SE1.210.014 exposes GetBoxVoxelHand, not CreateBox.
                // Its local boundaries plus Transform describe an oriented native cut.
                var maps = MyAPIGateway.Session.VoxelMaps;
                IMyVoxelShapeBox shape = maps.GetBoxVoxelHand();
                if (shape == null) return false;
                double half = CutEdgeMetres * .5;
                shape.Boundaries = new BoundingBoxD(new Vector3D(-half), new Vector3D(half));
                shape.Transform = transform;
                maps.CutOutShape(root, shape);
                if (log != null) log("TERRAIN DIG native cut entity=" + root.EntityId + " hitEntity=" + voxel.EntityId + " frameCell=" + cell + " frameCentre=" + inside + " edge=" + CutEdgeMetres);
                // The native API is void: true means the cut was submitted, not an
                // invented ore yield or an assertion about the resulting surface mesh.
                return true;
            }
            catch (Exception error)
            {
                if (log != null) log("TERRAIN DIG failed: " + error.Message);
                return false;
            }
        }

        static bool Finite(double value) { return !Double.IsNaN(value) && !Double.IsInfinity(value); }
        static bool Finite(Vector3D value) { return Finite(value.X) && Finite(value.Y) && Finite(value.Z); }
        static bool UnitFrame(MatrixD frame)
        {
            Vector3D right = frame.Right, up = frame.Up, backward = frame.Backward;
            return Finite(frame.Translation) && Finite(right) && Finite(up) && Finite(backward) &&
                Math.Abs(right.LengthSquared() - 1) < .001 && Math.Abs(up.LengthSquared() - 1) < .001 && Math.Abs(backward.LengthSquared() - 1) < .001 &&
                Math.Abs(Vector3D.Dot(right, up)) < .001 && Math.Abs(Vector3D.Dot(right, backward)) < .001 && Math.Abs(Vector3D.Dot(up, backward)) < .001;
        }
    }
}
