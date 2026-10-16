package semc.v2;
import java.lang.classfile.ClassFile;
import java.lang.classfile.instruction.InvokeInstruction;
import java.nio.file.Path;
import java.util.jar.JarFile;
public final class VerifyTntTransform {
    public static void main(String[] args)throws Exception{
        try(JarFile game=new JarFile(args[0])){
            byte[] original=game.getInputStream(game.getJarEntry("net/minecraft/world/entity/item/PrimedTnt.class")).readAllBytes();
            byte[] transformed=TntHooks.transformTnt(original);
            var model=ClassFile.of().parse(transformed);
            int[] callbacks={0};
            model.methods().forEach(method->method.code().ifPresent(code->code.forEach(element->{
                if(element instanceof InvokeInstruction call&&call.owner().asInternalName().equals("semc/v2/TntHooks")&&call.name().equalsString("nativeExplosion"))callbacks[0]++;
            })));
            if(callbacks[0]!=1)throw new IllegalStateException("Unexpected hook count "+callbacks[0]);
            System.out.println("TNT transform verified: exactly one callback after native Level.explode; bytes "+original.length+" -> "+transformed.length);
        }
    }
}
