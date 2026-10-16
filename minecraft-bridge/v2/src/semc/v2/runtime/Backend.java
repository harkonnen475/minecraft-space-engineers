package semc.v2.runtime;

import com.google.gson.Gson;
import com.google.gson.GsonBuilder;
import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import java.lang.invoke.MethodHandles;
import java.lang.invoke.VarHandle;
import java.nio.ByteOrder;
import java.nio.MappedByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.nio.file.StandardOpenOption;
import java.time.Instant;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import net.minecraft.client.Minecraft;
import net.minecraft.client.server.IntegratedServer;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.Identifier;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.InteractionHand;
import net.minecraft.world.InteractionResult;
import net.minecraft.world.entity.item.PrimedTnt;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.context.BlockPlaceContext;
import net.minecraft.world.level.GameType;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.block.state.properties.Property;
import net.minecraft.world.level.storage.LevelResource;
import net.minecraft.world.phys.AABB;
import net.minecraft.world.phys.BlockHitResult;
import net.minecraft.world.phys.Vec3;
import org.lwjgl.sdl.SDLVideo;

/** Minecraft owns native block mechanics; SE owns the visible camera and actor. */
public final class Backend implements AutoCloseable {
    static final int LENGTH=8*1024*1024, MAILBOX=4*1024*1024, RECORDS=4096;
    final int SIZE, COUNT;
    static final String WORLD="SEMC Backend";
    static final String[] ITEM_NAMES={"dirt","stone","oak_planks","oak_log","glass","cobblestone","bricks","gold_block",
        "redstone_block","redstone_lamp","redstone","lever","tnt","crafting_table","chest","sticky_piston","piston","observer","white_wool","torch","flint_and_steel",
        "oak_stairs","oak_slab","stone_bricks","oak_door","oak_fence","ladder","grass_block","sand"};
    static final VarHandle INTS=MethodHandles.byteBufferViewVarHandle(int[].class,ByteOrder.LITTLE_ENDIAN);
    static final Gson JSON=new GsonBuilder().disableHtmlEscaping().create();
    static final net.minecraft.client.color.block.BlockColors DEFAULT_COLORS=net.minecraft.client.color.block.BlockColors.createDefault();
    final Path root;
    final FileChannel channel;
    final MappedByteBuffer memory;
    final ScheduledExecutorService timer;
    final AtomicBoolean serverQueued=new AtomicBoolean(), discoveryQueued=new AtomicBoolean();
    final Map<Integer,JsonObject> palette=new LinkedHashMap<>();
    final Map<Integer,Integer> stateFlags=new LinkedHashMap<>();
    final Map<Integer,Integer> stateColors=new LinkedHashMap<>();
    final Set<Integer> previousPaletteIds=new java.util.TreeSet<>();
    final int[] records;
    final java.util.ArrayDeque<JsonObject> explosionEvents=new java.util.ArrayDeque<>();
    final java.util.Set<Long> terrainOwned=new java.util.HashSet<>();
    final java.util.ArrayDeque<ProxyEdit> terrainQueue=new java.util.ArrayDeque<>();
    volatile IntegratedServer currentServer;
    volatile UUID playerId;
    volatile boolean running=true, hidden, prepared, windowOperationPending, quitting;
    volatile int acknowledgement, commandResult;
    int sequence, lastCommand, originX=-16, originY=224, originZ=-16, selectedItem, paletteRevision;
    boolean paletteDirty, seededPalette, firstSnapshot;
    boolean explosionDirty, terrainOwnershipDirty;
    boolean previousPauseOnLostFocus;
    long nextDiscovery, nextSnapshot, nextMetadata, lastFault, lastFreshHost, nextEventId, nextTerrainRead, terrainRevision;
    volatile boolean recoveryQueued;
    long nextHostIdentityCheck,hostProcessId,hostProcessStartedAt;
    boolean hostProcessAlive;
    boolean autoLoadStarted, startupRequested, startupHideQueued;
    long startupDeadline;
    volatile double captureMillis;
    volatile int clientMenuId=-1;
    volatile String clientMenuTitle="";
    long menuSequence;
    final String paletteFingerprint;
    volatile String worldId="",worldPath="",terrainOwnedWorldId="";
    volatile long saveCheckpoint,savedAt;
    Path worldMetadataPath;
    volatile ChunkStream chunkStream;
    String worldToken="";

    public Backend(Path root) throws Exception {
        this.root=root.toAbsolutePath();
        paletteFingerprint=computePaletteFingerprint();
        SIZE=Files.exists(root.resolve("mc-v2-capture64.flag"))?64:32;COUNT=SIZE*SIZE*SIZE;records=new int[COUNT*3];
        originX=-SIZE/2;originZ=-SIZE/2;originY=240-SIZE/2;
        Files.createDirectories(root);
        Path previousPalette=root.resolve("palette-v2.json");
        if(Files.exists(previousPalette)){
            try{
                JsonObject previous=com.google.gson.JsonParser.parseString(Files.readString(previousPalette)).getAsJsonObject();
                if(previous.has("paletteFingerprint")&&paletteFingerprint.equals(previous.get("paletteFingerprint").getAsString())
                    &&previous.has("minecraftVersion")&&"26.3".equals(previous.get("minecraftVersion").getAsString())){
                    for(var entry:previous.getAsJsonArray("states")){
                        int id=entry.getAsJsonObject().get("stateId").getAsInt();
                        BlockState state=Block.stateById(id);
                        if(id>=0&&Block.getId(state)==id)previousPaletteIds.add(id);
                    }
                }
            }catch(Exception failure){log("PALETTE previous state IDs unavailable; rebuilding native descriptors: "+failure.getClass().getSimpleName());}
        }
        channel=FileChannel.open(root.resolve("bridge-v2.bin"),StandardOpenOption.READ,StandardOpenOption.WRITE,StandardOpenOption.CREATE);
        if(channel.size()<LENGTH){channel.position(LENGTH-1);channel.write(java.nio.ByteBuffer.wrap(new byte[]{0}));}
        memory=channel.map(FileChannel.MapMode.READ_WRITE,0,LENGTH);memory.order(ByteOrder.LITTLE_ENDIAN);
        if(memory.getInt(0)==0x32434D53 && memory.getInt(4)==2){sequence=memory.getInt(8)&~1;acknowledgement=memory.getInt(56);lastCommand=acknowledgement;commandResult=memory.getInt(60);paletteRevision=Math.max(0,memory.getInt(48));}
        timer=Executors.newSingleThreadScheduledExecutor(r->{Thread t=new Thread(r,"SEMC v2 scheduler");t.setDaemon(true);return t;});
        Path previousEvents=root.resolve("explosion-v2.json");
        if(Files.exists(previousEvents)){
            JsonObject previous=com.google.gson.JsonParser.parseString(Files.readString(previousEvents)).getAsJsonObject();
            nextEventId=previous.get("lastId").getAsLong();
            for(var event:previous.getAsJsonArray("events"))explosionEvents.add(event.getAsJsonObject());
        }
        nextEventId=Math.max(nextEventId,System.currentTimeMillis()*1000L);
        Path startupFlag=root.resolve("mc-v2-startup.flag");
        startupRequested=Boolean.getBoolean("semc.backend.autoStart")
            ||(Files.exists(startupFlag)&&Files.readString(startupFlag).trim().equals(WORLD));
        startupDeadline=System.currentTimeMillis()+180000;
        Path ownedFile=root.resolve("terrain-proxy-owned-v2.json");
        if(Files.exists(ownedFile)){
            JsonObject previous=com.google.gson.JsonParser.parseString(Files.readString(ownedFile)).getAsJsonObject();
            if(previous.has("worldId"))terrainOwnedWorldId=previous.get("worldId").getAsString();
            for(var packed:previous.getAsJsonArray("positions"))terrainOwned.add(packed.getAsLong());
        }
    }

    public void start(){
        log("START v2: native server block simulation; guarded world='"+WORLD+"'; no automatic edits/hide");
        semc.v2.TntHooks.setListener(this::nativeExplosion);
        publish(null,null,false);
        timer.scheduleAtFixedRate(this::schedule,0,50,TimeUnit.MILLISECONDS);
    }

    void schedule(){
        try{
            if(!running||quitting)return;
            if(Files.exists(root.resolve("mc-v2-stop.flag"))){close();return;}
            long now=System.currentTimeMillis();
            if(now>=nextHostIdentityCheck)hostStillAlive(now);
            long hostHeartbeat=memory.getLong(MAILBOX+256);
            if(hostHeartbeat>0&&now-hostHeartbeat>=-30000&&now-hostHeartbeat<3000)lastFreshHost=now;
            if(hidden&&lastFreshHost>0&&now-lastFreshHost>15000&&!recoveryQueued){
                IntegratedServer active=currentServer;
                if(managedBackend()&&active!=null&&validWorld(active)&&prepared){
                    if(!hostStillAlive(now)){
                        recoveryQueued=true;
                        active.execute(()->{try{saveAndQuit(active,null,"managed backend host exited after stale heartbeat");}catch(Throwable failure){fault(failure);recoveryQueued=false;}});
                    }
                }else{
                    recoveryQueued=true;
                    Minecraft.getInstance().execute(()->{
                    try{restoreWindow("SE heartbeat lost for over 15 seconds");}
                    catch(Throwable failure){fault(failure);}
                    finally{recoveryQueued=false;}
                    });
                }
            }
            if(now>=nextDiscovery && discoveryQueued.compareAndSet(false,true)){
                nextDiscovery=now+500;
                Minecraft.getInstance().execute(()->{
                    try{
                        Minecraft client=Minecraft.getInstance();currentServer=client.getSingleplayerServer();playerId=client.player==null?null:client.player.getUUID();
                        if(client.gui.screen() instanceof net.minecraft.client.gui.screens.inventory.AbstractContainerScreen<?> screen){
                            clientMenuTitle=screen.getTitle().getString();clientMenuId=screen.getMenu().containerId;
                        }else{clientMenuId=-1;clientMenuTitle="";}
                        Path autoload=root.resolve("mc-v2-autoload.flag");
                        if(startupRequested&&currentServer!=null&&!validWorld(currentServer)){
                            startupRequested=false;restoreWindow("startup refused another loaded world");
                            log("STARTUP refused another loaded world");
                        }
                        if(startupRequested&&System.currentTimeMillis()>startupDeadline){
                            startupRequested=false;restoreWindow("backend startup timed out");
                            log("STARTUP timeout; Minecraft shown for recovery");
                        }
                        if(startupRequested&&!hidden&&(currentServer==null||validWorld(currentServer)))hideWindow();
                        if(!autoLoadStarted&&currentServer==null&&client.level==null&&(startupRequested||Files.exists(autoload))
                            &&client.gui.screen() instanceof net.minecraft.client.gui.screens.TitleScreen&&client.gui.overlay()==null){
                            if(Files.exists(autoload)&&!Files.readString(autoload).trim().equals(WORLD))throw new IllegalStateException("Autoload flag must name only SEMC Backend");
                            if(!Files.exists(client.gameDirectory.toPath().resolve("saves").resolve(WORLD).resolve("level.dat")))throw new IllegalStateException("Copied backend world is missing");
                            autoLoadStarted=true;Files.deleteIfExists(autoload);log("AUTOLOAD requested dedicated backend");
                            client.createWorldOpenFlows().openWorld(WORLD,()->{autoLoadStarted=false;log("AUTOLOAD returned to title");});
                        }
                        writeLifecycle(client);
                    }catch(Throwable failure){fault(failure);}
                    finally{discoveryQueued.set(false);}
                });
            }
            IntegratedServer server=currentServer; UUID uuid=playerId;
            if(server==null || uuid==null || server.isStopped()){
                if(now>=nextSnapshot){nextSnapshot=now+100;prepared=false;publish(null,null,false);}
                return;
            }
            if(!serverQueued.compareAndSet(false,true))return;
            server.execute(()->{try{if(running)step(server,uuid);}catch(Throwable failure){fault(failure);}finally{serverQueued.set(false);}});
        }catch(Throwable failure){fault(failure);}
    }

    boolean validWorld(IntegratedServer server){
        Path path=server.getWorldPath(LevelResource.ROOT).toAbsolutePath().normalize();
        return server.isSingleplayer() && !server.isDedicatedServer() && path.getFileName().toString().equals(WORLD);
    }

    void step(IntegratedServer server,UUID uuid){
        ServerPlayer player=server.getPlayerList().getPlayer(uuid);
        boolean valid=player!=null && validWorld(server);
        String token=valid?server.getWorldPath(LevelResource.ROOT).toAbsolutePath().normalize()+":"+System.identityHashCode(server):"";
        if(!token.equals(worldToken)){
            worldToken=token;prepared=false;firstSnapshot=false;worldId="";worldPath="";
            log(valid?"WORLD accepted "+WORLD:"WORLD unavailable or rejected; original saves remain protected");
        }
        if(valid&&worldId.isEmpty())loadWorldIdentity(server);
        if(startupRequested&&valid&&!prepared&&Files.exists(root.resolve("enable-v2-edits.flag"))){
            prepare(player);startupRequested=false;
            try{Files.deleteIfExists(root.resolve("mc-v2-startup.flag"));}catch(Exception failure){fault(failure);}
            log("STARTUP dedicated backend prepared; native world simulation ready");
        }
        Command cmd=readCommand();
        if(cmd!=null)execute(server,player,valid,cmd);
        if(!running||quitting)return;
        long now=System.currentTimeMillis();
        if(valid&&prepared&&Files.exists(root.resolve("enable-v2-edits.flag")))updateTerrainProxy(player.level(),now);
        if(valid&&prepared){
            try{
                if(chunkStream==null)chunkStream=new ChunkStream(this);
                chunkStream.tick(player.level(),now);
            }catch(Exception failure){fault(failure);}
            exportEntities(server,player);
        }
        if(now<nextSnapshot)return;
        nextSnapshot=now+100;
        if(valid){
            ServerLevel level=player.level();
            if(!seededPalette){seedPalette(level,player.blockPosition());seededPalette=true;}
            long captureStarted=System.nanoTime();capture(level);captureMillis=(System.nanoTime()-captureStarted)/1000000.0;
            flushPalette();
            if(prepared)exportMenu(player);
            if(now>=nextMetadata){nextMetadata=now+500;writeMetadata(server,player);}
        }else java.util.Arrays.fill(records,0);
        publish(server,player,valid);
        if(valid&&!firstSnapshot){firstSnapshot=true;log("SNAPSHOT ready origin="+originX+","+originY+","+originZ+" palette="+palette.size()+" prepared="+prepared);}
    }

    record Command(int sequence,int operation,int x,int y,int z,int item,int face,int flags,float hx,float hy,float hz,double px,double py,double pz,float yaw,float pitch){}
    Command readCommand(){
        if(windowOperationPending)return null;
        int seq=(int)INTS.getAcquire(memory,MAILBOX);
        if(seq<=0||seq<=lastCommand)return null;
        Command cmd=new Command(seq,memory.getInt(MAILBOX+4),memory.getInt(MAILBOX+8),memory.getInt(MAILBOX+12),memory.getInt(MAILBOX+16),
            memory.getInt(MAILBOX+20),memory.getInt(MAILBOX+24),memory.getInt(MAILBOX+28),memory.getFloat(MAILBOX+32),memory.getFloat(MAILBOX+36),memory.getFloat(MAILBOX+40),
            memory.getDouble(MAILBOX+48),memory.getDouble(MAILBOX+56),memory.getDouble(MAILBOX+64),memory.getFloat(MAILBOX+72),memory.getFloat(MAILBOX+76));
        VarHandle.acquireFence();if(seq!=(int)INTS.getAcquire(memory,MAILBOX))return null;
        lastCommand=seq;commandResult=0;return cmd;
    }

    void execute(IntegratedServer server,ServerPlayer player,boolean valid,Command cmd){
        try{
            int op=cmd.operation;
            if(op==4){ack(cmd,1,"stop");publish(server,player,false);close();return;}
            if(op==8){window(cmd,false);return;}
            if(!valid){ack(cmd,-1,"requires separate world folder '"+WORLD+"'");return;}
            if(op==3){
                if(Math.abs((long)cmd.x)>29_999_900L||Math.abs((long)cmd.z)>29_999_900L||cmd.y< -64||cmd.y>288){ack(cmd,-1,"view outside world bounds");return;}
                originX=cmd.x;originY=cmd.y;originZ=cmd.z;nextSnapshot=0;ack(cmd,1,"view origin "+originX+","+originY+","+originZ);return;
            }
            if(op==7){if(!prepared){ack(cmd,-1,"prepare backend before hiding");return;}window(cmd,true);return;}
            if(!Files.exists(root.resolve("enable-v2-edits.flag"))){ack(cmd,-1,"enable-v2-edits.flag is absent");return;}
            if(op==10){
                prepare(player);
                ack(cmd,1,"creative native backend prepared at 0.5,241,0.5");return;
            }
            if(!prepared||!player.gameMode.isCreative()){ack(cmd,-1,"PREPARE_CREATIVE required");return;}
            if(op==11){server.saveEverything(false,true,true);recordSaveCheckpoint(server,"SAVE");saveTerrainOwnership();if(explosionDirty)flushExplosions();ack(cmd,1,"native save complete checkpoint="+saveCheckpoint);nextSnapshot=0;return;}
            if(op==12){saveAndQuit(server,cmd,"SAVE_AND_QUIT command");return;}
            if(op==13||op==14){
                var menu=player.containerMenu;
                if(menu==player.inventoryMenu||menu.containerId!=cmd.x){ack(cmd,-1,"stale or closed native container");return;}
                if(op==14){player.closeContainer();ack(cmd,1,"native container closed; carried item returned");exportMenu(player);return;}
                if(!menu.stillValid(player)){player.closeContainer();exportMenu(player);ack(cmd,-1,"native container no longer reachable");return;}
                if(cmd.y<0||cmd.y>=menu.slots.size()||(cmd.z!=0&&cmd.z!=1)||(cmd.item!=0&&cmd.item!=1)){ack(cmd,-1,"unsupported native slot click");return;}
                var action=cmd.item==0?net.minecraft.world.inventory.ContainerInput.PICKUP:net.minecraft.world.inventory.ContainerInput.QUICK_MOVE;
                menu.clicked(cmd.y,cmd.z,action,player);menu.broadcastChanges();ack(cmd,1,"native container click slot="+cmd.y+" action="+action);exportMenu(player);nextSnapshot=0;return;
            }
            if(op==6){select(player,cmd.item);ack(cmd,1,"selected "+ITEM_NAMES[selectedItem]);return;}
            if(op==9){
                applyPose(player,cmd);ack(cmd,1,"pose");return;
            }
            if(op!=1&&op!=2&&op!=5){ack(cmd,-1,"unsupported operation");return;}
            if((cmd.flags&1)!=0)applyPose(player,cmd);
            boolean retainedBreak=op==1&&Files.exists(root.resolve("mc-v2-streaming.flag"));
            if(!inside(cmd.x,cmd.y,cmd.z)&&!retainedBreak){ack(cmd,-1,"target outside capture");return;}
            BlockPos target=new BlockPos(cmd.x,cmd.y,cmd.z);ServerLevel level=player.level();
            if(Math.abs((long)cmd.x)>29999900||Math.abs((long)cmd.z)>29999900||cmd.y<level.getMinY()||cmd.y>=level.getMaxY()){ack(cmd,-1,"target outside native world");return;}
            if(retainedBreak&&!level.hasChunkAt(target))level.getChunkAt(target);
            if(!level.hasChunkAt(target)){ack(cmd,-1,"target chunk not loaded");return;}
            Direction face=Direction.from3DDataValue(Math.max(0,Math.min(5,cmd.face)));
            if(op==1){boolean result=level.getBlockState(target).isAir()||player.gameMode.destroyBlock(target);if(chunkStream!=null)chunkStream.prioritize(target);ack(cmd,result?1:-1,"native break "+target);nextSnapshot=0;return;}
            float hx=finiteFraction(cmd.hx),hy=finiteFraction(cmd.hy),hz=finiteFraction(cmd.hz);
            BlockHitResult hit=new BlockHitResult(new Vec3(cmd.x+hx,cmd.y+hy,cmd.z+hz),face,target,false);
            if(op==2){
                select(player,cmd.item);ItemStack stack=player.getItemInHand(InteractionHand.MAIN_HAND);
                if(!(stack.getItem() instanceof BlockItem blockItem)){ack(cmd,-1,"selected item is not placeable");return;}
                // The host already identified the destination. A replaceable target is the
                // clicked cell, so native BlockPlaceContext places exactly there, while
                // retaining support/orientation/waterlogging/block-entity behavior.
                if(!level.getBlockState(target).canBeReplaced()){ack(cmd,-1,"destination is not replaceable");return;}
                BlockPlaceContext context=new BlockPlaceContext(player,InteractionHand.MAIN_HAND,stack,hit){
                    @Override public Direction[] getNearestLookingDirections(){
                        // The requested cell is replaceable air, so vanilla otherwise
                        // ignores the clicked-face preference. Restore that preference
                        // while leaving single-direction player orientation native.
                        Direction[] original=super.getNearestLookingDirections(),ordered=new Direction[original.length];
                        Direction support=face.getOpposite();ordered[0]=support;int index=1;
                        for(Direction direction:original)if(direction!=support)ordered[index++]=direction;
                        return ordered;
                    }
                };
                InteractionResult result=blockItem.place(context);
                ack(cmd,result.consumesAction()?1:-1,"native place "+ITEM_NAMES[selectedItem]+" result="+result+" state="+level.getBlockState(target));
            }else{
                ItemStack interactionStack=ItemStack.EMPTY;
                if(cmd.item==20){select(player,20);interactionStack=player.getItemInHand(InteractionHand.MAIN_HAND);}
                InteractionResult result=player.gameMode.useItemOn(player,level,interactionStack,InteractionHand.MAIN_HAND,hit);
                ack(cmd,result.consumesAction()?1:-1,"native interact result="+result+" state="+level.getBlockState(target));
            }
            if(chunkStream!=null)chunkStream.prioritize(target);nextSnapshot=0;
        }catch(Throwable failure){ack(cmd,-1,failure.toString());fault(failure);}
    }

    float finiteFraction(float value){return Float.isFinite(value)?Math.max(0,Math.min(1,value)):0.5f;}
    boolean hostStillAlive(long now){
        if(now<nextHostIdentityCheck)return hostProcessAlive;
        nextHostIdentityCheck=now+1000;hostProcessAlive=false;
        Path identity=root.resolve("host-lifecycle-v2.json");
        try{
            if(Files.exists(identity)){
                JsonObject state=com.google.gson.JsonParser.parseString(Files.readString(identity)).getAsJsonObject();
                if(state.get("version").getAsInt()==2){
                    long pid=state.get("processId").getAsLong(),startedAt=state.get("processStartedAt").getAsLong();
                    if(pid>0&&startedAt>0){hostProcessId=pid;hostProcessStartedAt=startedAt;}
                }
            }
        }catch(Exception failure){fault(failure);}
        // Keep a previously validated identity through a transient file replacement.
        // An older host with no identity still uses the original heartbeat-only fallback.
        if(hostProcessId>0){
            var handle=ProcessHandle.of(hostProcessId);
            if(handle.isPresent()&&handle.get().isAlive()){
                var started=handle.get().info().startInstant();
                hostProcessAlive=started.isPresent()&&Math.abs(started.get().toEpochMilli()-hostProcessStartedAt)<=3000;
            }
        }
        return hostProcessAlive;
    }
    boolean managedBackend(){return Minecraft.getInstance().gameDirectory.toPath().toAbsolutePath().normalize().equals(root.resolve("minecraft-backend").normalize());}
    void saveAndQuit(IntegratedServer server,Command command,String reason){
        if(quitting)return;
        if(!prepared||!validWorld(server))throw new IllegalStateException("native quit requires prepared dedicated backend");
        ServerPlayer activePlayer=server.getPlayerList().getPlayer(playerId);
        if(activePlayer!=null&&activePlayer.containerMenu!=activePlayer.inventoryMenu){activePlayer.closeContainer();exportMenu(activePlayer);}
        server.saveEverything(false,true,true);recordSaveCheckpoint(server,reason);saveTerrainOwnership();if(explosionDirty)flushExplosions();
        if(command!=null)ack(command,1,"native save complete; graceful client exit requested");
        publish(server,server.getPlayerList().getPlayer(playerId),true);
        quitting=true;timer.shutdown();log("SAVE_AND_QUIT "+reason);
        Minecraft.getInstance().execute(()->Minecraft.getInstance().stop());
    }
    void prepare(ServerPlayer player){
        player.setGameMode(GameType.CREATIVE);player.getAbilities().flying=true;player.onUpdateAbilities();player.setNoGravity(true);
        player.teleportTo(player.level(),0.5,241,0.5,Set.of(),0,0,true);
        if(player.getInventory().getItem(0).isEmpty())select(player,0);
        else{
            String selectedName=BuiltInRegistries.ITEM.getKey(player.getInventory().getItem(0).getItem()).getPath();
            for(int index=0;index<ITEM_NAMES.length;index++)if(ITEM_NAMES[index].equals(selectedName)){selectedItem=index;break;}
        }
        prepared=true;nextSnapshot=0;
    }
    void applyPose(ServerPlayer player,Command cmd){
        if(!Double.isFinite(cmd.px)||!Double.isFinite(cmd.py)||!Double.isFinite(cmd.pz)||!Float.isFinite(cmd.yaw)||!Float.isFinite(cmd.pitch))
            throw new IllegalArgumentException("nonfinite pose");
        boolean streaming=Files.exists(root.resolve("mc-v2-streaming.flag"));
        if(streaming){
            if(Math.abs(cmd.px)>29999900||Math.abs(cmd.pz)>29999900||cmd.py<player.level().getMinY()||cmd.py>player.level().getMaxY()+8)
                throw new IllegalArgumentException("pose outside native world bounds");
            int ox=streamOrigin(cmd.px,originX),oy=Math.max(player.level().getMinY(),Math.min(player.level().getMaxY()-SIZE,streamOrigin(cmd.py,originY))),oz=streamOrigin(cmd.pz,originZ);
            if(ox!=originX||oy!=originY||oz!=originZ){originX=ox;originY=oy;originZ=oz;nextSnapshot=0;log("STREAM origin="+ox+","+oy+","+oz+"; world blocks preserved");}
        }else if(cmd.px<originX-4||cmd.px>originX+SIZE+4||cmd.py<originY-4||cmd.py>originY+SIZE+8||cmd.pz<originZ-4||cmd.pz>originZ+SIZE+4)
            throw new IllegalArgumentException("pose outside capture margin; streaming is disabled");
        player.teleportTo(player.level(),cmd.px,cmd.py,cmd.pz,Set.of(),cmd.yaw,Math.max(-90,Math.min(90,cmd.pitch)),true);
        player.setShiftKeyDown((cmd.flags&2)!=0);
    }
    int streamOrigin(double position,int origin){int margin=SIZE/4;return position<origin+margin||position>=origin+SIZE-margin?(int)Math.floor(position/8)*8-SIZE/2:origin;}
    boolean inside(int x,int y,int z){return x>=originX&&x<originX+SIZE&&y>=originY&&y<originY+SIZE&&z>=originZ&&z<originZ+SIZE;}
    void select(ServerPlayer player,int index){
        if(index<0||index>=ITEM_NAMES.length)throw new IllegalArgumentException("Unknown item index "+index);
        Item item=BuiltInRegistries.ITEM.getValue(Identifier.withDefaultNamespace(ITEM_NAMES[index]));
        if(item==null)throw new IllegalArgumentException("Missing native item "+ITEM_NAMES[index]);
        selectedItem=index;player.getInventory().setSelectedSlot(0);player.getInventory().setItem(0,new ItemStack(item,index==20?1:64));
        player.inventoryMenu.broadcastChanges();
    }

    void window(Command cmd,boolean hide){
        windowOperationPending=true;
        Minecraft.getInstance().execute(()->{
            try{
                Minecraft client=Minecraft.getInstance();
                if(hide){
                    if(!running||client.getSingleplayerServer()!=currentServer||!prepared)throw new IllegalStateException("backend changed before hiding");
                    hideWindow();
                }else{
                    if(!SDLVideo.SDL_ShowWindow(client.getWindow().handle()))throw new IllegalStateException("SDL_ShowWindow failed");
                    if(hidden)client.options.pauseOnLostFocus=previousPauseOnLostFocus;
                    hidden=false;
                }
                ack(cmd,1,hide?"SDL window hidden; focus pause disabled":"SDL window shown");
            }catch(Throwable failure){ack(cmd,-1,failure.toString());fault(failure);}
            finally{windowOperationPending=false;}
        });
    }

    void hideWindow(){
        Minecraft client=Minecraft.getInstance();
        if(!hidden)previousPauseOnLostFocus=client.options.pauseOnLostFocus;
        client.options.pauseOnLostFocus=false;
        if(prepared)client.gui.setScreen(null);
        if(!SDLVideo.SDL_HideWindow(client.getWindow().handle()))throw new IllegalStateException("SDL_HideWindow failed");
        hidden=true;
    }

    void writeLifecycle(Minecraft client){
        JsonObject state=new JsonObject();state.addProperty("version",2);state.addProperty("timestamp",System.currentTimeMillis());
        state.addProperty("processId",ProcessHandle.current().pid());state.addProperty("gameDirectory",client.gameDirectory.getAbsolutePath());
        state.addProperty("phase",currentServer==null?"menu":validWorld(currentServer)?(prepared?"ready":"backend-loaded"):"other-world");
        state.addProperty("prepared",prepared);state.addProperty("hidden",hidden);state.addProperty("startupRequested",startupRequested);
        state.addProperty("streaming",Files.exists(root.resolve("mc-v2-streaming.flag")));
        state.addProperty("terrainProxyBlocks",terrainOwned.size());state.addProperty("terrainRevision",terrainRevision);
        state.addProperty("captureSize",SIZE);state.addProperty("captureMillis",captureMillis);state.addProperty("managed",managedBackend());
        state.addProperty("worldId",worldId);state.addProperty("saveCheckpoint",saveCheckpoint);state.addProperty("savedAt",savedAt);
        ChunkStream remote=chunkStream;
        if(remote!=null){state.addProperty("remoteInterests",remote.interests.size());state.addProperty("remoteSampleMillis",remote.sampleMillis);state.addProperty("remoteSamples",remote.totalSamples);state.addProperty("remoteChanges",remote.totalChanges);state.addProperty("remoteSequence",remote.latest);}
        if(currentServer!=null)state.addProperty("averageTickMillis",currentServer.getAverageTickTimeNanos()/1000000.0);
        writeJson("backend-lifecycle-v2.json",state);
    }

    record ProxyEdit(long packed,boolean present){}
    void updateTerrainProxy(ServerLevel level,long now){
        if(!level.dimension().equals(net.minecraft.world.level.Level.OVERWORLD))return;
        if(now>=nextTerrainRead){
            nextTerrainRead=now+500;
            Path file=root.resolve("terrain-v2.json");
            if(Files.exists(file))try{
                JsonObject terrain=com.google.gson.JsonParser.parseString(Files.readString(file)).getAsJsonObject();
                long revision=terrain.get("revision").getAsLong();
                if(revision!=terrainRevision){
                    int terrainX=terrain.get("sizeX").getAsInt(),terrainZ=terrain.get("sizeZ").getAsInt();
                    if(terrain.get("version").getAsInt()!=2||terrainX<1||terrainX>64||terrainZ<1||terrainZ>64)
                        throw new IllegalArgumentException("terrain proxy requires protocol2 and at most 64x64 columns");
                    int tx=terrain.get("originX").getAsInt(),tz=terrain.get("originZ").getAsInt();
                    if(Math.abs((long)tx)>29999900||Math.abs((long)tz)>29999900)throw new IllegalArgumentException("terrain origin outside native world");
                    JsonArray heights=terrain.getAsJsonArray("heights");
                    if(heights.size()!=terrainX*terrainZ)throw new IllegalArgumentException("terrain height count mismatch");
                    java.util.Set<Long> desired=new java.util.HashSet<>();
                    for(int z=0;z<terrainZ;z++)for(int x=0;x<terrainX;x++){
                        var value=heights.get(x+terrainX*z);if(value.isJsonNull())continue;
                        int top=value.getAsInt();if(top<level.getMinY()+4||top>level.getMaxY())continue;
                        for(int depth=1;depth<=4;depth++)desired.add(BlockPos.asLong(tx+x,top-depth,tz+z));
                    }
                    terrainQueue.clear();
                    // Reconcile only the sampled footprint. Older support persists until
                    // resampled, just as native blocks outside the render window persist.
                    for(long packed:terrainOwned){
                        BlockPos pos=BlockPos.of(packed);
                        if(pos.getX()>=tx&&pos.getX()<tx+terrainX&&pos.getZ()>=tz&&pos.getZ()<tz+terrainZ&&!desired.contains(packed))terrainQueue.add(new ProxyEdit(packed,false));
                    }
                    for(long packed:desired)terrainQueue.add(new ProxyEdit(packed,true));
                    terrainRevision=revision;
                    log("TERRAIN queued revision="+revision+" desired="+desired.size()+" work="+terrainQueue.size());
                }
            }catch(Throwable failure){fault(failure);}
        }
        if(terrainQueue.isEmpty()){if(terrainOwnershipDirty)saveTerrainOwnership();return;}
        java.util.ArrayList<ProxyEdit> batch=new java.util.ArrayList<>();
        boolean ownershipChanged=false;
        int inspected=0,initialQueueSize=terrainQueue.size();
        while(!terrainQueue.isEmpty()&&inspected++<Math.min(256,initialQueueSize)){
            ProxyEdit edit=terrainQueue.remove();BlockPos pos=BlockPos.of(edit.packed);
            if(!level.hasChunkAt(pos)){terrainQueue.add(edit);continue;}
            BlockState existing=level.getBlockState(pos);
            if(edit.present){
                if(existing.isAir()){
                    ownershipChanged|=terrainOwned.add(edit.packed);batch.add(edit);
                }else if(!existing.is(Blocks.BARRIER)&&terrainOwned.remove(edit.packed))ownershipChanged=true;
            }else if(terrainOwned.contains(edit.packed))batch.add(edit);
        }
        // Ownership is journaled BEFORE adding barriers; a crash can leave an owned
        // air cell, never a newly-created unowned barrier hidden from reconciliation.
        terrainOwnershipDirty|=ownershipChanged;
        if(terrainOwnershipDirty&&!saveTerrainOwnership()){
            for(int i=batch.size()-1;i>=0;i--)terrainQueue.addFirst(batch.get(i));
            return;
        }
        for(ProxyEdit edit:batch){
            BlockPos pos=BlockPos.of(edit.packed);BlockState existing=level.getBlockState(pos);
            if(edit.present){if(existing.isAir())level.setBlock(pos,Blocks.BARRIER.defaultBlockState(),3);}
            else{if(existing.is(Blocks.BARRIER))level.setBlock(pos,Blocks.AIR.defaultBlockState(),3);terrainOwned.remove(edit.packed);ownershipChanged=true;}
        }
        if(ownershipChanged){terrainOwnershipDirty=true;saveTerrainOwnership();}
        if(terrainQueue.isEmpty())log("TERRAIN applied revision="+terrainRevision+" owned="+terrainOwned.size());
    }

    boolean saveTerrainOwnership(){
        JsonObject state=new JsonObject();state.addProperty("version",2);state.addProperty("revision",terrainRevision);
        state.addProperty("worldId",worldId);
        JsonArray positions=new JsonArray();for(long packed:terrainOwned)positions.add(packed);state.add("positions",positions);
        boolean saved=writeJson("terrain-proxy-owned-v2.json",state);if(saved)terrainOwnershipDirty=false;return saved;
    }

    void restoreWindow(String reason){
        if(!hidden)return;
        Minecraft client=Minecraft.getInstance();
        if(!SDLVideo.SDL_ShowWindow(client.getWindow().handle()))throw new IllegalStateException("Recovery SDL_ShowWindow failed");
        client.options.pauseOnLostFocus=previousPauseOnLostFocus;hidden=false;
        log("RECOVERY window shown: "+reason);
    }

    void capture(ServerLevel level){
        BlockPos.MutableBlockPos pos=new BlockPos.MutableBlockPos();
        int i=0;
        for(int y=0;y<SIZE;y++)for(int z=0;z<SIZE;z++)for(int x=0;x<SIZE;x++){
            pos.set(originX+x,originY+y,originZ+z);
            if(!level.hasChunkAt(pos)){records[i++]=-1;records[i++]=0;records[i++]=0;continue;}
            BlockState state=level.getBlockState(pos);
            if(state.isAir()||(state.is(Blocks.BARRIER)&&terrainOwned.contains(pos.asLong()))){records[i++]=0;records[i++]=0;records[i++]=0;continue;}
            int id=Block.getId(state);ensureState(state,level,pos);
            records[i++]=id;records[i++]=stateColors.get(id);records[i++]=stateFlags.get(id);
        }
    }

    void seedPalette(ServerLevel level,BlockPos pos){
        for(int id:previousPaletteIds)ensureState(Block.stateById(id),level,pos);
        int restored=palette.size();previousPaletteIds.clear();
        for(String name:ITEM_NAMES){
            if(name.equals("flint_and_steel"))continue;
            String blockName=name.equals("redstone")?"redstone_wire":name;
            Block block=BuiltInRegistries.BLOCK.getValue(Identifier.withDefaultNamespace(blockName));
            if(block!=null)for(BlockState state:block.getStateDefinition().getPossibleStates())ensureState(state,level,pos);
        }
        log("PALETTE seeded native states="+palette.size()+" restored="+restored);
    }

    @SuppressWarnings({"rawtypes","unchecked"})
    void ensureState(BlockState state,ServerLevel level,BlockPos pos){
        int id=Block.getId(state);if(palette.containsKey(id))return;
        Identifier name=BuiltInRegistries.BLOCK.getKey(state.getBlock());
        var collision=state.getCollisionShape(level,pos);
        int flags=(state.isAir()?0:1)|(state.isCollisionShapeFullBlock(level,pos)?2:0)|(!collision.isEmpty()?4:0)|(!state.getFluidState().isEmpty()?8:0);
        int rgb=state.getMapColor(level,pos).col&0xffffff;
        JsonObject entry=new JsonObject();entry.addProperty("stateId",id);entry.addProperty("registryName",name.toString());
        JsonObject properties=new JsonObject();
        for(Property property:state.getProperties())properties.addProperty(property.getName(),property.getName(state.getValue(property)));
        entry.add("properties",properties);entry.addProperty("mapRGB",rgb);entry.addProperty("flags",flags);
        var tint=DEFAULT_COLORS.getTintSource(state,0);
        entry.addProperty("tintRGB",tint==null?-1:tint.color(state)&0xffffff);
        entry.addProperty("tintMode","native-default-state; biome variation is not per-cell yet");
        entry.addProperty("blockstateResource","assets/"+name.getNamespace()+"/blockstates/"+name.getPath()+".json");
        entry.addProperty("modelResourceHint",name.getNamespace()+":block/"+name.getPath());
        entry.addProperty("lightEmission",state.getLightEmission());entry.addProperty("hasBlockEntity",state.hasBlockEntity());
        entry.addProperty("dynamicCollision",state.is(Blocks.MOVING_PISTON));
        JsonArray boxes=new JsonArray();for(AABB box:collision.toAabbs()){
            JsonArray values=new JsonArray();values.add(box.minX);values.add(box.minY);values.add(box.minZ);values.add(box.maxX);values.add(box.maxY);values.add(box.maxZ);boxes.add(values);
        }
        entry.add("collisionBoxes",boxes);palette.put(id,entry);stateFlags.put(id,flags);stateColors.put(id,rgb);paletteDirty=true;
    }

    void flushPalette(){
        if(!paletteDirty)return;
        JsonObject document=new JsonObject();document.addProperty("version",2);document.addProperty("revision",paletteRevision+1);
        document.addProperty("minecraftVersion","26.3");document.addProperty("paletteFingerprint",paletteFingerprint);JsonArray states=new JsonArray();palette.values().forEach(states::add);document.add("states",states);
        if(writeJson("palette-v2.json",document)){paletteRevision++;paletteDirty=false;}
    }

    void writeMetadata(IntegratedServer server,ServerPlayer player){
        publishWorldIdentity(server,player.level());
        JsonObject data=new JsonObject();data.addProperty("version",2);data.addProperty("timestamp",System.currentTimeMillis());
        data.addProperty("worldFolder",WORLD);data.addProperty("selectedItemIndex",selectedItem);data.addProperty("selectedItem",ITEM_NAMES[selectedItem]);
        data.addProperty("creative",player.gameMode.isCreative());data.addProperty("paused",server.isPaused());
        JsonArray items=new JsonArray();
        for(int slot=0;slot<player.getInventory().getContainerSize();slot++){
            ItemStack item=player.getInventory().getItem(slot);if(item.isEmpty())continue;
            JsonObject entry=new JsonObject();entry.addProperty("slot",slot);entry.addProperty("item",BuiltInRegistries.ITEM.getKey(item.getItem()).toString());entry.addProperty("count",item.getCount());items.add(entry);
        }
        data.add("inventory",items);
        JsonArray blockEntities=new JsonArray();
        for(int y=0;y<SIZE;y++)for(int z=0;z<SIZE;z++)for(int x=0;x<SIZE;x++){
            int index=x+SIZE*(z+SIZE*y);int state=records[index*3];JsonObject paletteEntry=palette.get(state);
            if(paletteEntry==null||!paletteEntry.get("hasBlockEntity").getAsBoolean())continue;
            BlockPos pos=new BlockPos(originX+x,originY+y,originZ+z);var entity=player.level().getBlockEntity(pos);if(entity==null)continue;
            JsonObject entry=new JsonObject();entry.addProperty("x",pos.getX());entry.addProperty("y",pos.getY());entry.addProperty("z",pos.getZ());
            entry.addProperty("type",BuiltInRegistries.BLOCK_ENTITY_TYPE.getKey(entity.getType()).toString());
            entry.addProperty("snbt",entity.saveWithFullMetadata(player.level().registryAccess()).toString());blockEntities.add(entry);
        }
        data.add("blockEntities",blockEntities);writeJson("backend-v2.json",data);
    }

    @SuppressWarnings({"rawtypes","unchecked"})
    String computePaletteFingerprint()throws Exception{
        var digest=java.security.MessageDigest.getInstance("SHA-256");
        digest.update("Minecraft26.3\n".getBytes(java.nio.charset.StandardCharsets.UTF_8));
        java.util.TreeMap<Integer,BlockState> ordered=new java.util.TreeMap<>();
        for(BlockState state:Block.BLOCK_STATE_REGISTRY)ordered.put(Block.getId(state),state);
        for(var entry:ordered.entrySet()){
            BlockState state=entry.getValue();java.util.TreeMap<String,String> properties=new java.util.TreeMap<>();
            for(Property property:state.getProperties())properties.put(property.getName(),property.getName(state.getValue(property)));
            String line=entry.getKey()+"|"+BuiltInRegistries.BLOCK.getKey(state.getBlock())+"|"+properties+"\n";
            digest.update(line.getBytes(java.nio.charset.StandardCharsets.UTF_8));
        }
        return java.util.HexFormat.of().formatHex(digest.digest());
    }

    void loadWorldIdentity(IntegratedServer server){
        try{
            Path directory=server.getWorldPath(LevelResource.ROOT).toAbsolutePath().normalize();
            Path metadata=directory.resolve("semc-bridge-world.json");
            String id;long checkpoint=0,lastSaved=0;
            if(Files.exists(metadata)){
                JsonObject saved=com.google.gson.JsonParser.parseString(Files.readString(metadata)).getAsJsonObject();
                id=UUID.fromString(saved.get("worldId").getAsString()).toString();
                if(saved.has("saveCheckpoint"))checkpoint=saved.get("saveCheckpoint").getAsLong();
                if(saved.has("savedAt"))lastSaved=saved.get("savedAt").getAsLong();
            }else{
                id=UUID.randomUUID().toString();JsonObject initial=new JsonObject();initial.addProperty("version",2);initial.addProperty("worldId",id);
                initial.addProperty("saveCheckpoint",0);initial.addProperty("savedAt",0);initial.addProperty("createdAt",System.currentTimeMillis());
                if(!writeJsonFile(metadata,initial))throw new java.io.IOException("Cannot persist dedicated world identity");
            }
            if(!terrainOwnedWorldId.isEmpty()&&!terrainOwnedWorldId.equals(id)){
                terrainOwned.clear();terrainQueue.clear();terrainRevision=0;
                log("WORLD terrain ownership reset for different world identity; no world blocks changed");
            }
            worldMetadataPath=metadata;worldPath=directory.toString();saveCheckpoint=checkpoint;savedAt=lastSaved;worldId=id;terrainOwnedWorldId=id;
            terrainOwnershipDirty=true;saveTerrainOwnership();
            log("WORLD identity="+id+" checkpoint="+checkpoint+" palette="+paletteFingerprint);
        }catch(Throwable failure){throw new IllegalStateException("Cannot establish persistent backend world identity",failure);}
    }

    void recordSaveCheckpoint(IntegratedServer server,String reason){
        if(worldId.isEmpty()||worldMetadataPath==null)throw new IllegalStateException("World identity is unavailable for checkpoint");
        long next=saveCheckpoint+1,now=System.currentTimeMillis();
        JsonObject record=new JsonObject();record.addProperty("version",2);record.addProperty("worldId",worldId);
        record.addProperty("saveCheckpoint",next);record.addProperty("savedAt",now);record.addProperty("savedServerTick",server.getTickCount());record.addProperty("reason",reason);
        if(!writeJsonFile(worldMetadataPath,record))throw new IllegalStateException("Native save finished but checkpoint metadata could not be persisted");
        saveCheckpoint=next;savedAt=now;
        ServerPlayer player=server.getPlayerList().getPlayer(playerId);if(player!=null)publishWorldIdentity(server,player.level());
    }

    void publishWorldIdentity(IntegratedServer server,ServerLevel level){
        if(worldId.isEmpty())return;
        JsonObject state=new JsonObject();state.addProperty("version",2);state.addProperty("timestamp",System.currentTimeMillis());
        state.addProperty("worldId",worldId);state.addProperty("worldPath",worldPath);state.addProperty("dimension",level.dimension().identifier().toString());
        state.addProperty("minecraftVersion","26.3");state.addProperty("paletteFingerprint",paletteFingerprint);state.addProperty("paletteRevision",paletteRevision);
        state.addProperty("saveCheckpoint",saveCheckpoint);state.addProperty("savedAt",savedAt);state.addProperty("serverTick",server.getTickCount());
        state.addProperty("checkpointScope","explicit native SAVE/SAVE_AND_QUIT; autosaves are not numbered");
        writeJson("world-v2.json",state);
    }

    void exportEntities(IntegratedServer server,ServerPlayer player){
        if(explosionDirty)flushExplosions();
        DynamicScene.export(this,player);
    }
    JsonObject describeItem(ItemStack item){
        JsonObject result=new JsonObject();
        if(item.isEmpty()){
            result.addProperty("itemId","minecraft:air");result.addProperty("count",0);result.addProperty("maxStack",0);result.addProperty("displayName","");return result;
        }
        Identifier id=BuiltInRegistries.ITEM.getKey(item.getItem());
        result.addProperty("itemId",id.toString());result.addProperty("count",item.getCount());result.addProperty("maxStack",item.getMaxStackSize());
        result.addProperty("displayName",item.getHoverName().getString());
        result.addProperty("itemModelResource","assets/"+id.getNamespace()+"/items/"+id.getPath()+".json");
        result.addProperty("iconTextureHint",id.getNamespace()+":item/"+id.getPath());
        if(item.getItem() instanceof BlockItem blockItem)result.addProperty("blockStateId",Block.getId(blockItem.getBlock().defaultBlockState()));
        return result;
    }

    void exportMenu(ServerPlayer player){
        var menu=player.containerMenu;boolean open=menu!=player.inventoryMenu&&menu.containerId!=0;
        JsonObject state=new JsonObject();state.addProperty("version",2);state.addProperty("timestamp",System.currentTimeMillis());
        state.addProperty("sequence",++menuSequence);state.addProperty("open",open);state.addProperty("containerId",menu.containerId);
        state.addProperty("revision",menu.getStateId());state.addProperty("commandAck",acknowledgement);
        String type=open?BuiltInRegistries.MENU.getKey(menu.getType()).toString():"minecraft:inventory";
        state.addProperty("type",type);state.addProperty("title",open&&clientMenuId==menu.containerId&&!clientMenuTitle.isEmpty()?clientMenuTitle:type);
        JsonArray slots=new JsonArray();
        if(open)for(int index=0;index<menu.slots.size();index++){
            var slot=menu.slots.get(index);JsonObject entry=describeItem(slot.getItem());entry.addProperty("index",index);entry.addProperty("x",slot.x);entry.addProperty("y",slot.y);
            entry.addProperty("active",slot.isActive());entry.addProperty("mayPickup",slot.mayPickup(player));entry.addProperty("slotMax",slot.getMaxStackSize());slots.add(entry);
        }
        state.addProperty("slotCount",open?menu.slots.size():0);state.add("slots",slots);state.add("carried",describeItem(menu.getCarried()));
        writeJson("menu-v2.json",state);
    }

    void nativeExplosion(Object entity,Float power){
        try{
            if(!running||!prepared||!(entity instanceof PrimedTnt tnt)||!(tnt.level() instanceof ServerLevel level)
                ||!(level.getServer() instanceof IntegratedServer server)||server!=currentServer||!validWorld(server))return;
            if(tnt.getX()<originX-64||tnt.getX()>originX+SIZE+64||tnt.getY()<originY-64||tnt.getY()>originY+SIZE+64||tnt.getZ()<originZ-64||tnt.getZ()>originZ+SIZE+64)return;
            JsonObject event=new JsonObject();event.addProperty("id",++nextEventId);event.addProperty("timestamp",System.currentTimeMillis());
            event.addProperty("type","minecraft:tnt");event.addProperty("uuid",tnt.getUUID().toString());event.addProperty("dimension",level.dimension().identifier().toString());
            event.addProperty("x",tnt.getX());event.addProperty("y",tnt.getY(0.0625));event.addProperty("z",tnt.getZ());event.addProperty("power",power);
            event.addProperty("evidence","after-native-Level.explode");explosionEvents.add(event);while(explosionEvents.size()>256)explosionEvents.removeFirst();
            explosionDirty=true;flushExplosions();
            nextSnapshot=0;log("EXPLOSION native id="+nextEventId+" position="+tnt.position()+" power="+power);
        }catch(Throwable failure){fault(failure);}
    }

    void flushExplosions(){
        JsonObject document=new JsonObject();document.addProperty("version",2);document.addProperty("lastId",nextEventId);JsonArray events=new JsonArray();explosionEvents.forEach(events::add);document.add("events",events);
        if(writeJson("explosion-v2.json",document))explosionDirty=false;
    }

    boolean writeJson(String name,JsonObject value){
        return writeJsonFile(root.resolve(name),value);
    }
    boolean writeJsonFile(Path target,JsonObject value){
        Path temporary=target.resolveSibling(target.getFileName()+".tmp");
        try{Files.writeString(temporary,JSON.toJson(value),StandardOpenOption.CREATE,StandardOpenOption.TRUNCATE_EXISTING);
            try{Files.move(temporary,target,StandardCopyOption.ATOMIC_MOVE,StandardCopyOption.REPLACE_EXISTING);}
            catch(java.nio.file.AtomicMoveNotSupportedException ignored){Files.move(temporary,target,StandardCopyOption.REPLACE_EXISTING);}return true;
        }catch(Throwable failure){fault(failure);return false;}
    }

    synchronized void publish(IntegratedServer server,ServerPlayer player,boolean valid){
        INTS.setVolatile(memory,8,sequence+1);VarHandle.fullFence();
        memory.putInt(0,0x32434D53);memory.putInt(4,2);memory.putInt(12,(valid?1:0)|(prepared&&valid?2:0)|(hidden?4:0));
        memory.putLong(16,System.currentTimeMillis());memory.putInt(24,SIZE);memory.putInt(28,SIZE);memory.putInt(32,SIZE);
        memory.putInt(36,originX);memory.putInt(40,originY);memory.putInt(44,originZ);memory.putInt(48,paletteRevision);memory.putInt(52,COUNT);
        memory.putInt(56,acknowledgement);memory.putInt(60,commandResult);memory.putLong(64,server==null?0:server.getTickCount());
        if(player!=null){memory.putDouble(72,player.getX());memory.putDouble(80,player.getY());memory.putDouble(88,player.getZ());memory.putFloat(96,player.getYRot());memory.putFloat(100,player.getXRot());}
        memory.putInt(104,12);int nonair=0;for(int i=0;i<records.length;i++){memory.putInt(RECORDS+i*4,records[i]);if(i%3==0&&records[i]>0)nonair++;}
        memory.putInt(108,nonair);sequence+=2;INTS.setRelease(memory,8,sequence);
    }

    void ack(Command cmd,int result,String detail){commandResult=result;acknowledgement=cmd.sequence;log("COMMAND seq="+cmd.sequence+" op="+cmd.operation+" result="+result+" "+detail);}
    void fault(Throwable error){long now=System.currentTimeMillis();if(now-lastFault>5000){lastFault=now;log("ERROR "+error);}}
    synchronized void log(String text){try{Files.writeString(root.resolve("mc-v2.log"),Instant.now()+" "+text+"\n",StandardOpenOption.CREATE,StandardOpenOption.APPEND);}catch(Exception ignored){}}
    @Override public void close(){
        if(!running)return;running=false;prepared=false;timer.shutdownNow();semc.v2.TntHooks.setListener(null);
        if(hidden)Minecraft.getInstance().execute(()->{try{restoreWindow("backend stopped");}catch(Throwable failure){fault(failure);}});
        if(chunkStream!=null)chunkStream.close();
        try{channel.close();}catch(Exception ignored){}log("STOP v2 backend; native game remains running");
    }
}
