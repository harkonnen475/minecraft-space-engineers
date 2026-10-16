package semc.v2.runtime;

import com.google.gson.JsonObject;
import java.lang.invoke.MethodHandles;
import java.lang.invoke.VarHandle;
import java.nio.ByteOrder;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardOpenOption;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashSet;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;

/** Single native server-thread writer; this transport never changes native blocks. */
final class ChunkStream implements AutoCloseable {
    static final int HEADER=4096,SLOTS=256,SLOT_BYTES=8192,LENGTH=HEADER+SLOTS*SLOT_BYTES;
    static final VarHandle INTS=MethodHandles.byteBufferViewVarHandle(int[].class,ByteOrder.LITTLE_ENDIAN);
    final Backend backend;
    final FileChannel channel;
    final MappedByteBuffer memory;
    final ArrayList<BlockPos> interests=new ArrayList<>();
    final Set<BlockPos> interestSet=new HashSet<>(),prioritySet=new HashSet<>();
    final ArrayDeque<BlockPos> priority=new ArrayDeque<>();
    final Map<BlockPos,int[]> previous=new HashMap<>();
    String worldId="",dimension="";
    long epoch,latest,revision=Long.MIN_VALUE,nextRead,nextHeader,totalSamples,totalChanges;
    int cursor,headerSequence;
    double sampleMillis;

    ChunkStream(Backend backend)throws Exception{
        this.backend=backend;
        channel=FileChannel.open(backend.root.resolve("chunks-v2.bin"),StandardOpenOption.READ,StandardOpenOption.WRITE,StandardOpenOption.CREATE);
        if(channel.size()<LENGTH){channel.position(LENGTH-1);channel.write(java.nio.ByteBuffer.wrap(new byte[]{0}));}
        memory=channel.map(FileChannel.MapMode.READ_WRITE,0,LENGTH);memory.order(ByteOrder.LITTLE_ENDIAN);
        headerSequence=memory.getInt(8)&~1;
    }

    void tick(ServerLevel level,long now){
        String currentDimension=level.dimension().identifier().toString();
        if(!worldId.equals(backend.worldId)||!dimension.equals(currentDimension)){
            worldId=backend.worldId;dimension=currentDimension;epoch=UUID.randomUUID().getLeastSignificantBits()&Long.MAX_VALUE;
            if(epoch==0)epoch=1;latest=0;revision=Long.MIN_VALUE;cursor=0;interests.clear();interestSet.clear();previous.clear();priority.clear();prioritySet.clear();nextRead=0;
            publishHeader(now);
        }
        if(now>=nextRead){nextRead=now+500;readInterests(level);}
        if(!interests.isEmpty()){
            long started=System.nanoTime();int count=Math.min(4,interests.size());Set<BlockPos> sampled=new HashSet<>();
            while(sampled.size()<count){
                BlockPos origin=priority.pollFirst();
                if(origin!=null){prioritySet.remove(origin);if(!interestSet.contains(origin))continue;}
                else{if(cursor>=interests.size())cursor=0;origin=interests.get(cursor++);}
                if(!sampled.add(origin))continue;
                int[] cells=sample(level,origin);totalSamples++;
                if(Arrays.equals(cells,previous.get(origin)))continue;
                backend.flushPalette();
                if(backend.paletteDirty)continue; // Descriptors must be readable before their state IDs.
                publish(level,origin,cells,now);previous.put(origin,cells);totalChanges++;
            }
            sampleMillis=(System.nanoTime()-started)/1000000.0;
        }else sampleMillis=0;
        if(now>=nextHeader)publishHeader(now);
    }

    void readInterests(ServerLevel level){
        Path file=backend.root.resolve("retained-chunks-v2.json");
        if(!Files.exists(file))return;
        try{
            JsonObject document=com.google.gson.JsonParser.parseString(Files.readString(file)).getAsJsonObject();
            if(document.get("version").getAsInt()!=2||!worldId.equals(document.get("worldId").getAsString())
                ||!dimension.equals(document.get("dimension").getAsString())
                ||!backend.paletteFingerprint.equals(document.get("paletteFingerprint").getAsString()))return;
            long incomingRevision=document.get("revision").getAsLong();
            if(incomingRevision==revision)return;
            var origins=document.getAsJsonArray("origins");
            if(origins.size()>8192)throw new IllegalArgumentException("At most8192 retained chunk interests");
            Set<BlockPos> accepted=new LinkedHashSet<>();
            for(var value:origins){
                var coordinates=value.getAsJsonArray();if(coordinates.size()!=3)throw new IllegalArgumentException("Chunk origin must have three coordinates");
                int x=coordinates.get(0).getAsInt(),y=coordinates.get(1).getAsInt(),z=coordinates.get(2).getAsInt();
                if((x&7)!=0||(y&7)!=0||(z&7)!=0||Math.abs((long)x)>29_999_896L||Math.abs((long)z)>29_999_896L||y<level.getMinY()||y+8>level.getMaxY())
                    throw new IllegalArgumentException("Retained chunk origin outside aligned native bounds");
                accepted.add(new BlockPos(x,y,z));
            }
            interests.clear();interests.addAll(accepted);interestSet.clear();interestSet.addAll(accepted);
            previous.clear();priority.clear();prioritySet.clear();cursor=0;revision=incomingRevision;
            backend.log("REMOTE interests="+interests.size()+" revision="+revision+"; full refresh requested");
        }catch(Exception failure){backend.fault(failure);}
    }

    int[] sample(ServerLevel level,BlockPos origin){
        int[] cells=new int[512*3];BlockPos.MutableBlockPos position=new BlockPos.MutableBlockPos();int i=0;
        for(int y=0;y<8;y++)for(int z=0;z<8;z++)for(int x=0;x<8;x++){
            position.set(origin.getX()+x,origin.getY()+y,origin.getZ()+z);
            if(!level.hasChunkAt(position)){cells[i]=-1;i+=3;continue;}
            var state=level.getBlockState(position);
            if(!state.isAir()&&!(state.is(Blocks.BARRIER)&&backend.terrainOwned.contains(position.asLong()))){
                backend.ensureState(state,level,position);int id=Block.getId(state);
                cells[i]=id;cells[i+1]=backend.stateColors.get(id);cells[i+2]=backend.stateFlags.get(id);
            }
            i+=3;
        }
        return cells;
    }

    void prioritize(BlockPos cell){
        for(int dy=-1;dy<=1;dy++)for(int dz=-1;dz<=1;dz++)for(int dx=-1;dx<=1;dx++){
            BlockPos origin=new BlockPos(Math.floorDiv(cell.getX()+dx,8)*8,Math.floorDiv(cell.getY()+dy,8)*8,Math.floorDiv(cell.getZ()+dz,8)*8);
            if(interestSet.contains(origin)&&prioritySet.add(origin))priority.addLast(origin);
        }
    }

    void publish(ServerLevel level,BlockPos origin,int[] cells,long now){
        long id=latest+1;int offset=HEADER+(int)((id-1)%SLOTS)*SLOT_BYTES;
        int lock=(memory.getInt(offset)&~1)+1;INTS.setVolatile(memory,offset,lock);
        memory.putLong(offset+8,id);memory.putLong(offset+16,now);
        memory.putInt(offset+24,origin.getX());memory.putInt(offset+28,origin.getY());memory.putInt(offset+32,origin.getZ());
        memory.putInt(offset+36,512);memory.putInt(offset+40,backend.paletteRevision);memory.putInt(offset+44,1);
        memory.putLong(offset+48,level.getServer().getTickCount());memory.putLong(offset+56,epoch);
        int record=offset+128;for(int value:cells){memory.putInt(record,value);record+=4;}
        INTS.setRelease(memory,offset,lock+1);latest=id;publishHeader(now);
    }

    void publishHeader(long now){
        INTS.setVolatile(memory,8,++headerSequence);
        memory.putInt(0,0x324B4353);memory.putInt(4,2);memory.putInt(12,SLOTS);memory.putInt(16,SLOT_BYTES);
        memory.putInt(20,8);memory.putInt(24,12);memory.putInt(28,HEADER);
        memory.putLong(32,latest);memory.putLong(40,epoch);memory.putLong(48,now);
        putText(64,40,worldId);putText(104,64,dimension);putText(168,64,backend.paletteFingerprint);
        INTS.setRelease(memory,8,++headerSequence);nextHeader=now+500;
    }
    void putText(int offset,int length,String value){
        byte[] encoded=value.getBytes(StandardCharsets.UTF_8);
        if(encoded.length>length)throw new IllegalArgumentException("Ring identity field exceeds fixed buffer");
        for(int i=0;i<length;i++)memory.put(offset+i,i<encoded.length?encoded[i]:(byte)0);
    }
    @Override public void close(){try{channel.close();}catch(Exception ignored){}}
}
