using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
namespace OpenStreetsLauncher {
    static class UnrealPackageChecks {
        static int checks;
        static void Check(bool ok,string name){Console.WriteLine((ok?"PASS ":"FAIL ")+name);if(!ok)throw new Exception(name);checks++;}
        static bool Fails(Action action){try{action();return false;}catch{return true;}}
        static string Hash(byte[] bytes){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-","").ToLowerInvariant();}
        static void Write(ZipArchive zip,string path,string text){using(var w=new StreamWriter(zip.CreateEntry(path).Open(),new UTF8Encoding(false)))w.Write(text);}
        static string Fixture(string root,string fault) {
            string path=Path.Combine(root,Guid.NewGuid().ToString("N")+".zip");
            var files=new Dictionary<string,string>{{"OpenStreets.exe","native bootstrap fixture"},{"OpenStreets/Binaries/Win64/OpenStreets-Win64-Shipping.exe","native game fixture"},{"OpenStreets/Content/Paks/OpenStreets-Windows.utoc","container index fixture"},{"OpenStreets/Content/Paks/OpenStreets-Windows.ucas","cooked content fixture"}};
            if(fault=="unsafe")files.Add("Engine/../escape.exe","escape");
            if(fault=="linkname")files.Add("Engine/CON.txt","reserved");
            if(fault=="missing")files.Remove("OpenStreets/Content/Paks/OpenStreets-Windows.utoc");
            var manifest=new StringBuilder();
            using(var z=ZipFile.Open(path,ZipArchiveMode.Create)) {
                foreach(var file in files){manifest.Append(fault=="hash"?new string('0',64):Hash(Encoding.UTF8.GetBytes(file.Value))).Append("  Game/").Append(file.Key).Append('\n');Write(z,"Game/"+file.Key,file.Value);}
                Write(z,"CHECKSUMS.sha256",manifest.ToString());
                Write(z,"update-info.json",new JavaScriptSerializer().Serialize(new {game="Open Streets",version="1.0.0 Unreal Street Explorer",runtime="unreal-win64"}));
                if(fault=="duplicate")Write(z,"Game/openstreets.exe","case duplicate");
                if(fault=="extra")Write(z,"Game/Engine/Extra.dll","not listed");
            }return path;
        }
        public static int Main(string[] args) {
            try {
                string root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
                string good=Fixture(root,null);
                var fresh=new GamePackage(root);Check(!fresh.IsInstalled,"Fresh launcher starts without game files");
                fresh.Import(good,CancellationToken.None,s=>{});fresh.Validate(CancellationToken.None);
                Check(fresh.IsUnreal&&fresh.IsInstalled,"Native Unreal package installs without an existing Godot EXE");
                var restarted=new GamePackage(root);restarted.Validate(CancellationToken.None);
                Check(restarted.IsUnreal&&restarted.DisplayVersion==fresh.DisplayVersion,"Unreal selection and nested hashes survive restart");
                string installed=fresh.GameDirectory;
                foreach(string fault in new[]{"unsafe","linkname","missing","hash","duplicate","extra"}) {
                    Check(Fails(()=>fresh.Import(Fixture(root,fault),CancellationToken.None,s=>{})),"Rejects "+fault+" package");
                    Check(fresh.GameDirectory==installed,"Failed "+fault+" import preserves active version");
                }
                var cts=new CancellationTokenSource();cts.Cancel();Check(Fails(()=>fresh.Import(good,cts.Token,s=>{})),"Import cancellation preserves active game");
                foreach(string folder in Directory.GetDirectories(Path.Combine(root,"Updates")))Check(!Path.GetFileName(folder).StartsWith("incoming-"),"No abandoned extraction stage");
                string previous=fresh.GameDirectory;fresh.Import(good,CancellationToken.None,s=>{});fresh.Rollback(CancellationToken.None);
                Check(fresh.GameDirectory==previous,"Native update rollback restores previous version");
                Check(Fails(()=>GamePackage.SafeGamePath("Engine/x.dll:stream")),"Rejects alternate data streams");
                Check(Fails(()=>GamePackage.SafeGamePath("Engine/trailing. /x")),"Rejects Windows-normalized path aliases");
                Console.WriteLine("RESULT "+checks+" checks passed");return 0;
            }catch(Exception ex){Console.WriteLine(ex);return 1;}
        }
    }
}
