package semc.v2;

import java.lang.classfile.ClassFile;
import java.lang.classfile.ClassTransform;
import java.lang.classfile.instruction.InvokeInstruction;
import java.lang.constant.ClassDesc;
import java.lang.constant.ConstantDescs;
import java.lang.constant.MethodTypeDesc;
import java.lang.instrument.ClassFileTransformer;
import java.lang.instrument.Instrumentation;
import java.security.ProtectionDomain;
import java.util.function.BiConsumer;

/** Emits only after the native explosion call actually ran, never from entity removal. */
public final class TntHooks {
    private static volatile BiConsumer<Object,Float> listener;
    private static boolean installed;
    public static void setListener(BiConsumer<Object,Float> callback){listener=callback;}
    public static void nativeExplosion(Object entity,float power){
        BiConsumer<Object,Float> callback=listener;
        if(callback!=null)try{callback.accept(entity,power);}catch(Throwable ignored){}
    }
    public static synchronized void install(Instrumentation instrumentation)throws Exception{
        if(installed)return;
        if(!instrumentation.isRetransformClassesSupported())throw new IllegalStateException("TNT hook requires retransformation support");
        ClassFileTransformer transformer=new ClassFileTransformer(){
            @Override public byte[] transform(Module module,ClassLoader loader,String name,Class<?> type,ProtectionDomain domain,byte[] bytes){
                if(!"net/minecraft/world/entity/item/PrimedTnt".equals(name))return null;
                return transformTnt(bytes);
            }
        };
        instrumentation.addTransformer(transformer,true);
        try{
            for(Class<?> type:instrumentation.getAllLoadedClasses())if(type.getName().equals("net.minecraft.world.entity.item.PrimedTnt")){
                if(!instrumentation.isModifiableClass(type))throw new IllegalStateException("PrimedTnt is not modifiable");
                instrumentation.retransformClasses(type);
            }
        }catch(Exception failure){
            instrumentation.removeTransformer(transformer);
            throw failure;
        }
        installed=true;
    }
    public static byte[] transformTnt(byte[] bytes){
        int[] injected={0};
        ClassFile classFile=ClassFile.of();
        byte[] result=classFile.transformClass(classFile.parse(bytes),ClassTransform.transformingMethodBodies(
            method->method.methodName().equalsString("explode")&&method.methodType().equalsString("()V"),
            (code,element)->{
                code.with(element);
                if(element instanceof InvokeInstruction call && call.owner().asInternalName().equals("net/minecraft/world/level/Level")
                    && call.name().equalsString("explode") && call.type().equalsString("(Lnet/minecraft/world/entity/Entity;Lnet/minecraft/world/damagesource/DamageSource;Lnet/minecraft/world/level/ExplosionDamageCalculator;DDDFZLnet/minecraft/world/level/Level$ExplosionInteraction;)V")){
                    code.aload(0).aload(0).getfield(ClassDesc.of("net.minecraft.world.entity.item.PrimedTnt"),"explosionPower",ConstantDescs.CD_float)
                        .invokestatic(ClassDesc.of("semc.v2.TntHooks"),"nativeExplosion",MethodTypeDesc.of(ConstantDescs.CD_void,ConstantDescs.CD_Object,ConstantDescs.CD_float));
                    injected[0]++;
                }
            }));
        if(injected[0]!=1)throw new IllegalStateException("Expected exactly one native TNT explosion call; found "+injected[0]);
        return result;
    }
}
