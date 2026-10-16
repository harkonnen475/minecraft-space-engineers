package semc.v2.runtime;

import com.google.gson.*;
import com.mojang.serialization.JsonOps;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.security.MessageDigest;
import java.util.*;
import net.minecraft.client.server.IntegratedServer;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.resources.Identifier;
import net.minecraft.resources.RegistryOps;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.item.*;
import net.minecraft.world.item.crafting.*;
import net.minecraft.world.level.block.Block;

/** Registry-driven inventory and native crafting. Every mutation runs on the server thread. */
final class Economy {
    private static final String MARKER="semc_economy_";
    final Backend backend;
    final List<Item> items=new ArrayList<>();
    final Map<String,Integer> indices=new HashMap<>();
    final Map<String,RecipeHolder<?>> recipes=new LinkedHashMap<>();
    private String loadedWorld="",lastRequest="",lastInventoryHash="";
    private JsonObject active;
    private long revision,nextSnapshot;
    private Path transactions;
    private RegistryOps<JsonElement> ops;
    Economy(Backend backend){
        this.backend=backend;
        // Keep the original wire indices; all remaining registry IDs are deterministic.
        for(String legacy:Backend.ITEM_NAMES)addItem(BuiltInRegistries.ITEM.getValue(Identifier.withDefaultNamespace(legacy)));
        BuiltInRegistries.ITEM.keySet().stream().sorted(Comparator.comparing(Object::toString)).forEach(id->{
            Item item=BuiltInRegistries.ITEM.getValue(id);if(item!=Items.AIR)addItem(item);
        });
    }
    private void addItem(Item item){String id=itemId(item);if(!indices.containsKey(id)){indices.put(id,items.size());items.add(item);}}
    String itemId(Item item){return BuiltInRegistries.ITEM.getKey(item).toString();}
    Item item(int index){if(index<0||index>=items.size())throw new IllegalArgumentException("Unknown catalog index "+index);return items.get(index);}
    int index(Item item){return indices.getOrDefault(itemId(item),-1);}
    boolean locked(){return active!=null;}
    void tick(IntegratedServer server,ServerPlayer player,long now)throws Exception{
        if(!backend.worldId.equals(loadedWorld))initialize(server,player);
        Path request=backend.root.resolve("economy-request-v3.json");
        if(Files.exists(request)){
            JsonObject command=read(request);String id=string(command,"id","");
            if(!id.isEmpty()&&!id.equals(lastRequest)){
                JsonObject response=base();response.addProperty("id",id);
                try{
                    if(integer(command,"version",0)!=3||!backend.worldId.equals(string(command,"worldId","")))throw new IllegalArgumentException("Wrong protocol or world identity");
                    JsonObject args=command.has("args")?command.getAsJsonObject("args"):new JsonObject();
                    String operation=string(command,"operation","");
                    switch(operation){
                        case "snapshot" -> {}
                        case "refresh" -> {requireUnlocked();exportCatalog(server,player);}
                        case "select" -> {requireUnlocked();select(player,args);}
                        case "grant" -> copyResult(grant(server,player,args,id),response);
                        case "prepare" -> copyResult(prepare(server,player,args),response);
                        case "commit" -> copyResult(finish(server,player,args,true),response);
                        case "abort" -> copyResult(finish(server,player,args,false),response);
                        case "status" -> copyResult(status(server,player,args),response);
                        default -> throw new IllegalArgumentException("Unknown economy operation "+operation);
                    }
                    response.addProperty("ok",true);
                }catch(Throwable failure){response.addProperty("ok",false);response.addProperty("error",failure.toString());backend.log("ECONOMY rejected "+id+": "+failure);}
                publish(player,now);response.addProperty("revision",revision);
                if(backend.writeJson("economy-response-v3.json",response))lastRequest=id;
            }
        }
        if(now>=nextSnapshot){nextSnapshot=now+250;publish(player,now);}
    }
    private void initialize(IntegratedServer server,ServerPlayer player)throws Exception{
        ops=RegistryOps.create(JsonOps.INSTANCE,server.registryAccess());
        transactions=Path.of(backend.worldPath).resolve("semc-economy-v3");Files.createDirectories(transactions);
        active=null;lastRequest="";lastInventoryHash="";
        try(var paths=Files.list(transactions)){
            for(Path file:paths.filter(p->p.getFileName().toString().endsWith(".json")).toList()){
                JsonObject tx=read(file);String state=string(tx,"status","");
                if(!backend.worldId.equals(string(tx,"worldId","")))continue;
                if(state.equals("prepared")||state.endsWith("ing")){
                    if(active!=null)throw new IllegalStateException("Multiple unsettled economy transactions; recovery required");
                    active=tx;recover(server,player,tx);
                    if(terminal(tx))active=null;
                }
            }
        }
        exportCatalog(server,player);
        loadedWorld=backend.worldId;
        backend.log("ECONOMY ready registryItems="+items.size()+" recipes="+recipes.size()+" pending="+(active==null?"none":string(active,"txId","")));
    }
    private JsonObject base(){JsonObject o=new JsonObject();o.addProperty("version",3);o.addProperty("worldId",backend.worldId);o.addProperty("timestamp",System.currentTimeMillis());return o;}
    private void exportCatalog(IntegratedServer server,ServerPlayer player){
        JsonObject catalog=base();catalog.addProperty("minecraftVersion","26.3");JsonArray entries=new JsonArray();
        for(int i=0;i<items.size();i++){
            ItemStack stack=new ItemStack(items.get(i));JsonObject entry=describe(stack);entry.addProperty("index",i);
            JsonArray tags=new JsonArray();stack.typeHolder().tags().map(t->t.location().toString()).sorted().forEach(tags::add);entry.add("tags",tags);
            entry.addProperty("enabled",stack.isItemEnabled(player.level().enabledFeatures()));entries.add(entry);
        }
        catalog.add("items",entries);catalog.addProperty("count",entries.size());writeRequired(backend.root.resolve("catalog-mc-v3.json"),catalog);
        recipes.clear();server.getRecipeManager().getRecipes().stream().sorted(Comparator.comparing(h->h.id().identifier().toString())).forEach(h->recipes.put(h.id().identifier().toString(),h));
        JsonObject document=base();JsonArray list=new JsonArray();
        for(var pair:recipes.entrySet()){
            Recipe<?> recipe=pair.getValue().value();JsonObject entry=new JsonObject();entry.addProperty("recipeId",pair.getKey());
            entry.addProperty("type",BuiltInRegistries.RECIPE_TYPE.getKey(recipe.getType()).toString());
            entry.addProperty("special",recipe.isSpecial());entry.addProperty("craftable",false);
            try{
                entry.add("nativeData",Recipe.DIRECT_CODEC.encodeStart(ops,recipe).getOrThrow());
                PlacementInfo placement=recipe.placementInfo();JsonArray ingredients=new JsonArray();
                for(Ingredient ingredient:placement.ingredients()){
                    JsonObject ing=new JsonObject();JsonArray choices=new JsonArray();ingredient.items().map(h->itemId(h.value())).sorted().forEach(choices::add);
                    ing.add("items",choices);ing.add("nativeData",Ingredient.CODEC.encodeStart(ops,ingredient).getOrThrow());ingredients.add(ing);
                }
                entry.add("ingredients",ingredients);JsonArray layout=new JsonArray();for(int slot:placement.slotsToIngredientIndex())layout.add(slot);entry.add("layout",layout);
                int width=recipe instanceof ShapedRecipe shaped?shaped.getWidth():Math.min(3,layout.size());
                int height=recipe instanceof ShapedRecipe shaped?shaped.getHeight():(layout.size()+2)/3;
                entry.addProperty("width",width);entry.addProperty("height",height);
                if(recipe instanceof CraftingRecipe crafting&&!placement.isImpossibleToPlace()&&width>0&&height>0){
                    List<ItemStack> sample=new ArrayList<>();
                    for(int i=0;i<width*height;i++){
                        int ingredient=i<layout.size()?layout.get(i).getAsInt():-1;
                        sample.add(ingredient<0?ItemStack.EMPTY:new ItemStack(placement.ingredients().get(ingredient).items().findFirst().orElseThrow()));
                    }
                    CraftingInput input=CraftingInput.of(width,height,sample);ItemStack result=crafting.assemble(input);
                    entry.add("result",describe(result));entry.addProperty("craftable",!result.isEmpty()&&crafting.matches(input,player.level()));
                }
            }catch(Throwable failure){entry.addProperty("importNote",failure.getClass().getSimpleName()+": "+failure.getMessage());}
            list.add(entry);
        }
        document.add("recipes",list);document.addProperty("count",list.size());writeRequired(backend.root.resolve("recipes-mc-v3.json"),document);
    }
    JsonObject describe(ItemStack stack){
        JsonObject out=backend.describeItem(stack);JsonElement data=encode(stack);out.add("stackData",data);out.addProperty("stackHash",hash(data));
        out.addProperty("index",stack.isEmpty()?-1:index(stack.getItem()));out.addProperty("damage",stack.isEmpty()?0:stack.getDamageValue());
        return out;
    }
    JsonElement encode(ItemStack stack){return ItemStack.OPTIONAL_CODEC.encodeStart(ops,stack).getOrThrow();}
    private ItemStack decode(JsonElement data){return ItemStack.OPTIONAL_CODEC.parse(ops,data).getOrThrow();}
    private void publish(ServerPlayer player,long now){
        JsonObject state=base();JsonArray slots=new JsonArray();
        for(int i=0;i<36;i++){JsonObject slot=describe(player.getInventory().getItem(i));slot.addProperty("slot",i);slots.add(slot);}
        String current=hash(slots);if(!current.equals(lastInventoryHash)){revision++;lastInventoryHash=current;}
        state.addProperty("revision",revision);state.addProperty("inventoryHash",current);state.addProperty("selectedSlot",player.getInventory().getSelectedSlot());
        state.addProperty("creative",player.gameMode.isCreative());state.add("slots",slots);
        if(active!=null){state.addProperty("pendingTransaction",string(active,"txId",""));state.addProperty("transactionStatus",string(active,"status",""));}
        backend.writeJson("economy-state-v3.json",state);
    }
    private void select(ServerPlayer player,JsonObject args){
        int slot=integer(args,"slot",-1);
        if(args.has("slot")&&(slot<0||slot>=36))throw new IllegalArgumentException("Selection slot is outside the shared inventory");
        if(slot<0){
            Item requested=nativeItem(string(args,"itemId",""));ItemStack held=player.getInventory().getSelectedItem();
            if(!held.isEmpty()&&held.getItem()==requested)slot=player.getInventory().getSelectedSlot();
            else for(int i=0;i<36;i++)if(player.getInventory().getItem(i).getItem()==requested&&!player.getInventory().getItem(i).isEmpty()){slot=i;break;}
        }
        if(slot<0||slot>=36||player.getInventory().getItem(slot).isEmpty())throw new IllegalArgumentException("Item is not in the shared inventory");
        ItemStack actual=player.getInventory().getItem(slot);validateSelection(args,actual,encode(actual));
        if(slot<9)player.getInventory().setSelectedSlot(slot);else player.getInventory().pickSlot(slot);
        backend.selectedItem=index(player.getInventory().getSelectedItem().getItem());player.inventoryMenu.broadcastChanges();
    }
    static void validateSelection(JsonObject args,ItemStack actual,JsonElement encoded){
        if(actual.isEmpty())throw new IllegalStateException("Selected inventory stack is empty");
        if(args.has("itemId")&&!BuiltInRegistries.ITEM.getKey(actual.getItem()).toString().equals(args.get("itemId").getAsString()))
            throw new IllegalStateException("Selected inventory slot now contains another item");
        if(args.has("stackHash")&&!hash(encoded).equals(args.get("stackHash").getAsString()))
            throw new IllegalStateException("Selected inventory stack changed; refresh the inventory");
    }
    void select(ServerPlayer player,int index){JsonObject args=new JsonObject();args.addProperty("itemId",itemId(item(index)));requireUnlocked();select(player,args);}
    private Item nativeItem(String id){Identifier key=Identifier.parse(id);if(!BuiltInRegistries.ITEM.containsKey(key))throw new IllegalArgumentException("Unknown registry item "+id);Item item=BuiltInRegistries.ITEM.getValue(key);if(item==Items.AIR)throw new IllegalArgumentException("Air is not an inventory item");return item;}
    private JsonObject grant(IntegratedServer server,ServerPlayer player,JsonObject args,String requestId){
        if(!player.gameMode.isCreative())throw new IllegalStateException("Explicit grants require creative mode");
        ItemStack stack=args.has("stackData")?decode(args.get("stackData")):new ItemStack(nativeItem(string(args,"itemId","")),integer(args,"count",1));
        if(stack.isEmpty()||stack.getCount()<1||stack.getCount()>4096)throw new IllegalArgumentException("Grant count outside 1..4096");
        JsonObject transaction=new JsonObject();transaction.addProperty("txId","creative-grant:"+requestId);transaction.add("takes",new JsonArray());JsonArray gives=new JsonArray();
        int count=stack.getCount();while(count>0){int part=Math.min(count,stack.getMaxStackSize());gives.add(encode(stack.copyWithCount(part)));count-=part;}transaction.add("gives",gives);
        prepare(server,player,transaction);JsonObject result=finish(server,player,transaction,true);backend.log("ECONOMY explicit creative grant "+stack);return result;
    }
    private JsonObject prepare(IntegratedServer server,ServerPlayer player,JsonObject args){
        String txId=transactionId(args);Path path=transactionPath(txId);String fingerprint=hash(args);
        if(Files.exists(path)){JsonObject existing=read(path);if(!fingerprint.equals(string(existing,"requestHash","")))throw new IllegalArgumentException("Transaction ID reused with different contents");if(string(existing,"status","").endsWith("ing"))recover(server,player,existing);return existing;}
        requireUnlocked();if(player.containerMenu!=player.inventoryMenu)throw new IllegalStateException("Close the native container before crafting");
        JsonArray takes=args.has("takes")?args.getAsJsonArray("takes"):new JsonArray();List<ItemStack> before=inventory(player),reserved=copy(before),escrow=new ArrayList<>();
        Map<Integer,Integer> quantities=new HashMap<>();
        for(JsonElement value:takes){
            JsonObject take=value.getAsJsonObject();int slot=integer(take,"slot",-1),count=integer(take,"count",0);
            if(slot<0||slot>=36||count<1||quantities.containsKey(slot))throw new IllegalArgumentException("Invalid or duplicate inventory take");
            ItemStack source=before.get(slot);if(source.getCount()<count||!hash(encode(source)).equals(string(take,"stackHash","")))throw new IllegalStateException("Inventory changed; refresh the recipe");
            quantities.put(slot,count);escrow.add(source.copyWithCount(count));reserved.get(slot).shrink(count);
        }
        List<ItemStack> gives=new ArrayList<>();JsonObject result=null;
        if(args.has("craft")){
            JsonObject craft=args.getAsJsonObject("craft");String recipeId=string(craft,"recipeId","");RecipeHolder<?> holder=recipes.get(recipeId);
            if(holder==null||!(holder.value() instanceof CraftingRecipe recipe))throw new IllegalArgumentException("Unknown native crafting recipe "+recipeId);
            List<CraftingInput> inputs=craftInputs(craft,before,quantities,this::nativeItem);int total=0;
            for(CraftingInput input:inputs){
                if(!recipe.matches(input,player.level()))throw new IllegalArgumentException("Native recipe rejected the grid");
                ItemStack output=recipe.assemble(input);if(output.isEmpty())throw new IllegalArgumentException("Native recipe produced no output");
                if(result==null)result=describe(output);total+=output.getCount();gives.add(output);
                for(ItemStack remainder:recipe.getRemainingItems(input))if(!remainder.isEmpty())gives.add(remainder.copy());
            }
            result.addProperty("totalCount",total);result.addProperty("craftCount",inputs.size());
        }else if(args.has("gives")){for(JsonElement data:args.getAsJsonArray("gives")){ItemStack stack=decode(data);if(!stack.isEmpty())gives.add(stack);}}
        List<ItemStack> committed=copy(reserved);for(ItemStack output:gives)insert(committed,output);
        JsonObject tx=base();tx.addProperty("txId",txId);tx.addProperty("requestHash",fingerprint);tx.addProperty("status","preparing");
        tx.add("before",encodeList(before));tx.add("reserved",encodeList(reserved));tx.add("committed",encodeList(committed));tx.add("escrow",encodeList(escrow));tx.add("gives",encodeList(gives));if(result!=null)tx.add("result",result);
        writeRequired(path,tx);active=tx;applyPhase(server,player,tx,"prepared",reserved);return tx;
    }
    private JsonObject finish(IntegratedServer server,ServerPlayer player,JsonObject args,boolean commit){
        String txId=transactionId(args);JsonObject tx=status(server,player,args);String state=string(tx,"status","");
        if(state.equals("unknown")&&!commit)return tx;
        if(state.equals(commit?"committed":"aborted"))return tx;
        if(terminal(tx))throw new IllegalStateException("Transaction is already "+state);
        if(active==null||!txId.equals(string(active,"txId","")))throw new IllegalStateException("Transaction is not active");
        if(!state.equals("prepared"))throw new IllegalStateException("Transaction requires recovery: "+state);
        // Ingredients already reside in escrow. Preserve native pickups or slot moves
        // occurring between phases; never replace them with an old whole-bag snapshot.
        List<ItemStack> current=inventory(player),next=copy(current);
        for(ItemStack stack:decodeList(tx.getAsJsonArray(commit?"gives":"escrow")))insert(next,stack);
        tx.add("finishBefore",encodeList(current));tx.add("finishAfter",encodeList(next));
        String phase=commit?"committed":"aborted";tx.addProperty("status",commit?"committing":"aborting");writeRequired(transactionPath(txId),tx);
        applyPhase(server,player,tx,phase,next);active=null;return tx;
    }
    /** Separate grids let a batch consume fragmented stacks without flattening components. */
    static List<CraftingInput> craftInputs(JsonObject craft,List<ItemStack> before,Map<Integer,Integer> quantities,java.util.function.Function<String,Item> itemResolver){
        int width=integer(craft,"width",0),height=integer(craft,"height",0);
        JsonArray grids=craft.has("grids")?craft.getAsJsonArray("grids"):null;
        int count=integer(craft,"count",grids==null?1:grids.size());
        if(count<1||count>64||grids!=null&&grids.size()!=count)throw new IllegalArgumentException("Craft count/grids must agree within 1..64");
        if(width<1||width>3||height<1||height>3)throw new IllegalArgumentException("Crafting grid must be within 3x3");
        Map<Integer,Integer> used=new HashMap<>();List<CraftingInput> inputs=new ArrayList<>();
        for(int repeat=0;repeat<count;repeat++){
            JsonArray grid=grids==null?craft.getAsJsonArray("grid"):grids.get(repeat).getAsJsonArray();
            if(grid==null||grid.size()!=width*height)throw new IllegalArgumentException("Crafting grid dimensions disagree");
            List<ItemStack> cells=new ArrayList<>();
            for(JsonElement element:grid){
                if(element.isJsonNull()){cells.add(ItemStack.EMPTY);continue;}JsonObject cell=element.getAsJsonObject();
                if(cell.has("slot")){
                    int slot=integer(cell,"slot",-1);if(slot<0||slot>=before.size()||!quantities.containsKey(slot))throw new IllegalArgumentException("Crafting slot was not reserved");
                    ItemStack stack=before.get(slot);if(cell.has("itemId")&&!BuiltInRegistries.ITEM.getKey(stack.getItem()).toString().equals(cell.get("itemId").getAsString()))throw new IllegalArgumentException("Craft item ID disagrees with native slot");
                    cells.add(stack.copyWithCount(1));used.merge(slot,1,Integer::sum);
                }else{
                    if(!cell.has("foreign")||!cell.get("foreign").getAsBoolean())throw new IllegalArgumentException("Missing native slot or explicit foreign credit");
                    cells.add(new ItemStack(itemResolver.apply(string(cell,"itemId",""))));
                }
            }
            inputs.add(CraftingInput.of(width,height,cells));
        }
        if(!used.equals(quantities))throw new IllegalArgumentException("Reserved quantities do not match native recipe inputs");
        return inputs;
    }
    private JsonObject status(JsonObject args){String id=transactionId(args);Path path=transactionPath(id);if(!Files.exists(path)){JsonObject unknown=base();unknown.addProperty("txId",id);unknown.addProperty("status","unknown");return unknown;}JsonObject tx=read(path);if(!backend.worldId.equals(string(tx,"worldId","")))throw new IllegalStateException("Transaction belongs to another world");return tx;}
    private JsonObject status(IntegratedServer server,ServerPlayer player,JsonObject args){
        JsonObject tx=status(args);if(string(tx,"status","").endsWith("ing")){
            if(active!=null&&!string(active,"txId","").equals(string(tx,"txId","")))throw new IllegalStateException("Another inventory transaction is active");
            active=tx;recover(server,player,tx);if(terminal(tx))active=null;
        }return tx;
    }
    private void recover(IntegratedServer server,ServerPlayer player,JsonObject tx){
        String state=string(tx,"status","");if(state.equals("prepared"))return;
        String phase=state.equals("preparing")?"prepared":state.equals("committing")?"committed":"aborted";
        String source=state.equals("preparing")?"before":"finishBefore",target=phase.equals("prepared")?"reserved":"finishAfter";
        if(player.entityTags().contains(marker(tx,phase))){save(server);tx.addProperty("status",phase);writeRequired(transactionPath(string(tx,"txId","")),tx);return;}
        if(!hash(encodeList(inventory(player))).equals(hash(tx.get(source))))throw new IllegalStateException("Ambiguous native transaction recovery; inventory retained without applying twice");
        applyPhase(server,player,tx,phase,decodeList(tx.getAsJsonArray(target)));
    }
    private void applyPhase(IntegratedServer server,ServerPlayer player,JsonObject tx,String phase,List<ItemStack> inventory){
        // Marker and inventory are serialized in the same native player NBT save.
        for(String tag:new ArrayList<>(player.entityTags()))if(tag.startsWith(MARKER))player.removeTag(tag);
        if(!player.addTag(marker(tx,phase)))throw new IllegalStateException("Could not install native transaction marker");
        apply(player,inventory);
        save(server);tx.addProperty("status",phase);tx.addProperty("updatedAt",System.currentTimeMillis());writeRequired(transactionPath(string(tx,"txId","")),tx);
    }
    private String marker(JsonObject tx,String phase){return MARKER+hash(new JsonPrimitive(string(tx,"txId",""))).substring(0,32)+"_"+phase;}
    private void save(IntegratedServer server){if(!server.saveEverything(false,true,true))throw new IllegalStateException("Native save did not complete");}
    private static boolean terminal(JsonObject tx){String state=string(tx,"status","");return state.equals("committed")||state.equals("aborted");}
    private void requireUnlocked(){if(active!=null)throw new IllegalStateException("Inventory reserved by transaction "+string(active,"txId",""));}
    private String transactionId(JsonObject args){String id=string(args,"txId","");if(id.isEmpty()||id.length()>160)throw new IllegalArgumentException("Invalid transaction ID");return id;}
    private Path transactionPath(String id){return transactions.resolve(hash(new JsonPrimitive(id))+".json");}
    private static void copyResult(JsonObject from,JsonObject to){for(String field:List.of("txId","status","result","updatedAt"))if(from.has(field))to.add(field,from.get(field).deepCopy());}
    private List<ItemStack> inventory(ServerPlayer player){List<ItemStack> result=new ArrayList<>();for(int i=0;i<36;i++)result.add(player.getInventory().getItem(i).copy());return result;}
    private static List<ItemStack> copy(List<ItemStack> source){return new ArrayList<>(source.stream().map(ItemStack::copy).toList());}
    private void apply(ServerPlayer player,List<ItemStack> inventory){for(int i=0;i<36;i++)player.getInventory().setItem(i,inventory.get(i).copy());player.getInventory().setChanged();player.inventoryMenu.broadcastChanges();}
    private JsonArray encodeList(List<ItemStack> items){JsonArray list=new JsonArray();for(ItemStack stack:items)list.add(encode(stack));return list;}
    private List<ItemStack> decodeList(JsonArray array){List<ItemStack> list=new ArrayList<>();for(JsonElement data:array)list.add(decode(data));return list;}
    private static void insert(List<ItemStack> into,ItemStack value){
        ItemStack remaining=value.copy();
        for(ItemStack stack:into)if(!stack.isEmpty()&&ItemStack.isSameItemSameComponents(stack,remaining)){
            int move=Math.min(remaining.getCount(),Math.max(0,stack.getMaxStackSize()-stack.getCount()));stack.grow(move);remaining.shrink(move);if(remaining.isEmpty())return;
        }
        for(int i=0;i<into.size();i++)if(into.get(i).isEmpty()){int move=Math.min(remaining.getCount(),remaining.getMaxStackSize());into.set(i,remaining.copyWithCount(move));remaining.shrink(move);if(remaining.isEmpty())return;}
        throw new IllegalStateException("Shared Minecraft inventory is full");
    }
    boolean canReceive(ServerPlayer player,List<ItemStack> drops){try{List<ItemStack> copy=inventory(player);for(ItemStack drop:drops)insert(copy,drop);return true;}catch(IllegalStateException full){return false;}}
    void receive(ServerPlayer player,List<ItemStack> drops){requireUnlocked();List<ItemStack> copy=inventory(player);for(ItemStack drop:drops)insert(copy,drop);apply(player,copy);}
    private void writeRequired(Path path,JsonObject value){if(!backend.writeJsonFile(path,value))throw new IllegalStateException("Could not persist economy state "+path.getFileName());}
    private static JsonObject read(Path path){try{return JsonParser.parseString(Files.readString(path)).getAsJsonObject();}catch(Exception failure){throw new IllegalStateException("Cannot read "+path.getFileName(),failure);}}
    static String hash(JsonElement element){try{return HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(Backend.JSON.toJson(canonical(element)).getBytes(StandardCharsets.UTF_8)));}catch(Exception impossible){throw new IllegalStateException(impossible);}}
    private static JsonElement canonical(JsonElement value){if(value.isJsonObject()){JsonObject result=new JsonObject();new TreeSet<>(value.getAsJsonObject().keySet()).forEach(k->result.add(k,canonical(value.getAsJsonObject().get(k))));return result;}if(value.isJsonArray()){JsonArray result=new JsonArray();value.getAsJsonArray().forEach(v->result.add(canonical(v)));return result;}return value;}
    private static String string(JsonObject o,String key,String fallback){return o.has(key)?o.get(key).getAsString():fallback;}
    private static int integer(JsonObject o,String key,int fallback){return o.has(key)?o.get(key).getAsInt():fallback;}
}
