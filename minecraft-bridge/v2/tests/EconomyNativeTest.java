package semc.v2.runtime;

import com.google.gson.*;
import com.mojang.serialization.JsonOps;
import java.lang.reflect.*;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;
import java.nio.file.*;
import java.util.*;
import net.minecraft.SharedConstants;
import net.minecraft.core.RegistryAccess;
import net.minecraft.core.component.DataComponents;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.core.registries.Registries;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.RegistryOps;
import net.minecraft.server.Bootstrap;
import net.minecraft.world.item.*;
import net.minecraft.world.item.crafting.*;
import net.minecraft.world.item.enchantment.Enchantments;
import net.minecraft.world.item.component.ItemContainerContents;

/** Headless contract tests against the installed game's real ItemStack implementation. */
public final class EconomyNativeTest {
    static int passed;
    static Method insert;
    static void check(boolean ok,String name){if(!ok)throw new AssertionError(name);passed++;System.out.println("PASS "+name);}
    static List<ItemStack> bag(){List<ItemStack> bag=new ArrayList<>();for(int i=0;i<36;i++)bag.add(ItemStack.EMPTY);return bag;}
    static void put(List<ItemStack> bag,ItemStack stack)throws Exception{
        try{insert.invoke(null,bag,stack);}catch(InvocationTargetException e){if(e.getCause() instanceof RuntimeException failure)throw failure;throw e;}
    }
    static CraftingRecipe recipe(String id,RegistryOps<JsonElement> ops)throws Exception{
        try(var input=EconomyNativeTest.class.getResourceAsStream("/data/minecraft/recipe/"+id+".json")){
            if(input==null)throw new AssertionError("Missing vanilla recipe "+id);
            return (CraftingRecipe)Recipe.DIRECT_CODEC.parse(ops,JsonParser.parseReader(new InputStreamReader(input,StandardCharsets.UTF_8))).getOrThrow();
        }
    }
    public static void main(String[] args)throws Exception{
        SharedConstants.tryDetectVersion();Bootstrap.bootStrap();
        var lookup=net.minecraft.data.registries.VanillaRegistries.createWorldLookup();
        for(var pending:BuiltInRegistries.DATA_COMPONENT_INITIALIZERS.build(lookup))pending.apply();
        var ops=RegistryOps.create(JsonOps.INSTANCE,lookup);
        insert=Economy.class.getDeclaredMethod("insert",List.class,ItemStack.class);insert.setAccessible(true);
        ItemStack named=new ItemStack(Items.IRON_INGOT,7);named.set(DataComponents.CUSTOM_NAME,Component.literal("SEMC component round trip"));
        JsonElement encoded=ItemStack.OPTIONAL_CODEC.encodeStart(ops,named).getOrThrow();ItemStack decoded=ItemStack.OPTIONAL_CODEC.parse(ops,encoded).getOrThrow();
        check(ItemStack.matches(named,decoded),"native codec preserves count and custom-name component");
        ItemStack sword=new ItemStack(Items.IRON_SWORD);sword.setDamageValue(17);sword.set(DataComponents.CUSTOM_NAME,Component.literal("Used tool"));
        ItemStack swordAgain=ItemStack.OPTIONAL_CODEC.parse(ops,ItemStack.OPTIONAL_CODEC.encodeStart(ops,sword).getOrThrow()).getOrThrow();
        check(swordAgain.getDamageValue()==17&&ItemStack.matches(sword,swordAgain),"native codec preserves damaged tool");
        check(!Economy.hash(encoded).equals(Economy.hash(ItemStack.OPTIONAL_CODEC.encodeStart(ops,new ItemStack(Items.IRON_INGOT,7)).getOrThrow())),"component-bearing stack has a different optimistic-lock hash");
        check(!Economy.hash(encoded).equals(Economy.hash(ItemStack.OPTIONAL_CODEC.encodeStart(ops,named.copyWithCount(6)).getOrThrow())),"count change invalidates optimistic-lock hash");
        check(Economy.hash(JsonParser.parseString("{\"txId\":\"x\",\"a\":1}")).equals(Economy.hash(JsonParser.parseString("{\"a\":1,\"txId\":\"x\"}"))),"transaction hashes ignore JSON property order");
        List<ItemStack> items=bag();items.set(0,new ItemStack(Items.IRON_INGOT,63));put(items,new ItemStack(Items.IRON_INGOT,3));
        check(items.get(0).getCount()==64&&items.get(1).getCount()==2,"merge respects stack limit and spills into next slot");
        put(items,named);check(items.get(2).getCount()==7&&ItemStack.isSameItemSameComponents(items.get(2),named),"named and ordinary items never merge");
        put(items,named.copyWithCount(2));check(items.get(2).getCount()==9&&items.get(3).isEmpty(),"identical component stacks merge without stripping components");
        List<ItemStack> full=bag();for(int i=0;i<36;i++)full.set(i,new ItemStack(Items.DIRT,64));
        List<ItemStack> candidate=new ArrayList<>(full.stream().map(ItemStack::copy).toList());boolean refused=false;
        try{put(candidate,new ItemStack(Items.STONE,1));}catch(IllegalStateException expected){refused=true;}
        check(refused&&full.stream().allMatch(s->s.getItem()==Items.DIRT&&s.getCount()==64),"full-bag rejection leaves authoritative original unchanged");
        full.set(0,new ItemStack(Items.DIRT,63));candidate=new ArrayList<>(full.stream().map(ItemStack::copy).toList());refused=false;
        try{put(candidate,new ItemStack(Items.DIRT,2));}catch(IllegalStateException expected){refused=true;}
        check(refused&&full.get(0).getCount()==63,"partial merge followed by overflow does not modify authoritative bag");
        List<ItemStack> tools=bag();put(tools,sword);put(tools,swordAgain);
        check(tools.get(0).getCount()==1&&tools.get(1).getCount()==1,"non-stackable tools retain separate native slots");
        check(ItemStack.OPTIONAL_CODEC.parse(ops,ItemStack.OPTIONAL_CODEC.encodeStart(ops,ItemStack.EMPTY).getOrThrow()).getOrThrow().isEmpty(),"empty slots survive native codec round trip");
        sword.enchant(lookup.lookupOrThrow(Registries.ENCHANTMENT).getOrThrow(Enchantments.UNBREAKING),3);
        JsonElement swordData=ItemStack.OPTIONAL_CODEC.encodeStart(ops,sword).getOrThrow();
        check(ItemStack.matches(sword,ItemStack.OPTIONAL_CODEC.parse(ops,swordData).getOrThrow()),"enchantment registry references survive native codec round trip");
        ItemStack shulker=new ItemStack(Items.SHULKER_BOX);shulker.set(DataComponents.CONTAINER,ItemContainerContents.fromItems(List.of(sword,named)));
        JsonElement shulkerData=ItemStack.OPTIONAL_CODEC.encodeStart(ops,shulker).getOrThrow();
        check(ItemStack.matches(shulker,ItemStack.OPTIONAL_CODEC.parse(ops,shulkerData).getOrThrow()),"container component preserves nested named and enchanted stacks");
        Economy catalog=new Economy(null);check(catalog.items.size()==BuiltInRegistries.ITEM.size()-1,"catalog contains every registered non-air item without whitelist");
        for(int i=0;i<Backend.ITEM_NAMES.length;i++)check(catalog.itemId(catalog.item(i)).equals("minecraft:"+Backend.ITEM_NAMES[i]),"legacy wire index "+i+" remains compatible");
        int roundTrips=0;for(Item item:catalog.items){ItemStack stack=new ItemStack(item);ItemStack roundTrip=ItemStack.OPTIONAL_CODEC.parse(ops,ItemStack.OPTIONAL_CODEC.encodeStart(ops,stack).getOrThrow()).getOrThrow();if(!ItemStack.matches(stack,roundTrip))throw new AssertionError(item.toString());roundTrips++;}
        check(roundTrips==catalog.items.size(),"all "+roundTrips+" registered items survive native stack serialization");
        CraftingRecipe ironBlock=recipe("iron_block",ops);List<ItemStack> grid=new ArrayList<>();for(int i=0;i<9;i++)grid.add(new ItemStack(Items.IRON_INGOT));CraftingInput ironInput=CraftingInput.of(3,3,grid);
        check(ironBlock.matches(ironInput,null)&&ironBlock.assemble(ironInput).getItem()==Items.IRON_BLOCK,"native shaped recipe matches and assembles nine ingots");
        grid.set(8,new ItemStack(Items.GOLD_INGOT));check(!ironBlock.matches(CraftingInput.of(3,3,grid),null),"native recipe rejects incorrect cross-engine credit");
        CraftingRecipe unpack=recipe("iron_ingot_from_iron_block",ops);CraftingInput unpackInput=CraftingInput.of(1,1,List.of(new ItemStack(Items.IRON_BLOCK)));
        check(unpack.matches(unpackInput,null)&&unpack.assemble(unpackInput).getCount()==9,"native shapeless recipe output quantity is preserved");
        CraftingRecipe cake=recipe("cake",ops);CraftingInput cakeInput=CraftingInput.of(3,3,List.of(new ItemStack(Items.MILK_BUCKET),new ItemStack(Items.MILK_BUCKET),new ItemStack(Items.MILK_BUCKET),new ItemStack(Items.SUGAR),new ItemStack(Items.EGG),new ItemStack(Items.SUGAR),new ItemStack(Items.WHEAT),new ItemStack(Items.WHEAT),new ItemStack(Items.WHEAT)));
        // Headless data-generation registries do not bind world tags (cake's egg tag).
        // Remainder evaluation itself is independent of those tags.
        check(cake.getRemainingItems(cakeInput).stream().filter(s->s.getItem()==Items.BUCKET).count()==3,"native crafting returns all three bucket remainders");
        List<ItemStack> fragments=bag();ItemStack fragment=new ItemStack(Items.IRON_BLOCK);fragment.set(DataComponents.CUSTOM_NAME,Component.literal("fragment A"));fragments.set(7,fragment);fragments.set(8,new ItemStack(Items.IRON_BLOCK));
        JsonObject batch=JsonParser.parseString("{\"width\":1,\"height\":1,\"count\":2,\"grids\":[[{\"slot\":7,\"itemId\":\"minecraft:iron_block\"}],[{\"slot\":8,\"itemId\":\"minecraft:iron_block\"}]]}").getAsJsonObject();
        var batches=Economy.craftInputs(batch,fragments,Map.of(7,1,8,1),id->Items.AIR);
        check(batches.size()==2&&batches.stream().allMatch(input->unpack.matches(input,null))&&batches.stream().mapToInt(input->unpack.assemble(input).getCount()).sum()==18,"fragmented one-plus-one stacks craft an entire native batch");
        check(batches.get(0).getItem(0).getCustomName()!=null&&batches.get(1).getItem(0).getCustomName()==null,"batch preserves each source stack's distinct components");
        boolean incorrect=false;try{Economy.craftInputs(batch,fragments,Map.of(7,2),id->Items.AIR);}catch(IllegalArgumentException expected){incorrect=true;}
        check(incorrect,"batch cannot consume a slot outside durable reservation");
        JsonObject legacy=JsonParser.parseString("{\"width\":1,\"height\":1,\"count\":2,\"grid\":[{\"slot\":7,\"itemId\":\"minecraft:iron_block\"}]}").getAsJsonObject();fragments.set(7,fragment.copyWithCount(2));
        check(Economy.craftInputs(legacy,fragments,Map.of(7,2),id->Items.AIR).size()==2,"legacy repeated-grid batch remains compatible");
        incorrect=false;batch.addProperty("count",3);try{Economy.craftInputs(batch,fragments,Map.of(7,1,8,1),id->Items.AIR);}catch(IllegalArgumentException expected){incorrect=true;}
        check(incorrect,"batch count/grid mismatch is rejected before inventory changes");
        JsonObject selection=new JsonObject();selection.addProperty("slot",12);selection.addProperty("itemId","minecraft:iron_sword");selection.addProperty("stackHash",Economy.hash(swordData));
        Economy.validateSelection(selection,sword,swordData);check(true,"exact-stack selection accepts the requested damaged enchanted tool");
        incorrect=false;ItemStack pristine=new ItemStack(Items.IRON_SWORD);try{Economy.validateSelection(selection,pristine,ItemStack.OPTIONAL_CODEC.encodeStart(ops,pristine).getOrThrow());}catch(IllegalStateException expected){incorrect=true;}
        check(incorrect,"exact-stack selection rejects a different variant with the same item ID");
        incorrect=false;try{Economy.validateSelection(selection,named,encoded);}catch(IllegalStateException expected){incorrect=true;}
        check(incorrect,"exact-stack selection rejects a slot whose item type changed");
        selection.remove("stackHash");Economy.validateSelection(selection,pristine,ItemStack.OPTIONAL_CODEC.encodeStart(ops,pristine).getOrThrow());
        check(true,"catalog selection without a stack hash remains compatible");
        JsonObject fixtures=new JsonObject();fixtures.addProperty("passed",passed);fixtures.addProperty("catalogItems",roundTrips);fixtures.add("namedIngot",encoded);fixtures.add("enchantedDamagedSword",swordData);fixtures.add("filledShulkerBox",shulkerData);
        if(args.length>0)Files.writeString(Path.of(args[0]),new GsonBuilder().setPrettyPrinting().create().toJson(fixtures));
        System.out.println("PASSED "+passed+" native economy tests");
    }
}
