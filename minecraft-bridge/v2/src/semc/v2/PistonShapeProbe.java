package semc.v2;

import com.google.gson.JsonObject;
import java.lang.instrument.Instrumentation;
import java.net.URL;
import java.net.URLClassLoader;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import net.minecraft.client.Minecraft;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.piston.PistonBaseBlock;
import net.minecraft.world.level.block.piston.PistonHeadBlock;
import net.minecraft.world.level.block.piston.PistonMovingBlockEntity;
import net.minecraft.world.phys.shapes.VoxelShape;

/** Read-only diagnostic: tests detached objects that are never inserted into a world. */
public final class PistonShapeProbe {
    public static void agentmain(String arguments,Instrumentation instrumentation)throws Exception{
        String[] values=arguments.split("\\|",2);Path output=Path.of(values[0]).resolve("piston-shape-probe-v2.json");
        try(URLClassLoader loader=new URLClassLoader(new URL[]{Path.of(values[1]).toUri().toURL()},Minecraft.class.getClassLoader()){
            @Override protected Class<?> loadClass(String name,boolean resolve)throws ClassNotFoundException{
                synchronized(getClassLoadingLock(name)){
                    Class<?> type=findLoadedClass(name);
                    if(type==null&&name.startsWith("semc.v2.runtime."))type=findClass(name);
                    if(type==null)type=super.loadClass(name,false);if(resolve)resolveClass(type);return type;
                }
            }
        }){
            Class<?> helper=Class.forName("semc.v2.runtime.DynamicScene",true,loader);
            var method=helper.getDeclaredMethod("addCollisionParts",JsonObject.class,net.minecraft.server.level.ServerLevel.class,PistonMovingBlockEntity.class,VoxelShape.class);
            method.setAccessible(true);
            var server=Minecraft.getInstance().getSingleplayerServer();if(server==null)throw new IllegalStateException("No native server");
            CompletableFuture<JsonObject> completed=new CompletableFuture<>();
            server.execute(()->{
                try{
                    var level=server.overworld();BlockPos pos=new BlockPos(0,250,0);
                    var progress=PistonMovingBlockEntity.class.getDeclaredField("progress");
                    var previous=PistonMovingBlockEntity.class.getDeclaredField("progressO");
                    progress.setAccessible(true);previous.setAccessible(true);
                    int checked=0,verified=0,stationaryParts=0;
                    for(Direction direction:Direction.values())for(boolean extending:new boolean[]{false,true})for(boolean source:new boolean[]{false,true})for(float phase:new float[]{0,.25f,.5f,.75f,1}){
                        var moved=source?(extending?Blocks.PISTON_HEAD.defaultBlockState().setValue(PistonHeadBlock.FACING,direction):Blocks.STICKY_PISTON.defaultBlockState().setValue(PistonBaseBlock.FACING,direction)):Blocks.STONE.defaultBlockState();
                        // Reflection only sets fields on this detached temporary object.
                        var detached=new PistonMovingBlockEntity(pos,Blocks.MOVING_PISTON.defaultBlockState(),moved,direction,extending,source);
                        detached.setLevel(level);progress.setFloat(detached,phase);previous.setFloat(detached,Math.max(0,phase-.5f));
                        JsonObject result=new JsonObject();method.invoke(null,result,level,detached,detached.getCollisionShape(level,pos));checked++;
                        if(result.get("collisionPartsVerified").getAsBoolean())verified++;
                        for(var part:result.getAsJsonArray("collisionParts"))if(!part.getAsJsonObject().get("moving").getAsBoolean())stationaryParts++;
                    }
                    JsonObject result=new JsonObject();result.addProperty("cases",checked);result.addProperty("verified",verified);result.addProperty("stationaryBaseCases",stationaryParts);
                    result.addProperty("worldEdits",0);result.addProperty("serverTick",server.getTickCount());completed.complete(result);
                }catch(Throwable failure){completed.completeExceptionally(failure);}
            });
            Files.writeString(output,completed.get(10,TimeUnit.SECONDS).toString());
        }
    }
}
