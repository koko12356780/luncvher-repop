using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OpenStreetsLauncher {
    static class SelfUpdateVerification {
        sealed class Server : HttpMessageHandler {
            internal Func<string,byte[]> Content;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(Content(request.RequestUri.AbsoluteUri))});
            }
        }
        static bool Fails(Action action){try{action();return false;}catch(Exception){return true;}}
        static string MakeZip(string root,string exe,string version,string fault) {
            string path=Path.Combine(root,"launcher-"+Guid.NewGuid().ToString("N")+".zip");
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Create)) {
                using(var writer=new StreamWriter(zip.CreateEntry("launcher-info.json").Open()))writer.Write(new JavaScriptSerializer().Serialize(new{game="Open Streets Launcher",version=fault=="version"?"9.0.0.0":version}));
                zip.CreateEntryFromFile(exe,fault=="missing"?"wrong.exe":"OpenStreetsLauncher.exe");
                if(fault=="traversal")using(var w=new StreamWriter(zip.CreateEntry("../escape.txt").Open()))w.Write("escape");
            }
            return path;
        }
        static string Case(string root,string fixture) {
            string path=Path.Combine(root,"su-"+Guid.NewGuid().ToString("N").Substring(0,8));Directory.CreateDirectory(path);
            File.Copy(fixture,Path.Combine(path,"OpenStreetsLauncher.exe"));
            Directory.CreateDirectory(Path.Combine(path,"Game"));File.WriteAllText(Path.Combine(path,"Game","sentinel.txt"),"keep game and saves");return path;
        }
        public static void Run(Action<bool,string> check,string root) {
            string baseDir=AppDomain.CurrentDomain.BaseDirectory,fixture=Path.Combine(baseDir,"OldLauncherFixture.exe"),exe=Path.Combine(baseDir,"OpenStreetsLauncher.exe");
            string version=AssemblyName.GetAssemblyName(exe).Version.ToString(),zip=MakeZip(root,exe,version,null);
            var release=new OnlineRelease{game="Open Streets Launcher",version=version,zip_url="https://github.com/test/launcher.zip",sha256=GamePackage.FileHash(zip),size=new FileInfo(zip).Length};
            Func<string,byte[]> response=url=>url==UpdateSource.LauncherFeed?Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(release)):File.ReadAllBytes(zip);
            using(var online=new OnlineUpdates(new Server{Content=url=>response(url)})) {
                check(SelfUpdater.Check(online,CancellationToken.None).GetAwaiter().GetResult().version==version,"Launcher uses its own dedicated GitHub feed");
                string folder=Case(root,fixture),target=Path.Combine(folder,"OpenStreetsLauncher.exe"),old=GamePackage.FileHash(target);
                check(UpdateSource.Load(folder).Url==UpdateSource.GameFeed,"Fresh launcher already knows the game GitHub address");
                var receipt=new ReleaseRecord{Version="0.11 Receipt Test",Files=new Dictionary<string,string>(BuildInfo.Files)};
                File.WriteAllText(Path.Combine(folder,"Game","release.json"),new JavaScriptSerializer().Serialize(receipt));
                check(new GamePackage(folder).DisplayVersion==receipt.Version,"A newer launcher honors the bundled game receipt rather than assuming a different base version");
                File.WriteAllText(Path.Combine(folder,"Game","release.json"),"{}");
                check(new GamePackage(folder).SelectionWarning!=null,"A damaged bundled receipt falls back without preventing launcher startup");
                File.WriteAllText(Path.Combine(folder,"update-source.json"),"{\"Url\":\"\",\"CheckOnStart\":true}");
                check(UpdateSource.Load(folder).Url==UpdateSource.GameFeed,"Upgrading an old empty configuration needs no URL pasting");
                string job=SelfUpdater.Prepare(online,release,folder,target,CancellationToken.None,s=>{}).GetAwaiter().GetResult();
                check(File.Exists(job)&&GamePackage.FileHash(target)==old,"Launcher download stages beside the old launcher without replacing a running EXE");
                SelfUpdater.ApplyFiles(job,Path.Combine(Path.GetDirectoryName(job),"Updater.exe"));
                check(GamePackage.FileHash(target)==GamePackage.FileHash(exe)&&AssemblyName.GetAssemblyName(target).Version.ToString()==version,"Launcher replacement installs the verified newer executable atomically");
                check(GamePackage.FileHash(Path.Combine(folder,"OpenStreetsLauncher.previous.exe"))==old,"Previous launcher executable is kept as a recovery backup");
                check(File.ReadAllText(Path.Combine(folder,"Game","sentinel.txt"))=="keep game and saves","Launcher replacement leaves game files and saves alone");
                check(Fails(()=>SelfUpdater.ApplyFiles(job,Path.Combine(Path.GetDirectoryName(job),"Updater.exe"))),"An already-applied launcher update cannot replay over a different executable");
                string tamper=Case(root,fixture),t=Path.Combine(tamper,"OpenStreetsLauncher.exe");
                string j=SelfUpdater.Prepare(online,release,tamper,t,CancellationToken.None,s=>{}).GetAwaiter().GetResult();
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(j),"launcher-next.exe"),"changed");
                check(Fails(()=>SelfUpdater.ApplyFiles(j,Path.Combine(Path.GetDirectoryName(j),"Updater.exe")))&&GamePackage.FileHash(t)==old,"Changing a staged launcher prevents replacement of the working copy");
                foreach(string fault in new[]{"version","missing","traversal"}) {
                    string bad=MakeZip(root,exe,version,fault),saved=zip;zip=bad;release.sha256=GamePackage.FileHash(zip);release.size=new FileInfo(zip).Length;
                    string f=Case(root,fixture),current=Path.Combine(f,"OpenStreetsLauncher.exe");
                    check(Fails(()=>SelfUpdater.Prepare(online,release,f,current,CancellationToken.None,s=>{}).GetAwaiter().GetResult())&&GamePackage.FileHash(current)==old,"Reject "+fault+" launcher archive without touching the installed launcher");
                    check(Directory.GetDirectories(Path.Combine(f,"LauncherUpdates")).Length==0,"Rejected "+fault+" launcher staging is cleaned up");
                    zip=saved;File.Delete(bad);
                }
                release.sha256=GamePackage.FileHash(zip);release.size=new FileInfo(zip).Length;
                string c=Case(root,fixture);var cancel=new CancellationTokenSource();
                check(Fails(()=>SelfUpdater.Prepare(online,release,c,Path.Combine(c,"OpenStreetsLauncher.exe"),cancel.Token,s=>cancel.Cancel()).GetAwaiter().GetResult())&&GamePackage.FileHash(Path.Combine(c,"OpenStreetsLauncher.exe"))==old,"Cancelled launcher transfer keeps the old launcher");
                string originalGame=release.game;release.game="Open Streets";
                check(Fails(()=>SelfUpdater.Check(online,CancellationToken.None).GetAwaiter().GetResult()),"Game packages cannot masquerade as launcher updates");release.game=originalGame;
            }
            File.Delete(zip);
            OneClick(check,root);
        }
        static void OneClick(Action<bool,string> check,string root) {
            string zip=OnlineVerification.MakeZip(root,Path.Combine(root,"Game"),"0.13 Click Test");
            var game=new GamePackage(root);int downloads=0;
            var release=new OnlineRelease{game="Open Streets",version="0.13 Click Test",zip_url="https://example.com/game.zip",sha256=GamePackage.FileHash(zip),size=new FileInfo(zip).Length};
            var feed=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(release));
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using(var form=new LauncherForm(game,true)) {
                form.OnlineFactory=()=>new OnlineUpdates(new Server{Content=url=>{if(url==release.zip_url){downloads++;return File.ReadAllBytes(zip);}return feed;}});
                form.Show();Application.DoEvents();
                var task=form.TestUpdateGame();var deadline=DateTime.UtcNow.AddSeconds(30);
                while(!task.IsCompleted&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(10);}
                check(task.IsCompleted&&!task.IsFaulted&&game.DisplayVersion==release.version&&form.TestStatus.StartsWith("Game updated"),"One Update Game click checks, downloads and installs without a file picker");
                string selected=game.GameDirectory;
                task=form.TestUpdateGame();while(!task.IsCompleted&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(10);}
                check(task.IsCompleted&&downloads==1&&game.GameDirectory==selected&&form.TestStatus.Contains("newest game"),"Clicking Update Game again keeps the latest game without re-downloading");
                form.Close();
            }
            SynchronizationContext.SetSynchronizationContext(null);File.Delete(zip);
        }
    }
}
