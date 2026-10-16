using System;
using System.IO;
using Newtonsoft.Json.Linq;
using Sandbox.Game;
using Sandbox.ModAPI;
using VRageMath;

namespace SEMC.V2
{
    // Only the server's post-Level.explode event authorizes a cross-engine blast.
    public sealed class ExplosionBridge
    {
        readonly string eventsFile, stateFile;
        readonly Action<string> log;
        long lastId, nextRead;
        public ExplosionBridge(string root, Action<string> logger)
        {
            eventsFile = Path.Combine(root, "explosion-v2.json");
            stateFile = Path.Combine(root, "explosion-se-v2.json");
            log = logger;
            // Starting a session must never replay old explosions from a previous run.
            try { lastId = (long?)JObject.Parse(BridgeIO.ReadSharedText(eventsFile))["lastId"] ?? 0; } catch { }
            Save();
        }
        public void Update(MatrixD absoluteFrame, PhysicsWorld physics)
        {
            long now = BridgeIO.Now();
            if (now < nextRead) return;
            nextRead = now + 50;
            if (!File.Exists(eventsFile)) return;
            JObject document;
            try { document = JObject.Parse(BridgeIO.ReadSharedText(eventsFile)); }
            catch (IOException) { return; }
            catch (Newtonsoft.Json.JsonException) { return; }
            JArray events = document["events"] as JArray;
            if (events == null) return;
            foreach (JObject value in events)
            {
                long id = (long?)value["id"] ?? 0;
                if (id <= lastId) continue;
                lastId = id;
                if ((string)value["dimension"] != "minecraft:overworld" ||
                    (string)value["evidence"] != "after-native-Level.explode" ||
                    now - ((long?)value["timestamp"] ?? 0) > 10000) { Save(); continue; }
                Vector3D local = new Vector3D((double)value["x"], (double)value["y"], (double)value["z"]);
                double power = (double?)value["power"] ?? 4;
                if (!local.IsValid() || Double.IsNaN(power) || power <= 0 || power > 16) { Save(); continue; }
                Vector3D center = Vector3D.Transform(local, absoluteFrame);
                var info = new MyExplosionInfo(0, (float)(power * 250), new BoundingSphereD(center, power),
                    MyExplosionTypeEnum.WARHEAD_EXPLOSION_02, true, true, true);
                info.VoxelExplosionCenter = center;
                info.VoxelCutoutScale = .85f;
                info.ParticleScale = 1;
                info.StrengthImpulse = 1f;
                info.LifespanMiliseconds = 700;
                info.PlaySound = true;
                info.ApplyForceAndDamage = true;
                info.AffectVoxels = true;
                info.CreateParticleEffect = true;
                info.CreateDecals = true;
                info.CreateDebris = true;
                info.CheckIntersections = true;
                info.ShouldDetonateAmmo = true;
                physics.SuppressDamage(1800);
                bool accepted = MyExplosions.AddExplosion(ref info, true);
                Save();
                log("TNT SE event=" + id + " accepted=" + accepted + " MC=" + local + " radius=" + power);
            }
        }
        void Save()
        {
            try { File.WriteAllText(stateFile, new JObject(new JProperty("lastId", lastId), new JProperty("timestamp", BridgeIO.Now())).ToString()); }
            catch { }
        }
    }
}
