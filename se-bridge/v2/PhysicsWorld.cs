using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Havok;
using Sandbox.Engine.Physics;
using Sandbox.Game.Entities;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SEMC.V2
{
    public struct BlockHit
    {
        public Vector3I Cell;
        public Vector3I FaceNormal;
        public Vector3 LocalPoint;
        public Vector3D WorldPoint;
        public double Distance;
    }

    // Main-thread owned except the contact callback, which only copies data into a bounded queue.
    // SE owns these colliders; Minecraft remains authoritative for every cell removal.
    public sealed class PhysicsWorld : IDisposable
    {
        const int ChunkSize = 8;
        const string ContactDebugFlag = @"C:\Users\konra\Documents\SEMC-Lab\contact-debug-v2.flag";
        readonly int sx, sy, sz;
        readonly int[] states, flags;
        readonly Action<string> log;
        readonly Dictionary<Vector3I, List<Collider>> chunks = new Dictionary<Vector3I, List<Collider>>();
        readonly Dictionary<Vector3I, CachedChunk> retained = new Dictionary<Vector3I, CachedChunk>();
        HashSet<int> dynamicCollisionStates = new HashSet<int>();
        IDictionary<int, Vector3[]> stairCollisionStates = new Dictionary<int, Vector3[]>();
        readonly HashSet<Vector3I> retryChunks = new HashSet<Vector3I>();
        readonly HashSet<long> entityIds = new HashSet<long>();
        readonly ConcurrentQueue<Impact> impacts = new ConcurrentQueue<Impact>();
        readonly ConcurrentQueue<string> contactDiagnostics = new ConcurrentQueue<string>();
        readonly Dictionary<Vector3I, long> lastDamage = new Dictionary<Vector3I, long>();
        readonly HashSet<IMyEntity> nearby = new HashSet<IMyEntity>();
        IDictionary<int, BoundingBox[]> shapes;
        MatrixD frame = MatrixD.Identity, inverse = MatrixD.Identity;
        MatrixD absoluteFrame = MatrixD.Identity, absoluteInverse = MatrixD.Identity;
        Vector3I origin;
        BoundingBoxD retainedBounds;
        bool hasRetainedBounds;
        bool initialized;
        int generation, queued;
        long suppressUntil, nextToolScan, nextContactDiagnostic, nextDebugFlagCheck, nextRetry, nextRebuildError;
        volatile bool contactDiagnosticsEnabled;
        public int ColliderCount { get { return entityIds.Count; } }
        public int RetainedChunkCount { get { return retained.Count; } }
        public int CachedCellCount { get; private set; }
        public void SuppressDamage(int milliseconds)
        {
            if (milliseconds > 0) suppressUntil = Math.Max(suppressUntil, Now() + milliseconds);
        }
        public MatrixD Frame { get { return frame; } }
        public Vector3I Dimensions { get { return new Vector3I(sx, sy, sz); } }

        public void SetDynamicCollisionStates(IEnumerable<int> stateIds)
        {
            var next = stateIds == null ? new HashSet<int>() : new HashSet<int>(stateIds);
            if (dynamicCollisionStates.SetEquals(next)) return;
            var affected = new HashSet<int>(dynamicCollisionStates); affected.UnionWith(next);
            dynamicCollisionStates = next;
            foreach (var pair in retained)
            {
                bool rebuild = false;
                for (int i = 0; i < 512 && !rebuild; i++) rebuild = (pair.Value.Flags[i] & 1) != 0 && affected.Contains(pair.Value.States[i]);
                if (rebuild) Rebuild(pair.Key);
            }
        }

        public void SetStairCollisionStates(IDictionary<int, Vector3[]> verticesByState)
        {
            var next = new Dictionary<int, Vector3[]>();
            if (verticesByState != null) foreach (var pair in verticesByState)
            {
                if (pair.Value == null || pair.Value.Length != 8) throw new ArgumentException("A stair state requires eight centered vertices.");
                Vector3[] copy = (Vector3[])pair.Value.Clone();
                foreach (Vector3 point in copy)
                    if (Single.IsNaN(point.X) || Single.IsNaN(point.Y) || Single.IsNaN(point.Z) ||
                        Math.Abs(point.X) > .501f || Math.Abs(point.Y) > .501f || Math.Abs(point.Z) > .501f)
                        throw new ArgumentException("A stair vertex lies outside its cell.");
                next.Add(pair.Key, copy);
            }
            var affected = new HashSet<int>();
            foreach (var pair in next)
            {
                Vector3[] old;
                if (!stairCollisionStates.TryGetValue(pair.Key, out old)) { affected.Add(pair.Key); continue; }
                for (int i = 0; i < 8; i++) if (old[i] != pair.Value[i]) { affected.Add(pair.Key); break; }
            }
            foreach (int id in stairCollisionStates.Keys) if (!next.ContainsKey(id)) affected.Add(id);
            stairCollisionStates = next;
            if (affected.Count == 0) return;
            foreach (var pair in retained)
            {
                bool rebuild = false;
                for (int i = 0; i < 512 && !rebuild; i++) rebuild = (pair.Value.Flags[i] & 1) != 0 && affected.Contains(pair.Value.States[i]);
                if (rebuild) Rebuild(pair.Key);
            }
        }

        bool CollisionChanged(int oldState, int newState, int oldFlags, int newFlags)
        {
            return (oldFlags & 6) != (newFlags & 6) ||
                (oldState != newState && (((newFlags & 4) != 0 && (newFlags & 2) == 0) ||
                dynamicCollisionStates.Contains(oldState) != dynamicCollisionStates.Contains(newState) ||
                stairCollisionStates.ContainsKey(oldState) || stairCollisionStates.ContainsKey(newState)));
        }

        sealed class Collider
        {
            public DamageEntity Entity;
            public MyPhysicsBody Body;
            public MyPhysicsBody.PhysicsContactHandler Handler;
            public long IgnoreContactsUntil;
        }
        sealed class CachedChunk
        {
            public readonly int[] States = new int[ChunkSize * ChunkSize * ChunkSize];
            public readonly int[] Flags = new int[ChunkSize * ChunkSize * ChunkSize];
            public int NonAir;
        }
        struct Impact
        {
            public Vector3D Position;
            public Vector3 Normal;
            public int Generation;
            public string Reason;
        }
        sealed class DamageEntity : MyEntity, IMyDestroyableObject
        {
            public Action<Vector3D, Vector3, string> Damage;
            public float Integrity { get { return 100f; } }
            public bool UseDamageSystem { get { return false; } }
            public void OnDestroy() { if (Damage != null) Damage(PositionComp.GetPosition(), Vector3.Zero, "destroy"); }
            public bool DoDamage(float amount, MyStringHash type, bool sync, MyHitInfo? hitInfo = null,
                long attackerId = 0, long realHitEntityId = 0, bool shouldDetonateAmmo = true, MyStringHash? extraInfo = null)
            {
                if (amount <= 0 || Damage == null) return false;
                Damage(hitInfo.HasValue ? hitInfo.Value.Position : PositionComp.GetPosition(),
                    hitInfo.HasValue ? hitInfo.Value.Normal : Vector3.Zero, "damage:" + type);
                return true;
            }
        }

        public PhysicsWorld(int sizeX, int sizeY, int sizeZ, Action<string> logger)
        {
            if (sizeX <= 0 || sizeY <= 0 || sizeZ <= 0 || (long)sizeX * sizeY * sizeZ > 262144)
                throw new ArgumentOutOfRangeException("sizeX");
            sx = sizeX; sy = sizeY; sz = sizeZ;
            states = new int[sx * sy * sz]; flags = new int[states.Length];
            log = logger ?? delegate { };
        }

        public void Update(int[] newStates, int[] newFlags, IDictionary<int, BoundingBox[]> collisionShapes, MatrixD worldFrame)
        { Update(newStates, newFlags, collisionShapes, worldFrame, Vector3I.Zero); }

        // The shared store verifies world/palette/frame identity before handing these records to us.
        // Restore on the game thread before the first capture, so parked native grids retain support.
        public void RestoreRetained(IEnumerable<CachedTerrainChunk> records, IDictionary<int, BoundingBox[]> collisionShapes, MatrixD absoluteWorldFrame)
        {
            if (records == null) throw new ArgumentNullException("records");
            var restored = new Dictionary<Vector3I, CachedChunk>();
            int totalCells = 0;
            foreach (CachedTerrainChunk record in records)
            {
                if (record == null || record.States == null || record.Flags == null ||
                    record.States.Length != 512 || record.Flags.Length != 512 ||
                    (record.Minimum.X & 7) != 0 || (record.Minimum.Y & 7) != 0 || (record.Minimum.Z & 7) != 0)
                    throw new ArgumentException("Invalid retained physics chunk.");
                Vector3I key = ChunkKey(record.Minimum);
                if (restored.ContainsKey(key)) throw new ArgumentException("Duplicate retained physics chunk.");
                var cached = new CachedChunk();
                for (int i = 0; i < 512; i++)
                {
                    if ((record.Flags[i] & 1) == 0) continue;
                    cached.States[i] = record.States[i]; cached.Flags[i] = record.Flags[i]; cached.NonAir++;
                }
                if (cached.NonAir == 0) continue;
                restored.Add(key, cached); totalCells += cached.NonAir;
            }
            Clear(); shapes = collisionShapes;
            absoluteFrame = frame = absoluteWorldFrame;
            absoluteInverse = inverse = MatrixD.Invert(absoluteWorldFrame);
            origin = Vector3I.Zero;
            foreach (var pair in restored) retained.Add(pair.Key, pair.Value);
            CachedCellCount = totalCells; initialized = true;
            RefreshRetainedBounds();
            foreach (Vector3I key in retained.Keys) Rebuild(key);
            log("PHYSICS RESTORE retained=" + RetainedChunkCount + " cachedCells=" + CachedCellCount + " colliders=" + ColliderCount);
        }

        public void ApplyRemoteChunk(CachedTerrainChunk record, IDictionary<int, BoundingBox[]> collisionShapes, MatrixD absoluteWorldFrame)
        { ApplyRemoteChunks(new[] { record }, collisionShapes, absoluteWorldFrame); }

        // Full authoritative chunk data: state -1 or negative flags preserve the previously observed cell.
        // Explicit air replaces only its cell; other distant chunks and the active camera frame stay intact.
        public void ApplyRemoteChunks(IEnumerable<CachedTerrainChunk> records, IDictionary<int, BoundingBox[]> collisionShapes, MatrixD absoluteWorldFrame)
        {
            if (records == null) throw new ArgumentNullException("records");
            var valid = new List<CachedTerrainChunk>();
            foreach (CachedTerrainChunk record in records)
            {
                if (record == null || record.States == null || record.Flags == null || record.States.Length != 512 || record.Flags.Length != 512 ||
                    (record.Minimum.X & 7) != 0 || (record.Minimum.Y & 7) != 0 || (record.Minimum.Z & 7) != 0)
                    throw new ArgumentException("Invalid remote physics chunk.");
                for (int i = 0; i < 512; i++) if (record.States[i] < -1) throw new ArgumentException("Invalid remote physics state.");
                valid.Add(record);
            }
            if (initialized && !SameFrame(absoluteFrame, absoluteWorldFrame))
                throw new InvalidOperationException("Remote physics chunks belong to a different world frame.");
            if (!initialized)
            {
                absoluteFrame = frame = absoluteWorldFrame; absoluteInverse = inverse = MatrixD.Invert(absoluteWorldFrame);
                origin = Vector3I.Zero; initialized = true;
            }
            bool paletteChanged = !Object.ReferenceEquals(shapes, collisionShapes);
            shapes = collisionShapes;
            var dirty = new HashSet<Vector3I>();
            foreach (CachedTerrainChunk record in valid)
            {
                Vector3I key = ChunkKey(record.Minimum);
                CachedChunk cached; retained.TryGetValue(key, out cached);
                for (int y = 0; y < 8; y++) for (int z = 0; z < 8; z++) for (int x = 0; x < 8; x++)
                {
                    int i = x + 8 * (z + 8 * y), newState = record.States[i], newFlags = record.Flags[i];
                    if (newState == -1 || newFlags < 0) continue;
                    if (newState == 0 || (newFlags & 1) == 0) newState = newFlags = 0;
                    Vector3I local = record.Minimum + new Vector3I(x, y, z) - origin;
                    if (InBounds(local)) { int active = Index(local.X, local.Y, local.Z); states[active] = newState; flags[active] = newFlags; }
                    if (cached == null)
                    {
                        if (newFlags == 0) continue;
                        cached = new CachedChunk(); retained.Add(key, cached);
                    }
                    int oldFlags = cached.Flags[i], oldState = cached.States[i];
                    if (CollisionChanged(oldState, newState, oldFlags, newFlags)) dirty.Add(key);
                    int delta = ((newFlags & 1) != 0 ? 1 : 0) - ((oldFlags & 1) != 0 ? 1 : 0);
                    cached.NonAir += delta; CachedCellCount += delta;
                    cached.States[i] = newState; cached.Flags[i] = newFlags;
                }
                if (cached != null && cached.NonAir == 0) { retained.Remove(key); dirty.Add(key); }
            }
            if (paletteChanged) foreach (Vector3I key in retained.Keys) dirty.Add(key);
            RefreshRetainedBounds();
            foreach (Vector3I key in dirty) Rebuild(key);
        }

        public void Update(int[] newStates, int[] newFlags, IDictionary<int, BoundingBox[]> collisionShapes, MatrixD worldFrame, Vector3I minecraftOrigin)
        {
            if (newStates == null || newFlags == null || newStates.Length != states.Length || newFlags.Length != flags.Length)
                throw new ArgumentException("Physics snapshot dimensions do not match");
            MatrixD candidateAbsolute = worldFrame;
            candidateAbsolute.Translation -= Vector3D.TransformNormal((Vector3D)minecraftOrigin, worldFrame);
            bool frameChanged = !initialized || !SameFrame(absoluteFrame, candidateAbsolute);
            bool paletteChanged = !Object.ReferenceEquals(shapes, collisionShapes);
            shapes = collisionShapes;
            if (frameChanged)
            {
                Clear(); absoluteFrame = candidateAbsolute; absoluteInverse = MatrixD.Invert(absoluteFrame);
            }
            frame = worldFrame; inverse = MatrixD.Invert(frame); origin = minecraftOrigin;
            HashSet<Vector3I> dirty = new HashSet<Vector3I>();
            HashSet<Vector3I> touched = new HashSet<Vector3I>();
            for (int y = 0; y < sy; y++) for (int z = 0; z < sz; z++) for (int x = 0; x < sx; x++)
            {
                int i = Index(x, y, z);
                states[i] = newStates[i]; flags[i] = newFlags[i];
                Vector3I absoluteCell = minecraftOrigin + new Vector3I(x, y, z), key = ChunkKey(absoluteCell);
                CachedChunk cached;
                if (!retained.TryGetValue(key, out cached))
                {
                    if ((newFlags[i] & 1) == 0) continue;
                    cached = new CachedChunk(); retained.Add(key, cached);
                }
                touched.Add(key);
                int ci = ChunkIndex(absoluteCell - key * ChunkSize);
                int oldFlags = cached.Flags[ci], oldState = cached.States[ci];
                bool shapeChanged = CollisionChanged(oldState, newStates[i], oldFlags, newFlags[i]);
                if (shapeChanged || paletteChanged) dirty.Add(key);
                int delta = ((newFlags[i] & 1) != 0 ? 1 : 0) - ((oldFlags & 1) != 0 ? 1 : 0);
                cached.NonAir += delta; CachedCellCount += delta;
                cached.States[ci] = newStates[i]; cached.Flags[ci] = newFlags[i];
            }
            // A partially covered chunk keeps all previously observed cells outside this snapshot.
            foreach (Vector3I key in touched) if (retained[key].NonAir == 0) { retained.Remove(key); dirty.Add(key); }
            if (paletteChanged) foreach (Vector3I key in retained.Keys) dirty.Add(key);
            initialized = true;
            RefreshRetainedBounds();
            if (dirty.Count == 0) return;
            foreach (Vector3I key in dirty) Rebuild(key);
            log("PHYSICS chunks=" + dirty.Count + " colliders=" + ColliderCount + " retained=" + RetainedChunkCount + " cachedCells=" + CachedCellCount);
        }

        static bool SameFrame(MatrixD a, MatrixD b)
        {
            return Vector3D.DistanceSquared(a.Translation, b.Translation) < 1e-8 &&
                Vector3D.DistanceSquared(a.Right, b.Right) < 1e-16 && Vector3D.DistanceSquared(a.Up, b.Up) < 1e-16 &&
                Vector3D.DistanceSquared(a.Backward, b.Backward) < 1e-16;
        }
        static Vector3I ChunkKey(Vector3I cell) { return new Vector3I(cell.X >> 3, cell.Y >> 3, cell.Z >> 3); }
        static int ChunkIndex(Vector3I cell) { return cell.X + ChunkSize * (cell.Z + ChunkSize * cell.Y); }
        public bool IsOccupiedAbsolute(Vector3I cell)
        {
            Vector3I key = ChunkKey(cell); CachedChunk cached;
            return retained.TryGetValue(key, out cached) && (cached.Flags[ChunkIndex(cell - key * ChunkSize)] & 1) != 0;
        }
        public void ApplyConfirmedRemoval(Vector3I absoluteCell)
        {
            Vector3I key = ChunkKey(absoluteCell); CachedChunk cached;
            if (!retained.TryGetValue(key, out cached)) return;
            int ci = ChunkIndex(absoluteCell - key * ChunkSize);
            if ((cached.Flags[ci] & 1) == 0) return;
            cached.States[ci] = cached.Flags[ci] = 0; cached.NonAir--; CachedCellCount--;
            Vector3I local = absoluteCell - origin;
            if (InBounds(local)) { int i = Index(local.X, local.Y, local.Z); states[i] = flags[i] = 0; }
            if (cached.NonAir == 0) retained.Remove(key);
            Rebuild(key); RefreshRetainedBounds();
        }
        void RefreshRetainedBounds()
        {
            hasRetainedBounds = false;
            foreach (Vector3I key in retained.Keys)
            {
                Vector3D min = key * ChunkSize, max = min + new Vector3D(ChunkSize);
                if (!hasRetainedBounds) { retainedBounds = new BoundingBoxD(min, max); hasRetainedBounds = true; }
                else { retainedBounds.Min = Vector3D.Min(retainedBounds.Min, min); retainedBounds.Max = Vector3D.Max(retainedBounds.Max, max); }
            }
        }

        public bool Owns(IMyEntity entity) { return entity != null && entityIds.Contains(entity.EntityId); }
        public bool InBounds(Vector3I cell) { return cell.X >= 0 && cell.X < sx && cell.Y >= 0 && cell.Y < sy && cell.Z >= 0 && cell.Z < sz; }
        public bool IsOccupied(Vector3I cell) { return InBounds(cell) && (flags[Index(cell.X, cell.Y, cell.Z)] & 1) != 0; }
        public Vector3D WorldPoint(Vector3D local) { return Vector3D.Transform(local, frame); }
        public Vector3D LocalPoint(Vector3D world) { return Vector3D.Transform(world, inverse); }
        public Vector3D LocalDirection(Vector3D worldDirection) { return Vector3D.TransformNormal(worldDirection, inverse); }
        int Index(int x, int y, int z) { return x + sx * (z + sz * y); }

        void Rebuild(Vector3I key)
        {
            List<Collider> old;
            chunks.TryGetValue(key, out old);
            CachedChunk cached;
            if (!retained.TryGetValue(key, out cached))
            {
                if (old != null) foreach (Collider collider in old) Close(collider);
                chunks.Remove(key); retryChunks.Remove(key); return;
            }
            List<Collider> created = new List<Collider>();
            try
            {
            Vector3I min = key * ChunkSize;
            Vector3I max = min + new Vector3I(ChunkSize);
            bool[] consumed = new bool[ChunkSize * ChunkSize * ChunkSize];
            Func<int, int, int, bool> full = delegate(int x, int y, int z)
            {
                return x < max.X && y < max.Y && z < max.Z && !consumed[(x - min.X) + ChunkSize * ((z - min.Z) + ChunkSize * (y - min.Y))]
                    && (cached.Flags[ChunkIndex(new Vector3I(x, y, z) - min)] & 2) != 0
                    && !dynamicCollisionStates.Contains(cached.States[ChunkIndex(new Vector3I(x, y, z) - min)])
                    && !stairCollisionStates.ContainsKey(cached.States[ChunkIndex(new Vector3I(x, y, z) - min)]);
            };
            for (int y = min.Y; y < max.Y; y++) for (int z = min.Z; z < max.Z; z++) for (int x = min.X; x < max.X; x++)
            {
                int i = ChunkIndex(new Vector3I(x, y, z) - min);
                if (full(x, y, z))
                {
                    int ex = x + 1, ez = z + 1, ey = y + 1;
                    while (full(ex, y, z)) ex++;
                    bool extend = true;
                    while (extend && ez < max.Z)
                    {
                        for (int xx = x; xx < ex; xx++) if (!full(xx, y, ez)) { extend = false; break; }
                        if (extend) ez++;
                    }
                    extend = true;
                    while (extend && ey < max.Y)
                    {
                        for (int zz = z; zz < ez && extend; zz++) for (int xx = x; xx < ex; xx++)
                            if (!full(xx, ey, zz)) { extend = false; break; }
                        if (extend) ey++;
                    }
                    for (int yy = y; yy < ey; yy++) for (int zz = z; zz < ez; zz++) for (int xx = x; xx < ex; xx++)
                        consumed[(xx - min.X) + ChunkSize * ((zz - min.Z) + ChunkSize * (yy - min.Y))] = true;
                    created.Add(CreateCollider(new Vector3D(x, y, z), new Vector3D(ex, ey, ez)));
                }
                else if ((cached.Flags[i] & 4) != 0 && (cached.Flags[i] & 2) == 0 && !dynamicCollisionStates.Contains(cached.States[i]))
                {
                    Vector3[] stairVertices;
                    if (stairCollisionStates.TryGetValue(cached.States[i], out stairVertices))
                    {
                        created.Add(CreateCollider(new Vector3D(x, y, z), new Vector3D(x + 1, y + 1, z + 1), stairVertices));
                        continue;
                    }
                    BoundingBox[] boxes;
                    if (shapes == null || !shapes.TryGetValue(cached.States[i], out boxes)) boxes = new[] { new BoundingBox(Vector3.Zero, Vector3.One) };
                    foreach (BoundingBox box in boxes)
                    {
                        Vector3D bmin = new Vector3D(x, y, z) + box.Min;
                        Vector3D bmax = new Vector3D(x, y, z) + box.Max;
                        if (bmax.X > bmin.X && bmax.Y > bmin.Y && bmax.Z > bmin.Z)
                            created.Add(CreateCollider(bmin, bmax));
                    }
                }
            }
            }
            catch (Exception error)
            {
                foreach (Collider collider in created) Close(collider);
                retryChunks.Add(key);
                if (Now() >= nextRebuildError)
                { nextRebuildError = Now() + 3000; log("PHYSICS REBUILD retained old collision; retry " + key + ": " + error.GetType().Name + " " + error.Message); }
                return;
            }
            // Publish the replacement only after all native shapes exist. A failed rebuild never drops old support.
            chunks[key] = created; retryChunks.Remove(key);
            if (old != null) foreach (Collider collider in old) Close(collider);
        }

        Collider CreateCollider(Vector3D min, Vector3D max, Vector3[] stairVertices = null)
        {
            Vector3D center = (min + max) * 0.5;
            Vector3 half = (Vector3)((max - min) * 0.5);
            MatrixD matrix = absoluteFrame; matrix.Translation = Vector3D.Transform(center, absoluteFrame);
            DamageEntity entity = new DamageEntity();
            try
            {
            entity.Init(new StringBuilder("Minecraft collision"), null, null, null, null);
            entity.Save = false;
            entity.Render.Visible = false;
            ((IMyEntity)entity).LocalAABB = new BoundingBox(-half, half);
            ((IMyEntity)entity).SetWorldMatrix(matrix, null);
            PhysicsSettings settings = MyAPIGateway.Physics.CreateSettingsForPhysics(entity, matrix, Vector3.Zero,
                0f, 0f, 15, RigidBodyFlag.RBF_STATIC, false, null);
            settings.MaterialType = MyStringHash.GetOrCompute("Rock");
            if (stairVertices == null) MyAPIGateway.Physics.CreateBoxPhysics(settings, half, 0.01f);
            else StairCollision.CreatePhysics(settings, stairVertices);
            MyPhysicsBody body = entity.Physics as MyPhysicsBody;
            if (body == null || body.RigidBody == null) throw new InvalidOperationException("Native collision body was not created.");
            Collider collider = new Collider { Entity = entity, Body = body, IgnoreContactsUntil = Now() + 250 };
            int colliderGeneration = generation;
            entity.Damage = delegate(Vector3D position, Vector3 normal, string reason) { QueueImpact(position, normal, colliderGeneration, reason); };
            if (body != null)
            {
                collider.Handler = delegate(ref MyPhysics.MyContactPointEvent contact)
                {
                    try
                    {
                        HkRigidBody otherBody = contact.ContactPointEvent.Base.BodyA == body.RigidBody
                            ? contact.ContactPointEvent.Base.BodyB : contact.ContactPointEvent.Base.BodyA;
                        MyPhysicsBody other = otherBody == null ? null : otherBody.UserObject as MyPhysicsBody;
                        long now = Now(), oldDiagnostic = Interlocked.Read(ref nextContactDiagnostic);
                        if (contactDiagnosticsEnabled && now >= oldDiagnostic && Interlocked.CompareExchange(ref nextContactDiagnostic, now + 1000, oldDiagnostic) == oldDiagnostic)
                            contactDiagnostics.Enqueue("CONTACT speed=" + contact.ContactPointEvent.SeparatingVelocity +
                                " type=" + contact.ContactPointEvent.EventType + " other=" +
                                (other == null ? (otherBody == null || otherBody.UserObject == null ? "null" : otherBody.UserObject.GetType().Name) : other.Entity == null ? "no entity" : other.Entity.GetType().Name));
                        if (other == null || !(other.Entity is IMyCubeGrid)) return;
                        float closingSpeed = Math.Abs(contact.ContactPointEvent.SeparatingVelocity);
                        // Some manifold callbacks arrive after the solver has removed the approach velocity.
                        closingSpeed = Math.Max(closingSpeed, Math.Abs(Vector3.Dot(other.LastLinearVelocity, contact.Normal)));
                        if (now < suppressUntil || now < collider.IgnoreContactsUntil || closingSpeed < 5f) return;
                        QueueImpact(contact.Position, contact.Normal, colliderGeneration, "vehicle impact");
                    }
                    catch { }
                };
                body.ContactPointDelay = 0;
                body.ReportAllContacts = true;
                body.ContactPointCallback += collider.Handler;
                // Subscribing the managed delegate does not enable Havok's native listener.
                if (body.RigidBody != null)
                { body.RigidBody.ContactPointCallbackEnabled = true; body.RigidBody.ContactPointCallbackDelay = 0; }
            }
            MyAPIGateway.Entities.AddEntity(entity, true);
            if (entity.Physics != null) entity.Physics.Enabled = true;
            entityIds.Add(entity.EntityId);
            return collider;
            }
            catch { entity.Damage = null; if (!entity.Closed && !entity.MarkedForClose) entity.Close(); throw; }
        }

        void QueueImpact(Vector3D position, Vector3 normal, int version, string reason)
        {
            if (Interlocked.Increment(ref queued) > 512) { Interlocked.Decrement(ref queued); return; }
            impacts.Enqueue(new Impact { Position = position, Normal = normal, Generation = version, Reason = reason });
        }

        public void DrainDamage(Action<Vector3I, string> requestBreak)
        {
            if (requestBreak == null) return;
            DrainDamageAbsolute(delegate(Vector3I cell, string reason) { requestBreak(cell - origin, reason); });
        }

        public void DrainDamageAbsolute(Action<Vector3I, string> requestBreak)
        {
            if (requestBreak == null) return;
            long now = Now();
            if (retryChunks.Count > 0 && now >= nextRetry)
            {
                nextRetry = now + 500;
                foreach (Vector3I key in new List<Vector3I>(retryChunks)) Rebuild(key);
            }
            if (now >= nextDebugFlagCheck)
            {
                nextDebugFlagCheck = now + 1000;
                contactDiagnosticsEnabled = System.IO.File.Exists(ContactDebugFlag);
            }
            string diagnostic;
            int diagnosticCount = 0;
            while (diagnosticCount++ < 8 && contactDiagnostics.TryDequeue(out diagnostic)) if (contactDiagnosticsEnabled) log(diagnostic);
            Impact impact;
            int handled = 0;
            while (handled++ < 64 && impacts.TryDequeue(out impact))
            {
                Interlocked.Decrement(ref queued);
                if (impact.Generation != generation || now < suppressUntil) continue;
                Vector3I a = Vector3I.Floor(Vector3D.Transform(impact.Position - (Vector3D)impact.Normal * 0.05, absoluteInverse));
                Vector3I b = Vector3I.Floor(Vector3D.Transform(impact.Position + (Vector3D)impact.Normal * 0.05, absoluteInverse));
                Vector3I cell = IsOccupiedAbsolute(a) ? a : b;
                RequestOnce(cell, impact.Reason, now, requestBreak);
            }
            if (now < nextToolScan || !initialized || !hasRetainedBounds) return;
            nextToolScan = now + 250;
            BoundingBoxD bounds = retainedBounds.TransformFast(absoluteFrame);
            bounds.Inflate(5.0);
            nearby.Clear();
            MyAPIGateway.Entities.GetEntities(nearby, delegate(IMyEntity entity) { return entity is IMyCubeGrid && entity.WorldAABB.Intersects(bounds); });
            foreach (IMyEntity entity in nearby)
            {
                IMyCubeGrid grid = (IMyCubeGrid)entity;
                foreach (IMyShipDrill apiDrill in grid.GetFatBlocks<IMyShipDrill>())
                {
                    MyShipDrill drill = apiDrill as MyShipDrill;
                    if (drill == null || !drill.IsWorking || (!drill.Enabled && !drill.IsShooting) || drill.DrillBase == null) continue;
                    BoundingSphereD sphere = drill.DrillBase.CutOut.Sphere;
                    Vector3D local = Vector3D.Transform(sphere.Center, absoluteInverse);
                    Vector3I from = Vector3I.Floor(local - sphere.Radius);
                    Vector3I to = Vector3I.Floor(local + sphere.Radius);
                    for (int y = from.Y; y <= to.Y; y++) for (int z = from.Z; z <= to.Z; z++) for (int x = from.X; x <= to.X; x++)
                    {
                        Vector3I cell = new Vector3I(x, y, z);
                        if (!IsOccupiedAbsolute(cell)) continue;
                        BoundingBoxD box = new BoundingBoxD(cell, (Vector3D)cell + Vector3D.One);
                        if (box.Intersects(new BoundingSphereD(local, sphere.Radius))) RequestOnce(cell, "ship drill", now, requestBreak);
                    }
                }
            }
        }

        void RequestOnce(Vector3I cell, string reason, long now, Action<Vector3I, string> requestBreak)
        {
            if (!IsOccupiedAbsolute(cell)) return;
            long previous;
            if (lastDamage.TryGetValue(cell, out previous) && now - previous < 750) return;
            lastDamage[cell] = now;
            log("PHYSICS BREAK " + reason + " cell=" + cell.X + "," + cell.Y + "," + cell.Z);
            requestBreak(cell, reason);
        }

        public bool TryRaycast(Vector3D worldFrom, Vector3D worldDirection, double range, out BlockHit result)
        {
            result = new BlockHit();
            if (!initialized) return false;
            Vector3D origin = LocalPoint(worldFrom), direction = LocalDirection(worldDirection);
            direction.Normalize();
            double entry, exit; Vector3I ignored;
            if (!RayBox(origin, direction, Vector3D.Zero, new Vector3D(sx, sy, sz), out entry, out exit, out ignored) || exit < 0 || entry > range) return false;
            double distance = Math.Max(0, entry) + 0.00001;
            Vector3I cell = Vector3I.Floor(origin + direction * distance);
            for (int step = 0; step < sx + sy + sz + 4 && InBounds(cell) && distance <= range; step++)
            {
                int index = Index(cell.X, cell.Y, cell.Z);
                if ((flags[index] & 1) != 0)
                {
                    BoundingBox[] boxes;
                    if (shapes == null || !shapes.TryGetValue(states[index], out boxes) || boxes.Length == 0)
                        boxes = new[] { new BoundingBox(Vector3.Zero, Vector3.One) };
                    double closest = Double.MaxValue; Vector3I normal = Vector3I.Zero;
                    foreach (BoundingBox box in boxes)
                    {
                        double t0, t1; Vector3I hitNormal;
                        if (RayBox(origin, direction, (Vector3D)cell + box.Min, (Vector3D)cell + box.Max, out t0, out t1, out hitNormal)
                            && t1 >= 0 && t0 <= range && Math.Max(0, t0) < closest)
                        { closest = Math.Max(0, t0); normal = hitNormal; }
                    }
                    if (closest != Double.MaxValue)
                    {
                        Vector3D localHit = origin + direction * closest;
                        result = new BlockHit { Cell = cell, FaceNormal = normal, Distance = closest,
                            LocalPoint = (Vector3)(localHit - (Vector3D)cell), WorldPoint = WorldPoint(localHit) };
                        return true;
                    }
                }
                double tx = NextBoundary(origin.X, direction.X, cell.X), ty = NextBoundary(origin.Y, direction.Y, cell.Y), tz = NextBoundary(origin.Z, direction.Z, cell.Z);
                distance = Math.Min(tx, Math.Min(ty, tz)) + 0.00001;
                cell = Vector3I.Floor(origin + direction * distance);
            }
            return false;
        }

        static double NextBoundary(double origin, double direction, int cell)
        { return Math.Abs(direction) < 1e-10 ? Double.PositiveInfinity : ((direction > 0 ? cell + 1 : cell) - origin) / direction; }
        static bool RayBox(Vector3D origin, Vector3D direction, Vector3D min, Vector3D max, out double near, out double far, out Vector3I normal)
        {
            near = Double.NegativeInfinity; far = Double.PositiveInfinity; normal = Vector3I.Zero;
            for (int axis = 0; axis < 3; axis++)
            {
                double o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
                double d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
                double lo = axis == 0 ? min.X : axis == 1 ? min.Y : min.Z;
                double hi = axis == 0 ? max.X : axis == 1 ? max.Y : max.Z;
                if (Math.Abs(d) < 1e-10) { if (o < lo || o > hi) return false; continue; }
                double a = (lo - o) / d, b = (hi - o) / d;
                int sign = -1;
                if (a > b) { double swap = a; a = b; b = swap; sign = 1; }
                if (a > near) { near = a; normal = axis == 0 ? new Vector3I(sign, 0, 0) : axis == 1 ? new Vector3I(0, sign, 0) : new Vector3I(0, 0, sign); }
                far = Math.Min(far, b);
                if (near > far) return false;
            }
            return true;
        }

        void Close(Collider collider)
        {
            collider.Entity.Damage = null;
            if (collider.Body != null && collider.Handler != null) collider.Body.ContactPointCallback -= collider.Handler;
            entityIds.Remove(collider.Entity.EntityId);
            if (!collider.Entity.Closed && !collider.Entity.MarkedForClose) collider.Entity.Close();
        }
        public void Clear()
        {
            generation++;
            foreach (List<Collider> list in chunks.Values) foreach (Collider collider in list) Close(collider);
            chunks.Clear(); retained.Clear(); retryChunks.Clear(); entityIds.Clear(); lastDamage.Clear(); CachedCellCount = 0; hasRetainedBounds = false;
            Impact unused; while (impacts.TryDequeue(out unused)) Interlocked.Decrement(ref queued);
            Array.Clear(states, 0, states.Length); Array.Clear(flags, 0, flags.Length); initialized = false;
        }
        public void Dispose() { Clear(); }
        static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    }
}
