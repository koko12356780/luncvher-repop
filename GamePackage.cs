using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace OpenStreetsLauncher {
    public sealed class ReleaseRecord {
        public string Version {get;set;}
        public Dictionary<string,string> Files {get;set;}
        public string Runtime {get;set;}
    }
    public sealed class GameSelection {
        public string Current {get;set;}
        public string Previous {get;set;}
    }
    // Imports are separate immutable versions. The shipped Game folder and user saves
    // are never replaced. The only commit is an atomic switch of current-game.json.
    public sealed class GamePackage {
        public readonly string Root;
        string current="base", previous;
        ReleaseRecord release;
        public string SelectionWarning {get;private set;}
        public string DisplayVersion {get {return release.Version;} }
        public string GameDirectory {get {return VersionDirectory(current);} }
        public string GameExe {get {return Path.Combine(GameDirectory,"OpenStreets.exe");} }
        public bool IsUnreal {get {return release.Runtime=="unreal-win64";} }
        public bool IsInstalled {get {return File.Exists(GameExe)&&(IsUnreal?File.Exists(Path.Combine(GameDirectory,"OpenStreets","Binaries","Win64","OpenStreets-Win64-Shipping.exe")):File.Exists(Path.Combine(GameDirectory,"OpenStreets.pck")));} }
        public bool CanRollback {get {return previous!=null && previous!=current;} }
        string SelectionPath {get {return Path.Combine(Root,"current-game.json");} }
        internal static readonly string[] Names={"OpenStreets.exe","OpenStreets.pck","GAME-LICENSE.txt","ENGINE-LICENSES.txt"};
        public GamePackage(string directory) {
            Root=Path.GetFullPath(directory);
            release=ReadRelease("base");
            try {
                if(File.Exists(SelectionPath)) {
                    var state=new JavaScriptSerializer().Deserialize<GameSelection>(ReadSmall(SelectionPath));
                    if(state==null||!ValidId(state.Current)||(state.Previous!=null&&!ValidId(state.Previous)))throw new IOException("Invalid update selection.");
                    var record=ReadRelease(state.Current);
                    current=state.Current;previous=state.Previous;release=record;
                }
            }catch(Exception) {
                SelectionWarning="Update selection could not be read. Using the bundled game; import the update again.";
            }
        }
        static bool ValidId(string id){return id=="base" || (id!=null&&Regex.IsMatch(id,"\\A[a-f0-9]{32}\\z"));}
        string VersionDirectory(string id) {
            if(!ValidId(id))throw new IOException("Invalid version directory.");
            return id=="base"?Path.Combine(Root,"Game"):Path.Combine(Root,"Updates",id);
        }
        internal static string ReadSmall(string path) {
            if(new FileInfo(path).Length>65536)throw new IOException("Update metadata is too large.");
            return File.ReadAllText(path,Encoding.UTF8);
        }
        ReleaseRecord ReadRelease(string id) {
            if(id=="base") {
                string receipt=Path.Combine(Root,"Game","release.json");
                if(File.Exists(receipt))try{return ReadReceipt(receipt);}catch(Exception){SelectionWarning="Bundled receipt could not be read. Checking the known base game instead.";}
                var files=new Dictionary<string,string>(BuildInfo.Files);
                string version=BuildInfo.GameVersion;
                // The older portable v1.2 used the same unchanged official runtime.
                // Recognize its known PCK so upgrading just the launcher still allows
                // both Play and Restore Previous before/after importing Citylight.
                string pck=Path.Combine(Root,"Game","OpenStreets.pck");
                const string citylight="1014d625e9a90c239b2db9fc1de77b577707095716747e247efeb9125609e8d5";
                if(File.Exists(pck)&&FileHash(pck)==citylight){files["OpenStreets.pck"]=citylight;version="0.8 Citylight";}
                const string thrusters="e5fc2f0b96c2f36b5e666f512ff72da37dfb60f137de9cf05225dff7d22867d5";
                const string ice="0a669ef5a577608a3b539454dfa985b3785a864cb401358c8409a7b8b9204db6";
                if(File.Exists(pck)&&FileHash(pck)==thrusters){files["OpenStreets.pck"]=thrusters;version="0.9 Thrusters";}
                if(File.Exists(pck)&&FileHash(pck)==ice){files["OpenStreets.pck"]=ice;version="0.10 Ice City";}
                const string explorer="8b6483685b0a9baecd8d009db5bc1f8d5ee550d7741445021e45e98f4d3af169";
                if(File.Exists(pck)&&FileHash(pck)==explorer){files["OpenStreets.pck"]=explorer;version="0.11 Street Explorer";}
                const string streetlife="3b5ef6516ae52585545ec8ecced53b578cf17d205be3b1cc4b69a8243bfcfecf";
                if(File.Exists(pck)&&FileHash(pck)==streetlife){files["OpenStreets.pck"]=streetlife;version="0.7 Streetlife";}
                return new ReleaseRecord{Version=version,Files=files};
            }
            return ReadReceipt(Path.Combine(VersionDirectory(id),"release.json"));
        }
        static ReleaseRecord ReadReceipt(string receipt) {
            if(new FileInfo(receipt).Length>1048576)throw new IOException("Release receipt is too large.");
            string raw=File.ReadAllText(receipt,Encoding.UTF8);
            var r=new JavaScriptSerializer().Deserialize<ReleaseRecord>(raw);
            if(r!=null&&r.Runtime=="unreal-win64") { ValidateUnrealReceipt(r);return r; }
            if(raw.Length>65536)throw new IOException("Legacy release receipt is too large.");
            if(r==null||(!String.IsNullOrEmpty(r.Runtime)&&r.Runtime!="godot")||String.IsNullOrWhiteSpace(r.Version)||r.Version.Length>80||r.Files==null||r.Files.Count!=4)throw new IOException("Invalid release receipt.");
            foreach(string name in Names)if(!r.Files.ContainsKey(name)||!Regex.IsMatch(r.Files[name]??"","\\A[a-f0-9]{64}\\z"))throw new IOException("Invalid release hashes.");
            if(r.Files["OpenStreets.exe"]!=BuildInfo.OfficialRuntimeHash)throw new IOException("This update needs a different game runtime.");
            return r;
        }
        internal static void ValidateUnrealReceipt(ReleaseRecord r) {
            if(r==null||r.Runtime!="unreal-win64"||String.IsNullOrWhiteSpace(r.Version)||r.Version.Length>80||r.Files==null||r.Files.Count<4||r.Files.Count>4096)throw new IOException("Invalid Unreal release receipt.");
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var file in r.Files) {
                SafeGamePath(file.Key);
                if(!names.Add(file.Key)||!Regex.IsMatch(file.Value??"","\\A[a-f0-9]{64}\\z"))throw new IOException("Invalid Unreal file hashes.");
            }
            if(!r.Files.ContainsKey("OpenStreets.exe")||!r.Files.ContainsKey("OpenStreets/Binaries/Win64/OpenStreets-Win64-Shipping.exe"))throw new IOException("Unreal game executables are missing from the manifest.");
            bool container=false;foreach(string key in r.Files.Keys)if(key.StartsWith("OpenStreets/Content/Paks/",StringComparison.Ordinal)&&key.EndsWith(".utoc",StringComparison.Ordinal))container=true;
            if(!container)throw new IOException("Unreal cooked game content is missing.");
        }
        internal static void SafeGamePath(string path) {
            if(String.IsNullOrEmpty(path)||path.Length>240||path.IndexOf('\\')>=0||path.IndexOf(':')>=0||path.StartsWith("/")||path.EndsWith("/")||Regex.IsMatch(path,"[\\x00-\\x1f]"))throw new IOException("Unsafe game file path.");
            foreach(string part in path.Split('/'))if(part.Length==0||part=="."||part==".."||part.EndsWith(".")||part.EndsWith(" ")||Regex.IsMatch(part,"\\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|$)",RegexOptions.IgnoreCase))throw new IOException("Unsafe game file path.");
            if(path!="OpenStreets.exe"&&path!="GAME-LICENSE.txt"&&path!="ENGINE-LICENSES.txt"&&!path.StartsWith("Engine/",StringComparison.Ordinal)&&!path.StartsWith("OpenStreets/",StringComparison.Ordinal))throw new IOException("Unexpected game file location.");
        }
        public static string FileHash(string path) {
            using(var file=File.OpenRead(LongPath(path)))using(var sha=SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();
        }
        // Keep path validation/comparisons in ordinary absolute form. Only the final
        // Windows filesystem call uses extended syntax for deeply nested UE files.
        internal static string LongPath(string path) {
            if(path.StartsWith(@"\\?\",StringComparison.Ordinal))return path;
            string full=Path.GetFullPath(path);
            return full.StartsWith(@"\\",StringComparison.Ordinal)?@"\\?\UNC\"+full.Substring(2):@"\\?\"+full;
        }
        internal static void ValidateDirectory(string directory,ReleaseRecord r,CancellationToken token) {
            foreach(string name in r.Files.Keys) {
                token.ThrowIfCancellationRequested();
                string path=Path.Combine(directory,name);
                if(!File.Exists(LongPath(path))||FileHash(path)!=r.Files[name])throw new IOException("Game files are missing or changed. Import a complete update ZIP, or restore the previous version.");
            }
        }
        public void Validate(CancellationToken token){ValidateDirectory(GameDirectory,release,token);}
        void Commit(string id,string oldId,ReleaseRecord record) {
            string temporary=Path.Combine(Root,"selection-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                byte[] data=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new GameSelection{Current=id,Previous=oldId}));
                using(var f=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)){f.Write(data,0,data.Length);f.Flush(true);}
                if(File.Exists(SelectionPath))File.Replace(temporary,SelectionPath,null);
                else File.Move(temporary,SelectionPath);
                current=id;previous=oldId;release=record;SelectionWarning=null;
            }finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
        public void Import(string zipPath,CancellationToken token,Action<string> report) {
            using(var gate=new FileStream(Path.Combine(Root,"update-import.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            using(var package=new UpdatePackage(zipPath)) {
                token.ThrowIfCancellationRequested();
                string id=Guid.NewGuid().ToString("N");
                string updates=Path.Combine(Root,"Updates");Directory.CreateDirectory(updates);
                string stage=Path.Combine(updates,"incoming-"+id), final=VersionDirectory(id);
                Directory.CreateDirectory(stage);
                bool moved=false;
                try {
                    package.Extract(stage,GameExe,token,report);
                    ValidateDirectory(stage,package.Record,token);
                    File.WriteAllText(Path.Combine(stage,"release.json"),new JavaScriptSerializer().Serialize(package.Record),Encoding.UTF8);
                    token.ThrowIfCancellationRequested();
                    Directory.Move(stage,final);moved=true;
                    // Once commit starts, finish atomically rather than cancel halfway.
                    Commit(id,current,package.Record);
                }finally {
                    // Only files we created in this generated staging directory are removed.
                    if(!moved&&Directory.Exists(stage)) {
                        DeleteStage(stage,updates);
                    }
                }
            }
        }
        internal static void DeleteStage(string stage,string updates) {
            string expected=Path.GetFullPath(updates)+Path.DirectorySeparatorChar;
            string root=Path.GetFullPath(stage);
            if(!root.StartsWith(expected,StringComparison.OrdinalIgnoreCase)||!Path.GetFileName(root).StartsWith("incoming-",StringComparison.Ordinal))throw new IOException("Invalid update staging folder.");
            DeleteOwnedTree(LongPath(root));
        }
        static void DeleteOwnedTree(string directory) {
            if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)throw new IOException("Update staging folder was replaced with a link.");
            foreach(string file in Directory.GetFiles(directory))File.Delete(file);
            foreach(string child in Directory.GetDirectories(directory))DeleteOwnedTree(child);
            Directory.Delete(directory,false);
        }
        public void Rollback(CancellationToken token) {
            if(!CanRollback)throw new IOException("There is no previous update yet.");
            using(var gate=new FileStream(Path.Combine(Root,"update-import.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None)) {
                var target=ReadRelease(previous);
                ValidateDirectory(VersionDirectory(previous),target,token);
                token.ThrowIfCancellationRequested();Commit(previous,current,target);
            }
        }
        public Process Launch(bool test) {
            if(IsUnreal&&Path.Combine(GameDirectory,"OpenStreets","Binaries","Win64","OpenStreets-Win64-Shipping.exe").Length>=260)
                throw new IOException("The Unreal game executable is in a folder that is too deep. Move the complete launcher folder to a short location such as C:\\Games\\OpenStreets and try Play again.");
            var start=new ProcessStartInfo(GameExe){WorkingDirectory=GameDirectory,UseShellExecute=false};
#if TEST
            if(test) {
                string profile=Path.Combine(Root,"test-profile");Directory.CreateDirectory(profile);
                start.Arguments=IsUnreal?"-OpenStreetsTest -OpenStreetsSmokeTest -nullrhi -unattended -OpenStreetsReport=\""+Path.Combine(Root,"packaged-smoke.json")+"\"":"--headless --audio-driver Dummy --quit-after 45 --log-file \""+Path.Combine(Root,"game-smoke.log")+"\"";
                start.EnvironmentVariables["APPDATA"]=profile;start.CreateNoWindow=true;
            }
#endif
            return Process.Start(start);
        }
    }
}
