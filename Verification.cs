using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace OpenStreetsLauncher {
    static class Verification {
        static int checks;
        static StreamWriter log;
        static void Check(bool ok,string name){log.WriteLine((ok?"PASS ":"FAIL ")+name);log.Flush();if(!ok)throw new Exception(name);checks++;}
        static bool Fails(Action action){try{action();return false;}catch(Exception){return true;}}
        public static int ImportArtifact(string archive,string baseline) {
            string baseDir=AppDomain.CurrentDomain.BaseDirectory;
            string qa=Path.Combine(Path.GetTempPath(),"OpenStreetsArtifact-"+Guid.NewGuid().ToString("N").Substring(0,10));
            Directory.CreateDirectory(Path.Combine(qa,"Game"));checks=0;
            using(log=new StreamWriter(Path.Combine(baseDir,"artifact-tests.log"),true))try {
                foreach(string name in GamePackage.Names)File.Copy(Path.Combine(baseline??Path.Combine(baseDir,"Game"),name),Path.Combine(qa,"Game",name));
                var before=new GamePackage(qa);before.Validate(CancellationToken.None);
                var game=new GamePackage(qa);game.Import(Path.GetFullPath(archive),CancellationToken.None,s=>{});game.Validate(CancellationToken.None);
                Check(game.CanRollback&&game.DisplayVersion.Length>0,"Delivered ZIP imports and passes its hashes: "+Path.GetFileName(archive));
                Check(new GamePackage(qa).GameDirectory==game.GameDirectory,"Delivered ZIP selection persists across restart");
                using(var process=game.Launch(true))Check(process.WaitForExit(30000)&&process.ExitCode==0,"Delivered ZIP's game actually starts");
                if(game.IsUnreal)Check(new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(File.ReadAllText(Path.Combine(qa,"packaged-smoke.json")))["passed"].Equals(true),"Delivered native game passes its packaged runtime checks");
                else Check(!File.ReadAllText(Path.Combine(qa,"game-smoke.log")).Contains("ERROR"),"Delivered ZIP has no runtime resource/script errors");
                game.Rollback(CancellationToken.None);game.Validate(CancellationToken.None);
                Check(game.GameDirectory==Path.Combine(qa,"Game"),"Delivered ZIP can be rolled back to bundled game");
                log.WriteLine("RESULT: "+checks+" artifact checks passed; 0 failures; baseline "+before.DisplayVersion);return 0;
            }catch(Exception ex){log.WriteLine(ex.ToString());return 1;}
        }
        public static int Run() {
            string baseDir=AppDomain.CurrentDomain.BaseDirectory;
            string qa=Path.Combine(baseDir,"verification",Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(qa);
            using(log=new StreamWriter(Path.Combine(baseDir,"portable-tests.log")))try {
                foreach(string resource in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                    Check(resource=="hero.png"||resource=="app.ico","Only artwork is embedded: "+resource);
                var original=new GamePackage(baseDir);
                original.Validate(CancellationToken.None);
                Check(true,"Visible game files match release hashes");
                Check(GamePackage.FileHash(original.GameExe)==BuildInfo.OfficialRuntimeHash,"Game executable is byte-identical to the official Godot template");
                Check(!new PresenceSettings().Enabled,"Discord is off until the player enables it");
                var missing=new GamePackage(qa);
                Check(!missing.IsInstalled&&Fails(()=>missing.Validate(CancellationToken.None)),"Missing game files are rejected");
                string gameDir=Path.Combine(qa,"Game");Directory.CreateDirectory(gameDir);
                foreach(string file in BuildInfo.Files.Keys)File.Copy(Path.Combine(original.GameDirectory,file),Path.Combine(gameDir,file));
                var package=new GamePackage(qa);
                var token=new CancellationTokenSource();token.Cancel();
                Check(Fails(()=>package.Validate(token.Token)),"File verification can be cancelled");
                string pck=Path.Combine(gameDir,"OpenStreets.pck");
                using(var f=new FileStream(pck,FileMode.Append))f.WriteByte(0);
                Check(Fails(()=>package.Validate(CancellationToken.None)),"Modified game data is rejected before launch");
                File.Copy(Path.Combine(original.GameDirectory,"OpenStreets.pck"),pck,true);
                package.Validate(CancellationToken.None);
                Check(true,"Replacing a damaged file restores verification");
                string save=Path.Combine(qa,"save-sentinel.cfg");File.WriteAllText(save,"keep cars and coins");
                using(var process=package.Launch(true))Check(process.WaitForExit(30000)&&process.ExitCode==0,"Official standalone runtime starts without the editor");
                var smoke=File.ReadAllText(Path.Combine(qa,"game-smoke.log"));
                Check(!smoke.Contains("ERROR")&&smoke.Contains("Godot Engine"),"Standalone game loads its PCK cleanly");
                Check(File.ReadAllText(save)=="keep cars and coins","Game launch leaves unrelated saved data intact");
                using(var form=new LauncherForm(package,true)) {
                    form.Show();Application.DoEvents();
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    Check(form.TestReady,"Portable launcher offers Play immediately");
                    form.TestInstallClick();
                    var deadline=DateTime.UtcNow.AddSeconds(30);
                    while(form.TestBusy&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(15);}
                    Check(form.TestRunning,"Play button starts the verified game");
                    Check(!form.TestImportEnabled,"Updating is disabled while the game is running");
                    while((form.TestRunning||!form.TestReady)&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(20);}
                    Check(form.TestReady,"Play becomes available after game exit");
                    using(var bmp=new Bitmap(form.Width,form.Height)) {form.DrawToBitmap(bmp,new Rectangle(0,0,form.Width,form.Height));bmp.Save(Path.Combine(baseDir,"portable-preview.png"));}
                    form.Close();
                }
                SynchronizationContext.SetSynchronizationContext(null);
                PresenceVerification.Run(Check,qa);
                UpdateVerification.Run(Check,qa);
                OnlineVerification.Run(Check,qa);
                SelfUpdateVerification.Run(Check,qa);
                log.WriteLine("RESULT: "+checks+" checks passed; 0 failures");return 0;
            }catch(Exception ex){log.WriteLine(ex.ToString());return 1;}
        }
    }
}
