using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Sandbox.Graphics.GUI;
using Sandbox.Graphics;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Input;
using VRage.Utils;
using VRageMath;

namespace SEMC.V2
{
    // Native Minecraft owns every slot, recipe and transfer; this screen only presents it in SE.
    public sealed class ContainerBridge : IDisposable
    {
        public static bool IsOpen { get; private set; }
        readonly string path;
        readonly Action<BridgeCommand> issue;
        readonly Action<string> log;
        readonly ItemIcons icons;
        ContainerScreen screen;
        long nextRead, nextCloseAttempt;
        int closedContainer = -1;
        bool disposing;
        public ContainerBridge(string root, Action<BridgeCommand> send, Action<string> logger)
        { path=Path.Combine(root,"menu-v2.json"); issue=send;log=logger;icons=new ItemIcons(root); }
        public void Update(bool connected)
        {
            if (BridgeIO.Now() < nextRead) return;
            nextRead = BridgeIO.Now()+100;
            if (!connected) { ResetCloseRequest(); CloseRemote(); return; }
            if (!File.Exists(path)) { CloseRemote(); return; }
            JObject menu;
            try { menu=JObject.Parse(BridgeIO.ReadSharedText(path)); }
            catch(IOException){return;}catch(Newtonsoft.Json.JsonException){return;}
            bool open=(bool?)menu["open"]??false;
            long timestamp=(long?)menu["timestamp"]??0, now=BridgeIO.Now();
            if (!open || timestamp<=0 || timestamp>now+1000 || now-timestamp>3000) { ResetCloseRequest();CloseRemote();return; }
            int id=(int?)menu["containerId"]??0;
            if(id==closedContainer) { RetryClose(now); return; }
            ResetCloseRequest();
            if(screen==null || screen.ContainerId!=id)
            {
                CloseRemote();
                screen=new ContainerScreen(id,icons,Click,log);
                IsOpen=true;
                screen.Closed+=OnClosed;
                screen.SetMenu(menu);
                MyGuiSandbox.AddScreen(screen);
                log("MENU opened native container="+id+" type="+(string)menu["type"]);
            }
            else screen.SetMenu(menu);
        }
        void Click(int id,int slot,int button,bool quick)
        {
            if(disposing || screen==null || screen.ContainerId!=id || closedContainer==id)return;
            issue(new BridgeCommand{Operation=13,X=id,Y=slot,Z=button,Item=quick?1:0,Description="container click"});
        }
        void OnClosed(MyGuiScreenBase closed,bool unloading)
        {
            ContainerScreen current=screen;
            if(current==null || !ReferenceEquals(current,closed))return;
            screen=null;IsOpen=false;closedContainer=current.ContainerId;
            nextCloseAttempt=0;
            RetryClose(BridgeIO.Now());
        }
        void ResetCloseRequest() { closedContainer=-1;nextCloseAttempt=0; }
        void RetryClose(long now)
        {
            if(disposing || closedContainer<0 || now<nextCloseAttempt)return;
            nextCloseAttempt=now+500;
            try { issue(new BridgeCommand{Operation=14,X=closedContainer,Description="close container"}); }
            catch(Exception error) { log("MENU close deferred: "+error.GetType().Name+": "+error.Message); }
        }
        void CloseRemote()
        {
            ContainerScreen current=screen;if(current==null)return;
            screen=null;IsOpen=false;current.Closed-=OnClosed;current.CloseScreenNow();
        }
        public void Dispose(){disposing=true;ResetCloseRequest();CloseRemote();}

        sealed class ContainerScreen:MyGuiScreenBase
        {
            const string Blank="Textures\\GUI\\Blank.dds";
            const float Scale=.0028f, ScaleY=Scale*4/3;
            static readonly Vector4 White=new Vector4(.94f,.97f,.98f,1),Muted=new Vector4(.63f,.72f,.76f,1),Mint=new Vector4(.52f,.88f,.74f,1);
            public readonly int ContainerId;
            readonly ItemIcons icons;
            readonly Action<int,int,int,bool> click;
            readonly Action<string> log;
            readonly Dictionary<int,SlotButton> slots=new Dictionary<int,SlotButton>();
            JObject menu;
            string layout;
            MyGuiControlLabel title,carried;
            MyGuiControlImage carriedIcon;
            MyGuiControlLabel carriedCount;
            public ContainerScreen(int id,ItemIcons itemIcons,Action<int,int,int,bool> onClick,Action<string> logger)
                :base(new Vector2(.5f),new Vector4(.035f,.065f,.085f,.98f),new Vector2(.6f,.88f),true,Blank)
            {
                ContainerId=id;icons=itemIcons;click=onClick;log=logger;
                EnabledBackgroundFade=true;m_backgroundFadeColor=new Color(0,0,0,180);
                CanHideOthers=false;CanBeHidden=false;CanHaveFocus=true;DrawMouseCursor=true;
                m_closeOnEsc=true;m_canShareInput=false;CloseButtonEnabled=true;
            }
            public override string GetFriendlyName(){return "SEMC_MinecraftContainer";}
            public void SetMenu(JObject value)
            {
                menu=value;JArray list=value["slots"] as JArray;if(list==null)return;
                string nextLayout=((string)value["type"]??"")+":"+list.Count;
                if(nextLayout!=layout){layout=nextLayout;Build(list);}
                if(title!=null)title.Text=PolishTitle((string)value["type"],(string)value["title"]);
                foreach(JObject item in list){SlotButton button;if(slots.TryGetValue((int)item["index"],out button))button.SetItem(item,icons);}
                JObject held=value["carried"] as JObject;
                int count=held==null?0:(int?)held["count"]??0;
                if(carried!=null)carried.Text=count>0?"Trzymasz: "+((string)held["displayName"]??(string)held["itemId"])+" ×"+count:"";
                if(carriedIcon!=null)
                {
                    string path=count>0?icons.Get((string)held["itemId"]):null;
                    carriedIcon.Visible=path!=null;
                    if(path!=null)carriedIcon.SetTextures(new[]{path});
                    carriedCount.Visible=count>1;carriedCount.Text=count.ToString();
                }
            }
            void Build(JArray list)
            {
                base.RecreateControls(false);slots.Clear();
                title=Label("Minecraft",new Vector2(-.26f,-.38f),.9f,White);
                Label("LPM — przenieś   PPM — podziel   Shift+LPM — szybki transfer",new Vector2(-.26f,.345f),.49f,Muted);
                Label("Esc — wróć do gry",new Vector2(-.26f,.385f),.55f,Muted);
                carried=Label("",new Vector2(-.26f,.295f),.56f,Mint);
                if(list.Count==46) Label("→",new Vector2(.035f,-.14f),1.15f,Muted);
                Label("Ekwipunek",new Vector2(-.225f,.005f),.48f,Muted);
                foreach(JObject slot in list)
                {
                    int index=(int)slot["index"],x=(int?)slot["x"]??0,y=(int?)slot["y"]??0;
                    var button=new SlotButton(new Vector2((x+8-88)*Scale,(y+8)*ScaleY-.3f));
                    int captured=index;
                    button.ButtonClicked+=delegate{click(ContainerId,captured,0,MyInput.Static!=null&&MyInput.Static.IsAnyShiftKeyPressed());};
                    button.SecondaryButtonClicked+=delegate{click(ContainerId,captured,1,false);};
                    Controls.Add(button);slots[index]=button;
                }
                carriedIcon=new MyGuiControlImage(position:Vector2.Zero,size:new Vector2(.037f,.0493f),backgroundColor:Vector4.One,textures:new[]{Blank});
                carriedIcon.Visible=false;Passive(carriedIcon);Controls.Add(carriedIcon);
                carriedCount=Label("",Vector2.Zero,.5f,White);carriedCount.Visible=false;
            }
            public override bool Update(bool hasFocus)
            {
                bool result=base.Update(hasFocus);
                if(carriedIcon!=null)
                {
                    Vector2 cursor=MyGuiManager.MouseCursorPosition-GetPosition();
                    carriedIcon.Position=cursor+new Vector2(.017f,.025f);
                    carriedCount.Position=cursor+new Vector2(.03f,.045f);
                }
                return result;
            }
            MyGuiControlLabel Label(string text,Vector2 position,float scale,Vector4 color)
            {
                var label=new MyGuiControlLabel(position:position,text:text,textScale:scale,colorMask:color,font:"White",originAlign:MyGuiDrawAlignEnum.HORISONTAL_LEFT_AND_VERTICAL_CENTER);
                Passive(label);Controls.Add(label);return label;
            }
            static string PolishTitle(string type,string fallback)
            { if(type!=null&&type.Contains("crafting"))return "Stół rzemieślniczy";if(type!=null&&type.Contains("generic"))return "Skrzynia";return String.IsNullOrEmpty(fallback)?"Minecraft":fallback; }
            static void Passive(MyGuiControlBase control){control.CanHaveFocus=false;control.IsHitTestVisible=false;control.SkipForMouseTest=true;}
            sealed class SlotButton:MyGuiControlButton
            {
                string signature;
                public SlotButton(Vector2 position):base(position:position,activateOnMouseRelease:true)
                {
                    var texture=new MyGuiCompositeTexture(Blank);
                    CustomStyle=new StyleDefinition{NormalFont="White",HighlightFont="White",NormalTexture=texture,HighlightTexture=texture,FocusTexture=texture,ActiveTexture=texture,SizeOverride=new Vector2(17*Scale,17*ScaleY),BackgroundColor=new Vector4(.09f,.14f,.18f,1)};
                    BorderEnabled=true;BorderSize=1;BorderHighlightEnabled=false;
                }
                public void SetItem(JObject item,ItemIcons icons)
                {
                    string id=(string)item["itemId"]??"minecraft:air";int count=(int?)item["count"]??0;
                    string next=id+":"+count;if(next==signature)return;signature=next;Elements.Clear();
                    SetToolTip(count==0?"Puste miejsce":((string)item["displayName"]??id)+" ×"+count);
                    if(count==0)return;
                    string path=icons.Get(id);
                    if(path!=null){var image=new MyGuiControlImage(position:new Vector2(0,-.002f),size:new Vector2(.037f,.0493f),backgroundColor:Vector4.One,textures:new[]{path});Passive(image);Elements.Add(image);}
                    else{var label=new MyGuiControlLabel(position:new Vector2(0,-.003f),text:((string)item["displayName"]??id),textScale:.32f,font:"White",originAlign:MyGuiDrawAlignEnum.HORISONTAL_CENTER_AND_VERTICAL_CENTER,isAutoScaleEnabled:true,maxWidth:.05f,minimumTextScale:.25f);Passive(label);Elements.Add(label);}
                    if(count>1){var label=new MyGuiControlLabel(position:new Vector2(.024f,.018f),text:count.ToString(),textScale:.5f,font:"White",originAlign:MyGuiDrawAlignEnum.HORISONTAL_RIGHT_AND_VERTICAL_CENTER);Passive(label);Elements.Add(label);}
                }
                public override void Draw(float alpha,float backgroundAlpha)
                {bool hover=HasHighlight||HasFocus;ColorMask=hover?new Vector4(.19f,.29f,.31f,1):new Vector4(.09f,.14f,.18f,1);BorderColor=hover?Mint:new Vector4(.22f,.31f,.35f,1);base.Draw(alpha,backgroundAlpha);}
                protected override void OnColorMaskChanged() { }
            }
        }
    }
}
