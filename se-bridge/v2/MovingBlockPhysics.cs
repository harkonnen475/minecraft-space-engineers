using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Havok;
using Newtonsoft.Json.Linq;
using Sandbox.Engine.Physics;
using Sandbox.ModAPI;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;

namespace SEMC.V2
{
    // Native keyframed bodies follow verified Minecraft piston collision parts.
    // Havok resolves contacts with characters and grids; their poses/velocities are never assigned here.
    public sealed class MovingBlockPhysics : IDisposable
    {
        const int MaxBodies = 512;
        const double SampleMillis = 50, MaxSpeed = 20;
        readonly string root, file;
        readonly Action<string> log;
        readonly Dictionary<string, Body> bodies = new Dictionary<string, Body>();
        readonly HashSet<int> dynamicStates = new HashSet<int>();
        string worldId, dimension;
        long nextRead, nextIdentity, nextPalette, nextError, lastTimestamp, lastTick = -1, paletteWrite;
        MatrixD frame = MatrixD.Identity;
        bool hasFrame;
        int contacts;
        public int BodyCount { get { return bodies.Count; } }
        public int ContactCount { get { return Interlocked.CompareExchange(ref contacts, 0, 0); } }
        public long LastSampleTimestamp { get { return lastTimestamp; } }

        sealed class Body
        {
            public MyEntity Entity;
            public MyPhysicsBody Physics;
            public MyPhysicsBody.PhysicsContactHandler Contact;
            public Vector3 Half;
            public Vector3D From, To;
            public long Started, Seen;
        }
        sealed class Sample
        {
            public string Key;
            public Vector3D Center, Previous;
            public Vector3 Half;
        }
        public MovingBlockPhysics(string rootPath, Action<string> logger)
        { root = rootPath; file = Path.Combine(root, "entities-v2.json"); log = logger ?? delegate { }; }

        public void Update(MatrixD absoluteFrame, PhysicsWorld staticWorld)
        {
            long now = BridgeIO.Now();
            if (MyAPIGateway.Session == null || MyAPIGateway.Entities == null) { Clear(); return; }
            try
            {
                if (!hasFrame || !SameFrame(frame, absoluteFrame)) { Clear(); frame = absoluteFrame; hasFrame = true; }
                if (now >= nextIdentity)
                {
                    nextIdentity = now + 1000;
                    JObject identity = JObject.Parse(BridgeIO.ReadSharedText(Path.Combine(root, "world-v2.json")));
                    string nextWorld = (string)identity["worldId"], nextDimension = (string)identity["dimension"];
                    if (nextWorld != worldId || nextDimension != dimension) { Clear(); worldId = nextWorld; dimension = nextDimension; }
                }
                if (now >= nextPalette)
                {
                    nextPalette = now + 250;
                    string paletteFile = Path.Combine(root, "palette-v2.json");
                    long write = File.GetLastWriteTimeUtc(paletteFile).Ticks;
                    if (write != paletteWrite)
                    {
                        JObject palette = JObject.Parse(BridgeIO.ReadSharedText(paletteFile));
                        var next = new HashSet<int>();
                        foreach (JToken entry in (JArray)palette["states"])
                            if ((bool?)entry["dynamicCollision"] == true || (string)entry["registryName"] == "minecraft:moving_piston") next.Add((int)entry["stateId"]);
                        dynamicStates.Clear(); dynamicStates.UnionWith(next); paletteWrite = write;
                    }
                }
                if (staticWorld != null) staticWorld.SetDynamicCollisionStates(dynamicStates);
                if (now >= nextRead)
                {
                    nextRead = now + 25;
                    JObject document = JObject.Parse(BridgeIO.ReadSharedText(file));
                    long timestamp = (long?)document["timestamp"] ?? 0, tick = (long?)document["serverTick"] ?? -1;
                    if ((string)document["worldId"] == worldId && (string)document["dimension"] == dimension &&
                        timestamp > lastTimestamp && now - timestamp <= 500 && timestamp - now < 1000)
                    {
                        List<Sample> samples = ReadSamples(document);
                        // serverTick restarts with the backend process; the stable world identity does not.
                        if (tick < lastTick) ClearBodies();
                        Apply(samples, now, timestamp);
                        lastTimestamp = timestamp; lastTick = tick;
                    }
                }
                if (now - lastTimestamp > 500) { ClearBodies(); return; }
                Advance(now);
            }
            catch (Exception error)
            {
                // A malformed/stale document may never leave a moving body travelling indefinitely.
                Freeze();
                if (now - lastTimestamp > 500) ClearBodies();
                if (now >= nextError) { nextError = now + 3000; log("MOVING PHYSICS " + error.GetType().Name + ": " + error.Message); }
            }
        }

        static List<Sample> ReadSamples(JObject document)
        {
            var result = new List<Sample>();
            JArray pistons = document["movingBlocks"] as JArray;
            if (pistons == null) return result;
            if (pistons.Count > MaxBodies) throw new InvalidDataException("Too many moving piston entries.");
            var seen = new HashSet<string>();
            foreach (JToken piston in pistons)
            {
                string key = (string)piston["uuid"];
                if (String.IsNullOrEmpty(key) || key.Length > 250) throw new InvalidDataException("Invalid piston key.");
                JArray parts = piston["collisionParts"] as JArray;
                // Unverified unions have ambiguous stationary bases: keep their exact current geometry still.
                if ((bool?)piston["collisionPartsVerified"] != true || parts == null)
                {
                    JObject fallback = new JObject(); fallback["moving"] = false;
                    fallback["collisionBoxes"] = piston["collisionBoxes"] == null ? new JArray() : piston["collisionBoxes"].DeepClone();
                    parts = new JArray(fallback);
                }
                for (int p = 0; p < parts.Count; p++)
                {
                    JArray boxes = parts[p]["collisionBoxes"] as JArray, previous = parts[p]["previousCollisionBoxes"] as JArray;
                    if (boxes == null) continue;
                    bool moving = (bool?)parts[p]["moving"] == true;
                    for (int i = 0; i < boxes.Count; i++)
                    {
                        BoundingBoxD box = ReadBox(boxes[i]);
                        Vector3D center = box.Center, prior = center;
                        Vector3 half = (Vector3)(box.Size * .5);
                        if (moving && previous != null && previous.Count == boxes.Count)
                        {
                            BoundingBoxD old = ReadBox(previous[i]);
                            if (Vector3D.DistanceSquared(old.Size, box.Size) < .000001 && Vector3D.DistanceSquared(old.Center, center) <= 2.25) prior = old.Center;
                        }
                        string bodyKey = key + ":" + p + ":" + i;
                        if (!seen.Add(bodyKey)) throw new InvalidDataException("Duplicate moving piston body.");
                        if (result.Count >= MaxBodies) throw new InvalidDataException("Too many moving piston bodies.");
                        result.Add(new Sample { Key = bodyKey, Center = center, Previous = prior, Half = half });
                    }
                }
            }
            return result;
        }
        static BoundingBoxD ReadBox(JToken token)
        {
            JArray array = token as JArray;
            if (array == null || array.Count != 6) throw new InvalidDataException("Invalid piston collision box.");
            double[] a = new double[6];
            for (int i = 0; i < 6; i++)
            {
                a[i] = (double)array[i];
                if (Double.IsNaN(a[i]) || Double.IsInfinity(a[i]) || Math.Abs(a[i]) > 30000000) throw new InvalidDataException("Invalid piston coordinates.");
            }
            BoundingBoxD box = new BoundingBoxD(new Vector3D(a[0], a[1], a[2]), new Vector3D(a[3], a[4], a[5]));
            Vector3D size = box.Size;
            if (size.X <= 0 || size.Y <= 0 || size.Z <= 0 || size.X > 4 || size.Y > 4 || size.Z > 4) throw new InvalidDataException("Invalid piston box extent.");
            return box;
        }
        void Apply(List<Sample> samples, long now, long timestamp)
        {
            int before = bodies.Count;
            foreach (Sample sample in samples)
            {
                Body body;
                if (bodies.TryGetValue(sample.Key, out body) && Vector3.DistanceSquared(body.Half, sample.Half) > .000001f)
                { Close(body); bodies.Remove(sample.Key); body = null; }
                if (body == null)
                {
                    body = Create(Vector3D.Transform(sample.Previous, frame), sample.Half);
                    bodies.Add(sample.Key, body);
                }
                body.From = Vector3D.Transform(sample.Previous, frame);
                body.To = Vector3D.Transform(sample.Center, frame);
                body.Started = timestamp; body.Seen = timestamp;
            }
            if (bodies.Count != before) log("MOVING PHYSICS bodies=" + bodies.Count + " sample=" + timestamp + " contacts=" + ContactCount);
        }
        Body Create(Vector3D position, Vector3 half)
        {
            MyEntity entity = new MyEntity();
            try
            {
                entity.Init(new StringBuilder("Minecraft moving collision"), null, null, null, null);
                entity.Save = false; entity.Render.Visible = false;
                ((IMyEntity)entity).LocalAABB = new BoundingBox(-half, half);
                MatrixD matrix = frame; matrix.Translation = position;
                ((IMyEntity)entity).SetWorldMatrix(matrix, null);
                PhysicsSettings settings = MyAPIGateway.Physics.CreateSettingsForPhysics(entity, matrix, Vector3.Zero,
                    0f, 0f, 15, RigidBodyFlag.RBF_KINEMATIC, false, null);
                settings.MaterialType = MyStringHash.GetOrCompute("Rock");
                MyAPIGateway.Physics.CreateBoxPhysics(settings, half, .005f);
                MyPhysicsBody physics = entity.Physics as MyPhysicsBody;
                if (physics == null || physics.RigidBody == null) throw new InvalidOperationException("Native piston body was not created.");
                var body = new Body { Entity = entity, Physics = physics, Half = half };
                body.Contact = delegate(ref MyPhysics.MyContactPointEvent contact) { Interlocked.Increment(ref contacts); };
                physics.ContactPointCallback += body.Contact;
                physics.RigidBody.ContactPointCallbackEnabled = true;
                physics.RigidBody.ContactPointCallbackDelay = 0;
                MyAPIGateway.Entities.AddEntity(entity, true); physics.Enabled = true;
                physics.RigidBody.MaxLinearVelocity = (float)MaxSpeed;
                return body;
            }
            catch { if (!entity.Closed && !entity.MarkedForClose) entity.Close(); throw; }
        }
        void Advance(long now)
        {
            var remove = new List<string>();
            Quaternion orientation = Quaternion.CreateFromRotationMatrix(ref frame);
            foreach (var pair in bodies)
            {
                Body body = pair.Value;
                // Finish a received 50ms motion segment before handing collision back to the settled snapshot.
                if (body.Seen != lastTimestamp && now > body.Started + 100) { remove.Add(pair.Key); continue; }
                double blend = MathHelper.Clamp((now + 1000.0 / 60 - body.Started) / SampleMillis, 0, 1);
                Vector3D target = Vector3D.Lerp(body.From, body.To, blend);
                Vector3D current = body.Physics.ClusterToWorld(body.Physics.RigidBody.Position);
                Vector3D delta = target - current;
                double maximum = MaxSpeed / 60;
                if (delta.LengthSquared() > maximum * maximum) target = current + Vector3D.Normalize(delta) * maximum;
                // Synchronize the invisible entity's broadphase bounds to the body, never move other entities.
                MatrixD currentMatrix = frame; currentMatrix.Translation = current;
                ((IMyEntity)body.Entity).SetWorldMatrix(currentMatrix, body.Physics);
                Vector3 cluster = (Vector3)body.Physics.WorldToCluster(target);
                Vector4 position = new Vector4(cluster, 0);
                HkKeyFrameUtility.ApplyHardKeyFrame(ref position, ref orientation, 60f, body.Physics.RigidBody);
                body.Physics.RigidBody.IsActive = true;
            }
            foreach (string key in remove) { Close(bodies[key]); bodies.Remove(key); }
        }
        public bool Owns(IMyEntity entity)
        {
            if (entity == null) return false;
            foreach (Body body in bodies.Values) if (body.Entity.EntityId == entity.EntityId) return true;
            return false;
        }
        void Freeze()
        {
            foreach (Body body in bodies.Values) if (body.Physics.RigidBody != null) body.Physics.ClearSpeed();
        }
        void Close(Body body)
        {
            body.Physics.ContactPointCallback -= body.Contact;
            if (!body.Entity.Closed && !body.Entity.MarkedForClose) body.Entity.Close();
        }
        void ClearBodies() { foreach (Body body in bodies.Values) Close(body); bodies.Clear(); }
        public void Clear() { ClearBodies(); lastTimestamp = 0; lastTick = -1; }
        public void Dispose() { Clear(); }
        static bool SameFrame(MatrixD a, MatrixD b)
        { return Vector3D.DistanceSquared(a.Translation, b.Translation) < 1e-8 && Vector3D.DistanceSquared(a.Right, b.Right) < 1e-16 && Vector3D.DistanceSquared(a.Up, b.Up) < 1e-16; }
    }
}
