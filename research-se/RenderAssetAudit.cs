using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
namespace SEMC.V2 {
    static class RenderAssetAudit {
        static void Main() {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e) {
                string p=Path.Combine(@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64",new AssemblyName(e.Name).Name+".dll");
                return File.Exists(p)?Assembly.LoadFrom(p):null;
            };
            Run();
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Run() {
            string root=@"C:\Users\konra\Documents\SEMC-Lab";
            var resolver=new ModelResolver(root,"audit_",Console.WriteLine); resolver.ReloadPalette();
            var json=new JavaScriptSerializer {MaxJsonLength=64000000};
            var doc=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(Path.Combine(root,"palette-v2.json")));
            int total=0,faces=0; var names=new Dictionary<string,HashSet<int>>();
            foreach(var obj in (object[])doc["states"]) {
                var row=(Dictionary<string,object>)obj; string name=(string)row["registryName"];
                var block=resolver.Get(Convert.ToInt32(row["stateId"]),Convert.ToInt32(row["mapRGB"]),Convert.ToInt32(row["flags"]));
                if(block.Fluid && name!="minecraft:water" && name!="minecraft:lava")throw new Exception("Waterlogged solid was classified as a liquid surface.");
                total++;faces+=block.Faces.Count;
                if(!names.ContainsKey(name))names[name]=new HashSet<int>();names[name].Add(block.Faces.Count);
            }
            foreach(var entry in names)Console.WriteLine(entry.Key+" faceCounts="+String.Join(",",entry.Value.OrderBy(v=>v)));
            Console.WriteLine("AUDIT states="+total+" modelFaces="+faces);
            MeshAudit(root,(object[])doc["states"]);
            TransparencyAudit(root);
            CacheAudit(root,(object[])doc["states"]);
            PersistenceAudit(root);
            DynamicEntityAudit(root,(object[])doc["states"]);
        }
        static void DynamicEntityAudit(string root, object[] palette) {
            int state=Convert.ToInt32(palette.Cast<Dictionary<string,object>>().First(v=>(string)v["registryName"]=="minecraft:oak_planks")["stateId"]);
            var renderer=new MinecraftRenderer(root,delegate{});var scope=BindingFlags.Instance|BindingFlags.NonPublic;
            var read=typeof(MinecraftRenderer).GetMethod("ReadEntitySamples",scope);
            var samples=(System.Collections.IDictionary)typeof(MinecraftRenderer).GetField("entitySamples",scope).GetValue(renderer);
            var falling=new Dictionary<string,object>{{"type","minecraft:falling_block"},{"uuid","fall"},{"stateId",state},{"x",3.5},{"y",245.0},{"z",4.5}};
            var tnt=new Dictionary<string,object>{{"type","minecraft:tnt"},{"uuid","tnt"},{"stateId",state},{"fuseTicks",8},{"x",1.5},{"y",240.0},{"z",2.5}};
            var piston=new Dictionary<string,object>{{"type","minecraft:moving_piston"},{"uuid","piston:test"},{"previousOffset",new object[]{-1.0,0.0,0.0}},{"offset",new object[]{-.5,0.0,0.0}},
                {"renderParts",new object[]{new Dictionary<string,object>{{"stateId",state},{"x",10.5},{"y",240.0},{"z",20.0},{"moving",true}},new Dictionary<string,object>{{"stateId",state},{"x",11.0},{"y",240.0},{"z",20.0},{"moving",false}}}}};
            var document=new Dictionary<string,object>{{"entities",new object[]{falling,tnt}},{"movingBlocks",new object[]{piston}}};
            read.Invoke(renderer,new object[]{document,1000L});
            if(samples.Count!=4)throw new Exception("Native dynamic render parts were not all decoded.");
            object part=samples["piston:test:part:0"],stationary=samples["piston:test:part:1"];
            var type=part.GetType();
            var position=(VRageMath.Vector3D)type.GetField("Position").GetValue(part);var previous=(VRageMath.Vector3D)type.GetField("Previous").GetValue(part);
            if(position!=new VRageMath.Vector3D(11,240.5,20.5)||previous!=new VRageMath.Vector3D(10.5,240.5,20.5))throw new Exception("Piston mincorner/native previousOffset conversion failed.");
            if(!type.GetField("Position").GetValue(stationary).Equals(type.GetField("Previous").GetValue(stationary)))throw new Exception("Stationary piston base moved.");
            falling["y"]=244.8;read.Invoke(renderer,new object[]{document,1050L});
            object fall=samples["minecraft:falling_block:fall"];
            position=(VRageMath.Vector3D)type.GetField("Position").GetValue(fall);previous=(VRageMath.Vector3D)type.GetField("Previous").GetValue(fall);
            if(Math.Abs(position.Y-245.3)>1e-8||Math.Abs(previous.Y-245.5)>1e-8)throw new Exception("Falling block native center/bottom interpolation endpoints failed.");
            var alpha=typeof(MinecraftRenderer).GetMethod("SampleAlpha",BindingFlags.Static|BindingFlags.NonPublic);
            if((double)alpha.Invoke(null,new object[]{1000L,1050L,1025L})!=.5||(double)alpha.Invoke(null,new object[]{1000L,1050L,2000L})!=1||(double)alpha.Invoke(null,new object[]{1000L,1050L,0L})!=0)throw new Exception("Entity interpolation extrapolated past native endpoints.");
            falling["x"]=1000.0;read.Invoke(renderer,new object[]{document,1100L});fall=samples["minecraft:falling_block:fall"];
            if(!type.GetField("Position").GetValue(fall).Equals(type.GetField("Previous").GetValue(fall)))throw new Exception("Large entity teleport was interpolated through terrain.");
            read.Invoke(renderer,new object[]{new Dictionary<string,object>(),1200L});if(samples.Count!=0)throw new Exception("Disappeared dynamic actors retained source samples.");
            var resolver=(ModelResolver)typeof(MinecraftRenderer).GetField("models",scope).GetValue(renderer);
            var states=(Dictionary<int,Dictionary<string,object>>)typeof(ModelResolver).GetField("palette",scope).GetValue(resolver);
            states[900100]=new Dictionary<string,object>{{"registryName","minecraft:moving_piston"}};
            if(resolver.Get(900100,0,7).Faces.Count!=0||resolver.IsOccluding(900100))throw new Exception("Static moving piston placeholder was rendered or occluded neighbours.");
            Console.WriteLine("ENTITY AUDIT native falling/TNT/piston parts, center/mincorner conversion, native previousOffset, stationary base, bounded interpolation, teleports, disappearance and invisible placeholder passed");
        }
        static void PersistenceAudit(string root) {
            string scratch=Path.Combine(root,"research-se","cache-audit-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(scratch);
            string worldFile=Path.Combine(scratch,"world-v2.json");
            File.WriteAllText(worldFile,new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"worldId",Guid.NewGuid().ToString("D")},{"dimension","minecraft:overworld"},{"paletteFingerprint",new String('a',64)},{"minecraftVersion","26.3"}}));
            var frame=VRageMath.MatrixD.Identity;frame.Translation=new VRageMath.Vector3D(10,20,30);
            string identity=RetainedWorldStore.ReadIdentity(scratch,root,frame);
            if(identity==null)throw new Exception("Persistence identity was not accepted.");
            var moved=frame;moved.Translation+=VRageMath.Vector3D.One;
            if(identity==RetainedWorldStore.ReadIdentity(scratch,root,moved))throw new Exception("Persistence frame was not bound to identity.");
            var store=new RetainedWorldStore(scratch,identity,Console.WriteLine);
            var chunk=new CachedTerrainChunk{Minimum=new VRageMath.Vector3I(-16,240,8),States=new int[512],Colors=new int[512],Flags=new int[512]};
            chunk.States[7]=123;chunk.Colors[7]=0xabcdef;chunk.Flags[7]=7;
            var snapshot=new RetainedWorldSnapshot{Identity=identity,Revision=27,Chunks=new[]{chunk}};store.Save(snapshot);
            var loaded=store.Load();
            if(loaded==null||loaded.Revision!=27||loaded.Chunks.Length!=1||loaded.Chunks[0].States[7]!=123||loaded.Chunks[0].Colors[7]!=0xabcdef||loaded.Chunks[0].Flags[7]!=7||loaded.Chunks[0].Minimum!=chunk.Minimum)throw new Exception("Sparse persistence round-trip failed.");
            chunk.States[7]=456;snapshot.Revision++;store.Save(snapshot);
            if(store.Load().Chunks[0].States[7]!=456)throw new Exception("Atomic existing-cache replacement failed.");
            string binary=Directory.GetFiles(Path.Combine(scratch,"world-cache"),"*.bin").Single();byte[] broken=File.ReadAllBytes(binary);broken[80]^=1;File.WriteAllBytes(binary,broken);
            if(store.Load()!=null)throw new Exception("Corrupt persistence cache was restored.");
            store.Save(new RetainedWorldSnapshot{Identity=identity,Revision=29,Chunks=new CachedTerrainChunk[0]});
            if(store.Load().Chunks.Length!=0)throw new Exception("Observed empty cache failed to persist.");
            if(new RetainedWorldStore(scratch,new String('b',64),Console.WriteLine).Load()!=null)throw new Exception("Different world identity restored old cache.");
            File.Delete(binary);File.Delete(worldFile);Directory.Delete(Path.Combine(scratch,"world-cache"));Directory.Delete(scratch);
            Console.WriteLine("PERSISTENCE AUDIT sparse state/RGB/flags round-trip, frame identity, atomic replacement, checksum rejection, empty-world and cross-world rejection passed");
        }
        static void CacheAudit(string root, object[] palette) {
            var row=palette.Cast<Dictionary<string,object>>().First(v=>(string)v["registryName"]=="minecraft:oak_planks");
            int id=Convert.ToInt32(row["stateId"]),rgb=Convert.ToInt32(row["mapRGB"]);
            var cache=new TerrainCache();
            var origin=new VRageMath.Vector3I(-9,240,-1);
            cache.Merge(2,1,1,new[]{id,id},new[]{rgb,rgb},new[]{3,3},origin);
            if(cache.Count!=2 || cache.Chunks.Count!=2)throw new Exception("Negative chunk split failed.");
            var stable=cache.Merge(1,1,1,new[]{id},new[]{rgb},new[]{3},origin+VRageMath.Vector3I.Right);
            if(stable.Count!=0 || cache.Count!=2)throw new Exception("Partial window erased an unseen cached block.");
            cache.Merge(1,1,1,new[]{id},new[]{rgb},new[]{3},new VRageMath.Vector3I(500,240,500));
            if(cache.Count!=3)throw new Exception("Remote capture did not retain old chunks.");
            var renderer=new MinecraftRenderer(root,delegate{});var scope=BindingFlags.Instance|BindingFlags.NonPublic;
            ((ModelResolver)typeof(MinecraftRenderer).GetField("models",scope).GetValue(renderer)).ReloadPalette();
            var build=typeof(MinecraftRenderer).GetMethod("BuildChunk",scope);
            int[] states=new int[1000],colors=new int[1000],flags=new int[1000];
            var key=TerrainCache.ChunkAt(origin);cache.Halo(key,states,colors,flags);
            var mesh=(MeshBuild)build.Invoke(renderer,new object[]{1,1,1,10,10,10,states,colors,flags});
            if(mesh.Data.Indices.Count/6!=5)throw new Exception("Retained neighbour did not cull shared face.");
            var removed=cache.Remove(origin+VRageMath.Vector3I.Right);
            if(cache.Count!=2 || !removed.Contains(key))throw new Exception("Off-window removal failed to invalidate neighbour.");
            cache.Halo(key,states,colors,flags);
            mesh=(MeshBuild)build.Invoke(renderer,new object[]{1,1,1,10,10,10,states,colors,flags});
            if(mesh.Data.Indices.Count/6!=6)throw new Exception("Removed neighbour did not reveal retained block face.");
            cache.Merge(1,1,1,new[]{0},new[]{0},new[]{0},origin);
            if(cache.Count!=1 || cache.Chunks.Count!=1)throw new Exception("Observed air did not remove only known block.");
            const int side=64;int count=side*side*side;
            int[] solid=Enumerable.Repeat(id,count).ToArray(),rgbValues=Enumerable.Repeat(rgb,count).ToArray(),solidFlags=Enumerable.Repeat(3,count).ToArray();
            var watch=System.Diagnostics.Stopwatch.StartNew();cache.Merge(side,side,side,solid,rgbValues,solidFlags,VRageMath.Vector3I.Zero);
            watch.Restart();var unchanged=cache.Merge(side,side,side,solid,rgbValues,solidFlags,VRageMath.Vector3I.Zero);watch.Stop();
            if(unchanged.Count!=0)throw new Exception("Unchanged 64 cube snapshot dirtied cache.");
            Console.WriteLine("CACHE AUDIT partial/negative origins, remote retention, accepted removal, neighbour reculling and explicit air passed; stable64^3 merge CPUms="+watch.ElapsedMilliseconds);
            var remoteCache=new TerrainCache();var start=new VRageMath.Vector3I(7,240,0);
            remoteCache.Merge(2,1,1,new[]{id,id},new[]{rgb,rgb},new[]{3,3},start);
            var remote=new CachedTerrainChunk{Minimum=new VRageMath.Vector3I(0,240,0),States=Enumerable.Repeat(-1,512).ToArray(),Colors=new int[512],Flags=Enumerable.Repeat(-1,512).ToArray()};
            remote.States[6]=id;remote.Colors[6]=rgb;remote.Flags[6]=3;
            remote.States[7]=0;remote.Flags[7]=7; // Explicit zero must remove even with a stale nonair flag.
            TerrainCache.ValidateRemote(remote);
            var changed=remoteCache.Merge(8,8,8,remote.States,remote.Colors,remote.Flags,remote.Minimum);
            if(remoteCache.Count!=2||!changed.Contains(new VRageMath.Vector3I(8,240,0)))throw new Exception("Remote chunk replacement or boundary invalidation failed.");
            remoteCache.Halo(remote.Minimum,states,colors,flags);
            if(states[7+10*(1+10)]!=id||states[8+10*(1+10)]!=0||states[9+10*(1+10)]!=id)throw new Exception("Remote additions/removals or neighbour preservation failed.");
            long revision=remoteCache.Revision;
            remote.States=Enumerable.Repeat(-1,512).ToArray();remote.Flags=new int[512];
            if(remoteCache.Merge(8,8,8,remote.States,remote.Colors,remote.Flags,remote.Minimum).Count!=0||remoteCache.Revision!=revision)throw new Exception("Unknown remote cells erased cache or dirtied persistence.");
            remote.States=new int[512];remote.Flags=new int[512];
            remoteCache.Merge(8,8,8,remote.States,remote.Colors,remote.Flags,remote.Minimum);
            if(remoteCache.Count!=1||remoteCache.Chunks.ContainsKey(remote.Minimum))throw new Exception("Authoritative empty remote chunk was not removed.");
            Console.WriteLine("REMOTE CACHE AUDIT full8^3 apply, -1 preservation, explicit zero removal, adjacent retention/reculling and revision behavior passed");
        }
        static void TransparencyAudit(string root) {
            var renderer=new MinecraftRenderer(root,delegate{});var scope=BindingFlags.Instance|BindingFlags.NonPublic;
            var resolver=(ModelResolver)typeof(MinecraftRenderer).GetField("models",scope).GetValue(renderer);resolver.ReloadPalette();
            typeof(ModelResolver).GetField("nativeTransparency",scope).SetValue(resolver,true);
            var palette=(Dictionary<int,Dictionary<string,object>>)typeof(ModelResolver).GetField("palette",scope).GetValue(resolver);
            for(int level=0;level<8;level++)palette[900000+level]=new Dictionary<string,object>{{"registryName","minecraft:water"},{"properties",new Dictionary<string,object>{{"level",level.ToString()}}}};
            var high=resolver.Get(900000,0,9);var low=resolver.Get(900007,0,9);
            if(Math.Abs(high.FluidHeight-8f/9)>1e-6 || Math.Abs(low.FluidHeight-1f/9)>1e-6)throw new Exception("Liquid level geometry mismatch.");
            if(!high.Faces[0].Material.Transparent)throw new Exception("Opt-in water transparency not enabled.");
            var build=typeof(MinecraftRenderer).GetMethod("BuildChunk",scope);
            var mesh=(MeshBuild)build.Invoke(renderer,new object[]{0,0,0,2,1,1,new[]{900000,900007},new[]{0,0},new[]{9,9}});
            if(mesh.Data.Indices.Count/6!=11)throw new Exception("Liquid level side clipping mismatch.");
            var stack=(MeshBuild)build.Invoke(renderer,new object[]{0,0,0,1,2,1,new[]{900000,900000},new[]{0,0},new[]{9,9}});
            if(Math.Abs(stack.Data.AABB.Max.Y-(1+8f/9))>1e-6 || stack.Data.Indices.Count/6!=10)throw new Exception("Stacked liquid fill mismatch.");
            byte[] raw=File.ReadAllBytes(high.Faces[0].Material.ColorAlpha);
            using(var bitmap=new System.Drawing.Bitmap(Path.Combine(root,"render-assets","minecraft","textures","block","water_still.png")))
                if(raw[131]!=(int)(bitmap.GetPixel(0,0).A*.5))throw new Exception("Transparent DDS alpha mismatch.");
            Console.WriteLine("TRANSPARENCY AUDIT opt-in material/alpha and liquid levels/stack/neighbor clipping passed; native draw remains live-test dependent");
        }
        static void MeshAudit(string root,object[] palette) {
            var row=palette.Cast<Dictionary<string,object>>().First(v=>(string)v["registryName"]=="minecraft:oak_planks");
            int id=Convert.ToInt32(row["stateId"]),rgb=Convert.ToInt32(row["mapRGB"]);
            var renderer=new MinecraftRenderer(root,delegate{});
            var scope=BindingFlags.Instance|BindingFlags.NonPublic;
            ((ModelResolver)typeof(MinecraftRenderer).GetField("models",scope).GetValue(renderer)).ReloadPalette();
            var build=typeof(MinecraftRenderer).GetMethod("BuildChunk",scope);
            var hash=typeof(MinecraftRenderer).GetMethod("HashChunk",BindingFlags.Static|BindingFlags.NonPublic);
            const int side=64;int count=side*side*side;
            int[] states=Enumerable.Repeat(id,count).ToArray(),colors=Enumerable.Repeat(rgb,count).ToArray(),flags=Enumerable.Repeat(3,count).ToArray();
            var watch=System.Diagnostics.Stopwatch.StartNew();int totalFaces=0,totalVertices=0;
            for(int y=0;y<side;y+=8)for(int z=0;z<side;z+=8)for(int x=0;x<side;x+=8){
                var mesh=(MeshBuild)build.Invoke(renderer,new object[]{x,y,z,side,side,side,states,colors,flags});
                totalFaces+=mesh.Data.Indices.Count/6;totalVertices+=mesh.Data.Positions.Count;
            }
            watch.Stop();
            if(totalFaces!=6*side*side)throw new Exception("Hidden face culling audit failed: "+totalFaces);
            object hashA=hash.Invoke(null,new object[]{8,8,8,side,side,side,states,colors,flags});
            object hashB=hash.Invoke(null,new object[]{7,8,8,side,side,side,states,colors,flags});
            if(!hashA.Equals(hashB))throw new Exception("Shifted interior chunk hash is not stable.");
            var baseWorld=VRageMath.MatrixD.CreateRotationY(.47);baseWorld.Translation=new VRageMath.Vector3D(10000,20000,30000);
            var a=baseWorld;a.Translation=VRageMath.Vector3D.Transform(new VRageMath.Vector3D(-9,224,5),baseWorld);
            var b=baseWorld;b.Translation=VRageMath.Vector3D.Transform(new VRageMath.Vector3D(-8,224,5),baseWorld);
            var pa=VRageMath.Vector3D.Transform(new VRageMath.Vector3D(1,0,3),a);
            var pb=VRageMath.Vector3D.Transform(new VRageMath.Vector3D(0,0,3),b);
            if(VRageMath.Vector3D.Distance(pa,pb)>1e-8)throw new Exception("Capture origin transform audit failed.");
            Console.WriteLine("MESH AUDIT 64^3 solid cells="+count+" exposedFaces="+totalFaces+" vertices="+totalVertices+" CPUms="+watch.ElapsedMilliseconds+"; interior hash and world position stable across origin shift");
        }
    }
}
