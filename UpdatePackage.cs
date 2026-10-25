using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace OpenStreetsLauncher {
    // ZIP contents are read explicitly into four fixed filenames. General-purpose
    // extraction and execution of installers/scripts from the archive are not used.
    public sealed class UpdatePackage : IDisposable {
        readonly ZipArchive archive;
        readonly Dictionary<string,ZipArchiveEntry> entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        string prefix;
        public ReleaseRecord Record {get;private set;}
        const long MaxArchive=512L*1024*1024;
        public UpdatePackage(string path) {
            if(new FileInfo(path).Length>MaxArchive)throw new IOException("Update ZIP exceeds 512 MB.");
            archive=ZipFile.OpenRead(path);
            try{Inspect(path);}catch{archive.Dispose();throw;}
        }
        void Inspect(string path) {
            if(archive.Entries.Count>8192)throw new IOException("This is not an Open Streets game update ZIP.");
            ZipArchiveEntry manifest=null;
            foreach(var e in archive.Entries) {
                string name=e.FullName;
                if(((e.ExternalAttributes>>16)&0xf000)==0xa000)throw new IOException("Links are not allowed in game updates.");
                if(name.IndexOf('\\')>=0||name.StartsWith("/")||name.IndexOf(':')>=0||name.IndexOf('\0')>=0)throw new IOException("Unsafe path in update ZIP.");
                foreach(string part in name.Split('/'))if(part==".."||part==".")throw new IOException("Unsafe path in update ZIP.");
                if(entries.ContainsKey(name))throw new IOException("Duplicate filenames in update ZIP.");
                entries.Add(name,e);
                if(name=="CHECKSUMS.sha256"||name.EndsWith("/CHECKSUMS.sha256",StringComparison.Ordinal)) {
                    if(manifest!=null)throw new IOException("Update ZIP has more than one checksum manifest.");
                    manifest=e;
                }
            }
            if(manifest==null)throw new IOException("Choose a game update or Windows game ZIP containing CHECKSUMS.sha256. A source-project ZIP is not a playable update.");
            prefix=manifest.FullName.Substring(0,manifest.FullName.Length-"CHECKSUMS.sha256".Length);
            ZipArchiveEntry runtimeInfo;
            if(entries.TryGetValue(prefix+"update-info.json",out runtimeInfo)) {
                var metadata=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Text(runtimeInfo));
                if(metadata!=null&&metadata.ContainsKey("runtime")&&(metadata["runtime"] as string)=="unreal-win64") { InspectUnreal(manifest,metadata);return; }
            }
            var hashes=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(string line in Text(manifest).Split('\n')) {
                if(String.IsNullOrWhiteSpace(line))continue;
                var match=Regex.Match(line.TrimEnd('\r'),"\\A([a-fA-F0-9]{64})  (.+)\\z");
                if(!match.Success)throw new IOException("Invalid checksum manifest.");
                string filename=match.Groups[2].Value;
                if(!filename.StartsWith("Game/",StringComparison.Ordinal))continue;
                string name=filename.Substring(5);
                if(Array.IndexOf(GamePackage.Names,name)<0)throw new IOException("Unexpected game file in update manifest.");
                if(hashes.ContainsKey(name))throw new IOException("Duplicate game hashes in update manifest.");
                hashes.Add(name,match.Groups[1].Value.ToLowerInvariant());
            }
            if(hashes.Count!=4)throw new IOException("Update manifest must identify all four game files.");
            if(hashes["OpenStreets.exe"]!=BuildInfo.OfficialRuntimeHash)throw new IOException("This update needs a newer launcher/runtime. The current launcher will not import a replacement engine executable.");
            foreach(string name in GamePackage.Names) {
                if(name=="OpenStreets.exe")continue; // Copy pinned official runtime locally.
                if(!entries.ContainsKey(prefix+"Game/"+name))throw new IOException("Update is missing "+name+".");
                var e=entries[prefix+"Game/"+name];
                long limit=name=="OpenStreets.pck"?256L*1024*1024:2L*1024*1024;
                if(e.Length<=0||e.Length>limit)throw new IOException("Invalid game file size: "+name);
            }
            string version=Path.GetFileNameWithoutExtension(path);
            ZipArchiveEntry info;
            if(entries.TryGetValue(prefix+"update-info.json",out info)) {
                var metadata=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(Text(info));
                if(metadata==null||!metadata.ContainsKey("game")||(metadata["game"] as string)!="Open Streets"||!metadata.ContainsKey("version"))throw new IOException("This package is for a different game.");
                version=metadata["version"] as string;
            }
            if(String.IsNullOrWhiteSpace(version)||version.Length>80||Regex.IsMatch(version,"[\\x00-\\x1f]"))throw new IOException("Invalid update version label.");
            Record=new ReleaseRecord{Version=version,Files=hashes};
        }
        void InspectUnreal(ZipArchiveEntry manifest,Dictionary<string,object> metadata) {
            if(!metadata.ContainsKey("game")||(metadata["game"] as string)!="Open Streets"||!metadata.ContainsKey("version"))throw new IOException("This Unreal package is for a different game.");
            var hashes=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(string line in Text(manifest,1048576).Split('\n')) {
                if(String.IsNullOrWhiteSpace(line))continue;
                var match=Regex.Match(line.TrimEnd('\r'),"\\A([a-fA-F0-9]{64})  Game/(.+)\\z");
                if(!match.Success)throw new IOException("Invalid Unreal checksum manifest.");
                string name=match.Groups[2].Value;GamePackage.SafeGamePath(name);
                if(hashes.ContainsKey(name))throw new IOException("Duplicate Unreal game hash.");
                hashes.Add(name,match.Groups[1].Value.ToLowerInvariant());
            }
            Record=new ReleaseRecord{Runtime="unreal-win64",Version=metadata["version"] as string,Files=hashes};
            GamePackage.ValidateUnrealReceipt(Record);
            long total=0;
            foreach(string name in hashes.Keys) {
                ZipArchiveEntry entry;if(!entries.TryGetValue(prefix+"Game/"+name,out entry)||entry.Length<=0||entry.Length>2L*1024*1024*1024)throw new IOException("Missing or oversized Unreal game file: "+name);
                total+=entry.Length;if(total>4L*1024*1024*1024)throw new IOException("Unreal update expands beyond its size limit.");
            }
            foreach(string name in entries.Keys)if(name.StartsWith(prefix+"Game/",StringComparison.Ordinal)&&!name.EndsWith("/",StringComparison.Ordinal)&&name!=prefix+"Game/release.json"&&!hashes.ContainsKey(name.Substring(prefix.Length+5)))throw new IOException("Unlisted Unreal game file.");
        }
        static string Text(ZipArchiveEntry entry,int limit=65536) {
            if(entry.Length>limit)throw new IOException("Update metadata is too large.");
            using(var source=entry.Open())using(var output=new MemoryStream()) {
                byte[] buffer=new byte[4096];int n;
                while((n=source.Read(buffer,0,buffer.Length))>0){if(output.Length+n>limit)throw new IOException("Update metadata is too large.");output.Write(buffer,0,n);}
                return Encoding.UTF8.GetString(output.ToArray()).TrimStart('\ufeff');
            }
        }
        public void Extract(string directory,string runtime,CancellationToken token,Action<string> report) {
            if(Record.Runtime=="unreal-win64") {
                foreach(string name in Record.Files.Keys) {
                    token.ThrowIfCancellationRequested();report("Importing Unreal game: "+name);
                    string target=Path.GetFullPath(Path.Combine(directory,name.Replace('/',Path.DirectorySeparatorChar)));
                    if(!target.StartsWith(Path.GetFullPath(directory)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("Unsafe extraction path.");
                    Directory.CreateDirectory(GamePackage.LongPath(Path.GetDirectoryName(target)));
                    using(var input=entries[prefix+"Game/"+name].Open())using(var output=new FileStream(GamePackage.LongPath(target),FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                        var buffer=new byte[65536];int n;long total=0;
                        while((n=input.Read(buffer,0,buffer.Length))>0){token.ThrowIfCancellationRequested();total+=n;if(total>entries[prefix+"Game/"+name].Length)throw new IOException("Unreal file expands beyond its declared size.");output.Write(buffer,0,n);}output.Flush(true);
                    }
                    if(GamePackage.FileHash(target)!=Record.Files[name])throw new IOException("Unreal update failed its checksum: "+name);
                }return;
            }
            if(GamePackage.FileHash(runtime)!=BuildInfo.OfficialRuntimeHash)throw new IOException("The existing game runtime is missing or changed. Restore the complete launcher download first.");
            foreach(string name in GamePackage.Names) {
                token.ThrowIfCancellationRequested();report("Importing "+name+"...");
                string target=Path.Combine(directory,name);
                using(var input=name=="OpenStreets.exe"?(Stream)File.OpenRead(runtime):entries[prefix+"Game/"+name].Open())
                using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    byte[] buffer=new byte[65536];int n;long total=0;
                    long limit=name=="OpenStreets.exe"?128L*1024*1024:name=="OpenStreets.pck"?256L*1024*1024:2L*1024*1024;
                    while((n=input.Read(buffer,0,buffer.Length))>0) {
                        token.ThrowIfCancellationRequested();total+=n;if(total>limit)throw new IOException("Update expands beyond its size limit.");
                        output.Write(buffer,0,n);
                    }
                    output.Flush(true);
                }
                if(GamePackage.FileHash(target)!=Record.Files[name])throw new IOException("Update file failed its checksum: "+name+". Download it again.");
            }
        }
        public void Dispose(){archive.Dispose();}
    }
}
