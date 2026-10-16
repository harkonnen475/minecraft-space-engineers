package semc.v2.runtime;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.item.FallingBlockEntity;
import net.minecraft.world.entity.item.PrimedTnt;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.piston.PistonBaseBlock;
import net.minecraft.world.level.block.piston.PistonHeadBlock;
import net.minecraft.world.level.block.piston.PistonMovingBlockEntity;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.block.state.properties.PistonType;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.shapes.BooleanOp;
import net.minecraft.world.phys.shapes.Shapes;
import net.minecraft.world.phys.shapes.VoxelShape;

/** Native simulation observations, sampled exclusively on its owning server thread. */
final class DynamicScene {
    static void export(Backend backend,ServerPlayer player){
        ServerLevel level=player.level();JsonObject document=new JsonObject();
        document.addProperty("version",2);document.addProperty("timestamp",System.currentTimeMillis());
        document.addProperty("worldId",backend.worldId);document.addProperty("dimension",level.dimension().identifier().toString());
        document.addProperty("serverTick",level.getServer().getTickCount());document.addProperty("sampleIntervalMillis",50);
        JsonArray entities=new JsonArray();
        for(var entity:level.getAllEntities()){
            if(!entity.isAlive()||!(entity instanceof PrimedTnt)&&!(entity instanceof FallingBlockEntity))continue;
            if(!near(backend,entity.getX(),entity.getY(),entity.getZ()))continue;
            BlockState state=entity instanceof PrimedTnt tnt?tnt.getBlockState():((FallingBlockEntity)entity).getBlockState();
            backend.ensureState(state,level,entity.blockPosition());
            JsonObject entry=new JsonObject();entry.addProperty("uuid",entity.getUUID().toString());
            entry.addProperty("type",entity instanceof PrimedTnt?"minecraft:tnt":"minecraft:falling_block");
            entry.addProperty("x",entity.getX());entry.addProperty("y",entity.getY());entry.addProperty("z",entity.getZ());
            entry.addProperty("stateId",Block.getId(state));entry.addProperty("ageTicks",entity.tickCount);
            entry.add("velocity",vector(entity.getDeltaMovement().x,entity.getDeltaMovement().y,entity.getDeltaMovement().z));
            entry.add("boundingBox",box(entity.getBoundingBox()));
            if(entity instanceof PrimedTnt tnt)entry.addProperty("fuseTicks",tnt.getFuse());
            if(entity instanceof FallingBlockEntity falling){
                entry.addProperty("fallTicks",falling.time);entry.add("startPos",vector(falling.getStartPos().getX(),falling.getStartPos().getY(),falling.getStartPos().getZ()));
                JsonArray collisions=new JsonArray();collisions.add(box(falling.getBoundingBox()));entry.add("collisionBoxes",collisions);
            }
            entities.add(entry);
        }
        JsonArray movingBlocks=new JsonArray();
        int minChunkX=Math.floorDiv(backend.originX-64,16),maxChunkX=Math.floorDiv(backend.originX+backend.SIZE+64,16);
        int minChunkZ=Math.floorDiv(backend.originZ-64,16),maxChunkZ=Math.floorDiv(backend.originZ+backend.SIZE+64,16);
        for(int z=minChunkZ;z<=maxChunkZ;z++)for(int x=minChunkX;x<=maxChunkX;x++){
            // getChunkNow never forces disk loading/generation or native simulation tickets.
            var chunk=level.getChunkSource().getChunkNow(x,z);if(chunk==null)continue;
            for(var blockEntity:chunk.getBlockEntities().values()){
                if(!(blockEntity instanceof PistonMovingBlockEntity piston)||piston.isRemoved())continue;
                BlockPos pos=piston.getBlockPos();if(!near(backend,pos.getX(),pos.getY(),pos.getZ()))continue;
                movingBlocks.add(piston(backend,level,piston));
            }
        }
        backend.flushPalette();if(backend.paletteDirty)return;
        document.addProperty("paletteRevision",backend.paletteRevision);document.add("entities",entities);document.add("movingBlocks",movingBlocks);
        backend.writeJson("entities-v2.json",document);
    }

    static JsonObject piston(Backend backend,ServerLevel level,PistonMovingBlockEntity piston){
        BlockPos pos=piston.getBlockPos();BlockState moved=piston.getMovedState();backend.ensureState(moved,level,pos);
        BlockState placeholder=level.getBlockState(pos);backend.ensureState(placeholder,level,pos);
        float progress=piston.getProgress(1),previousProgress=piston.getProgress(0);
        double ox=piston.getXOff(1),oy=piston.getYOff(1),oz=piston.getZOff(1);
        double px=piston.getXOff(0),py=piston.getYOff(0),pz=piston.getZOff(0);
        JsonObject entry=new JsonObject();entry.addProperty("type","minecraft:moving_piston");
        entry.addProperty("uuid","piston:"+backend.worldId+":"+level.dimension().identifier()+":"+pos.getX()+":"+pos.getY()+":"+pos.getZ());
        entry.addProperty("x",pos.getX());entry.addProperty("y",pos.getY());entry.addProperty("z",pos.getZ());
        entry.addProperty("placeholderStateId",Block.getId(placeholder));
        entry.addProperty("movedStateId",Block.getId(moved));entry.addProperty("progress",progress);entry.addProperty("previousProgress",previousProgress);
        entry.addProperty("extending",piston.isExtending());entry.addProperty("source",piston.isSourcePiston());
        entry.addProperty("direction",piston.getDirection().get3DDataValue());entry.addProperty("movementDirection",piston.getMovementDirection().get3DDataValue());
        entry.addProperty("lastTicked",piston.getLastTicked());entry.add("offset",vector(ox,oy,oz));entry.add("previousOffset",vector(px,py,pz));
        entry.add("velocity",vector(ox-px,oy-py,oz-pz));
        JsonArray collisions=new JsonArray();AABB bounds=null;
        VoxelShape nativeCollision=piston.getCollisionShape(level,pos);
        for(AABB relative:nativeCollision.toAabbs()){
            AABB absolute=relative.move(pos);collisions.add(box(absolute));bounds=bounds==null?absolute:bounds.minmax(absolute);
        }
        entry.add("collisionBoxes",collisions);if(bounds!=null)entry.add("boundingBox",box(bounds));
        addCollisionParts(entry,level,piston,nativeCollision);
        JsonArray parts=new JsonArray();BlockState rendered=moved;
        // These state choices match installed26.3 PistonHeadRenderer.extractRenderState.
        // Collision geometry above comes directly from the native block entity, not this render adaptation.
        if(moved.is(Blocks.PISTON_HEAD))rendered=moved.setValue(PistonHeadBlock.SHORT,progress<=0.5f);
        else if(piston.isSourcePiston()&&!piston.isExtending()){
            rendered=Blocks.PISTON_HEAD.defaultBlockState().setValue(PistonHeadBlock.TYPE,moved.is(Blocks.STICKY_PISTON)?PistonType.STICKY:PistonType.DEFAULT)
                .setValue(PistonHeadBlock.FACING,moved.getValue(PistonBaseBlock.FACING)).setValue(PistonHeadBlock.SHORT,progress>=0.5f);
            parts.add(part(backend,level,pos,moved.setValue(PistonBaseBlock.EXTENDED,true),pos.getX(),pos.getY(),pos.getZ(),false));
        }
        parts.add(part(backend,level,pos,rendered,pos.getX()+ox,pos.getY()+oy,pos.getZ()+oz,true));entry.add("renderParts",parts);
        return entry;
    }

    static void addCollisionParts(JsonObject entry,ServerLevel level,PistonMovingBlockEntity piston,VoxelShape nativeCollision){
        BlockPos pos=piston.getBlockPos();BlockState moved=piston.getMovedState();
        VoxelShape stationary=Shapes.empty();
        if(!piston.isExtending()&&piston.isSourcePiston()&&moved.getBlock() instanceof PistonBaseBlock)
            stationary=moved.setValue(PistonBaseBlock.EXTENDED,true).getCollisionShape(level,pos);
        VoxelShape current=movingCollision(level,piston,piston.getProgress(1)).move(piston.getXOff(1),piston.getYOff(1),piston.getZOff(1));
        VoxelShape previous=movingCollision(level,piston,piston.getProgress(0)).move(piston.getXOff(0),piston.getYOff(0),piston.getZOff(0));
        // Verify this semantic decomposition against native geometry before exposing it.
        if(Shapes.joinIsNotEmpty(Shapes.or(stationary,current),nativeCollision,BooleanOp.NOT_SAME)){
            entry.addProperty("collisionPartsVerified",false);return;
        }
        JsonArray parts=new JsonArray();
        if(!stationary.isEmpty())parts.add(collisionPart(pos,stationary,stationary,false,0,0,0));
        if(!current.isEmpty())parts.add(collisionPart(pos,current,previous,true,piston.getXOff(1)-piston.getXOff(0),piston.getYOff(1)-piston.getYOff(0),piston.getZOff(1)-piston.getZOff(0)));
        entry.addProperty("collisionPartsVerified",true);entry.add("collisionParts",parts);
    }
    static VoxelShape movingCollision(ServerLevel level,PistonMovingBlockEntity piston,float progress){
        BlockState state=piston.getMovedState();
        if(piston.isSourcePiston())state=Blocks.PISTON_HEAD.defaultBlockState().setValue(PistonHeadBlock.FACING,piston.getDirection())
            .setValue(PistonHeadBlock.SHORT,piston.isExtending()!=((1-progress)<0.25f));
        return state.getCollisionShape(level,piston.getBlockPos());
    }
    static JsonObject collisionPart(BlockPos pos,VoxelShape current,VoxelShape previous,boolean moving,double vx,double vy,double vz){
        JsonObject part=new JsonObject();part.addProperty("moving",moving);part.add("velocity",vector(vx,vy,vz));
        JsonArray boxes=new JsonArray(),oldBoxes=new JsonArray();
        for(AABB box:current.toAabbs())boxes.add(box(box.move(pos)));
        for(AABB box:previous.toAabbs())oldBoxes.add(box(box.move(pos)));
        part.add("collisionBoxes",boxes);part.add("previousCollisionBoxes",oldBoxes);return part;
    }

    static JsonObject part(Backend backend,ServerLevel level,BlockPos pos,BlockState state,double x,double y,double z,boolean moving){
        backend.ensureState(state,level,pos);JsonObject part=new JsonObject();part.addProperty("stateId",Block.getId(state));
        part.addProperty("x",x);part.addProperty("y",y);part.addProperty("z",z);part.addProperty("moving",moving);return part;
    }
    static boolean near(Backend backend,double x,double y,double z){
        return x>=backend.originX-64&&x<=backend.originX+backend.SIZE+64&&y>=backend.originY-64&&y<=backend.originY+backend.SIZE+64&&z>=backend.originZ-64&&z<=backend.originZ+backend.SIZE+64;
    }
    static JsonArray vector(double x,double y,double z){JsonArray values=new JsonArray();values.add(x);values.add(y);values.add(z);return values;}
    static JsonArray box(AABB b){JsonArray values=new JsonArray();values.add(b.minX);values.add(b.minY);values.add(b.minZ);values.add(b.maxX);values.add(b.maxY);values.add(b.maxZ);return values;}
}
