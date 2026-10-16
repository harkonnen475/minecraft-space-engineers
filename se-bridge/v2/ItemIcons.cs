using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;

namespace SEMC.V2
{
    public sealed class ItemIcons
    {
        readonly string root;
        readonly Dictionary<string,string> cache = new Dictionary<string,string>();
        static readonly Dictionary<string,string> aliases = new Dictionary<string,string> {
            {"redstone", "redstone"}, {"tnt", "tnt_side"}, {"crafting_table", "crafting_table_front"},
            {"sticky_piston", "piston_top_sticky"}, {"piston", "piston_top"}, {"observer", "observer_front"}
        };
        public ItemIcons(string rootPath) { root = rootPath; }
        public string Get(string itemId)
        {
            if (String.IsNullOrEmpty(itemId) || itemId == "minecraft:air") return null;
            string value;
            if (cache.TryGetValue(itemId, out value)) return value;
            string name = itemId.StartsWith("minecraft:") ? itemId.Substring(10) : itemId;
            foreach (char c in name) if (!Char.IsLetterOrDigit(c) && c != '_') return null;
            string alias; if (!aliases.TryGetValue(name, out alias)) alias = name;
            string target = Path.Combine(root, "render-assets", "icons", alias + ".dds");
            if (!File.Exists(target))
            {
                string source = Path.Combine(root, "render-assets", "minecraft", "textures", "item", name + ".png");
                if (!File.Exists(source)) source = Path.Combine(root, "render-assets", "minecraft", "textures", "block", alias + ".png");
                if (File.Exists(source)) Convert(source, target); else target = null;
            }
            cache[itemId] = target; return target;
        }
        static void Convert(string source, string target)
        {
            using (var bitmap = new Bitmap(source))
            using (var file = new BinaryWriter(File.Create(target)))
            {
                const int size = 64;
                file.Write(0x20534444); file.Write(124); file.Write(0x100F); file.Write(size); file.Write(size);
                file.Write(size * 4); file.Write(0); file.Write(0);
                for (int i=0;i<11;i++) file.Write(0);
                file.Write(32); file.Write(0x41); file.Write(0); file.Write(32);
                file.Write(0x00ff0000); file.Write(0x0000ff00); file.Write(0x000000ff); file.Write(unchecked((int)0xff000000));
                file.Write(0x1000); for (int i=0;i<4;i++) file.Write(0);
                int height = Math.Min(bitmap.Width, bitmap.Height);
                for(int y=0;y<size;y++)for(int x=0;x<size;x++)
                { Color color=bitmap.GetPixel(x*bitmap.Width/size,y*height/size);file.Write(color.ToArgb()); }
            }
        }
    }
}
