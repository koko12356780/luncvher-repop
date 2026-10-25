using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OpenStreetsLauncher {
    public sealed class PresenceSettings {
        public string ApplicationId=BuildInfo.DiscordApplicationId;
        public bool Enabled=false;
        public static bool ValidId(string id) {
            ulong value;
            return id!=null && id.Length>=17 && id.Length<=20 && ulong.TryParse(id,out value) && value>0;
        }
        public static PresenceSettings Load(string path) {
            try { return new JavaScriptSerializer().Deserialize<PresenceSettings>(File.ReadAllText(path)); }
            catch { return new PresenceSettings(); }
        }
        public void Save(string path) {
            if(Enabled && !ValidId(ApplicationId))throw new IOException("Enter the public Discord Application ID (numbers only).");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path,new JavaScriptSerializer().Serialize(this));
        }
    }
    // Discord's documented Windows IPC protocol. No token, login, OAuth, or messages.
    public sealed class DiscordIpc : IDisposable {
        readonly Stream pipe;
        readonly JavaScriptSerializer json=new JavaScriptSerializer();
        public DiscordIpc(Stream stream){pipe=stream;}
        public static byte[] Encode(int opcode,string data) {
            byte[] body=Encoding.UTF8.GetBytes(data);
            var frame=new byte[body.Length+8];
            Buffer.BlockCopy(BitConverter.GetBytes(opcode),0,frame,0,4);
            Buffer.BlockCopy(BitConverter.GetBytes(body.Length),0,frame,4,4);
            Buffer.BlockCopy(body,0,frame,8,body.Length);
            return frame;
        }
        public void Send(int opcode,string data) {
            var frame=Encode(opcode,data);
            pipe.Write(frame,0,frame.Length);pipe.Flush();
        }
        public static async Task<byte[]> Exact(Stream stream,int size,CancellationToken token) {
            var bytes=new byte[size];int offset=0;
            while(offset<size) {
                var read=stream.ReadAsync(bytes,offset,size-offset,token);
                if(await Task.WhenAny(read,Task.Delay(4000,token)).ConfigureAwait(false)!=read) {
                    stream.Dispose();
                    // Observe the pending read if disposal completes it with an error.
                    var observation=read.ContinueWith(t=>{var ignored=t.Exception;},TaskContinuationOptions.OnlyOnFaulted);
                    token.ThrowIfCancellationRequested();throw new IOException("Discord did not respond.");
                }
                int count=await read.ConfigureAwait(false);if(count==0)throw new EndOfStreamException();offset+=count;
            }
            return bytes;
        }
        public static async Task<Tuple<int,string>> ReadFrame(Stream stream,CancellationToken token) {
            byte[] header=await Exact(stream,8,token).ConfigureAwait(false);
            int opcode=BitConverter.ToInt32(header,0),size=BitConverter.ToInt32(header,4);
            if(size<0||size>1024*1024)throw new IOException("Invalid Discord frame length.");
            return Tuple.Create(opcode,Encoding.UTF8.GetString(await Exact(stream,size,token).ConfigureAwait(false)));
        }
        async Task<Dictionary<string,object>> Response(CancellationToken token) {
            for(int n=0;n<12;n++) {
                var frame=await ReadFrame(pipe,token).ConfigureAwait(false);
                if(frame.Item1==3){Send(4,frame.Item2);continue;}
                if(frame.Item1==2)throw new IOException("Discord closed the connection.");
                var response=json.Deserialize<Dictionary<string,object>>(frame.Item2);
                object evt;
                if(response.TryGetValue("evt",out evt)&&Convert.ToString(evt)=="ERROR")throw new IOException("Discord rejected the activity. Check the Application ID.");
                return response;
            }
            throw new IOException("Discord did not acknowledge the request.");
        }
        public async Task Handshake(string id,CancellationToken token) {
            if(!PresenceSettings.ValidId(id))throw new IOException("Missing Discord Application ID.");
            Send(0,json.Serialize(new {v=1,client_id=id}));
            var ready=await Response(token).ConfigureAwait(false);object evt;
            if(!ready.TryGetValue("evt",out evt)||Convert.ToString(evt)!="READY")throw new IOException("Discord is not ready.");
        }
        public async Task SetActivity(int pid,object activity,CancellationToken token) {
            string nonce=Guid.NewGuid().ToString("N");
            Send(1,json.Serialize(new {cmd="SET_ACTIVITY",args=new {pid=pid,activity=activity},nonce=nonce}));
            for(int n=0;n<12;n++) {
                var reply=await Response(token).ConfigureAwait(false);object result;
                if(reply.TryGetValue("nonce",out result)&&Convert.ToString(result)==nonce)return;
            }
            throw new IOException("Discord did not acknowledge the activity.");
        }
        public void Dispose(){pipe.Dispose();}
    }
    public sealed class PresenceBridge : IDisposable {
        CancellationTokenSource stop;
        NamedPipeClientStream pipe;
        readonly object gate=new object();
        public volatile string Status="Ready. Open Discord desktop, then play.";
        public static object Activity(string path,int pid,long started) {
            if(!File.Exists(path)||new FileInfo(path).Length>8192)return null;
            var state=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
            double now=(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds;
            if(Convert.ToInt32(state["pid"])!=pid || Math.Abs(now-Convert.ToDouble(state["updated"]))>10)return null;
            string details=Convert.ToString(state["details"]),district=Convert.ToString(state["state"]);
            if(details.Length>128||district.Length>128)return null;
            return new {details=details,state=district,timestamps=new {start=started}};
        }
        public void Start(string id,int pid,string statePath) {
            Stop();
            if(!PresenceSettings.ValidId(id)){Status="Add a Discord Application ID first.";return;}
            stop=new CancellationTokenSource();var token=stop.Token;
            long started=(long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds;
            Task.Run(async()=> {
                while(!token.IsCancellationRequested) {
                    NamedPipeClientStream owned=null;
                    try {
                        Status="Connecting to Discord desktop...";
                        NamedPipeClientStream connected=null;
                        for(int index=0;index<10&&!token.IsCancellationRequested;index++) {
                            var candidate=new NamedPipeClientStream(".","discord-ipc-"+index,PipeDirection.InOut,PipeOptions.Asynchronous);
                            try{candidate.Connect(80);connected=candidate;break;}
                            catch(TimeoutException){candidate.Dispose();}
                            catch(IOException){candidate.Dispose();}
                        }
                        if(connected==null)throw new IOException("Open Discord desktop to share your activity.");
                        owned=connected;
                        lock(gate){if(token.IsCancellationRequested){connected.Dispose();return;}pipe=connected;}
                        using(var rpc=new DiscordIpc(connected)) {
                            await rpc.Handshake(id,token);
                            string previous="";
                            int heartbeat=0;
                            while(!token.IsCancellationRequested) {
                                object activity=null;
                                try{activity=Activity(statePath,pid,started);}catch(IOException){}catch(ArgumentException){}catch(KeyNotFoundException){}
                                string next=new JavaScriptSerializer().Serialize(activity);
                                if(next!=previous||heartbeat>=3){await rpc.SetActivity(pid,activity,token);previous=next;heartbeat=0;}
                                heartbeat++;
                                Status=activity==null?"Connected. Waiting for game activity...":"Sharing your car, time of day and area on Discord.";
                                await Task.Delay(5000,token);
                            }
                        }
                    }catch(OperationCanceledException){break;}
                    catch(Exception ex){if(token.IsCancellationRequested)break;Status=ex is IOException?ex.Message:"Discord is unavailable. The game still works.";}
                    finally{lock(gate){if(owned!=null){owned.Dispose();if(Object.ReferenceEquals(pipe,owned))pipe=null;}}}
                    try{await Task.Delay(10000,token);}catch(OperationCanceledException){break;}
                }
            });
        }
        public void Stop() {
            if(stop!=null){stop.Cancel();stop=null;}
            lock(gate){if(pipe!=null){pipe.Dispose();pipe=null;}}
            Status="Discord activity is off.";
        }
        public void Dispose(){Stop();}
    }
}
