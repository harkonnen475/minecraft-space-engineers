using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using VRageMath;
namespace SEMC.V2 {
    static class RemoteChunksAudit {
        const int Length=4096+256*8192;
        static readonly string World=Guid.NewGuid().ToString("D"), Fingerprint=new string('a',64);
        static void Main(string[] args) {
            AppDomain.CurrentDomain.AssemblyResolve+=delegate(object s,ResolveEventArgs e){string p=Path.Combine(@"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers\Bin64",new AssemblyName(e.Name).Name+".dll");return File.Exists(p)?Assembly.LoadFrom(p):null;}; Run(args);
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Run(string[] args) {
            string lab=@"C:\Users\konra\Documents\SEMC-Lab";
            if(args.Length>0&&args[0]=="live") {
                using(var live=new RemoteChunks(lab,Console.WriteLine)) {
                    var data=live.Poll(); Console.WriteLine("LIVE READ ONLY chunks="+data.Count+" sequence="+live.LastSequence+" palette="+live.RequiredPaletteRevision+" nonair="+data.Sum(c=>c.Flags.Count(f=>(f&1)!=0)));
                }
                return;
            }
            string root=Path.Combine(lab,"research-se","remote-audit-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string metadata=Path.Combine(root,"world-v2.json"),ring=Path.Combine(root,"chunks-v2.bin"),interests=Path.Combine(root,"retained-chunks-v2.json");
            File.WriteAllText(metadata,new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"worldId",World},{"dimension","minecraft:overworld"},{"paletteFingerprint",Fingerprint}}));
            using(var f=new FileStream(ring,FileMode.Create,FileAccess.ReadWrite,FileShare.ReadWrite|FileShare.Delete)) {
                f.SetLength(Length); Header(f,1,100,2); Slot(f,1,100,new Vector3I(0,240,0),2);
                using(var reader=new RemoteChunks(root,Console.WriteLine)) {
                    var first=reader.Poll(); Check(first.Count==1&&reader.LastSequence==1&&reader.RequiredPaletteRevision==9&&first[0].States[1]==-1,"stable mixed-known slot");
                    Header(f,2,100,2);Slot(f,2,100,new Vector3I(8,240,0),3);
                    Check(reader.Poll().Count==0&&reader.LastSequence==1,"odd slot seqlock rejected");
                    Slot(f,2,100,new Vector3I(8,240,0),4);Header(f,2,100,3);
                    Check(reader.Poll().Count==0&&reader.LastSequence==1,"odd header seqlock rejected");
                    Header(f,2,100,4);
                    var active=new Snapshot{Flags=3,Timestamp=BridgeIO.Now(),X=8,Y=240,Z=0,SizeX=8,SizeY=8,SizeZ=8};
                    Check(reader.Poll(active).Count==0&&reader.LastSequence==2,"fresh main-capture overlap consumed without apply");
                    Header(f,3,100,6);Slot(f,3,999,new Vector3I(16,240,0),2);
                    Check(reader.Poll().Count==0&&reader.LastSequence==2,"slot epoch mismatch rejected");
                    Slot(f,3,100,new Vector3I(16,240,0),4);
                    Check(reader.Poll().Count==1&&reader.LastSequence==3,"same slot succeeds once matching epoch committed");
                    var renderer=new MinecraftRenderer(lab,delegate{}); var terrain=(TerrainCache)typeof(MinecraftRenderer).GetField("terrain",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(renderer);
                    terrain.Merge(1,1,1,new[]{2},new[]{123},new[]{3},new Vector3I(-1,240,1));
                    reader.UpdateInterests(renderer);long initial=Revision(interests);
                    Check(reader.InterestCount==7,"retained chunk plus six neighbours subscribed");
                    terrain.Remove(new Vector3I(-1,240,1));
                    typeof(RemoteChunks).GetField("nextInterests",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(reader,0L);
                    reader.UpdateInterests(renderer);
                    Check(reader.InterestCount==7&&Revision(interests)==initial,"temporarily empty subscribed origins retained without redundant publication");
                    Header(f,300,100,8);for(long i=45;i<=300;i++)Slot(f,i,100,new Vector3I((int)i*8,240,0),2);
                    var overrun=reader.Poll(null,999);
                    Check(overrun.Count==32&&reader.LastSequence==76&&reader.Overruns==1,"overrun recovery and 32-slot cap");
                    reader.UpdateInterests(renderer);Check(Revision(interests)>initial,"overrun forces interests revision resend");
                    Header(f,1,200,10);Slot(f,1,200,new Vector3I(0,248,0),2);
                    Check(reader.Poll().Count==1&&reader.Epoch==200&&reader.LastSequence==1,"new epoch resets sequence cursor");
                    f.Position=48;f.Write(BitConverter.GetBytes(BridgeIO.Now()+60000),0,8);f.Flush();
                    Check(reader.Poll().Count==0&&reader.LastSequence==1,"future header timestamp rejected without consuming slots");
                    typeof(RemoteChunks).GetField("nextOpen",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(reader,0L);
                    Header(f,2,200,12);Slot(f,2,200,new Vector3I(8,240,0),2);
                    active.Timestamp=BridgeIO.Now()+60000;
                    Check(reader.Poll(active).Count==1&&reader.LastSequence==2,"future main snapshot cannot suppress valid remote data");
                    Header(f,3,200,14);Slot(f,3,200,new Vector3I(8,240,0),2);
                    long extra=4096+2*8192+128+4*12;
                    f.Position=extra;f.Write(BitConverter.GetBytes(7),0,4);f.Position=extra+8;f.Write(BitConverter.GetBytes(3),0,4);f.Flush();
                    active.Timestamp=BridgeIO.Now();active.SizeX=4;
                    var partial=reader.Poll(active);
                    Check(partial.Count==1&&partial[0].States[0]==-1&&partial[0].States[4]==7,"partial overlap masks only active cells and keeps remote edge changes");
                    f.Position=64;f.Write(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("D")),0,36);f.Flush();
                    Check(reader.Poll().Count==0,"mismatched world identity rejected");
                }
            }
            File.Delete(interests);File.Delete(metadata);File.Delete(ring);Directory.Delete(root);
            Console.WriteLine("REMOTE RING AUDIT stable/odd seqlocks, exact sequence+epoch+world, unknown cells, active overlap, 32-slot cap, overrun resend and epoch restart passed");
        }
        static long Revision(string file) {return Convert.ToInt64(((Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(file)))["revision"]);}
        static void Check(bool value,string name) {if(!value)throw new Exception("Remote ring audit failed: "+name);}
        static void Header(FileStream stream,long sequence,long epoch,int lockValue) {
            byte[] h=new byte[4096];using(var m=new MemoryStream(h))using(var w=new BinaryWriter(m)) {
                w.Write(0x324B4353);w.Write(2);w.Write(lockValue);w.Write(256);w.Write(8192);w.Write(8);w.Write(12);w.Write(4096);w.Write(sequence);w.Write(epoch);w.Write(BridgeIO.Now());
                m.Position=64;w.Write(Encoding.UTF8.GetBytes(World));m.Position=104;w.Write(Encoding.UTF8.GetBytes("minecraft:overworld"));m.Position=168;w.Write(Encoding.UTF8.GetBytes(Fingerprint));
            }stream.Position=0;stream.Write(h,0,h.Length);stream.Flush();
        }
        static void Slot(FileStream stream,long sequence,long epoch,Vector3I minimum,int lockValue) {
            byte[] bytes=new byte[8192];using(var m=new MemoryStream(bytes))using(var w=new BinaryWriter(m)) {
                w.Write(lockValue);m.Position=8;w.Write(sequence);w.Write(BridgeIO.Now());w.Write(minimum.X);w.Write(minimum.Y);w.Write(minimum.Z);w.Write(512);w.Write(9);w.Write(1);w.Write(123L);w.Write(epoch);m.Position=128;
                for(int i=0;i<512;i++){w.Write(i==0?2:-1);w.Write(123);w.Write(i==0?3:0);}
            }stream.Position=4096+((sequence-1)%256)*8192;stream.Write(bytes,0,bytes.Length);stream.Flush();
        }
    }
}
