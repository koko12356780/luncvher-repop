using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace OpenStreetsLauncher {
    static class UpdateVerification {
        static bool Fails(Action a){try{a();return false;}catch(Exception){return true;}}
        static void Entry(ZipArchive z,string name,byte[] data){using(var s=z.CreateEntry(name,CompressionLevel.Optimal).Open())s.Write(data,0,data.Length);}
        static void Text(ZipArchive z,string name,string value){Entry(z,name,Encoding.UTF8.GetBytes(value));}
        static string Make(string root,string source,string version,string fault) {
            string path=Path.Combine(root,"update-"+Guid.NewGuid().ToString("N")+".zip");
            string prefix="OpenStreets-Citylight/";
            var hashes=new Dictionary<string,string>(BuildInfo.Files);
            byte[] original=File.ReadAllBytes(Path.Combine(source,"OpenStreets.pck"));
            byte[] pck=new byte[original.Length+1];Buffer.BlockCopy(original,0,pck,0,original.Length);pck[pck.Length-1]=42;
            using(var sha=System.Security.Cryptography.SHA256.Create())hashes["OpenStreets.pck"]=BitConverter.ToString(sha.ComputeHash(pck)).Replace("-","").ToLowerInvariant();
            if(fault=="engine")hashes["OpenStreets.exe"]=new string('a',64);
            if(fault=="hash")pck[pck.Length-1]=43;
            using(var z=ZipFile.Open(path,ZipArchiveMode.Create)) {
                var manifest=new StringBuilder();foreach(var kv in hashes)manifest.Append(kv.Value+"  Game/"+kv.Key+"\n");
                Text(z,prefix+"CHECKSUMS.sha256",manifest.ToString());
                Text(z,prefix+"update-info.json",new JavaScriptSerializer().Serialize(new {game=fault=="other"?"Other Game":"Open Streets",version=version}));
                Entry(z,prefix+"Game/OpenStreets.pck",pck);
                if(fault!="missing")Text(z,prefix+"Game/GAME-LICENSE.txt",File.ReadAllText(Path.Combine(source,"GAME-LICENSE.txt")));
                if(fault=="size")Entry(z,prefix+"Game/ENGINE-LICENSES.txt",new byte[2*1024*1024+1]);
                else Entry(z,prefix+"Game/ENGINE-LICENSES.txt",File.ReadAllBytes(Path.Combine(source,"ENGINE-LICENSES.txt")));
                // These arbitrary extras must never be extracted or executed.
                Text(z,prefix+"OpenStreetsLauncher.exe","ignored archive executable");
                Text(z,prefix+"Game/OpenStreets.exe","ignored replacement runtime");
                Text(z,prefix+"launcher-sentinel.txt","overwrite attempt");
                if(fault=="traversal")Text(z,"../escape.txt","escape");
                if(fault=="duplicate")Entry(z,prefix+"Game/OpenStreets.pck",pck);
            }
            return path;
        }
        public static void Run(Action<bool,string> check,string root) {
            var game=new GamePackage(root);
            string baseDir=game.GameDirectory;
            string original=GamePackage.FileHash(game.GameExe);
            string sentinel=Path.Combine(root,"launcher-sentinel.txt");File.WriteAllText(sentinel,"keep launcher");
            string save=Path.Combine(root,"test-profile","save-sentinel.cfg");Directory.CreateDirectory(Path.GetDirectoryName(save));File.WriteAllText(save,"keep cars, coins and upgrades");
            string good=Make(root,baseDir,"0.9 verification",null);
            game.Import(good,CancellationToken.None,s=>{});
            string imported=game.GameDirectory;
            check(imported!=baseDir&&Directory.Exists(imported)&&game.CanRollback,"ZIP update selects a separate version and offers rollback");
            check(game.DisplayVersion=="0.9 verification"&&GamePackage.FileHash(Path.Combine(imported,"OpenStreets.pck"))!=BuildInfo.Files["OpenStreets.pck"],"New game data and version label are imported");
            check(GamePackage.FileHash(game.GameExe)==original,"Imported ZIP cannot replace the pinned official runtime");
            check(File.ReadAllText(sentinel)=="keep launcher"&&File.ReadAllText(save)=="keep cars, coins and upgrades","Import leaves launcher files and saved progress intact");
            check(GamePackage.FileHash(Path.Combine(baseDir,"OpenStreets.pck"))==BuildInfo.Files["OpenStreets.pck"],"Bundled game is not overwritten by an update");
            game.Validate(CancellationToken.None);
            check(new GamePackage(root).GameDirectory==imported,"Selected update survives launcher restart");
            using(var process=game.Launch(true))check(process.WaitForExit(30000)&&process.ExitCode==0,"Actual imported PCK launches with the official runtime");
            string smoke=File.ReadAllText(Path.Combine(root,"game-smoke.log"));
            check(!smoke.Contains("ERROR"),"Imported game loads without script or resource errors");
            game.Rollback(CancellationToken.None);
            check(game.GameDirectory==baseDir&&Directory.Exists(imported),"Restore Previous returns to bundled game and retains update files");
            game.Rollback(CancellationToken.None);
            check(game.GameDirectory==imported,"Restore Previous can switch forward again");
            foreach(string fault in new[]{"hash","engine","traversal","duplicate","size","missing","other"}) {
                string bad=Make(root,baseDir,"bad test",fault);
                check(Fails(()=>game.Import(bad,CancellationToken.None,s=>{}))&&game.GameDirectory==imported&&new GamePackage(root).GameDirectory==imported,"Reject "+fault+" package without changing selected game");
                File.Delete(bad);
            }
            var cancel=new CancellationTokenSource();cancel.Cancel();
            check(Fails(()=>game.Import(good,cancel.Token,s=>{}))&&game.GameDirectory==imported,"Cancelled import preserves selection");
            var mid=new CancellationTokenSource();
            check(Fails(()=>game.Import(good,mid.Token,s=>{if(s.Contains("OpenStreets.pck"))mid.Cancel();}))&&game.GameDirectory==imported,"Cancellation during extraction preserves the working game");
            check(Directory.GetDirectories(Path.Combine(root,"Updates"),"incoming-*").Length==0,"Failed and cancelled staging files are cleaned up");
            check(!File.Exists(Path.Combine(Path.GetDirectoryName(root),"escape.txt")),"ZIP traversal creates no escaped file");
            string pointer=Path.Combine(root,"current-game.json"), stored=File.ReadAllText(pointer);
            File.WriteAllText(pointer,"{\"Current\":\"../../outside\"}");
            var invalid=new GamePackage(root);
            check(invalid.GameDirectory==baseDir&&!String.IsNullOrEmpty(invalid.SelectionWarning),"Invalid stored path falls back to bundled game without traversing directories");
            File.WriteAllText(pointer,stored);
            string receipt=Path.Combine(imported,"release.json"), savedReceipt=File.ReadAllText(receipt);
            File.WriteAllText(receipt,"{}");
            invalid=new GamePackage(root);
            check(invalid.GameDirectory==baseDir&&!String.IsNullOrEmpty(invalid.SelectionWarning),"Damaged release receipt falls back safely");
            File.WriteAllText(receipt,savedReceipt);
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            using(var form=new LauncherForm(game,true)) {
                form.Show();Application.DoEvents();
                var task=form.TestImport(good);DateTime deadline=DateTime.UtcNow.AddSeconds(30);
                while(!task.IsCompleted&&DateTime.UtcNow<deadline){Application.DoEvents();Thread.Sleep(10);}
                check(task.IsCompleted&&!task.IsFaulted&&form.TestReady&&form.TestStatus.StartsWith("Update ready:"),"UI import handler finishes with Play ready and success status");
                form.TestShowUpdates();Application.DoEvents();
                using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(0,0,form.Width,form.Height));bmp.Save(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"launcher-updates-preview.png"));}
                form.Close();
            }
            SynchronizationContext.SetSynchronizationContext(null);
            check(File.ReadAllText(save)=="keep cars, coins and upgrades","Repeated updates and rollback preserve saved progress");
            File.Delete(good);
        }
    }
}
