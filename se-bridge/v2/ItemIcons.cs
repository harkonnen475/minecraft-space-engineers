using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Newtonsoft.Json.Linq;
namespace SEMC.V2
{
    public sealed class ItemIcons
    {
        readonly string root;
        readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        public ItemIcons(string rootPath) { root = rootPath; }
        // SE uses native definition icons; Minecraft resolves item-model textures.
        public string Get(string itemId, string definitionIcon)
        {
            if (!String.IsNullOrEmpty(definitionIcon))
            {
                string icon = definitionIcon.Replace('/', '\\');
                if (Path.IsPathRooted(icon)) { if (File.Exists(icon)) return icon; }
                else if (icon.StartsWith("Textures\\", StringComparison.OrdinalIgnoreCase) && !icon.Contains("..")) return icon;
            }
            return Get(itemId);
        }
        public string Get(string itemId)
        {
            if (String.IsNullOrEmpty(itemId) || itemId == "minecraft:air") return null;
            string value;
            if (cache.TryGetValue(itemId, out value)) return value;
            int separator = itemId.IndexOf(':');
            if (separator <= 0) return null;
            string ns = itemId.Substring(0, separator), name = itemId.Substring(separator + 1);
            if (ns == "se" || !SafeResource(ns) || ns.Contains("/") || !SafeResource(name)) return null;
            try
            {
                string source = ResourcePath("textures", ns + ":item/" + name, ".png");
                if (source == null || !File.Exists(source)) source = FindModelTexture(ns + ":item/" + name);
                if (source == null || !File.Exists(source)) source = FindModelTexture(ns + ":block/" + name);
                if (source == null || !File.Exists(source)) source = ResourcePath("textures", ns + ":block/" + name, ".png");
                if (source != null && File.Exists(source))
                {
                    string directory = Path.Combine(root, "render-assets", "icons", "registry", ns);
                    Directory.CreateDirectory(directory);
                    value = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar) + ".dds");
                    Directory.CreateDirectory(Path.GetDirectoryName(value));
                    if (!File.Exists(value) || File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(value)) ConvertTexture(source, value);
                }
                else value = null;
            }
            catch (IOException) { value = null; }
            catch (ArgumentException) { value = null; }
            catch (System.Runtime.InteropServices.ExternalException) { value = null; }
            catch (Newtonsoft.Json.JsonException) { value = null; }
            cache[itemId] = value;
            return value;
        }
        string FindModelTexture(string model)
        {
            var textures = new Dictionary<string, string>(StringComparer.Ordinal);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            string current = model;
            for (int depth = 0; depth < 16 && !String.IsNullOrEmpty(current) && visited.Add(current); depth++)
            {
                string path = ResourcePath("models", current, ".json");
                if (path == null || !File.Exists(path)) break;
                JObject data = JObject.Parse(File.ReadAllText(path));
                JObject declared = data["textures"] as JObject;
                if (declared != null) foreach (JProperty property in declared.Properties())
                    if (!textures.ContainsKey(property.Name)) textures[property.Name] = (string)property.Value;
                current = (string)data["parent"];
            }
            string[] candidates = { "layer0", "front", "side", "all", "top", "end" };
            foreach (string candidate in candidates)
            {
                string texture;
                if (!textures.TryGetValue(candidate, out texture)) continue;
                var references = new HashSet<string>(StringComparer.Ordinal);
                while (texture != null && texture.StartsWith("#", StringComparison.Ordinal))
                {
                    string key = texture.Substring(1);
                    if (!references.Add(key) || !textures.TryGetValue(key, out texture)) { texture = null; break; }
                }
                string path = ResourcePath("textures", texture, ".png");
                if (path != null && File.Exists(path)) return path;
            }
            return null;
        }
        string ResourcePath(string folder, string resource, string suffix)
        {
            if (String.IsNullOrEmpty(resource)) return null;
            int colon = resource.IndexOf(':');
            string ns = colon < 0 ? "minecraft" : resource.Substring(0, colon);
            string name = colon < 0 ? resource : resource.Substring(colon + 1);
            if (!SafeResource(ns) || !SafeResource(name)) return null;
            return Path.Combine(root, "render-assets", ns, folder, name.Replace('/', Path.DirectorySeparatorChar) + suffix);
        }
        static bool SafeResource(string name)
        {
            if (String.IsNullOrEmpty(name) || name.StartsWith("/", StringComparison.Ordinal) || name.Contains("..")) return false;
            foreach (char c in name) if (!Char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '/' && c != '.') return false;
            return true;
        }
        static void ConvertTexture(string source, string target)
        {
            string temporary = target + ".tmp";
            using (var bitmap = new Bitmap(source))
            using (var file = new BinaryWriter(File.Create(temporary)))
            {
                const int size = 64;
                file.Write(0x20534444); file.Write(124); file.Write(0x100F); file.Write(size); file.Write(size);
                file.Write(size * 4); file.Write(0); file.Write(0);
                for (int i = 0; i < 11; i++) file.Write(0);
                file.Write(32); file.Write(0x41); file.Write(0); file.Write(32);
                file.Write(0x00ff0000); file.Write(0x0000ff00); file.Write(0x000000ff); file.Write(unchecked((int)0xff000000));
                file.Write(0x1000); for (int i = 0; i < 4; i++) file.Write(0);
                int height = Math.Min(bitmap.Width, bitmap.Height);
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                    file.Write(bitmap.GetPixel(x * bitmap.Width / size, y * height / size).ToArgb());
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(temporary, target);
        }
    }
}
