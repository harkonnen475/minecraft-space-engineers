using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using VRageMath;
namespace SEMC.V2 {
    static class GenerateThumbnails {
        const int Size=128;
        static readonly Dictionary<string,uint[]> cache=new Dictionary<string,uint[]>();
        static void Main(){
            AppDomain.CurrentDomain.AssemblyResolve+=delegate(object s,ResolveEventArgs e){string p=Path.Combine(@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;};Run();
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Run(){
            string root=@"C:\Users\konra\Documents\SEMC-Lab";
            var resolver=new ModelResolver(root,"thumbnail_",delegate{});resolver.ReloadPalette();
            var json=new JavaScriptSerializer{MaxJsonLength=64000000};
            var doc=(Dictionary<string,object>)json.DeserializeObject(File.ReadAllText(Path.Combine(root,"palette-v2.json")));
            var states=((object[])doc["states"]).Cast<Dictionary<string,object>>().ToArray();
            foreach(string name in new[]{"oak_stairs","oak_slab","stone_bricks","oak_fence","ladder","grass_block","sand","chest"}){
                var row=states.Where(s=>(string)s["registryName"]=="minecraft:"+name).OrderByDescending(Score).First();
                var model=resolver.Get(Convert.ToInt32(row["stateId"]),Convert.ToInt32(row["mapRGB"]),Convert.ToInt32(row["flags"]));
                uint[] pixels=new uint[Size*Size];float[] depth=Enumerable.Repeat(float.MinValue,pixels.Length).ToArray();
                foreach(var face in model.Faces){
                    if(Vector3.Dot(face.Normal,new Vector3(1,.8f,1))<.01f)continue;
                    var p=face.Points.Select(Project).ToArray();
                    Draw(pixels,depth,p[0],p[1],p[2],face.UV[0],face.UV[1],face.UV[2],face);
                    Draw(pixels,depth,p[0],p[2],p[3],face.UV[0],face.UV[2],face.UV[3],face);
                }
                Write(Path.Combine(root,"render-assets","icons",name+".dds"),pixels);
                Console.WriteLine(name+" modelFaces="+model.Faces.Count+" native DDS ready");
            }
        }
        static int Score(Dictionary<string,object> row){
            var p=(Dictionary<string,object>)row["properties"];int score=0;
            foreach(var pair in p){string v=Convert.ToString(pair.Value);if(v=="false"||v=="bottom"||v=="straight"||v=="south"||v=="single")score++;}return score;
        }
        static Vector3 Project(Vector3 p){p-=new Vector3(.5f);return new Vector3(64+(p.X-p.Z)*.7071068f*83,66+(p.X+p.Z)*.3535534f*83-p.Y*.8660254f*83,(p.X+p.Z)*.6123724f+p.Y*.5f);}
        static float Edge(Vector3 a,Vector3 b,float x,float y){return (x-a.X)*(b.Y-a.Y)-(y-a.Y)*(b.X-a.X);}
        static uint[] Texture(string path){uint[] saved;if(cache.TryGetValue(path,out saved))return saved;byte[] raw=File.ReadAllBytes(path);saved=new uint[Size*Size];Buffer.BlockCopy(raw,128,saved,0,saved.Length*4);cache[path]=saved;return saved;}
        static void Draw(uint[] pixels,float[] depths,Vector3 a,Vector3 b,Vector3 c,Vector2 ua,Vector2 ub,Vector2 uc,FaceTemplate face){
            float area=Edge(a,b,c.X,c.Y);if(Math.Abs(area)<.0001)return;
            int minX=Math.Max(0,(int)Math.Floor(Math.Min(a.X,Math.Min(b.X,c.X)))),maxX=Math.Min(Size-1,(int)Math.Ceiling(Math.Max(a.X,Math.Max(b.X,c.X))));
            int minY=Math.Max(0,(int)Math.Floor(Math.Min(a.Y,Math.Min(b.Y,c.Y)))),maxY=Math.Min(Size-1,(int)Math.Ceiling(Math.Max(a.Y,Math.Max(b.Y,c.Y))));
            uint[] color=Texture(face.Material.ColorMetal),alpha=face.Material.Cutout?Texture(face.Material.Alpha):null;
            float shade=.6f+.4f*Math.Max(0,Vector3.Dot(face.Normal,Vector3.Normalize(new Vector3(.4f,1,.7f))));
            for(int y=minY;y<=maxY;y++)for(int x=minX;x<=maxX;x++){
                float wa=Edge(b,c,x+.5f,y+.5f)/area,wb=Edge(c,a,x+.5f,y+.5f)/area,wc=1-wa-wb;if(wa<0||wb<0||wc<0)continue;
                float d=a.Z*wa+b.Z*wb+c.Z*wc;int dest=x+Size*y;if(d<depths[dest])continue;
                Vector2 uv=ua*wa+ub*wb+uc*wc;int tx=Math.Max(0,Math.Min(127,(int)(uv.X*128))),ty=Math.Max(0,Math.Min(127,(int)(uv.Y*128))),ti=tx+128*ty;
                if(alpha!=null&&(alpha[ti]&255)<128)continue;
                uint v=color[ti];int r=(int)((v&255)*shade),g=(int)(((v>>8)&255)*shade),bl=(int)(((v>>16)&255)*shade);
                pixels[dest]=(uint)(r|(g<<8)|(bl<<16)|unchecked((int)0xff000000));depths[dest]=d;
            }
        }
        static void Write(string path,uint[] pixels){using(var f=File.Create(path))using(var w=new BinaryWriter(f)){w.Write(0x20534444);w.Write(124);w.Write(0x100F);w.Write(Size);w.Write(Size);w.Write(Size*4);w.Write(0);w.Write(1);for(int i=0;i<11;i++)w.Write(0);w.Write(32);w.Write(0x41);w.Write(0);w.Write(32);w.Write(0xff);w.Write(0xff00);w.Write(0xff0000);w.Write(unchecked((int)0xff000000));w.Write(0x1000);for(int i=0;i<4;i++)w.Write(0);foreach(uint p in pixels)w.Write(p);}}
    }
}
