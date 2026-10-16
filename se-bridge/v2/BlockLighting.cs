using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Game.Lights;
using VRageMath;

namespace SEMC.V2
{
    public sealed partial class MinecraftRenderer
    {
        readonly Dictionary<Vector3I,MyLight> blockLights=new Dictionary<Vector3I,MyLight>();
        void UpdateCachedBlockLights()
        {
            var active=new HashSet<Vector3I>();
            foreach(var chunk in terrain.Chunks) {
            if(active.Count>=64)break;
            for(int y=0;y<8;y++)for(int z=0;z<8;z++)for(int x=0;x<8;x++) {
                int index=x+8*(z+8*y);bool torch;
                if((chunk.Value.Flags[index]&1)==0 || !models.IsLight(chunk.Value.States[index],out torch) || active.Count>=64)continue;
                Vector3I key=chunk.Key+new Vector3I(x,y,z);
                active.Add(key);
                try {
                    MyLight light;
                    if(!blockLights.TryGetValue(key,out light)) {
                        light=MyLights.AddLight();
                        if(light==null)continue;
                        light.Start(prefix+"blocklight_"+ChunkKey(key));
                        blockLights.Add(key,light);
                    }
                    light.Position=Vector3D.Transform((Vector3D)key+new Vector3D(.5,torch?.7:.5,.5),frame);
                    light.Color=torch?new Color(255,185,100):new Color(255,222,164);
                    light.Range=torch?5f:7f;light.Intensity=torch?1.15f:1.6f;
                    light.Falloff=2f;light.LightOn=true;light.ReflectorOn=false;
                    light.CastShadows=false;light.GlareOn=false;
                    light.UpdateLight();
                }
                catch(Exception error){log("RENDER block light: "+error.Message);}
            }
            }
            foreach(Vector3I key in blockLights.Keys.Where(k=>!active.Contains(k)).ToArray()) {
                try{MyLights.RemoveLight(blockLights[key]);}catch(Exception error){log("RENDER remove light: "+error.Message);}
                blockLights.Remove(key);
            }
        }
        void ClearBlockLights()
        {
            foreach(var light in blockLights.Values)try{MyLights.RemoveLight(light);}catch(Exception error){log("RENDER clear light: "+error.Message);}
            blockLights.Clear();
        }
    }
}
