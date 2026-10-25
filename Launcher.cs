using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpenStreetsLauncher {
    static class Program {
        [STAThread] static int Main(string[] args) {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length==2&&args[0]=="--apply-launcher-update")return SelfUpdater.RunHelper(args[1]);
#if TEST
            if (args.Length == 1 && args[0] == "--verify") return Verification.Run();
            if ((args.Length == 2 || args.Length == 3) && args[0] == "--verify-import") return Verification.ImportArtifact(args[1],args.Length==3?args[2]:null);
            if (args.Length == 1 && (args[0] == "--preview" || args[0] == "--discord-preview" || args[0] == "--updates-preview")) {
                using (var form = new LauncherForm(new GamePackage(AppDomain.CurrentDomain.BaseDirectory) ,true)) {
                    form.Show(); Application.DoEvents();
                    if(args[0]=="--discord-preview")form.TestShowDiscord();
                    if(args[0]=="--updates-preview")form.TestShowUpdates();
                    using (var bmp = new Bitmap(form.Width,form.Height)) { form.DrawToBitmap(bmp,new Rectangle(0,0,form.Width,form.Height)); bmp.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,args[0]=="--updates-preview"?"launcher-updates-preview.png":args[0]=="--discord-preview"?"launcher-discord-preview.png":"launcher-preview.png")); }
                    form.Close();
                }
                return 0;
            }
#endif
            var launcher=new LauncherForm(new GamePackage(AppDomain.CurrentDomain.BaseDirectory),false);
            if(args.Length==2&&args[0]=="--updated")launcher.Shown+=(s,e)=>SelfUpdater.MarkReady(args[1]);
            Application.Run(launcher);
            return 0;
        }
    }
    sealed class GameButton : Button {
        bool hover;
        public GameButton(string text, bool primary) {
            Text=text; FlatStyle=FlatStyle.Flat; FlatAppearance.BorderSize=1;
            FlatAppearance.BorderColor=Color.FromArgb(46,62,76);
            BackColor=primary ? Color.FromArgb(140,239,199) : Color.FromArgb(20,32,46);
            ForeColor=primary ? Color.FromArgb(8,27,28) : Color.FromArgb(223,231,239);
            Font=new Font("Segoe UI",10.5f,FontStyle.Bold); Cursor=Cursors.Hand;
            UseVisualStyleBackColor=false;
        }
        protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
        protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
        protected override void OnPaint(PaintEventArgs e) {
            var fill=Enabled?(hover?ControlPaint.Light(BackColor,0.12f):BackColor):Color.FromArgb(16,26,38);
            e.Graphics.Clear(fill);
            using(var pen=new Pen(Color.FromArgb(46,62,76)))e.Graphics.DrawRectangle(pen,0,0,Width-1,Height-1);
            TextRenderer.DrawText(e.Graphics,Text,Font,ClientRectangle,Enabled?ForeColor:Color.FromArgb(99,118,137),TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter);
            if(Focused)ControlPaint.DrawFocusRectangle(e.Graphics,new Rectangle(5,5,Width-10,Height-10),ForeColor,fill);
        }
    }
    public sealed class LauncherForm : Form {
        internal Func<OnlineUpdates> OnlineFactory=()=>new OnlineUpdates();
        readonly GamePackage installer;
        readonly bool preview;
        readonly PresenceBridge presence=new PresenceBridge();
        PresenceSettings presenceSettings;
        string settingsPath;
        Label discordStatus;
        System.Windows.Forms.Timer discordTimer;
        readonly Color muted=Color.FromArgb(147,164,183);
        Panel library, details;
        Label state, status, location;
        ProgressBar progress;
        GameButton primary,repair,cancel,importUpdate,updateChoice,restorePrevious;
        Label updateVersion;
        CancellationTokenSource cancellation;
        Process running;
        UpdateSource updateSource;
        OnlineRelease available;
        GameButton checkOnlineButton,launcherUpdate;
        string sourceError;
        bool busy;
        bool closeWhenIdle;
        public bool TestReady { get { return primary.Text == "PLAY" && primary.Enabled; } }
        public bool TestBusy { get { return busy; } }
        public string TestStatus { get { return status.Text; } }
        public string TestDebug { get { return primary.Text+" enabled="+primary.Enabled+" installed="+installer.IsInstalled+" busy="+busy; } }
        public bool TestRunning { get { return running!=null && !running.HasExited; } }
        public LauncherForm(GamePackage installer,bool preview) {
            this.installer=installer;this.preview=preview;
            settingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OpenStreets","Launcher","preferences.json");
#if TEST
            settingsPath=Path.Combine(installer.Root,"test-preferences.json");
#endif
            presenceSettings=PresenceSettings.Load(settingsPath);
            try{updateSource=UpdateSource.Load(installer.Root);}catch(Exception ex){sourceError=ex.Message;updateSource=new UpdateSource{Url=UpdateSource.GameFeed,CheckOnStart=true};}
            Text="Open Streets Launcher"; ClientSize=new Size(1060,690);
            FormBorderStyle=FormBorderStyle.FixedSingle;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;
            BackColor=Color.FromArgb(11,18,29); ForeColor=Color.White;Font=new Font("Segoe UI",10);
            using (var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico")) Icon=new Icon(s);
            var rail=new Panel { Bounds=new Rectangle(0,0,200,690),BackColor=Color.FromArgb(8,14,23) };Controls.Add(rail);
            AddLabel(rail,"O P E N\nSTREETS",24,FontStyle.Bold,24,28,170,95,Color.White);
            AddLabel(rail,"GAME LAUNCHER",9,FontStyle.Bold,26,124,170,25,muted);
            var lib=AddButton(rail,"  LIBRARY",false,18,202,164,46); lib.Click+=(s,e)=>ShowLibrary();
            var controls=AddButton(rail,"  CONTROLS",false,18,258,164,46); controls.Click+=(s,e)=>ShowInfo(false);
            var about=AddButton(rail,"  ABOUT",false,18,314,164,46); about.Click+=(s,e)=>ShowInfo(true);
            var discord=AddButton(rail,"  DISCORD",false,18,370,164,46);discord.Click+=(s,e)=>ShowDiscord();
            var updates=AddButton(rail,"  UPDATES",false,18,426,164,46);updates.Click+=(s,e)=>ShowUpdates();
            AddLabel(rail,"WINDOWS 64-BIT\n\nIMPORT GAME UPDATES\nKeep your launcher.\nKeep your saved progress.",9,FontStyle.Regular,24,540,165,125,muted);
            BuildLibrary();
            details=new Panel{Bounds=new Rectangle(200,0,860,690),Visible=false}; Controls.Add(details);
            FormClosing+=(s,e)=> {
                if (busy) { e.Cancel=true; closeWhenIdle=true;cancellation.Cancel(); status.Text="Finishing safely before closing..."; }
            };
            discordTimer=new System.Windows.Forms.Timer{Interval=1000};
            discordTimer.Tick+=(s,e)=> {if(discordStatus!=null&&!discordStatus.IsDisposed)discordStatus.Text=presenceSettings.Enabled?presence.Status:"Discord activity is off.";};
            discordTimer.Start();
            FormClosed+=(s,e)=>{presence.Dispose();discordTimer.Dispose();};
            RefreshState();
            Shown+=async(s,e)=>{if(!preview&&updateSource.CheckOnStart&&!String.IsNullOrWhiteSpace(updateSource.Url))await CheckOnline();else if(sourceError!=null)status.Text=sourceError;};
        }
        Label AddLabel(Control parent,string text,float size,FontStyle style,int x,int y,int w,int h,Color color) {
            var l=new Label {Text=text,Bounds=new Rectangle(x,y,w,h),Font=new Font("Segoe UI",size,style),ForeColor=color,BackColor=Color.Transparent};parent.Controls.Add(l);return l;
        }
        GameButton AddButton(Control parent,string text,bool main,int x,int y,int w,int h) {
            var b=new GameButton(text,main){Bounds=new Rectangle(x,y,w,h)};parent.Controls.Add(b);return b;
        }
        void BuildLibrary() {
            library=new Panel{Bounds=new Rectangle(200,0,860,690)};Controls.Add(library);
            var hero=new Panel{Bounds=new Rectangle(0,0,860,348)};
            using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream("hero.png")) { hero.BackgroundImage=new Bitmap(s); }
            hero.BackgroundImageLayout=ImageLayout.Stretch;
            hero.Paint+=(s,e)=> {
                using(var shade=new LinearGradientBrush(new Rectangle(0,0,860,348),Color.FromArgb(40,6,13,24),Color.FromArgb(240,11,18,29),90)) e.Graphics.FillRectangle(shade,hero.ClientRectangle);
                e.Graphics.TextRenderingHint=System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using(var f=new Font("Segoe UI",10,FontStyle.Bold)) e.Graphics.DrawString("YOUR NEXT DRIVE STARTS HERE",f,Brushes.White,34,30);
                using(var f=new Font("Segoe UI",39,FontStyle.Bold)) e.Graphics.DrawString("OPEN STREETS",f,Brushes.White,28,192);
                using(var f=new Font("Segoe UI",12)) using(var b=new SolidBrush(Color.FromArgb(177,199,214))) e.Graphics.DrawString("Drive. Earn. Build. Take flight.",f,b,34,265);
            };
            library.Controls.Add(hero);
            AddLabel(library,"STREET EXPLORER",11,FontStyle.Bold,34,352,260,26,Color.FromArgb(140,239,199));
            state=AddLabel(library,"",10,FontStyle.Regular,576,354,250,25,muted);state.TextAlign=ContentAlignment.TopRight;
            AddLabel(library,"Park up. Walk out. Explore the city.",20,FontStyle.Bold,32,387,780,42,Color.White);
            AddLabel(library,"256 city blocks   /   3.35 km highway   /   Cockpit + flight\nVisible shops, walking mode, Ice City and online game updates.",10,FontStyle.Regular,34,435,780,50,muted);
            primary=AddButton(library,"PLAY",true,34,504,250,54);primary.Click+=async(s,e)=>await MainAction();
            repair=AddButton(library,"CHECK FILES",false,300,504,128,54);repair.Click+=async(s,e)=>await CheckFiles();
            cancel=AddButton(library,"CANCEL",false,300,504,128,54);cancel.Visible=false;cancel.Click+=(s,e)=>cancellation.Cancel();
            var files=AddButton(library,"GAME FILES",false,444,504,162,54); files.Click+=(s,e)=> {
                if(!Directory.Exists(installer.GameDirectory)) { status.Text="Extract the complete game ZIP first.";return; }
                Process.Start(new ProcessStartInfo(installer.GameDirectory){UseShellExecute=true});
            };
            importUpdate=AddButton(library,"UPDATE GAME",false,622,504,204,54);importUpdate.Click+=async(s,e)=>await UpdateGame();
            progress=new ProgressBar{Bounds=new Rectangle(34,573,792,7),Minimum=0,Maximum=100,Visible=false};library.Controls.Add(progress);
            status=AddLabel(library,"",10,FontStyle.Regular,34,591,792,28,Color.FromArgb(203,217,230));
            location=AddLabel(library,"",8.5f,FontStyle.Regular,34,633,792,38,muted);
        }
        void ShowLibrary(){details.Visible=false;library.Visible=true;}
        void ShowInfo(bool about) {
            library.Visible=false;details.Controls.Clear();details.Visible=true;
            AddLabel(details,about?"ABOUT OPEN STREETS":"TAKE THE WHEEL",28,FontStyle.Bold,34,44,790,65,Color.White);
            string text=about ? "Open Streets\nLauncher v1.6 / GitHub game + launcher updates / Windows x64\n\nUse CHECK UPDATES to download new game versions.\nUpdates stay in separate folders, with Restore Previous available.\nUpdates download from the configured HTTPS address.\nDiscord is optional and starts off until you enable it.\n\nThe game uses the unchanged official Godot runtime.\nGame and launcher code: MIT. Engine licenses accompany the game.\nThe launcher uses Windows .NET Framework.\n\nGame files stay beside the launcher. Saved cars and coins live\nseparately and are kept when importing updates.\n\nOnly import game ZIPs from a source you trust. File checks\nconfirm integrity; they are not a digital signature."
                : "W / Up             Accelerate\nS / Down           Brake / reverse\nA / D                  Steer\nSpace                 Handbrake; climb during flight\nCtrl                     Descend during flight\nF                         Toggle Volt Aero flight\nR                        Reset and repair\nE                        Leave / re-enter car\nShift                  Run on foot\nV / C                  Change camera\nN                        Day / night\nG                        Garage and upgrades\n1 - 5                   Buy / equip a car inside the garage\nM                        Lock / release mouse\nW + Space           Burnout at low speed\nEscape               Pause and options\n\nEarn 1 coin per 10 metres.\nAurora RS: 6,000 coins / 300 mph.\nVolt Aero: 12,000 coins / 0-60 in 0.56 s / flight.\nCrash setting: Options > Gameplay.";
            AddLabel(details,text,12,FontStyle.Regular,36,127,790,485,muted);
            var back=AddButton(details,"BACK TO LIBRARY",true,36,615,250,46);back.Click+=(s,e)=>ShowLibrary();
        }
        void StartPresence() {
            if(preview||running==null||running.HasExited||!presenceSettings.Enabled)return;
            string stateFile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Godot","app_userdata","Open Streets","presence.json");
            presence.Start(presenceSettings.ApplicationId,running.Id,stateFile);
        }
        void ShowDiscord() {
            library.Visible=false;details.Controls.Clear();details.Visible=true;
            AddLabel(details,"DISCORD ACTIVITY",28,FontStyle.Bold,34,44,790,65,Color.White);
            AddLabel(details,"Let friends see your drive.",18,FontStyle.Bold,36,126,790,45,Color.White);
            AddLabel(details,"Driving at night in the Aurora RS\nCity Centre",16,FontStyle.Regular,36,179,790,70,Color.FromArgb(140,239,199));
            AddLabel(details,"Activity changes with your car, day/night, flight and burnouts.\nKeep this launcher and the Discord desktop app open while playing.",11,FontStyle.Regular,36,266,790,58,muted);
            var enabled=new CheckBox{Text="Share my Open Streets activity on Discord",Checked=presenceSettings.Enabled,ForeColor=Color.White,Bounds=new Rectangle(36,340,740,32)};details.Controls.Add(enabled);
            AddLabel(details,"DISCORD APPLICATION ID (PUBLIC NUMBER)",9,FontStyle.Bold,36,390,750,24,muted);
            var id=new TextBox{Text=presenceSettings.ApplicationId,Bounds=new Rectangle(36,421,500,32),Font=new Font("Segoe UI",12),BackColor=Color.FromArgb(20,32,46),ForeColor=Color.White,MaxLength=20};details.Controls.Add(id);
            var save=AddButton(details,"SAVE",true,554,419,180,40);
            discordStatus=AddLabel(details,presence.Status,11,FontStyle.Regular,36,492,790,60,muted);
            save.Click+=(s,e)=>{
                string value=id.Text.Trim();
                if(enabled.Checked&&!PresenceSettings.ValidId(value)){discordStatus.Text="Enter the public Application ID (numbers only).";return;}
                try {
                    presenceSettings.ApplicationId=value;presenceSettings.Enabled=enabled.Checked;presenceSettings.Save(settingsPath);presence.Stop();StartPresence();
                    discordStatus.Text=enabled.Checked?"Saved. Start the game with Discord desktop open.":"Discord activity is off.";
                }catch(Exception ex){discordStatus.Text=ex.Message;}
            };
            var back=AddButton(details,"BACK TO LIBRARY",false,36,615,250,46);back.Click+=(s,e)=>ShowLibrary();
        }
        void ShowUpdates() {
            library.Visible=false;details.Controls.Clear();details.Visible=true;
            AddLabel(details,"UPDATES",28,FontStyle.Bold,34,35,790,65,Color.White);
            updateVersion=AddLabel(details,"",17,FontStyle.Bold,36,112,790,55,Color.FromArgb(140,239,199));
            AddLabel(details,"One button checks GitHub, downloads the newest version and installs it.\nNo ZIP picking, extracting or uploading. Your saved progress stays separate.",12,FontStyle.Regular,36,183,750,65,muted);
            checkOnlineButton=AddButton(details,"UPDATE GAME",true,36,278,350,64);checkOnlineButton.Click+=async(s,e)=>await UpdateGame();
            launcherUpdate=AddButton(details,"UPDATE LAUNCHER",false,409,278,365,64);launcherUpdate.Click+=async(s,e)=>await UpdateLauncher();
            AddLabel(details,"Game: koko12356780 / open-world-sanboxcgame\nLauncher: koko12356780 / luncvher-repop\n\nGame updates install while the game is closed.\nLauncher updates restart this window and retain the previous EXE.\nIf GitHub is unavailable, you can still play your installed game.",12,FontStyle.Regular,36,375,750,150,muted);
            updateChoice=AddButton(details,"IMPORT LOCAL ZIP",false,36,542,330,44);updateChoice.Click+=async(s,e)=>await ChooseUpdate();
            restorePrevious=AddButton(details,"RESTORE PREVIOUS GAME",false,389,542,385,44);restorePrevious.Click+=async(s,e)=>await RestoreUpdate();
            var back=AddButton(details,"BACK TO LIBRARY",false,36,615,250,46);back.Click+=(s,e)=>ShowLibrary();
            RefreshState();
        }
        async Task UpdateGame() {
            if(busy||IsPlaying)return;
            ShowLibrary();BeginWork();string result;
            try {
                status.Text="Checking GitHub for the newest game...";
                Action<string> report=message=>{if(!IsDisposed)BeginInvoke((Action)(()=>status.Text=message));};
                using(var online=OnlineFactory()) {
                    var release=await online.Check(updateSource.Url,cancellation.Token);
                    if(!OnlineUpdates.IsNewer(release,installer.DisplayVersion))result="You already have the newest game. Press PLAY.";
                    else {await Task.Run(()=>online.Install(release,installer,cancellation.Token,report));available=null;result="Game updated to "+installer.DisplayVersion+". Press PLAY.";}
                }
            }catch(OperationCanceledException){result="Update cancelled. Your installed game is ready to play.";}
            catch(Exception ex){result="Could not update: "+ex.Message;}
            EndWork();status.Text=result;
        }
        async Task UpdateLauncher() {
            if(busy||IsPlaying)return;
            ShowLibrary();BeginWork();string result;string job=null;
            try {
                status.Text="Checking GitHub for a launcher update...";
                Action<string> report=message=>{if(!IsDisposed)BeginInvoke((Action)(()=>status.Text=message));};
                using(var online=OnlineFactory()) {
                    var release=await SelfUpdater.Check(online,cancellation.Token);
                    if(!OnlineUpdates.IsNewer(release,SelfUpdater.CurrentVersion))result="You already have the newest launcher ("+SelfUpdater.CurrentVersion+").";
                    else {job=await Task.Run(()=>SelfUpdater.Prepare(online,release,installer.Root,Assembly.GetExecutingAssembly().Location,cancellation.Token,report));result="Restarting to finish the launcher update...";}
                }
            }catch(OperationCanceledException){result="Launcher update cancelled. Your current launcher is kept.";}
            catch(Exception ex){result="Could not update launcher: "+ex.Message;}
            EndWork();status.Text=result;
            if(job!=null) {
                try{SelfUpdater.Start(job);Close();}catch(Exception ex){status.Text="Could not restart: "+ex.Message;}
            }
        }
        async Task CheckOnline() {
            if(busy||IsPlaying)return;
            if(String.IsNullOrWhiteSpace(updateSource.Url)){ShowUpdates();updateVersion.Text="Online updates need a permanent download address first.";return;}
            ShowLibrary();BeginWork();string result;
            try {
                status.Text="Checking for game updates...";
                using(var online=OnlineFactory()) {
                    var release=await online.Check(updateSource.Url,cancellation.Token);
                    available=OnlineUpdates.IsNewer(release,installer.DisplayVersion)?release:null;
                    result=available==null?"You have the newest game. Ready to play.":"New game: "+available.version+". Click UPDATE GAME to install it.";
                }
            }catch(OperationCanceledException){result="Update check stopped. Your installed game is ready to play.";}
            catch(Exception ex){result="Could not check online: "+ex.Message;}
            EndWork();status.Text=result;
        }
        async Task InstallOnline() {
            if(busy||IsPlaying||available==null)return;
            var release=available;ShowLibrary();BeginWork();string result;
            try {
                Action<string> report=message=>{if(!IsDisposed)BeginInvoke((Action)(()=>status.Text=message));};
                using(var online=OnlineFactory())await Task.Run(()=>online.Install(release,installer,cancellation.Token,report));
                available=null;result="Update ready: "+installer.DisplayVersion+". Press PLAY. Your save is kept.";
            }catch(OperationCanceledException){result="Update cancelled. Your previous game is still selected.";}
            catch(Exception ex){result="Update not applied: "+ex.Message;}
            EndWork();status.Text=result;
        }
        void PrepareRelease() {
            if(busy||IsPlaying)return;
            using(var picker=new OpenFileDialog{Title="Choose the new game update ZIP to publish",Filter="Open Streets update (*.zip)|*.zip",CheckFileExists=true}) {
                if(picker.ShowDialog(this)!=DialogResult.OK)return;
                try {
                    string folder=ReleasePublisher.Prepare(installer.Root,updateSource.Url,picker.FileName);
                    Process.Start(new ProcessStartInfo(folder){UseShellExecute=true});
                    updateVersion.Text="Release files prepared. Open UPLOAD-INSTRUCTIONS.txt in that folder.";
                }catch(Exception ex){updateVersion.Text=ex.Message;}
            }
        }
        bool IsPlaying {get {return running!=null&&!running.HasExited;} }
        async Task ChooseUpdate() {
            if(busy||IsPlaying)return;
            using(var picker=new OpenFileDialog{Title="Choose an Open Streets game update ZIP you trust",Filter="Open Streets update (*.zip)|*.zip",CheckFileExists=true,Multiselect=false}) {
                if(picker.ShowDialog(this)!=DialogResult.OK)return;
                ShowLibrary();await ImportSelected(picker.FileName);
            }
        }
        async Task ImportSelected(string path) {
            if(busy||IsPlaying)return;
            BeginWork();string result;
            try {
                status.Text="Checking update ZIP...";
                Action<string> report=message=>{if(!IsDisposed)BeginInvoke((Action)(()=>status.Text=message));};
                await Task.Run(()=>installer.Import(path,cancellation.Token,report));
                result="Update ready: "+installer.DisplayVersion+". Press PLAY. Your save is kept.";
            }catch(OperationCanceledException){result="Update cancelled. Your previous game is still selected.";}
            catch(Exception ex){result="Update not applied: "+ex.Message;}
            EndWork();status.Text=result;
        }
        async Task RestoreUpdate() {
            if(busy||IsPlaying||!installer.CanRollback)return;
            ShowLibrary();BeginWork();string result;
            try{status.Text="Checking the previous version...";await Task.Run(()=>installer.Rollback(cancellation.Token));result="Restored "+installer.DisplayVersion+". Your saved progress is kept.";}
            catch(OperationCanceledException){result="Restore cancelled.";}
            catch(Exception ex){result=ex.Message;}
            EndWork();status.Text=result;
        }
        void RefreshState() {
            bool playing=IsPlaying;
            bool installed=installer.IsInstalled;
            primary.Text=playing?"RUNNING":installed?"PLAY":"FILES MISSING";
            primary.Enabled=installed&&!busy&&!playing;repair.Enabled=!busy&&!playing;
            importUpdate.Enabled=!busy&&!playing;
            importUpdate.Text="UPDATE GAME";
            if(launcherUpdate!=null&&!launcherUpdate.IsDisposed)launcherUpdate.Enabled=!busy&&!playing;
            if(checkOnlineButton!=null&&!checkOnlineButton.IsDisposed)checkOnlineButton.Enabled=!busy&&!playing;
            if(updateChoice!=null&&!updateChoice.IsDisposed)updateChoice.Enabled=!busy&&!playing;
            if(restorePrevious!=null&&!restorePrevious.IsDisposed)restorePrevious.Enabled=!busy&&!playing&&installer.CanRollback;
            if(updateVersion!=null&&!updateVersion.IsDisposed)updateVersion.Text="Game: "+installer.DisplayVersion+" / Launcher: "+SelfUpdater.CurrentVersion;
            repair.Visible=!busy;cancel.Visible=busy;state.Text=playing?"GAME RUNNING":installed?installer.DisplayVersion:"GAME FOLDER MISSING";
            location.Text=preview?"Game files are visible in the Game folder. Saves stay separate.":"Game folder: "+installer.GameDirectory;
            if(!busy) status.Text=installer.SelectionWarning??(installed?"Ready to play. Click Update Game to get the newest release from GitHub.":"Extract the whole launcher ZIP first. Keep its Game folder beside it.");
        }
        void BeginWork() {busy=true;cancellation=new CancellationTokenSource();cancel.Enabled=true;progress.Value=0;progress.Visible=true;RefreshState();}
        void EndWork() {busy=false;progress.Visible=false;cancellation.Dispose();cancellation=null;RefreshState();if(closeWhenIdle)BeginInvoke((Action)(()=>Close()));}
        async Task CheckFiles() {
            BeginWork();string result;
            try{status.Text="Checking release hashes...";await Task.Run(()=>installer.Validate(cancellation.Token));result="All game files match this release's SHA-256 manifest.";}
            catch(OperationCanceledException){result="File check cancelled.";}
            catch(Exception ex){result=ex.Message;}
            EndWork();status.Text=result;
        }
        async Task MainAction() {
            if(!installer.IsInstalled){status.Text="Extract the complete ZIP first. The Game folder must stay beside this launcher.";return;}
            BeginWork();string error=null;
            try {
                status.Text="Checking game files...";
                await Task.Run(()=>installer.Validate(cancellation.Token));
                running=installer.Launch(preview);
                var process=running;
                running.Exited+=(s,e)=> {if(!IsDisposed) BeginInvoke((Action)(()=> {
                    if(running==process){presence.Stop();running.Dispose();running=null;}
                    RefreshState();
                }));};
                running.EnableRaisingEvents=true;
                StartPresence();
            } catch(OperationCanceledException){error="Launch cancelled.";}
            catch(Exception ex){error=ex.Message;}
            finally{EndWork();if(error!=null)status.Text=error;else status.Text="Open Streets is running in its own window.";}
        }
#if TEST
        public void TestShowDiscord(){ShowDiscord();}
        public void TestInstallClick() {primary.PerformClick();}
        public Task TestImport(string path){return ImportSelected(path);}
        public bool TestImportEnabled {get {return importUpdate.Enabled;} }
        public void TestShowUpdates(){ShowUpdates();}
        public Task TestUpdateGame(){return UpdateGame();}
#endif
    }
}
