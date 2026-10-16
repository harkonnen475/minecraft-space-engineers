using System;
using System.IO;
using System.Reflection;
using VRage.Plugins;

namespace SEMC.Bootstrap
{
    public sealed class Plugin : IPlugin, IHandleInputPlugin
    {
        const string Root = @"C:\Users\konra\Documents\SEMC-Lab";
        object game, runtime;
        Action update, input, dispose;
        long nextCheck;
        public void Init(object instance) { game = instance; Load(); }
        public void Update()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now >= nextCheck)
            {
                nextCheck = now + 1000;
                string flag = Path.Combine(Root, "reload-v2.flag");
                if (File.Exists(flag)) { File.Delete(flag); Unload(); Load(); }
            }
            try { if (update != null) update(); } catch (Exception e) { Log("UPDATE " + e); }
        }
        public void HandleInput() { try { if (input != null) input(); } catch (Exception e) { Log("INPUT " + e); } }
        void Load()
        {
            try
            {
                string file = Path.Combine(Root, @"se-bridge\v2\SEMC.Runtime.dll");
                var assembly = Assembly.Load(File.ReadAllBytes(file));
                var type = assembly.GetType("SEMC.V2.GameBridge", true);
                runtime = Activator.CreateInstance(type);
                dispose = (Action)Delegate.CreateDelegate(typeof(Action), runtime, type.GetMethod("Dispose"));
                type.GetMethod("Init").Invoke(runtime, new object[] { game });
                update = (Action)Delegate.CreateDelegate(typeof(Action), runtime, type.GetMethod("Update"));
                input = (Action)Delegate.CreateDelegate(typeof(Action), runtime, type.GetMethod("HandleInput"));
                Log("Runtime loaded " + File.GetLastWriteTimeUtc(file).ToString("O"));
            }
            catch (Exception e) { Log("LOAD " + e); Unload(); }
        }
        void Unload()
        {
            update = null; input = null;
            try { if (dispose != null) dispose(); } catch (Exception e) { Log("DISPOSE " + e); }
            dispose = null; runtime = null;
        }
        public void Dispose() { Unload(); }
        static void Log(string message) { try { File.AppendAllText(Path.Combine(Root, "bootstrap-v2.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine); } catch { } }
    }
}
