using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace OpenStreetsLauncher {
    public sealed class LauncherUpdateJob {
        public int ParentPid {get;set;}
        public string ExistingHash {get;set;}
        public string NewHash {get;set;}
        public string Version {get;set;}
    }
    public static class SelfUpdater {
        const string Product="Open Streets Launcher";
        public static string CurrentVersion {get{return Assembly.GetExecutingAssembly().GetName().Version.ToString();}}
        static string Quote(string value){if(value.IndexOf('"')>=0)throw new IOException("Invalid update path.");return "\""+value+"\"";}
        static void Cleanup(string stage) {
            foreach(string file in new[]{"launcher.zip","launcher-next.exe","Updater.exe","update-job.json","ready.flag"}) {
                string path=Path.Combine(stage,file);if(File.Exists(path))File.Delete(path);
            }
            if(Directory.Exists(stage)&&Directory.GetFileSystemEntries(stage).Length==0)Directory.Delete(stage,false);
        }
        public static Task<OnlineRelease> Check(OnlineUpdates online,CancellationToken token) {
            return online.CheckRelease(UpdateSource.LauncherFeed,Product,token);
        }
        public static async Task<string> Prepare(OnlineUpdates online,OnlineRelease release,string root,string runningExe,CancellationToken token,Action<string> report) {
            if(release.game!=Product||release.size>16*1024*1024||!OnlineUpdates.IsNewer(release,AssemblyName.GetAssemblyName(runningExe).Version.ToString()))throw new IOException("No newer compatible launcher update is available.");
            root=Path.GetFullPath(root);
            if(!String.Equals(Path.GetFullPath(runningExe),Path.Combine(root,"OpenStreetsLauncher.exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("Keep the launcher named OpenStreetsLauncher.exe in its game folder.");
            string stage=Path.Combine(root,"LauncherUpdates",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
            string zip=Path.Combine(stage,"launcher.zip"),payload=Path.Combine(stage,"launcher-next.exe");bool prepared=false;
            try {
                await online.Download(release,zip,token,report).ConfigureAwait(false);
                using(var archive=ZipFile.OpenRead(zip)) {
                    if(archive.Entries.Count>20)throw new IOException("Unexpected launcher package contents.");
                    var seen=new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach(var entry in archive.Entries) {
                        if(!seen.Add(entry.FullName)||entry.FullName.IndexOfAny(new[]{'\\',':','\0'})>=0||entry.FullName.StartsWith("/")||Array.Exists(entry.FullName.Split('/'),part=>part==".."||part=="."))throw new IOException("Unsafe launcher package path.");
                    }
                    var info=archive.GetEntry("launcher-info.json");
                    if(info==null||info.Length>8192)throw new IOException("Launcher update information is missing.");
                    OnlineRelease metadata;
                    using(var reader=new StreamReader(info.Open()))metadata=new JavaScriptSerializer().Deserialize<OnlineRelease>(reader.ReadToEnd());
                    if(metadata==null||metadata.game!=Product||metadata.version!=release.version)throw new IOException("Launcher package and online version do not match.");
                    var exe=archive.GetEntry("OpenStreetsLauncher.exe");
                    if(exe==null||exe.Length<=0||exe.Length>16*1024*1024)throw new IOException("Launcher executable is missing or too large.");
                    using(var input=exe.Open())using(var output=File.Create(payload)) {
                        byte[] bytes=new byte[65536];long total=0;int n;
                        while((n=input.Read(bytes,0,bytes.Length))>0){token.ThrowIfCancellationRequested();total+=n;if(total>16*1024*1024)throw new IOException("Launcher executable is too large.");output.Write(bytes,0,n);}
                    }
                }
                var identity=AssemblyName.GetAssemblyName(payload);
                if(identity.Name!="OpenStreetsLauncher"||identity.Version.ToString()!=release.version)throw new IOException("Downloaded launcher has the wrong application identity or version.");
                token.ThrowIfCancellationRequested();
                var job=new LauncherUpdateJob{ParentPid=Process.GetCurrentProcess().Id,ExistingHash=GamePackage.FileHash(runningExe),NewHash=GamePackage.FileHash(payload),Version=release.version};
                File.Copy(runningExe,Path.Combine(stage,"Updater.exe"));
                string path=Path.Combine(stage,"update-job.json");File.WriteAllText(path,new JavaScriptSerializer().Serialize(job),new UTF8Encoding(false));
                prepared=true;return path;
            }finally{if(File.Exists(zip))File.Delete(zip);if(!prepared)Cleanup(stage);}
        }
        static string RootForJob(string jobPath) {
            string path=Path.GetFullPath(jobPath),stage=Path.GetDirectoryName(path);
            if(Path.GetFileName(path)!="update-job.json"||!Regex.IsMatch(Path.GetFileName(stage),"\\A[a-f0-9]{32}\\z"))throw new IOException("Invalid launcher update job path.");
            var parent=Directory.GetParent(stage);
            if(parent==null||parent.Name!="LauncherUpdates"||parent.Parent==null)throw new IOException("Invalid launcher update folder.");
            return parent.Parent.FullName;
        }
        public static void Start(string jobPath) {
            string root=RootForJob(jobPath),helper=Path.Combine(Path.GetDirectoryName(jobPath),"Updater.exe");
            Process.Start(new ProcessStartInfo(helper,"--apply-launcher-update "+Quote(jobPath)){WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true});
        }
        internal static void ApplyFiles(string jobPath,string helper) {
            string root=RootForJob(jobPath),stage=Path.GetDirectoryName(Path.GetFullPath(jobPath));
            if(!String.Equals(Path.GetFullPath(helper),Path.Combine(stage,"Updater.exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("Update helper must run from its own staging folder.");
            var job=new JavaScriptSerializer().Deserialize<LauncherUpdateJob>(GamePackage.ReadSmall(jobPath));
            if(job==null||!Regex.IsMatch(job.ExistingHash??"","\\A[a-f0-9]{64}\\z")||!Regex.IsMatch(job.NewHash??"","\\A[a-f0-9]{64}\\z"))throw new IOException("Invalid launcher update hashes.");
            string target=Path.Combine(root,"OpenStreetsLauncher.exe"),payload=Path.Combine(stage,"launcher-next.exe");
            if(GamePackage.FileHash(helper)!=job.ExistingHash||GamePackage.FileHash(target)!=job.ExistingHash)throw new IOException("The old launcher changed while updating. Open it and try again.");
            if(GamePackage.FileHash(payload)!=job.NewHash)throw new IOException("The new launcher changed after download.");
            var identity=AssemblyName.GetAssemblyName(payload);
            if(identity.Name!="OpenStreetsLauncher"||identity.Version.ToString()!=job.Version||identity.Version<=AssemblyName.GetAssemblyName(target).Version)throw new IOException("Wrong launcher identity or version.");
            // One atomic replacement, with the prior working EXE retained beside it.
            // Game/, Updates/, saves and all preferences are outside this transaction.
            File.Replace(payload,target,Path.Combine(root,"OpenStreetsLauncher.previous.exe"),true);
        }
        public static void MarkReady(string jobPath) {
            string root=RootForJob(jobPath);
            if(!String.Equals(Assembly.GetExecutingAssembly().Location,Path.Combine(root,"OpenStreetsLauncher.exe"),StringComparison.OrdinalIgnoreCase))throw new IOException("Wrong launcher startup folder.");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(jobPath),"ready.flag"),CurrentVersion);
        }
        public static int RunHelper(string jobPath) {
            string root=null;
            try {
                root=RootForJob(jobPath);
                var job=new JavaScriptSerializer().Deserialize<LauncherUpdateJob>(GamePackage.ReadSmall(jobPath));
                if(job==null||job.ParentPid<=0||job.ParentPid==Process.GetCurrentProcess().Id)throw new IOException("Invalid launcher parent process.");
                try{using(var parent=Process.GetProcessById(job.ParentPid))if(!parent.WaitForExit(30000))throw new IOException("Close the old launcher before trying this update again.");}catch(ArgumentException){}
                ApplyFiles(jobPath,Assembly.GetExecutingAssembly().Location);
                string target=Path.Combine(root,"OpenStreetsLauncher.exe"),ready=Path.Combine(Path.GetDirectoryName(jobPath),"ready.flag");
                bool started=false;
                try {
                    using(var child=Process.Start(new ProcessStartInfo(target,"--updated "+Quote(jobPath)){WorkingDirectory=root,UseShellExecute=false})) {
                        var deadline=DateTime.UtcNow.AddSeconds(30);
                        while(DateTime.UtcNow<deadline&&!child.HasExited&&!File.Exists(ready))Thread.Sleep(100);
                        started=File.Exists(ready)&&File.ReadAllText(ready)==job.Version;
                        if(!started&&!child.HasExited){child.Kill();child.WaitForExit(5000);}
                    }
                }finally {
                    if(!started) {
                        File.Replace(Path.Combine(root,"OpenStreetsLauncher.previous.exe"),target,Path.Combine(root,"OpenStreetsLauncher.failed.exe"),true);
                        Process.Start(new ProcessStartInfo(target){WorkingDirectory=root,UseShellExecute=false});
                    }
                }
                if(!started)throw new IOException("The new launcher could not start. The previous launcher was restored.");
                File.WriteAllText(Path.Combine(root,"launcher-update-result.txt"),"Launcher updated to "+job.Version+". Your game and saves were kept.");
                return 0;
            }catch(Exception ex) {
                if(root!=null)try{File.WriteAllText(Path.Combine(root,"launcher-update-error.txt"),ex.Message);}catch(Exception){}
                MessageBox.Show("Launcher update could not finish: "+ex.Message,"Open Streets",MessageBoxButtons.OK,MessageBoxIcon.Information);return 1;
            }
        }
    }
}
