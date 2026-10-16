using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using VRageMath;

namespace SEMC.V2
{
    public sealed partial class MinecraftRenderer
    {
        // Render objects are render-thread-owned; source samples/geometry are game-thread-owned.
        readonly Dictionary<string, MovingObject> moving = new Dictionary<string, MovingObject>();
        readonly Dictionary<string, EntitySample> entitySamples = new Dictionary<string, EntitySample>();
        readonly Dictionary<int, MeshBuild> entityMeshes = new Dictionary<int, MeshBuild>();
        readonly JavaScriptSerializer entityJson = new JavaScriptSerializer { MaxJsonLength = 8000000 };
        DateTime entityDate;
        long latestEntityTimestamp, nextEntityError;
        int entityMeshRevision, actorSerial, entityModelRevision = -1;
        bool entityWasActive;
        string entityWorldId, entityDimension;
        sealed class MovingObject { public uint Id; public string ModelName; public string[] Materials; public bool Flash; public int State, Revision; public MatrixD World; }
        sealed class EntitySample
        {
            public string Key;
            public int State;
            public Vector3D Previous, Position;
            public long PreviousAt, Timestamp;
            public double PreviousScale = 1, Scale = 1;
            public bool Flash;
        }
        sealed class EntityUpload { public string Key; public int State, Revision; public MeshBuild Mesh; public MatrixD World; public bool Flash; }

        // Call once each game update. JSON is read only when its mtime changes; animation continues
        // between samples. Positions are interpolated between native samples, never extrapolated.
        public void UpdateEntities(int originX, int originY, int originZ, MatrixD worldFrame)
        {
            if (disposed || native == null) return;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string file = Path.Combine(root, "entities-v2.json");
            try
            {
                if (File.Exists(file))
                {
                    DateTime date = File.GetLastWriteTimeUtc(file);
                    if (date != entityDate)
                    {
                        var document = entityJson.DeserializeObject(RenderFiles.ReadText(file)) as Dictionary<string, object>;
                        if (AcceptEntityIdentity(document))
                        {
                            long timestamp = EntityLong(document, "timestamp");
                            if (timestamp > 0 && now - timestamp < 2000 && timestamp <= now + 1000)
                            { ReadEntitySamples(document, timestamp); latestEntityTimestamp = timestamp; }
                            else entitySamples.Clear();
                            entityDate = date;
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (Exception error)
            {
                if (now >= nextEntityError) { nextEntityError = now + 5000; log("RENDER entity samples: " + error.Message); }
            }
            if (now - latestEntityTimestamp >= 2000) entitySamples.Clear();
            if (entitySamples.Count == 0 && !entityWasActive) return;

            MatrixD absolute = worldFrame;
            absolute.Translation -= Vector3D.TransformNormal(new Vector3D(originX, originY, originZ), worldFrame);
            var uploads = new List<EntityUpload>();
            foreach (EntitySample sample in entitySamples.Values)
            {
                MeshBuild mesh;
                if (!entityMeshes.TryGetValue(sample.State, out mesh))
                {
                    mesh = new MeshBuild();
                    foreach (FaceTemplate face in models.Get(sample.State, 0, 3).Faces) mesh.Add(face, new Vector3(-.5f));
                    mesh.Finish(); entityMeshes.Add(sample.State, mesh);
                }
                if (mesh.Data.Indices.Count == 0) continue;
                double alpha = SampleAlpha(sample.PreviousAt, sample.Timestamp, now - 50);
                MatrixD local = MatrixD.CreateScale(sample.PreviousScale + (sample.Scale - sample.PreviousScale) * alpha);
                local.Translation = Vector3D.Lerp(sample.Previous, sample.Position, alpha);
                uploads.Add(new EntityUpload { Key = sample.Key, State = sample.State, Revision = entityMeshRevision,
                    Mesh = mesh, World = local * absolute, Flash = sample.Flash });
            }
            entityWasActive = uploads.Count != 0;
            int ticket = generation;
            native.Enqueue(delegate
            {
                if (disposed || ticket != generation) return;
                var active = new HashSet<string>(uploads.Select(v => v.Key));
                foreach (string key in moving.Keys.Where(k => !active.Contains(k)).ToArray()) RemoveMovingObject(key);
                foreach (EntityUpload upload in uploads) try
                {
                    MovingObject entry;
                    if (moving.TryGetValue(upload.Key, out entry) && (entry.State != upload.State || entry.Revision != upload.Revision))
                    { RemoveMovingObject(upload.Key); entry = null; }
                    if (entry == null)
                    {
                        foreach (RenderMaterial material in upload.Mesh.Materials.Values)
                            if (!registeredMaterials.Contains(material.Name)) { native.Register(material); registeredMaterials.Add(material.Name); }
                        string name = prefix + "actor_" + (++actorSerial);
                        entry = new MovingObject { Id = native.Create(name, upload.Mesh.Data, upload.World), ModelName = name,
                            Materials = upload.Mesh.Materials.Keys.ToArray(), State = upload.State, Revision = upload.Revision, World = upload.World };
                        moving.Add(upload.Key, entry);
                        log("RENDER native actor=" + entry.Id + " key=" + upload.Key + " state=" + upload.State);
                    }
                    else if (entry.World != upload.World) { native.Move(entry.Id, upload.World); entry.World = upload.World; }
                    if (entry.Flash != upload.Flash)
                    {
                        foreach (string material in entry.Materials) native.SetEmission(entry.Id, material, upload.Flash ? 1f : 0f);
                        entry.Flash = upload.Flash;
                    }
                }
                catch (Exception error) { log("RENDER entity " + upload.Key + ": " + error.Message); }
            });
        }

        void ReadEntitySamples(Dictionary<string, object> document, long timestamp)
        {
            models.ReloadPalette();
            if (models.Revision != entityModelRevision) { entityModelRevision = models.Revision; entityMeshes.Clear(); entityMeshRevision++; }
            var replacement = new Dictionary<string, EntitySample>();
            foreach (object value in EntityArray(document, "entities"))
            {
                var entity = value as Dictionary<string, object>; string kind = EntityText(entity, "type");
                if (kind != "minecraft:tnt" && kind != "minecraft:falling_block") continue;
                string uuid = EntityText(entity, "uuid"); int state = (int)EntityLong(entity, "stateId");
                if (uuid.Length == 0 || state <= 0) continue;
                Vector3D center = EntityPosition(entity) + new Vector3D(0, .5, 0);
                EntitySample sample = MakeSample(kind + ":" + uuid, state, center, timestamp);
                if (kind == "minecraft:tnt")
                {
                    int fuse = (int)EntityLong(entity, "fuseTicks"); double growth = Math.Max(0, Math.Min(1, 1 - fuse / 10.0)); growth *= growth; growth *= growth;
                    sample.Scale = .98 + growth * .3; sample.Flash = (fuse / 5) % 2 == 0;
                }
                replacement[sample.Key] = sample;
            }
            foreach (object value in EntityArray(document, "movingBlocks"))
            {
                var piston = value as Dictionary<string, object>;
                if (EntityText(piston, "type") != "minecraft:moving_piston") continue;
                string uuid = EntityText(piston, "uuid"); if (uuid.Length == 0) continue;
                Vector3D offsetDelta = EntityVector(piston, "previousOffset") - EntityVector(piston, "offset");
                int partIndex = 0;
                foreach (object partValue in EntityArray(piston, "renderParts"))
                {
                    var part = partValue as Dictionary<string, object>; int state = (int)EntityLong(part, "stateId");
                    string key = uuid + ":part:" + partIndex++;
                    if (part == null || state <= 0) continue;
                    EntitySample sample = MakeSample(key, state, EntityPosition(part) + new Vector3D(.5), timestamp);
                    // Native previousOffset captures the preceding piston tick even if a JSON sample was missed.
                    if (EntityBool(part, "moving")) { sample.Previous = sample.Position + offsetDelta; sample.PreviousAt = timestamp - 50; }
                    else { sample.Previous = sample.Position; sample.PreviousAt = timestamp; }
                    sample.Scale = sample.PreviousScale = 1;
                    replacement[key] = sample;
                }
            }
            entitySamples.Clear(); foreach (var pair in replacement) entitySamples.Add(pair.Key, pair.Value);
        }

        EntitySample MakeSample(string key, int state, Vector3D center, long timestamp)
        {
            var sample = new EntitySample { Key = key, State = state, Position = center, Previous = center, Timestamp = timestamp, PreviousAt = timestamp };
            EntitySample previous;
            if (entitySamples.TryGetValue(key, out previous) && timestamp > previous.Timestamp && timestamp - previous.Timestamp <= 250 && Vector3D.DistanceSquared(center,previous.Position) <= 64)
            { sample.Previous = previous.Position; sample.PreviousAt = previous.Timestamp; sample.PreviousScale = previous.Scale; }
            return sample;
        }
        static double SampleAlpha(long previous, long current, long renderTime)
        { return current <= previous ? 1 : Math.Max(0, Math.Min(1, (renderTime - previous) / (double)(current - previous))); }

        bool AcceptEntityIdentity(Dictionary<string, object> document)
        {
            if (document == null) return false;
            if (entityWorldId == null)
            {
                string file = Path.Combine(root, "world-v2.json"); if (!File.Exists(file)) return false;
                var identity = entityJson.DeserializeObject(RenderFiles.ReadText(file)) as Dictionary<string, object>;
                entityWorldId = EntityText(identity, "worldId"); entityDimension = EntityText(identity, "dimension");
                if (entityWorldId.Length == 0 || entityDimension.Length == 0) { entityWorldId = null; return false; }
            }
            return String.Equals(EntityText(document, "worldId"), entityWorldId, StringComparison.OrdinalIgnoreCase) && EntityText(document, "dimension") == entityDimension;
        }
        static object EntityValue(Dictionary<string, object> row, string key) { object value; return row != null && row.TryGetValue(key,out value) ? value : null; }
        static string EntityText(Dictionary<string, object> row, string key) { return Convert.ToString(EntityValue(row,key),CultureInfo.InvariantCulture); }
        static long EntityLong(Dictionary<string, object> row, string key) { return Convert.ToInt64(EntityValue(row,key),CultureInfo.InvariantCulture); }
        static bool EntityBool(Dictionary<string, object> row, string key) { return Convert.ToBoolean(EntityValue(row,key),CultureInfo.InvariantCulture); }
        static object[] EntityArray(Dictionary<string, object> row, string key) { return EntityValue(row,key) as object[] ?? new object[0]; }
        static Vector3D EntityPosition(Dictionary<string, object> row)
        { return new Vector3D(Convert.ToDouble(EntityValue(row,"x"),CultureInfo.InvariantCulture),Convert.ToDouble(EntityValue(row,"y"),CultureInfo.InvariantCulture),Convert.ToDouble(EntityValue(row,"z"),CultureInfo.InvariantCulture)); }
        static Vector3D EntityVector(Dictionary<string, object> row, string key)
        { object[] a=EntityArray(row,key); return a.Length>=3 ? new Vector3D(Convert.ToDouble(a[0]),Convert.ToDouble(a[1]),Convert.ToDouble(a[2])) : Vector3D.Zero; }

        void RemoveMovingObject(string key)
        {
            MovingObject old; if (!moving.TryGetValue(key,out old)) return;
            try { native.Remove(old.Id,old.ModelName); } catch (Exception error) { log("RENDER entity remove: " + error.Message); }
            moving.Remove(key);
        }
        void ResetEntitySamples()
        {
            entitySamples.Clear(); entityMeshes.Clear(); entityWasActive=false; latestEntityTimestamp=0;
            entityDate=DateTime.MinValue; entityModelRevision=-1; entityWorldId=entityDimension=null;
        }
        void ClearEntityObjects() { foreach (string key in moving.Keys.ToArray()) RemoveMovingObject(key); }
    }
}
