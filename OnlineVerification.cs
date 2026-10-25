using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OpenStreetsLauncher {
    static class OnlineVerification {
        sealed class Server : HttpMessageHandler {
            internal readonly Dictionary<string,Func<HttpResponseMessage>> Routes=new Dictionary<string,Func<HttpResponseMessage>>();
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
                token.ThrowIfCancellationRequested();
                Func<HttpResponseMessage> response;
                if(!Routes.TryGetValue(request.RequestUri.AbsoluteUri,out response))throw new IOException("Unexpected test address.");
                return Task.FromResult(response());
            }
        }
        static bool Fails(Action action){try{action();return false;}catch(Exception){return true;}}
        static HttpResponseMessage Bytes(byte[] bytes){return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}
        static void Text(ZipArchive z,string name,string text){using(var writer=new StreamWriter(z.CreateEntry(name).Open(),new UTF8Encoding(false)))writer.Write(text);}
        internal static string MakeZip(string root,string source,string version) {
            string path=Path.Combine(root,"online-test.zip");
            using(var zip=ZipFile.Open(path,ZipArchiveMode.Create)) {
                var hashes=new StringBuilder();foreach(var name in GamePackage.Names)hashes.Append(GamePackage.FileHash(Path.Combine(source,name))+"  Game/"+name+"\n");
                Text(zip,"CHECKSUMS.sha256",hashes.ToString());
                Text(zip,"update-info.json",new JavaScriptSerializer().Serialize(new{game="Open Streets",version=version}));
                foreach(var name in GamePackage.Names)if(name!="OpenStreets.exe")zip.CreateEntryFromFile(Path.Combine(source,name),"Game/"+name,CompressionLevel.Optimal);
            }
            return path;
        }
        public static void Run(Action<bool,string> check,string root) {
            string source=Path.Combine(root,"Game"),zip=MakeZip(root,source,"0.12 Network Test");
            var game=new GamePackage(root);
            var release=new OnlineRelease{game="Open Streets",version="0.12 Network Test",zip_url="https://downloads.example/game.zip",size=new FileInfo(zip).Length,sha256=GamePackage.FileHash(zip),notes="Walking and visible shops"};
            var server=new Server();var serializer=new JavaScriptSerializer();
            server.Routes["https://updates.example/latest.json"]=()=>Bytes(Encoding.UTF8.GetBytes(serializer.Serialize(release)));
            server.Routes[release.zip_url]=()=>Bytes(File.ReadAllBytes(zip));
            using(var online=new OnlineUpdates(server)) {
                var found=online.Check("https://updates.example/latest.json",CancellationToken.None).GetAwaiter().GetResult();
                check(found.version==release.version&&OnlineUpdates.IsNewer(found,game.DisplayVersion),"Online feed finds a newer game without needing a local ZIP");
                check(!OnlineUpdates.IsNewer(found,"0.12 Network Test")&&!OnlineUpdates.IsNewer(found,"0.13 Future"),"Same and older versions do not auto-install or downgrade");
                string previous=game.GameDirectory;
                online.Install(found,game,CancellationToken.None,s=>{}).GetAwaiter().GetResult();game.Validate(CancellationToken.None);
                check(game.DisplayVersion==release.version&&game.GameDirectory!=previous,"Download verifies the ZIP and imports the real game in one operation");
                check(new GamePackage(root).GameDirectory==game.GameDirectory,"Online update selection persists after restarting the launcher");
                check(Directory.GetFiles(Path.Combine(root,"Downloads")).Length==0,"Successful download leaves no temporary ZIP behind");
                game.Rollback(CancellationToken.None);check(game.GameDirectory==previous,"Online update supports Restore Previous");
                string selection=game.GameDirectory;
                string hash=release.sha256;release.sha256=new string('a',64);
                check(Fails(()=>online.Install(release,game,CancellationToken.None,s=>{}).GetAwaiter().GetResult())&&game.GameDirectory==selection,"Bad network checksum preserves the working game");release.sha256=hash;
                string version=release.version;release.version="0.13 Wrong Label";
                check(Fails(()=>online.Install(release,game,CancellationToken.None,s=>{}).GetAwaiter().GetResult())&&game.GameDirectory==selection,"Feed and ZIP version mismatch cannot change selection");release.version=version;
                long size=release.size;release.size++;
                check(Fails(()=>online.Install(release,game,CancellationToken.None,s=>{}).GetAwaiter().GetResult())&&game.GameDirectory==selection,"Unexpected download size is rejected before import");release.size=size;
                var cancel=new CancellationTokenSource();cancel.Cancel();
                check(Fails(()=>online.Install(release,game,cancel.Token,s=>{}).GetAwaiter().GetResult())&&game.GameDirectory==selection,"Cancelled download keeps the current version selected");
                var midway=new CancellationTokenSource();
                check(Fails(()=>online.Install(release,game,midway.Token,s=>midway.Cancel()).GetAwaiter().GetResult())&&game.GameDirectory==selection,"Cancellation during transfer discards the partial ZIP without switching versions");
                server.Routes[release.zip_url]=()=>{var r=Bytes(new byte[17]);r.Content.Headers.ContentLength=release.size;return r;};
                check(Fails(()=>online.Install(release,game,CancellationToken.None,s=>{}).GetAwaiter().GetResult())&&game.GameDirectory==selection,"A stream ending early cannot install a truncated game");
                server.Routes[release.zip_url]=()=>Bytes(File.ReadAllBytes(zip));
                check(Directory.GetFiles(Path.Combine(root,"Downloads")).Length==0,"Failed and cancelled downloads clean up their temporary ZIPs");
                release.game="Other Game";
                check(Fails(()=>online.Check("https://updates.example/latest.json",CancellationToken.None).GetAwaiter().GetResult()),"A feed for another game is rejected");release.game="Open Streets";
                foreach(string url in new[]{"http://updates.example/latest.json","file:///C:/secret","https://user:password@example.com/latest.json"})
                    check(Fails(()=>OnlineUpdates.Address(url)),"Reject insecure or credential-bearing address: "+url.Split(':')[0]);
                server.Routes["https://updates.example/redirect"]=()=>{var r=new HttpResponseMessage(HttpStatusCode.Redirect);r.Headers.Location=new Uri("http://updates.example/latest.json");return r;};
                check(Fails(()=>online.Check("https://updates.example/redirect",CancellationToken.None).GetAwaiter().GetResult()),"An HTTPS redirect cannot downgrade the connection to HTTP");
                server.Routes["https://updates.example/large"]=()=>Bytes(new byte[65537]);
                check(Fails(()=>online.Check("https://updates.example/large",CancellationToken.None).GetAwaiter().GetResult()),"Oversized update information is bounded and rejected");
                server.Routes["https://updates.example/missing"]=()=>new HttpResponseMessage(HttpStatusCode.NotFound);
                check(Fails(()=>online.Check("https://updates.example/missing",CancellationToken.None).GetAwaiter().GetResult())&&game.GameDirectory==selection,"Offline/server failures never remove the installed game");
                server.Routes["https://updates.example/redirect"]=()=>{var r=new HttpResponseMessage(HttpStatusCode.Redirect);r.Headers.Location=new Uri("https://updates.example/latest.json");return r;};
                check(online.Check("https://updates.example/redirect",CancellationToken.None).GetAwaiter().GetResult().version==release.version,"HTTPS release-asset redirects are supported");
                server.Routes["https://updates.example/loop"]=()=>{var r=new HttpResponseMessage(HttpStatusCode.Redirect);r.Headers.Location=new Uri("https://updates.example/loop");return r;};
                check(Fails(()=>online.Check("https://updates.example/loop",CancellationToken.None).GetAwaiter().GetResult()),"Redirect loops stop after a bounded number of requests");
            }
            var settings=new UpdateSource{Url="https://updates.example/latest.json",CheckOnStart=true};settings.Save(root);
            check(UpdateSource.Load(root).Url==settings.Url&&UpdateSource.Load(root).CheckOnStart,"Permanent address and startup check survive launcher restart");
            string sourceUrl=OnlineUpdates.SourceUrl("https://github.com/example/OpenStreets");
            check(sourceUrl=="https://github.com/example/OpenStreets/releases/latest/download/latest.json","Creator can paste a repository link instead of assembling a feed URL");
            string prepared=ReleasePublisher.Prepare(root,sourceUrl,zip);
            var publication=serializer.Deserialize<OnlineRelease>(File.ReadAllText(Path.Combine(prepared,"latest.json")));
            check(publication.version==release.version&&publication.sha256==GamePackage.FileHash(Path.Combine(prepared,"OpenStreets-update.zip"))&&publication.zip_url.Contains("/v0.12/"),"Creator's Prepare Release produces matching upload files with an immutable version link");
            File.Delete(zip);
        }
    }
}
