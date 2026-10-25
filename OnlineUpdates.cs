using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OpenStreetsLauncher {
    public sealed class UpdateSource {
        public const string GameFeed="https://github.com/koko12356780/open-world-sanboxcgame/releases/latest/download/latest.json";
        public const string LauncherFeed="https://github.com/koko12356780/luncvher-repop/releases/latest/download/launcher-latest.json";
        public string Url {get;set;}
        public bool CheckOnStart {get;set;}
        public static UpdateSource Load(string root) {
            string p=Path.Combine(root,"update-source.json");
            if(!File.Exists(p))return new UpdateSource{Url=GameFeed,CheckOnStart=true};
            var result=new JavaScriptSerializer().Deserialize<UpdateSource>(GamePackage.ReadSmall(p));
            if(result==null)throw new IOException("Update address could not be read.");
            if(String.IsNullOrWhiteSpace(result.Url))result.Url=GameFeed;
            if(!String.IsNullOrWhiteSpace(result.Url))OnlineUpdates.Address(result.Url);
            return result;
        }
        public void Save(string root) {
            if(!String.IsNullOrWhiteSpace(Url))OnlineUpdates.Address(Url);
            string target=Path.Combine(root,"update-source.json"),temp=Path.Combine(root,"source-"+Guid.NewGuid().ToString("N")+".tmp");
            try {
                File.WriteAllText(temp,new JavaScriptSerializer().Serialize(this),Encoding.UTF8);
                if(File.Exists(target))File.Replace(temp,target,null);else File.Move(temp,target);
            }finally{if(File.Exists(temp))File.Delete(temp);}
        }
    }
    public sealed class OnlineRelease {
        public string game {get;set;}
        public string version {get;set;}
        public string zip_url {get;set;}
        public string sha256 {get;set;}
        public long size {get;set;}
        public string notes {get;set;}
    }
    public sealed class OnlineUpdates : IDisposable {
        const long MaxZip=512L*1024*1024;
        readonly HttpClient client;
        public OnlineUpdates() : this(new HttpClientHandler{AllowAutoRedirect=false,UseCookies=false}) {}
        internal OnlineUpdates(HttpMessageHandler handler) {
            ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
            client=new HttpClient(handler);client.Timeout=Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("OpenStreetsLauncher/1.5");
            client.DefaultRequestHeaders.CacheControl=new System.Net.Http.Headers.CacheControlHeaderValue{NoCache=true};
        }
        public static Uri Address(string value) {
            Uri uri;
            if(value==null||value.Length>4096||!Uri.TryCreate(value,UriKind.Absolute,out uri)||uri.Scheme!="https"||String.IsNullOrEmpty(uri.Host)||!String.IsNullOrEmpty(uri.UserInfo)||!String.IsNullOrEmpty(uri.Fragment))
                throw new IOException("Use a direct HTTPS update address, without a login or password.");
            return uri;
        }
        public static string SourceUrl(string value) {
            value=(value??"").Trim().TrimEnd('/');
            var repo=Regex.Match(value,"\\A(?:https://github.com/)?([A-Za-z0-9-]+/[A-Za-z0-9_.-]+)\\z");
            if(repo.Success)return "https://github.com/"+repo.Groups[1].Value+"/releases/latest/download/latest.json";
            return String.IsNullOrWhiteSpace(value)?"":Address(value).AbsoluteUri;
        }
        static Version Number(string label) {
            if(String.IsNullOrWhiteSpace(label)||label.Length>80||Regex.IsMatch(label,"[\\x00-\\x1f]"))throw new IOException("Invalid online version.");
            var match=Regex.Match(label,"\\A([0-9]+\\.[0-9]+(?:\\.[0-9]+){0,2})(?: |$)");Version number;
            if(!match.Success||!Version.TryParse(match.Groups[1].Value,out number))throw new IOException("Invalid online version number.");
            return number;
        }
        public static bool IsNewer(OnlineRelease release,string installed){return Number(release.version)>Number(installed);}
        static void Validate(OnlineRelease r,string application="Open Streets") {
            if(r==null||r.game!=application)throw new IOException("This update address is for a different game.");
            Number(r.version);Address(r.zip_url);
            if(!Regex.IsMatch(r.sha256??"","\\A[a-fA-F0-9]{64}\\z")||r.size<=0||r.size>MaxZip)throw new IOException("Update checksum or size is invalid.");
            if(r.notes!=null&&r.notes.Length>2000)throw new IOException("Update notes are too long.");
        }
        async Task<HttpResponseMessage> Get(Uri uri,CancellationToken token) {
            for(int i=0;i<6;i++) {
                token.ThrowIfCancellationRequested();
                var response=await client.GetAsync(Address(uri.AbsoluteUri),HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
                int status=(int)response.StatusCode;
                if(status==301||status==302||status==303||status==307||status==308) {
                    var location=response.Headers.Location;response.Dispose();
                    if(location==null)throw new IOException("The download redirect is missing its address.");
                    uri=Address(new Uri(uri,location).AbsoluteUri);continue;
                }
                if(!response.IsSuccessStatusCode){response.Dispose();throw new IOException("Download server returned HTTP "+status+". You can still play your installed game.");}
                return response;
            }
            throw new IOException("Too many download redirects.");
        }
        public Task<OnlineRelease> Check(string url,CancellationToken token) {return CheckRelease(url,"Open Streets",token);}
        internal async Task<OnlineRelease> CheckRelease(string url,string application,CancellationToken token) {
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using(var response=await Get(Address(url),timeout.Token).ConfigureAwait(false))
                using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                using(var output=new MemoryStream()) {
                    var bytes=new byte[4096];int n;
                    while((n=await input.ReadAsync(bytes,0,bytes.Length,timeout.Token).ConfigureAwait(false))>0){if(output.Length+n>65536)throw new IOException("Update information is too large.");output.Write(bytes,0,n);}
                    var release=new JavaScriptSerializer().Deserialize<OnlineRelease>(Encoding.UTF8.GetString(output.ToArray()).TrimStart('\ufeff'));
                    Validate(release,application);return release;
                }
            }
        }
        public async Task Install(OnlineRelease release,GamePackage game,CancellationToken token,Action<string> report) {
            Validate(release);
            if(!IsNewer(release,game.DisplayVersion))throw new IOException("This version is already installed, or is older than your game.");
            string directory=Path.Combine(game.Root,"Downloads");Directory.CreateDirectory(directory);
            string path=Path.Combine(directory,"incoming-"+Guid.NewGuid().ToString("N")+".zip");
            try {
                await Download(release,path,token,report).ConfigureAwait(false);
                using(var zip=new UpdatePackage(path))if(zip.Record.Version!=release.version)throw new IOException("The download does not match the advertised game version.");
                game.Import(path,token,report);
            }finally{if(File.Exists(path))File.Delete(path);}
        }
        internal async Task Download(OnlineRelease release,string path,CancellationToken token,Action<string> report) {
            if(release.game!="Open Streets"&&release.game!="Open Streets Launcher")throw new IOException("Unknown update application.");
            Validate(release,release.game);
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    timeout.CancelAfter(TimeSpan.FromMinutes(10));
                    using(var response=await Get(Address(release.zip_url),timeout.Token).ConfigureAwait(false)) {
                        long? length=response.Content.Headers.ContentLength;
                        if(length.HasValue&&length.Value!=release.size)throw new IOException("The download size has changed. Check for updates again.");
                        using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true)) {
                            var buffer=new byte[65536];long total=0;int n,last=-1;
                            while((n=await input.ReadAsync(buffer,0,buffer.Length,timeout.Token).ConfigureAwait(false))>0) {
                                total+=n;if(total>release.size||total>MaxZip)throw new IOException("Update download exceeded its expected size.");
                                await output.WriteAsync(buffer,0,n,timeout.Token).ConfigureAwait(false);
                                int percent=(int)(total*100/release.size);if(percent!=last){last=percent;report("Downloading update... "+percent+"%");}
                            }
                            if(total!=release.size)throw new IOException("Download was interrupted. Your installed game is unchanged.");
                        }
                    }
                }
                token.ThrowIfCancellationRequested();report("Checking downloaded update...");
                if(!String.Equals(GamePackage.FileHash(path),release.sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("Downloaded update failed its checksum. Your installed game is unchanged.");
        }
        public void Dispose(){client.Dispose();}
    }
}
