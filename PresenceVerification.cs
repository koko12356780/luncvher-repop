using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace OpenStreetsLauncher {
    sealed class TestDuplexStream : Stream {
        readonly BlockingCollection<byte> incoming,outgoing;
        public TestDuplexStream(){incoming=new BlockingCollection<byte>();outgoing=new BlockingCollection<byte>();}
        TestDuplexStream(BlockingCollection<byte> input,BlockingCollection<byte> output){incoming=input;outgoing=output;}
        public TestDuplexStream Peer(){return new TestDuplexStream(outgoing,incoming);}
        int ReadCore(byte[] b,int offset,int count,CancellationToken token) {
            if(count==0)return 0;
            try{b[offset]=incoming.Take(token);}catch(InvalidOperationException){return 0;}
            int n=1;byte next;
            while(n<count&&incoming.TryTake(out next)){b[offset+n]=next;n++;}return n;
        }
        public override int Read(byte[] b,int o,int n){return ReadCore(b,o,n,CancellationToken.None);}
        public override Task<int> ReadAsync(byte[] b,int o,int n,CancellationToken token){return Task.Run(()=>ReadCore(b,o,n,token),token);}
        public override void Write(byte[] b,int o,int n){for(int i=0;i<n;i++)outgoing.Add(b[o+i]);}
        public override void Flush(){}
        public override bool CanRead{get{return true;}}
        public override bool CanWrite{get{return true;}}
        public override bool CanSeek{get{return false;}}
        public override long Length{get{throw new NotSupportedException();}}
        public override long Position{get{throw new NotSupportedException();}set{throw new NotSupportedException();}}
        public override long Seek(long o,SeekOrigin origin){throw new NotSupportedException();}
        public override void SetLength(long v){throw new NotSupportedException();}
        protected override void Dispose(bool disposing){incoming.CompleteAdding();outgoing.CompleteAdding();base.Dispose(disposing);}
    }
    static class PresenceVerification {
        public static void Run(Action<bool,string> check,string root) {
            check(PresenceSettings.ValidId(BuildInfo.DiscordApplicationId),"Configured Discord Application ID is a public numeric ID");
            check(!PresenceSettings.ValidId("not-a-token")&&!PresenceSettings.ValidId("0"),"Discord settings reject tokens and invalid IDs");
            var settings=new PresenceSettings{ApplicationId=BuildInfo.DiscordApplicationId,Enabled=false};
            string config=Path.Combine(root,"presence-settings.json");settings.Save(config);
            check(!PresenceSettings.Load(config).Enabled,"Discord sharing can be disabled and remembered");
            var json=new JavaScriptSerializer();
            string state=Path.Combine(root,"presence-state.json");
            double now=(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds;
            File.WriteAllText(state,json.Serialize(new{pid=1234,updated=now,details="Driving at night in the Aurora RS",state="City Centre"}));
            object activity=PresenceBridge.Activity(state,1234,100);
            check(json.Serialize(activity).Contains("Driving at night in the Aurora RS"),"Game state becomes the requested Discord activity text");
            check(PresenceBridge.Activity(state,5678,100)==null,"Another game process cannot overwrite this session's activity");
            File.WriteAllText(state,json.Serialize(new{pid=1234,updated=now-60,details="Old",state="Old"}));
            check(PresenceBridge.Activity(state,1234,100)==null,"Stale game state is not shared");
            var encoded=DiscordIpc.Encode(1,"{\"text\":\"night\"}");
            check(BitConverter.ToInt32(encoded,0)==1&&BitConverter.ToInt32(encoded,4)==encoded.Length-8,"IPC uses the documented little-endian frame header");
            string pipeName="open-streets-test-"+Guid.NewGuid().ToString("N");
            using(var tokenSource=new CancellationTokenSource(8000))
            using(var server=new TestDuplexStream())
            using(var client=server.Peer()) {
                var token=tokenSource.Token;
                var serverTask=Task.Run(async()=> {
                    var hello=await DiscordIpc.ReadFrame(server,token);
                    if(hello.Item1!=0||!hello.Item2.Contains(BuildInfo.DiscordApplicationId))throw new Exception("Bad handshake");
                    var ready=DiscordIpc.Encode(1,"{\"cmd\":\"DISPATCH\",\"evt\":\"READY\"}");
                    server.Write(ready,0,ready.Length);
                    var ping=DiscordIpc.Encode(3,"{\"ping\":1}");server.Write(ping,0,ping.Length);server.Flush();
                    var request=await DiscordIpc.ReadFrame(server,token);
                    var body=json.Deserialize<Dictionary<string,object>>(request.Item2);
                    if(request.Item1!=1||Convert.ToString(body["cmd"])!="SET_ACTIVITY"||!request.Item2.Contains("Driving at night"))throw new Exception("Wrong activity request");
                    var pong=await DiscordIpc.ReadFrame(server,token);
                    if(pong.Item1!=4||pong.Item2!="{\"ping\":1}")throw new Exception("PING was not answered");
                    var ack=DiscordIpc.Encode(1,json.Serialize(new{cmd="SET_ACTIVITY",nonce=body["nonce"]}));server.Write(ack,0,ack.Length);server.Flush();
                    var clear=await DiscordIpc.ReadFrame(server,token);
                    var clearBody=json.Deserialize<Dictionary<string,object>>(clear.Item2);
                    if(!clear.Item2.Contains("\"activity\":null"))throw new Exception("Activity did not clear");
                    ack=DiscordIpc.Encode(1,json.Serialize(new{cmd="SET_ACTIVITY",nonce=clearBody["nonce"]}));server.Write(ack,0,ack.Length);server.Flush();
                });
                {
                    using(var rpc=new DiscordIpc(client)) {
                        rpc.Handshake(BuildInfo.DiscordApplicationId,token).GetAwaiter().GetResult();
                        rpc.SetActivity(1234,activity,token).GetAwaiter().GetResult();
                        check(true,"Discord protocol handshake, activity, PING/PONG and matching acknowledgement succeed in mock transport");
                        rpc.SetActivity(1234,null,token).GetAwaiter().GetResult();
                        check(true,"SET_ACTIVITY supports clearing the activity");
                    }
                }
                serverTask.GetAwaiter().GetResult();
            }
            var invalid=DiscordIpc.Encode(1,"{}");Buffer.BlockCopy(BitConverter.GetBytes(2000000),0,invalid,4,4);
            bool rejected=false;
            try{DiscordIpc.ReadFrame(new MemoryStream(invalid),CancellationToken.None).GetAwaiter().GetResult();}catch(IOException){rejected=true;}
            check(rejected,"Oversized IPC frames are rejected");
        }
    }
}
