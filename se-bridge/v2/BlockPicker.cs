using System;
using Newtonsoft.Json.Linq;
namespace SEMC.V2
{
    // A registry view: selecting never creates an item.
    public static class BlockPicker
    {
        static Func<JObject> view;
        static Action<string> select;
        static Action<string, int> craft;
        static GameplayInput owner;
        public static bool IsOpen { get { return UnifiedInventoryScreen.IsOpen; } }
        public static void Configure(Func<JObject> getView, Action<string> selectItem, Action<string, int> craftRecipe)
        { view = getView; select = selectItem; craft = craftRecipe; }
        public static void Open(GameplayInput input, string root, Action<string> log)
        {
            if (input == null || IsOpen) return;
            owner = input;
            UnifiedInventoryScreen.Open(root, view, select, craft, log, true);
        }
        public static void Close(GameplayInput input)
        { if (ReferenceEquals(owner, input)) { UnifiedInventoryScreen.Close(); owner = null; } }
    }
}
