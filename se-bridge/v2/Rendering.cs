using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using VRage.Utils;
using VRageMath;
using VRageRender;
using VRageRender.Import;
using VRageRender.Messages;
using Color = VRageMath.Color;

namespace SEMC.V2
{
    // Coordinates are Minecraft block MIN corners. One local unit is one metre.
    // The frame maps local X/Y/Z to SE Right/Up/Backward, shared with Physics.cs.
    public sealed partial class MinecraftRenderer : IDisposable
    {
        const int Chunk = 8;
        readonly string root, prefix;
        readonly Action<string> log;
        readonly ModelResolver models;
        readonly Dictionary<string, ulong> hashes = new Dictionary<string, ulong>();
        readonly Dictionary<string, NativeChunk> rendered = new Dictionary<string, NativeChunk>();
        readonly HashSet<string> registeredMaterials = new HashSet<string>();
        readonly TerrainCache terrain = new TerrainCache();
        NativeRender native;
        MatrixD frame;
        bool hasFrame, disposed;
        int generation;
        int terrainModelRevision = -1;
        volatile bool retryUploads;
        public int RenderedCells { get; private set; }
        public int RenderedFaces { get; private set; }
        public long CacheRevision { get { return terrain.Revision; } }

        // Interests need only coordinates; avoid cloning all retained cell buffers each second.
        internal Vector3I[] CaptureRetainedOrigins() { return terrain.Chunks.Keys.ToArray(); }

        public RetainedWorldSnapshot CaptureRetained()
        {
            return new RetainedWorldSnapshot {
                Revision = terrain.Revision,
                Chunks = terrain.Chunks.Select(p => new CachedTerrainChunk {
                    Minimum = p.Key, States = (int[])p.Value.States.Clone(), Colors = (int[])p.Value.Colors.Clone(),
                    Flags = (int[])p.Value.Flags.Clone()
                }).ToArray()
            };
        }

        public void RestoreRetained(IEnumerable<CachedTerrainChunk> chunks, MatrixD absoluteWorldFrame)
        {
            if (disposed || chunks == null) return;
            // Load has already validated identity, checksum, coordinates and buffers.
            models.ReloadPalette();
            if (native == null) native = new NativeRender();
            frame = absoluteWorldFrame; hasFrame = true;
            var dirty = new HashSet<Vector3I>();
            foreach (CachedTerrainChunk chunk in chunks)
                dirty.UnionWith(terrain.Merge(8, 8, 8, chunk.States, chunk.Colors, chunk.Flags, chunk.Minimum));
            RefreshTerrain(dirty, true);
            log("RENDER restored retained source cells=" + terrain.Count);
        }

        public MinecraftRenderer(string rootPath, Action<string> logger)
        {
            root = rootPath; log = logger ?? delegate { };
            prefix = "semc_v2_" + Guid.NewGuid().ToString("N").Substring(0, 10) + "_";
            models = new ModelResolver(root, prefix, log);
        }

        public void Update(int sizeX, int sizeY, int sizeZ, int[] states, int[] colors, int[] flags, MatrixD worldFrame)
        {
            Update(sizeX,sizeY,sizeZ,states,colors,flags,worldFrame,Vector3I.Zero);
        }

        public void Update(int sizeX, int sizeY, int sizeZ, int[] states, int[] colors, int[] flags, MatrixD worldFrame, Vector3I minecraftOrigin)
        {
            if (disposed) return;
            int count = checked(sizeX * sizeY * sizeZ);
            if (states == null || colors == null || flags == null || states.Length < count || colors.Length < count || flags.Length < count)
                throw new ArgumentException("Renderer snapshot dimensions do not match its buffers.");
            models.ReloadPalette();
            bool rebuild = terrainModelRevision != models.Revision || retryUploads;
            terrainModelRevision = models.Revision;
            if (rebuild) { hashes.Clear(); retryUploads = false; }
            if (native == null) native = new NativeRender();
            MatrixD absoluteFrame = worldFrame;
            absoluteFrame.Translation -= Vector3D.TransformNormal((Vector3D)minecraftOrigin, worldFrame);
            bool moved = !hasFrame || Vector3D.DistanceSquared(frame.Translation, absoluteFrame.Translation) > 1e-12 ||
                Vector3D.DistanceSquared(frame.Right, absoluteFrame.Right) > 1e-20 ||
                Vector3D.DistanceSquared(frame.Up, absoluteFrame.Up) > 1e-20 ||
                Vector3D.DistanceSquared(frame.Backward, absoluteFrame.Backward) > 1e-20;
            frame = absoluteFrame; hasFrame = true;
            HashSet<Vector3I> dirty = terrain.Merge(sizeX,sizeY,sizeZ,states,colors,flags,minecraftOrigin);
            if (rebuild) foreach (Vector3I key in terrain.Chunks.Keys) dirty.Add(key);
            RefreshTerrain(dirty, moved);
        }

        // Only call after Minecraft accepts the native break. Used for SE impacts outside capture.
        public void ApplyConfirmedRemoval(Vector3I absoluteCell)
        {
            if (disposed || !hasFrame || native == null) return;
            RefreshTerrain(terrain.Remove(absoluteCell), false);
        }

        // Authoritative remote Minecraft capture; invoke on the same game thread as Update.
        // -1 states/negative flags preserve unknown cells; state zero explicitly removes a cell.
        public void ApplyRemoteChunk(CachedTerrainChunk chunk) { ApplyRemoteChunks(new[] { chunk }); }
        public void ApplyRemoteChunks(IEnumerable<CachedTerrainChunk> chunks)
        {
            if (disposed) return;
            if (!hasFrame || native == null) throw new InvalidOperationException("Configure renderer frame before applying remote chunks.");
            if (chunks == null) throw new ArgumentNullException("chunks");
            CachedTerrainChunk[] batch = chunks.ToArray();
            // Validate the entire batch before changing previously observed terrain.
            foreach (CachedTerrainChunk chunk in batch) TerrainCache.ValidateRemote(chunk);
            models.ReloadPalette();
            bool rebuild = terrainModelRevision != models.Revision || retryUploads;
            terrainModelRevision = models.Revision;
            if (rebuild) { hashes.Clear(); retryUploads = false; }
            var dirty = new HashSet<Vector3I>();
            foreach (CachedTerrainChunk chunk in batch)
                dirty.UnionWith(terrain.Merge(8,8,8,chunk.States,chunk.Colors,chunk.Flags,chunk.Minimum));
            if (rebuild) foreach (Vector3I key in terrain.Chunks.Keys) dirty.Add(key);
            RefreshTerrain(dirty, false);
        }

        void RefreshTerrain(HashSet<Vector3I> dirty, bool moved)
        {
            RenderedCells = terrain.Count;
            if (dirty.Count == 0 && !moved) return;
            var replacements = new List<ChunkUpload>();
            var chunkWorlds = new Dictionary<string,MatrixD>();
            int[] states = new int[1000], colors = new int[1000], flags = new int[1000];
            foreach (Vector3I absolute in dirty)
            {
                string key = ChunkKey(absolute);
                // There is no GPU object or source to change for an unobserved neighbour.
                if (!terrain.Chunks.ContainsKey(absolute) && !hashes.ContainsKey(key)) continue;
                MatrixD chunkWorld = ChunkWorld(absolute);
                terrain.Halo(absolute, states, colors, flags);
                ulong hash = HashChunk(1, 1, 1, 10, 10, 10, states, colors, flags);
                ulong previous;
                if (hashes.TryGetValue(key, out previous) && hash == previous) continue;
                var mesh = BuildChunk(1, 1, 1, 10, 10, 10, states, colors, flags);
                if (terrain.Chunks.ContainsKey(absolute)) hashes[key] = hash;
                else hashes.Remove(key);
                replacements.Add(new ChunkUpload { Key = key, Mesh = mesh, World = chunkWorld });
            }
            if (moved) foreach (Vector3I absolute in terrain.Chunks.Keys) chunkWorlds.Add(ChunkKey(absolute), ChunkWorld(absolute));
            if (replacements.Count == 0 && !moved) return;
            UpdateCachedBlockLights();
            int cells = terrain.Count;
            int ticket = generation;
            native.Enqueue(delegate {
                if (ticket != generation || disposed) return;
                foreach (var upload in replacements)
                {
                    try { ReplaceChunk(upload); }
                    catch (Exception error) { retryUploads = true; log("RENDER chunk " + upload.Key + " failed: " + error); }
                }
                foreach(var pair in rendered) {
                    MatrixD target;
                    if(!chunkWorlds.TryGetValue(pair.Key,out target) || pair.Value.World==target)continue;
                    try{native.Move(pair.Value.Id,target);pair.Value.World=target;}catch(Exception error){retryUploads=true;log("RENDER move chunk: "+error.Message);}
                }
                RenderedFaces = rendered.Values.Sum(v => v.Faces);
                log("RENDER native meshes=" + rendered.Count + " faces=" + RenderedFaces + " changedChunks=" + replacements.Count + " cells=" + cells);
            });
        }

        static string ChunkKey(Vector3I p) { return p.X + "_" + p.Y + "_" + p.Z; }
        MatrixD ChunkWorld(Vector3I absolute)
        { MatrixD world = frame; world.Translation = Vector3D.Transform((Vector3D)absolute, frame); return world; }

        static int FloorChunk(int value) { return (int)Math.Floor(value/(double)Chunk)*Chunk; }

        static ulong HashChunk(int cx, int cy, int cz, int sx, int sy, int sz, int[] states, int[] colors, int[] flags)
        {
            ulong hash = 1469598103934665603UL;
            unchecked {
                for (int y = cy - 1; y < cy + Chunk + 1; y++)
                for (int z = cz - 1; z < cz + Chunk + 1; z++)
                for (int x = cx - 1; x < cx + Chunk + 1; x++)
                {
                    int i = x>=0 && y>=0 && z>=0 && x<sx && y<sy && z<sz ? x+sx*(z+sz*y) : -1;
                    bool present=i>=0 && (flags[i]&1)!=0;
                    hash = (hash ^ (present?(uint)states[i]:0)) * 1099511628211UL;
                    hash = (hash ^ (present?(uint)colors[i]:0)) * 1099511628211UL;
                    hash = (hash ^ (present?(uint)flags[i]:0)) * 1099511628211UL;
                }
            }
            return hash;
        }

        MeshBuild BuildChunk(int cx, int cy, int cz, int sx, int sy, int sz, int[] states, int[] colors, int[] flags)
        {
            var build = new MeshBuild();
            for (int y = Math.Max(0,cy); y < Math.Min(sy, cy + Chunk); y++)
            for (int z = Math.Max(0,cz); z < Math.Min(sz, cz + Chunk); z++)
            for (int x = Math.Max(0,cx); x < Math.Min(sx, cx + Chunk); x++)
            {
                int i = x + sx * (z + sz * y);
                if ((flags[i] & 1) == 0) continue;
                BlockModel block = models.Get(states[i], colors[i], flags[i]);
                float fluidScale=1;
                if(block.Fluid && y+1<sy) {
                    int above=x+sx*(z+sz*(y+1));
                    if((flags[above]&8)!=0 && models.SameFluid(states[i],states[above]))fluidScale=1f/block.FluidHeight;
                }
                foreach (var face in block.Faces)
                {
                    float fluidBottom=0;
                    if (face.Cull >= 0)
                    {
                        Vector3I step = FaceTemplate.Directions[face.Cull];
                        int nx = x + step.X, ny = y + step.Y, nz = z + step.Z;
                        if (nx >= 0 && nx < sx && ny >= 0 && ny < sy && nz >= 0 && nz < sz)
                        {
                            int ni = nx + sx * (nz + sz * ny);
                            if ((flags[ni] & 2) != 0 && models.IsOccluding(states[ni])) continue;
                            if (block.Glass && states[ni] == states[i]) continue;
                            if (block.Fluid && (flags[ni] & 8) != 0 && models.SameFluid(states[i],states[ni])) {
                                if(face.Cull<2)continue;
                                float neighborHeight=models.Get(states[ni],colors[ni],flags[ni]).FluidHeight;
                                if(ny+1<sy) {int above=nx+sx*(nz+sz*(ny+1));if((flags[above]&8)!=0 && models.SameFluid(states[ni],states[above]))neighborHeight=1;}
                                if(neighborHeight>=block.FluidHeight*fluidScale-.0001f)continue;
                                fluidBottom=neighborHeight;
                            }
                        }
                    }
                    build.Add(face, new Vector3(x-cx, y-cy, z-cz),fluidScale,fluidBottom);
                }
            }
            build.Finish();
            return build;
        }

        void ReplaceChunk(ChunkUpload upload)
        {
            NativeChunk old;
            if (rendered.TryGetValue(upload.Key, out old))
            {
                native.Remove(old.Id, old.ModelName);
                rendered.Remove(upload.Key);
            }
            if (upload.Mesh.Data.Indices.Count == 0) return;
            foreach (var material in upload.Mesh.Materials.Values)
                if (!registeredMaterials.Contains(material.Name)) { native.Register(material); registeredMaterials.Add(material.Name); }
            string name = prefix + upload.Key;
            uint id = native.Create(name, upload.Mesh.Data, upload.World);
            rendered.Add(upload.Key, new NativeChunk { Id = id, ModelName = name, Faces = upload.Mesh.Data.Indices.Count / 6, World=upload.World });
        }

        public void Clear()
        {
            Interlocked.Increment(ref generation); hashes.Clear(); terrain.Clear(); hasFrame = false;
            RenderedCells = RenderedFaces = 0;
            ResetEntitySamples();
            ClearBlockLights();
            if (native == null) return;
            native.Enqueue(delegate {
                foreach (var entry in rendered.Values)
                    try { native.Remove(entry.Id, entry.ModelName); } catch (Exception error) { log("RENDER clear: " + error.Message); }
                rendered.Clear();
                ClearEntityObjects();
            });
        }
        public void Dispose() { if (disposed) return; Clear(); disposed = true; }
        sealed class ChunkUpload { public string Key; public MeshBuild Mesh; public MatrixD World; }
        sealed class NativeChunk { public uint Id; public string ModelName; public int Faces; public MatrixD World; }

        // Full-trust plugin bridge to the installed renderer. All mutations occur
        // inside MyRender11.ProcessUpdates, after its normal message queue drain.
        sealed class NativeRender
        {
            readonly Action<Action> enqueue;
            readonly Action<MyRenderMessageBase, int> process;
            readonly Func<MyRenderProxy.ObjectType, bool, uint> allocate;
            readonly Action<MyStringId> removeMesh;
            readonly MethodInfo material;
            readonly FieldInfo runtimeMeshNames, lodInfos, lodIndex, sectionNames;
            readonly MethodInfo getLodMesh;
            public NativeRender()
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "VRage.Render11");
                var render = assembly.GetType("VRageRender.MyRender11", true);
                var mesh = assembly.GetType("VRageRender.MyMeshes", true);
                var materials = assembly.GetType("VRageRender.MyMeshMaterials1", true);
                const BindingFlags scope = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
                enqueue = (Action<Action>)Delegate.CreateDelegate(typeof(Action<Action>), render.GetMethod("EnqueueUpdate", scope));
                process = (Action<MyRenderMessageBase, int>)Delegate.CreateDelegate(typeof(Action<MyRenderMessageBase, int>), render.GetMethod("ProcessMessageInternal", scope, null, new[] { typeof(MyRenderMessageBase), typeof(int) }, null));
                allocate = (Func<MyRenderProxy.ObjectType, bool, uint>)Delegate.CreateDelegate(typeof(Func<MyRenderProxy.ObjectType, bool, uint>), typeof(MyRenderProxy).GetMethod("GetMessageId", scope));
                removeMesh = (Action<MyStringId>)Delegate.CreateDelegate(typeof(Action<MyStringId>), mesh.GetMethod("RemoveMesh", scope, null, new[] { typeof(MyStringId) }, null));
                material = materials.GetMethod("GetMaterialId", scope, null, new[] { typeof(MyMaterialDescriptor), typeof(string), typeof(string) }, null);
                if (material == null) throw new MissingMethodException("VRageRender.MyMeshMaterials1.GetMaterialId descriptor overload");
                runtimeMeshNames = mesh.GetField("RuntimeMeshNameIndex", scope);
                lodInfos = mesh.GetField("LodMeshInfos", scope);
                getLodMesh = mesh.GetMethod("GetLodMesh", scope);
                const BindingFlags instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                lodIndex = assembly.GetType("VRageRender.LodMeshId",true).GetField("Index",instance);
                sectionNames = assembly.GetType("VRageRender.MyLodMeshInfo",true).GetField("SectionNames",instance);
            }
            public void Enqueue(Action action) { enqueue(action); }
            public void Register(RenderMaterial source)
            {
                var descriptor = new MyMaterialDescriptor(source.Name);
                descriptor.TechniqueEnum = source.Transparent ? MyMeshDrawTechnique.GLASS : source.Cutout ? MyMeshDrawTechnique.ALPHA_MASKED : MyMeshDrawTechnique.MESH;
                descriptor.Textures["ColorMetalTexture"] = source.ColorMetal;
                descriptor.Textures["NormalGlossTexture"] = source.NormalGloss;
                descriptor.Textures["AddMapsTexture"] = source.Extensions;
                if (source.Cutout) descriptor.Textures["AlphamaskTexture"] = source.Alpha;
                if(source.Transparent) {
                    // Installed MyTransparentModelPass requires this registry entry,
                    // matched to the GLASS mesh material name, before it draws a part.
                    MyTransparentMaterials.AddMaterial(new MyTransparentMaterial(
                        MyStringId.GetOrCompute(source.Name),MyTransparentMaterialTextureType.FileTexture,
                        source.ColorAlpha,source.NormalGloss,1f,true,false,Vector4.One,Vector4.Zero,
                        new Vector4(.65f,.65f,.65f,1f),Vector4.One,false,false,
                        useAtlas:false,reflectivity:.015f,fresnel:.06f,gloss:.3f,glossTextureAdd:.2f,specularColorFactor:.15f));
                }
                material.Invoke(null, new object[] { descriptor, Path.GetDirectoryName(source.ColorMetal), null });
            }
            public uint Create(string name, MyModelData data, MatrixD world)
            {
                if (data.Positions.Count >= 65536) throw new InvalidOperationException("Runtime mesh exceeds the engine's 16-bit vertex index limit.");
                process(new MyRenderMessageAddRuntimeModel { Name = name, ModelData = data }, 0);
                InitializeRuntimeMetadata(name);
                uint id = allocate(MyRenderProxy.ObjectType.Entity, true);
                try
                {
                    process(new MyRenderMessageCreateRenderEntity {
                        ID = id, DebugName = "Minecraft terrain", Model = name, WorldMatrix = world,
                        Technique = MyMeshDrawTechnique.MESH, CullingOptions = CullingOptions.Default,
                        Flags = RenderFlags.Visible | RenderFlags.CastShadows | RenderFlags.CastShadowsOnLow | RenderFlags.ForceOldPipeline | RenderFlags.NoBackFaceCulling,
                        MaxViewDistance = 5000f, Rescale = 1f, DepthBias = 0, ForceReload = false
                    }, 0);
                    process(new MyRenderMessageUpdateRenderEntity { ID = id, DiffuseColor = Color.White, ColorMaskHSV = Vector3.Zero, Dithering = 0f, FadeIn = false }, 0);
                    return id;
                }
                catch { MyRenderProxy.RemoveMessageId(id, MyRenderProxy.ObjectType.Entity, false); removeMesh(MyStringId.GetOrCompute(name)); throw; }
            }
            public void Remove(uint id, string model)
            {
                process(new MyRenderMessageRemoveRenderObject { ID = id, FadeOut = false }, 0);
                MyRenderProxy.RemoveMessageId(id, MyRenderProxy.ObjectType.Entity, false);
                removeMesh(MyStringId.GetOrCompute(model));
            }
            public void Move(uint id, MatrixD world)
            {
                process(new MyRenderMessageUpdateRenderObject { ID=id, LastMomentUpdateIndex=-1,
                    Data=new MyRenderObjectUpdateData {WorldMatrix=world,HasWorldMatrix=true} },0);
            }
            public void SetEmission(uint id, string materialName, float emission)
            {
                process(new MyRenderMessageUpdateModelProperties {ID=id,MaterialName=MyStringId.GetOrCompute(materialName),Emissivity=emission},0);
            }
            void InitializeRuntimeMetadata(string name)
            {
                // SE 1.210's CreateRuntimeMesh leaves SectionNames null, whereas
                // CreateRenderableProxyForPart unconditionally iterates it.
                // Runtime material parts need no named, independently drawn sections.
                var lookup = (IDictionary)runtimeMeshNames.GetValue(null);
                object mesh = lookup[MyStringId.GetOrCompute(name)];
                object lod = getLodMesh.Invoke(null,new[]{mesh,(object)0});
                int index = (int)lodIndex.GetValue(lod);
                object pool = lodInfos.GetValue(null);
                var array = (Array)pool.GetType().GetProperty("Data").GetValue(pool,null);
                object info = array.GetValue(index);
                if(sectionNames.GetValue(info)==null) { sectionNames.SetValue(info,new string[0]); array.SetValue(info,index); }
            }
        }
    }

    sealed class RenderMaterial
    {
        public string Name, ColorMetal, NormalGloss, Extensions, Alpha, ColorAlpha;
        public bool Cutout, Transparent;
    }
    sealed class FaceTemplate
    {
        public Vector3[] Points;
        public Vector2[] UV;
        public Vector3 Normal;
        public int Cull = -1;
        public RenderMaterial Material;
        public static readonly string[] Names = { "down", "up", "north", "south", "west", "east" };
        public static readonly Vector3I[] Directions = { new Vector3I(0,-1,0), new Vector3I(0,1,0), new Vector3I(0,0,-1), new Vector3I(0,0,1), new Vector3I(-1,0,0), new Vector3I(1,0,0) };
    }
    sealed class BlockModel { public readonly List<FaceTemplate> Faces = new List<FaceTemplate>(); public bool Glass, Fluid; public float FluidHeight=1f; }
    sealed class MeshBuild
    {
        public readonly MyModelData Data = new MyModelData();
        public readonly Dictionary<string, RenderMaterial> Materials = new Dictionary<string, RenderMaterial>();
        readonly Dictionary<string, List<int>> indices = new Dictionary<string, List<int>>();
        public void Add(FaceTemplate face, Vector3 offset, float heightScale=1f, float bottom=0f)
        {
            if (Data.Positions.Count + 4 >= 65536) throw new InvalidOperationException("Chunk model complexity exceeds 16-bit indices.");
            int start = Data.Positions.Count;
            Vector3 tangent = face.Points[1] - face.Points[0];
            if (tangent.LengthSquared() < 0.000001f) tangent = Vector3.Right; else tangent.Normalize();
            for (int v = 0; v < 4; v++) { Vector3 point=face.Points[v];if(heightScale!=1f || bottom>0f)point.Y=Math.Max(point.Y*heightScale,bottom);Data.Positions.Add(point + offset); Data.Normals.Add(face.Normal); Data.Tangents.Add(tangent); Data.TexCoords.Add(face.UV[v]); }
            List<int> list;
            if (!indices.TryGetValue(face.Material.Name, out list)) { list = new List<int>(); indices.Add(face.Material.Name, list); Materials.Add(face.Material.Name, face.Material); }
            list.Add(start); list.Add(start + 2); list.Add(start + 1); list.Add(start); list.Add(start + 3); list.Add(start + 2);
        }
        public void Finish()
        {
            foreach (var section in indices)
            {
                Data.Sections.Add(new MyRuntimeSectionInfo { IndexStart = Data.Indices.Count, TriCount = section.Value.Count / 3, MaterialName = section.Key });
                Data.Indices.AddRange(section.Value);
            }
            var bounds = BoundingBox.CreateInvalid();
            foreach (var point in Data.Positions) bounds.Include(point);
            Data.AABB = bounds;
        }
    }

    sealed class ModelResolver
    {
        readonly string root, assetRoot;
        readonly Action<string> log;
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 64000000, RecursionLimit = 128 };
        readonly Dictionary<int, Dictionary<string, object>> palette = new Dictionary<int, Dictionary<string, object>>();
        readonly Dictionary<int, BlockModel> blocks = new Dictionary<int, BlockModel>();
        readonly Dictionary<string, ModelDefinition> definitions = new Dictionary<string, ModelDefinition>();
        readonly TextureAssets textures;
        readonly bool nativeTransparency;
        DateTime paletteDate;
        public int Revision { get; private set; }
        public ModelResolver(string directory, string prefix, Action<string> logger) { root = directory; log = logger; assetRoot = Path.Combine(root, "render-assets", "minecraft"); textures = new TextureAssets(root, prefix); nativeTransparency=File.Exists(Path.Combine(root,"render-transparency.flag")); }
        public bool SameFluid(int a,int b) {Dictionary<string,object> first,second;return palette.TryGetValue(a,out first)&&palette.TryGetValue(b,out second)&&Text(Value(first,"registryName"))==Text(Value(second,"registryName"));}

        public bool ReloadPalette()
        {
            string path = Path.Combine(root, "palette-v2.json");
            DateTime date = File.GetLastWriteTimeUtc(path);
            if (!File.Exists(path) || date == paletteDate) return false;
            try
            {
                var document = ReadJson(path);
                var replacement = new Dictionary<int, Dictionary<string, object>>();
                foreach (var entry in Items(Value(document, "states"))) { var state = Dict(entry); replacement[Integer(Value(state, "stateId"))] = state; }
                palette.Clear(); foreach (var entry in replacement) palette.Add(entry.Key, entry.Value);
                blocks.Clear(); paletteDate = date; Revision++;
                log("RENDER palette states=" + palette.Count);
                return true;
            }
            catch (IOException) { return false; }
            catch (Exception error) { log("RENDER palette read: " + error.Message); return false; }
        }
        public bool IsOccluding(int id)
        {
            Dictionary<string, object> state;
            if (!palette.TryGetValue(id, out state)) return true;
            string name = Text(Value(state, "registryName"));
            return name != "minecraft:moving_piston" && !name.Contains("glass") && !name.Contains("leaves") && !name.Contains("ice") && !name.Contains("slime") && !name.Contains("honey");
        }
        public bool IsLight(int id, out bool torch)
        {
            torch=false;
            Dictionary<string,object> state;
            if(!palette.TryGetValue(id,out state))return false;
            string name=Text(Value(state,"registryName"));
            if(name.Contains("redstone_lamp"))return Text(Value(Dict(Value(state,"properties")),"lit"))=="true";
            torch=name.Contains("torch");
            if(torch && name.Contains("redstone"))return Text(Value(Dict(Value(state,"properties")),"lit"))=="true";
            return torch || name.Contains("glowstone") || name.Contains("sea_lantern");
        }
        public BlockModel Get(int id, int color, int flags)
        {
            BlockModel cached;
            if (blocks.TryGetValue(id, out cached)) return cached;
            var result = new BlockModel();
            Dictionary<string, object> state;
            if (!palette.TryGetValue(id, out state)) { state = new Dictionary<string, object>(); state["registryName"] = "minecraft:stone"; }
            string registry = Text(Value(state, "registryName"));
            // Native moving entities supply the actual block/head renderParts; the placeholder is invisible.
            if (registry == "minecraft:moving_piston") { blocks[id] = result; return result; }
            var properties = Dict(Value(state, "properties"));
            result.Glass = registry.Contains("glass");
            // Waterlogged stairs/fences retain their solid model geometry.
            result.Fluid = (flags & 8) != 0 && (registry=="minecraft:water" || registry=="minecraft:lava");
            if(result.Fluid) {int level=Integer(Value(properties,"level"));result.FluidHeight=(level>=8?8:8-level)/9f;}
            int tint = Integer(Value(state, "tintRGB"), -1);
            if (tint < 0) tint = registry.Contains("redstone_wire") ? RedstoneTint(Integer(Value(properties, "power"))) : (registry.Contains("leaves") ? 0x77AB2F : 0x91BD59);
            try
            {
                string resource = Text(Value(state, "blockstateResource"));
                if (resource.Length == 0) resource = "assets/minecraft/blockstates/" + registry.Split(':').Last() + ".json";
                string relative = resource.Replace("assets/minecraft/", "").Replace('/', Path.DirectorySeparatorChar);
                string file = Path.Combine(assetRoot, relative);
                var blockstate = File.Exists(file) ? ReadJson(file) : new Dictionary<string, object>();
                var variants = Dict(Value(blockstate, "variants"));
                foreach (var variant in variants)
                    if (MatchVariant(variant.Key, properties)) { AddVariant(result, First(variant.Value), tint, registry); break; }
                foreach (var piece in Items(Value(blockstate, "multipart")))
                {
                    var part = Dict(piece);
                    if (!part.ContainsKey("when") || MatchWhen(Dict(part["when"]), properties)) AddVariant(result, First(Value(part, "apply")), tint, registry);
                }
                if (result.Faces.Count == 0)
                {
                    string hint = Text(Value(state, "modelResourceHint"));
                    if (hint.Length == 0) hint = "minecraft:block/" + registry.Split(':').Last();
                    AddVariant(result, new Dictionary<string, object> { { "model", hint } }, tint, registry);
                }
            }
            catch (Exception error) { log("RENDER model " + registry + ": " + error.Message); }
            if (result.Faces.Count == 0 && registry == "minecraft:chest") AddChest(result, properties);
            if (result.Faces.Count == 0)
            {
                string texture = registry.Split(':').Last();
                if (registry.Contains("water")) texture = "water_still";
                if (registry.Contains("lava")) texture = "lava_still";
                if (!textures.Exists("block/" + texture)) texture = "stone";
                var material = textures.Get("block/" + texture, registry.Contains("water") ? 0x3F76E4 : -1, registry.Contains("lava") ? 0.65f : 0f,nativeTransparency && registry.Contains("water"),.5f);
                AddBoxFallback(result, material, result.Fluid ? result.FluidHeight : 1f);
                log("RENDER fallback cube for " + registry);
            }
            blocks[id] = result;
            return result;
        }
        static int RedstoneTint(int power)
        {
            float p = power / 15f; int r = (int)((power == 0 ? 0.3f : p * 0.6f + 0.4f) * 255);
            int g = (int)(Math.Max(0, p * p * 0.7f - 0.5f) * 255), b = (int)(Math.Max(0, p * p * 0.6f - 0.7f) * 255);
            return (r << 16) | (g << 8) | b;
        }
        // Vanilla ChestModel.createSingleBodyLayer/createDoubleBody*Layer values,
        // read from the locally installed client. Its block JSON is particle-only.
        void AddChest(BlockModel output, Dictionary<string, object> properties)
        {
            string type = Text(Value(properties, "type")), facing = Text(Value(properties, "facing"));
            bool left = type == "left", right = type == "right";
            string texture = "entity/chest/normal" + (left ? "_left" : right ? "_right" : "");
            RenderMaterial material = textures.Get(texture, -1, 0);
            float xmin = left ? 0 : 1, xmax = right ? 16 : 15;
            int hidden = left ? 4 : right ? 5 : -1;
            float angle = facing == "north" ? 180 : facing == "east" ? 90 : facing == "west" ? -90 : 0;
            Matrix rotation = Matrix.CreateRotationY(MathHelper.ToRadians(angle));
            AddEntityBox(output, material, new Vector3(xmin,0,1), new Vector3(xmax,10,15), 0,19, hidden, rotation);
            AddEntityBox(output, material, new Vector3(xmin,9,1), new Vector3(xmax,14,15), 0,0, hidden, rotation);
            float lockMin = left ? 0 : right ? 15 : 7, lockMax = left ? 1 : right ? 16 : 9;
            AddEntityBox(output, material, new Vector3(lockMin,7,15), new Vector3(lockMax,11,16), 0,0, hidden, rotation);
        }
        static void AddEntityBox(BlockModel output, RenderMaterial material, Vector3 min, Vector3 max, float u, float v, int hidden, Matrix rotation)
        {
            float dx=max.X-min.X,dy=max.Y-min.Y,dz=max.Z-min.Z;
            var uv=new[] {
                new[]{u+dz+dx,v+dz,u+dz+dx+dx,v}, new[]{u+dz,v,u+dz+dx,v+dz},
                new[]{u+dz,v+dz,u+dz+dx,v+dz+dy}, new[]{u+dz+dx+dz,v+dz,u+dz+dx+dz+dx,v+dz+dy},
                new[]{u,v+dz,u+dz,v+dz+dy}, new[]{u+dz+dx,v+dz,u+dz+dx+dz,v+dz+dy}
            };
            for(int face=0;face<6;face++) {
                if(face==hidden)continue;
                var quad=Quad(face,min/16,max/16);
                for(int i=0;i<4;i++)quad[i]=Vector3.Transform(quad[i]-new Vector3(.5f),rotation)+new Vector3(.5f);
                float[] r=uv[face];
                output.Faces.Add(new FaceTemplate { Points=quad, Normal=Vector3.TransformNormal((Vector3)FaceTemplate.Directions[face],rotation), Material=material,
                    UV=new[]{new Vector2(r[0]/64,r[1]/64),new Vector2(r[2]/64,r[1]/64),new Vector2(r[2]/64,r[3]/64),new Vector2(r[0]/64,r[3]/64)} });
            }
        }
        void AddVariant(BlockModel output, object value, int tint, string registry)
        {
            var variant = Dict(value);
            string resource = Text(Value(variant, "model"));
            if (resource.Length == 0) return;
            ModelDefinition model = LoadModel(resource, 0);
            float xrot = Number(Value(variant, "x")), yrot = Number(Value(variant, "y"));
            Matrix rotation = Matrix.CreateRotationX(MathHelper.ToRadians(-xrot)) * Matrix.CreateRotationY(MathHelper.ToRadians(-yrot));
            int elementIndex = 0;
            foreach (var item in model.Elements)
            {
                var element = Dict(item);
                Vector3 from = Vec(Value(element, "from")) / 16f, to = Vec(Value(element, "to")) / 16f;
                var localRotation = Dict(Value(element, "rotation"));
                foreach (var pair in Dict(Value(element, "faces")))
                {
                    int faceIndex = Array.IndexOf(FaceTemplate.Names, pair.Key);
                    if (faceIndex < 0) continue;
                    var face = Dict(pair.Value);
                    string texture = ResolveTexture(Value(face, "texture"), model.Textures);
                    if (texture.Length == 0) continue;
                    int faceTint = face.ContainsKey("tintindex") ? tint : -1;
                    float emission = registry.Contains("redstone_lamp") && resource.Contains("_on") ? 0.4f : (registry.Contains("torch") || registry.Contains("glowstone") ? 0.3f : 0f);
                    var material = textures.Get(texture, faceTint, emission,nativeTransparency && registry.Contains("glass"),.85f);
                    var quad = Quad(faceIndex, from, to);
                    Vector3 normal = FaceTemplate.Directions[faceIndex];
                    Vector3 localNormal = normal;
                    for (int v = 0; v < 4; v++)
                    {
                        Vector3 point = quad[v];
                        if (localRotation.Count > 0) point = RotateElement(point, localRotation);
                        point = Vector3.Transform(point - new Vector3(0.5f), rotation) + new Vector3(0.5f);
                        quad[v] = point;
                    }
                    if (localRotation.Count > 0)
                    {
                        Vector3 origin = Vec(Value(localRotation, "origin")) / 16f;
                        normal = RotateElement(origin + normal, localRotation) - origin;
                    }
                    normal = Vector3.TransformNormal(normal, rotation); normal.Normalize();
                    // Overlay elements (grass/redstone) must not z-fight with their base faces.
                    if (elementIndex > 0) for (int v = 0; v < 4; v++) quad[v] += normal * (elementIndex * 0.0003f);
                    int cull = Array.IndexOf(FaceTemplate.Names, Text(Value(face, "cullface")));
                    if (cull >= 0) cull = ClosestDirection(Vector3.TransformNormal((Vector3)FaceTemplate.Directions[cull], rotation));
                    var uvValues = Items(Value(face, "uv")).ToArray();
                    float[] uv = uvValues.Length == 4 ? uvValues.Select(Number).ToArray() : DefaultUV(faceIndex, from * 16, to * 16);
                    var coordinates = new[] { new Vector2(uv[0] / 16, uv[1] / 16), new Vector2(uv[2] / 16, uv[1] / 16), new Vector2(uv[2] / 16, uv[3] / 16), new Vector2(uv[0] / 16, uv[3] / 16) };
                    int turns = Integer(Value(face, "rotation")) / 90;
                    if (turns != 0) coordinates = Enumerable.Range(0, 4).Select(v => coordinates[(v + turns) % 4]).ToArray();
                    output.Faces.Add(new FaceTemplate { Points = quad, UV = coordinates, Normal = normal, Cull = cull, Material = material });
                }
                elementIndex++;
            }
        }
        static Vector3 RotateElement(Vector3 point, Dictionary<string, object> rotation)
        {
            Vector3 origin = Vec(Value(rotation, "origin")) / 16f;
            float angle = MathHelper.ToRadians(Number(Value(rotation, "angle")));
            string axis = Text(Value(rotation, "axis"));
            Matrix transform = axis == "x" ? Matrix.CreateRotationX(angle) : (axis == "y" ? Matrix.CreateRotationY(angle) : Matrix.CreateRotationZ(angle));
            Vector3 p = point - origin;
            if (Boolean(Value(rotation, "rescale")))
            {
                float scale = 1f / (float)Math.Cos(angle);
                p *= new Vector3(axis == "x" ? 1 : scale, axis == "y" ? 1 : scale, axis == "z" ? 1 : scale);
            }
            return Vector3.Transform(p, transform) + origin;
        }
        ModelDefinition LoadModel(string resource, int depth)
        {
            if (depth > 32) throw new InvalidDataException("Cyclic Minecraft model inheritance.");
            resource = resource.Replace("minecraft:", "");
            ModelDefinition saved;
            if (definitions.TryGetValue(resource, out saved)) return saved;
            string file = Path.Combine(assetRoot, "models", resource.Replace('/', Path.DirectorySeparatorChar) + ".json");
            if (!File.Exists(file)) return new ModelDefinition();
            var data = ReadJson(file); var result = new ModelDefinition();
            string parent = Text(Value(data, "parent"));
            if (parent.Length > 0 && !parent.Contains("builtin/"))
            {
                var inherited = LoadModel(parent, depth + 1);
                foreach (var item in inherited.Textures) result.Textures[item.Key] = item.Value;
                result.Elements.AddRange(inherited.Elements);
            }
            foreach (var item in Dict(Value(data, "textures"))) result.Textures[item.Key] = item.Value;
            if (data.ContainsKey("elements")) { result.Elements.Clear(); result.Elements.AddRange(Items(data["elements"])); }
            definitions[resource] = result; return result;
        }
        static string ResolveTexture(object resource, Dictionary<string, object> textures)
        {
            string value = resource is Dictionary<string, object> ? Text(Value(Dict(resource), "sprite")) : Text(resource);
            for (int depth = 0; depth < 32 && value.StartsWith("#"); depth++)
            {
                object next; if (!textures.TryGetValue(value.Substring(1), out next)) return "";
                value = next is Dictionary<string, object> ? Text(Value(Dict(next), "sprite")) : Text(next);
            }
            return value.StartsWith("#") ? "" : value.Replace("minecraft:", "");
        }
        static bool MatchVariant(string rule, Dictionary<string, object> properties)
        {
            if (rule.Length == 0) return true;
            foreach (string item in rule.Split(',')) { string[] pair = item.Split('='); if (pair.Length != 2 || !pair[1].Split('|').Contains(Text(Value(properties, pair[0])))) return false; }
            return true;
        }
        static bool MatchWhen(Dictionary<string, object> when, Dictionary<string, object> properties)
        {
            foreach (var pair in when)
            {
                if (pair.Key == "OR") { if (!Items(pair.Value).Any(v => MatchWhen(Dict(v), properties))) return false; }
                else if (pair.Key == "AND") { if (!Items(pair.Value).All(v => MatchWhen(Dict(v), properties))) return false; }
                else if (!Text(pair.Value).Split('|').Contains(Text(Value(properties, pair.Key)))) return false;
            }
            return true;
        }
        static int ClosestDirection(Vector3 normal) { int result = 0; float best = -2; for (int i = 0; i < 6; i++) { float dot = Vector3.Dot(normal, (Vector3)FaceTemplate.Directions[i]); if (dot > best) { result = i; best = dot; } } return result; }
        static void AddBoxFallback(BlockModel model, RenderMaterial material, float height) { for (int f = 0; f < 6; f++) model.Faces.Add(new FaceTemplate { Points = Quad(f, Vector3.Zero, new Vector3(1, height, 1)), UV = new[] { Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY }, Normal = FaceTemplate.Directions[f], Cull = f, Material = material }); }
        static Vector3[] Quad(int face, Vector3 a, Vector3 b)
        {
            switch (face)
            {
                case 0: return new[] { new Vector3(a.X,a.Y,b.Z), new Vector3(b.X,a.Y,b.Z), new Vector3(b.X,a.Y,a.Z), new Vector3(a.X,a.Y,a.Z) };
                case 1: return new[] { new Vector3(a.X,b.Y,a.Z), new Vector3(b.X,b.Y,a.Z), new Vector3(b.X,b.Y,b.Z), new Vector3(a.X,b.Y,b.Z) };
                case 2: return new[] { new Vector3(b.X,b.Y,a.Z), new Vector3(a.X,b.Y,a.Z), new Vector3(a.X,a.Y,a.Z), new Vector3(b.X,a.Y,a.Z) };
                case 3: return new[] { new Vector3(a.X,b.Y,b.Z), new Vector3(b.X,b.Y,b.Z), new Vector3(b.X,a.Y,b.Z), new Vector3(a.X,a.Y,b.Z) };
                case 4: return new[] { new Vector3(a.X,b.Y,a.Z), new Vector3(a.X,b.Y,b.Z), new Vector3(a.X,a.Y,b.Z), new Vector3(a.X,a.Y,a.Z) };
                default:return new[] { new Vector3(b.X,b.Y,b.Z), new Vector3(b.X,b.Y,a.Z), new Vector3(b.X,a.Y,a.Z), new Vector3(b.X,a.Y,b.Z) };
            }
        }
        static float[] DefaultUV(int face, Vector3 a, Vector3 b)
        {
            switch(face) { case 0:return new[]{a.X,16-b.Z,b.X,16-a.Z}; case 1:return new[]{a.X,a.Z,b.X,b.Z}; case 2:return new[]{16-b.X,16-b.Y,16-a.X,16-a.Y}; case 3:return new[]{a.X,16-b.Y,b.X,16-a.Y}; case 4:return new[]{a.Z,16-b.Y,b.Z,16-a.Y}; default:return new[]{16-b.Z,16-b.Y,16-a.Z,16-a.Y}; }
        }
        Dictionary<string, object> ReadJson(string file) { return Dict(json.DeserializeObject(RenderFiles.ReadText(file))); }
        static Dictionary<string, object> Dict(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        static object Value(Dictionary<string, object> dict, string key) { object value; return dict.TryGetValue(key, out value) ? value : null; }
        static string Text(object value) { return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture); }
        static int Integer(object value, int fallback = 0) { return value == null ? fallback : Convert.ToInt32(value, CultureInfo.InvariantCulture); }
        static float Number(object value) { return value == null ? 0 : Convert.ToSingle(value, CultureInfo.InvariantCulture); }
        static bool Boolean(object value) { return value != null && Convert.ToBoolean(value, CultureInfo.InvariantCulture); }
        static IEnumerable<object> Items(object value) { var array = value as IEnumerable; if (array == null || value is string || value is Dictionary<string, object>) yield break; foreach (object item in array) yield return item; }
        static object First(object value) { return value is Dictionary<string, object> ? value : Items(value).FirstOrDefault(); }
        static Vector3 Vec(object value) { var values = Items(value).Select(Number).ToArray(); return values.Length == 3 ? new Vector3(values[0], values[1], values[2]) : Vector3.Zero; }
        sealed class ModelDefinition { public readonly Dictionary<string, object> Textures = new Dictionary<string, object>(); public readonly List<object> Elements = new List<object>(); }
    }

    // Converts the user's own local Minecraft PNGs to renderer-native DDS maps.
    // Nearest expansion preserves the original pixel grid under SE's sampler.
    sealed class TextureAssets
    {
        const int Resolution = 128;
        readonly string source, output, prefix;
        readonly Dictionary<string, RenderMaterial> cache = new Dictionary<string, RenderMaterial>();
        readonly string normal;
        public TextureAssets(string root, string instancePrefix)
        {
            prefix = instancePrefix;
            source = Path.Combine(root, "render-assets", "minecraft", "textures"); output = Path.Combine(root, "render-assets", "se"); Directory.CreateDirectory(output);
            normal = Path.Combine(output, "normal_ng.dds");
            if (!File.Exists(normal)) WriteDds(normal, delegate(int x,int y) { return Pack(128,128,255,5); });
        }
        public bool Exists(string resource) { return File.Exists(Path.Combine(source, resource.Replace("minecraft:", "").Replace('/', Path.DirectorySeparatorChar) + ".png")); }
        public RenderMaterial Get(string resource, int tint, float emission, bool transparent=false, float opacity=1f)
        {
            string key = resource + "|" + tint + "|" + emission.ToString(CultureInfo.InvariantCulture)+(transparent?"|transparent|"+opacity.ToString(CultureInfo.InvariantCulture):"");
            RenderMaterial saved; if (cache.TryGetValue(key, out saved)) return saved;
            string file = Path.Combine(source, resource.Replace("minecraft:", "").Replace('/', Path.DirectorySeparatorChar) + ".png");
            if (!File.Exists(file)) file = Path.Combine(source, "block", "stone.png");
            uint checksum = 2166136261; unchecked { foreach (char c in key) checksum = (checksum ^ c) * 16777619; }
            string stem = "mc_" + checksum.ToString("x8");
            string cm = Path.Combine(output, stem + "_cm.dds"), alpha = Path.Combine(output, stem + "_alphamask.dds"), extra = Path.Combine(output, "extra_" + ((int)(emission * 255)).ToString() + ".dds"),ca=Path.Combine(output,stem+"_ca.dds");
            bool cutout = false;
            using (var bitmap = new Bitmap(file))
            {
                // Animated PNG strips use their first frame; UVs stay unchanged.
                int width = bitmap.Width, height = Math.Min(bitmap.Height, bitmap.Width);
                var pixels = new System.Drawing.Color[width * height];
                for (int y=0;y<height;y++) for(int x=0;x<width;x++) { var p=bitmap.GetPixel(x,y); pixels[x+width*y]=p; if(p.A<250) cutout=true; }
                if (!File.Exists(cm)) WriteDds(cm, delegate(int x,int y) {
                    var p=pixels[(x*width/Resolution)+width*(y*height/Resolution)];
                    int r=p.R,g=p.G,b=p.B; if(tint>=0){r=r*((tint>>16)&255)/255;g=g*((tint>>8)&255)/255;b=b*(tint&255)/255;}
                    return Pack(r,g,b,0);
                });
                if(cutout && !File.Exists(alpha)) WriteDds(alpha,delegate(int x,int y){int a=pixels[(x*width/Resolution)+width*(y*height/Resolution)].A;return Pack(a,a,a,255);});
                if(transparent && !File.Exists(ca))WriteDds(ca,delegate(int x,int y){
                    var p=pixels[(x*width/Resolution)+width*(y*height/Resolution)];int r=p.R,g=p.G,b=p.B;
                    if(tint>=0){r=r*((tint>>16)&255)/255;g=g*((tint>>8)&255)/255;b=b*(tint&255)/255;}
                    double a=p.A/255.0*opacity;
                    return Pack(PremultiplySrgb(r,a),PremultiplySrgb(g,a),PremultiplySrgb(b,a),(int)(a*255));
                });
            }
            if(!File.Exists(extra)) WriteDds(extra,delegate(int x,int y){return Pack(255,(int)(emission*255),0,0);});
            saved = new RenderMaterial { Name = prefix+stem, ColorMetal=cm, NormalGloss=normal, Extensions=extra, Alpha=alpha, Cutout=cutout&&!transparent, Transparent=transparent,ColorAlpha=ca };
            cache[key]=saved;return saved;
        }
        static uint Pack(int r,int g,int b,int a){return (uint)(r|(g<<8)|(b<<16)|(a<<24));}
        static int PremultiplySrgb(int channel,double alpha){double c=channel/255.0;double linear=(c<=.04045?c/12.92:Math.Pow((c+.055)/1.055,2.4))*alpha;return (int)(255*(linear<=.0031308?linear*12.92:1.055*Math.Pow(linear,1/2.4)-.055)+.5);}
        static void WriteDds(string path,Func<int,int,uint> pixel)
        {
            using(var file=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.Read)) using(var writer=new BinaryWriter(file))
            {
                writer.Write(0x20534444);writer.Write(124);writer.Write(0x100F);writer.Write(Resolution);writer.Write(Resolution);writer.Write(Resolution*4);writer.Write(0);writer.Write(1);
                for(int i=0;i<11;i++)writer.Write(0);
                writer.Write(32);writer.Write(0x41);writer.Write(0);writer.Write(32);writer.Write(0x000000ff);writer.Write(0x0000ff00);writer.Write(0x00ff0000);writer.Write(unchecked((int)0xff000000));
                writer.Write(0x1000);writer.Write(0);writer.Write(0);writer.Write(0);writer.Write(0);
                for(int y=0;y<Resolution;y++)for(int x=0;x<Resolution;x++)writer.Write(pixel(x,y));
            }
        }
    }

    static class RenderFiles
    {
        public static string ReadText(string file)
        {
            using(var stream=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
            using(var reader=new StreamReader(stream))return reader.ReadToEnd();
        }
    }
}
