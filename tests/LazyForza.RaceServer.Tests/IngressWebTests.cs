using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using LazyForza.RaceServer.Protocol;
using LazyForza.RaceServer.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class IngressWebTests
{
    [TestMethod]
    public async Task HttpFailuresReturn429RetryAfterAndRecover()
    {
        using var data=Configure();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host=await RaceRecoveryTests.NativeHost.Start(data.Options.DataDirectory,timeout.Token,
            "--RaceServer:Ingress:LoginFailureLimit","2","--RaceServer:Ingress:LoginFailureWindowSeconds","1");
        for(var i=0;i<2;i++) {
            using var wrong=await host.Client.PostAsJsonAsync("/api/admin/login",new {password="wrong"},timeout.Token);
            Assert.AreEqual(HttpStatusCode.Unauthorized,wrong.StatusCode);
        }
        using var blocked=await host.Client.PostAsJsonAsync("/api/admin/login",new {password="admin-pass"},timeout.Token);
        Assert.AreEqual(HttpStatusCode.TooManyRequests,blocked.StatusCode);
        Assert.IsNotNull(blocked.Headers.RetryAfter?.Delta);
        StringAssert.Contains(await blocked.Content.ReadAsStringAsync(timeout.Token),"retryAfterSeconds");
        await Task.Delay(blocked.Headers.RetryAfter!.Delta!.Value,timeout.Token);
        using var recovered=await host.Client.PostAsJsonAsync("/api/admin/login",new {password="admin-pass"},timeout.Token);
        Assert.AreEqual(HttpStatusCode.OK,recovered.StatusCode);
    }

    [TestMethod]
    public async Task FailedPlayerGetsRetryAndPolicyCloseWhileOtherPlayerCanLogin()
    {
        using var data=Configure();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host=await RaceRecoveryTests.NativeHost.Start(data.Options.DataDirectory,timeout.Token,
            "--RaceServer:Ingress:LoginFailureLimit","1");
        using(var wrong=await host.Connect(timeout.Token)) {
            await Send(wrong,"login",Login("bad") with {Password="wrong"},timeout.Token);
            Assert.AreEqual("invalidPassword",(await Receive(wrong,"loginRejected",timeout.Token)).Payload.GetProperty("code").GetString());
            await AssertClose(wrong,1008,timeout.Token);
        }
        using(var blocked=await host.Connect(timeout.Token)) {
            await Send(blocked,"login",Login("bad"),timeout.Token);
            var rejected=await Receive(blocked,"loginRejected",timeout.Token);
            Assert.AreEqual("rateLimited",rejected.Payload.GetProperty("code").GetString());
            Assert.IsTrue(rejected.Payload.GetProperty("retryAfterSeconds").GetInt32()>0);
            await AssertClose(blocked,1013,timeout.Token);
        }
        using var normal=await host.Connect(timeout.Token);
        await Send(normal,"login",Login("normal"),timeout.Token);
        _=await Receive(normal,"loginAccepted",timeout.Token);
    }

    [TestMethod]
    public async Task UnauthenticatedCapacityTimeoutAndEarlyDisconnectReleaseSlots()
    {
        using var data=Configure();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host=await RaceRecoveryTests.NativeHost.Start(data.Options.DataDirectory,timeout.Token,
            "--RaceServer:Ingress:MaximumUnauthenticatedConnections","1","--RaceServer:Ingress:MaximumUnauthenticatedPerSource","1",
            "--RaceServer:Ingress:LoginTimeoutSeconds","1");
        using var pending=await host.Connect(timeout.Token);
        await Assert.ThrowsAsync<WebSocketException>(async()=>{using var denied=await host.Connect(timeout.Token);});
        var rejected=await Receive(pending,"loginRejected",timeout.Token);
        Assert.AreEqual("loginTimeout",rejected.Payload.GetProperty("code").GetString());
        await AssertClose(pending,1008,timeout.Token);
        using(var early=await ConnectEventually(host,timeout.Token)) await early.CloseOutputAsync(WebSocketCloseStatus.NormalClosure,"done",timeout.Token);
        // Observe close cleanup, bounded by the test cancellation token.
        ClientWebSocket? reopened=null;
        while(reopened is null) {
            try {reopened=await host.Connect(timeout.Token);} catch(WebSocketException) {await Task.Delay(20,timeout.Token);}
        }
        using(reopened) {await Send(reopened,"login",Login("normal"),timeout.Token);_=await Receive(reopened,"loginAccepted",timeout.Token);}
    }

    [TestMethod]
    public async Task SharedExitSupportsTwelveDriversAndTwelveObserversWithOnePendingSlot()
    {
        using var data=Configure();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var host=await RaceRecoveryTests.NativeHost.Start(data.Options.DataDirectory,timeout.Token,
            "--RaceServer:Ingress:MaximumUnauthenticatedConnections","1","--RaceServer:Ingress:MaximumUnauthenticatedPerSource","1");
        var sockets=new List<ClientWebSocket>();
        try {
            for(var i=0;i<24;i++) {
                var socket=await host.Connect(timeout.Token);sockets.Add(socket);
                await Send(socket,"login",Login($"normal-{i}") with {IsObserver=i>=12,TeamId=i<6?"team-1":"team-2"},timeout.Token);
                _=await Receive(socket,"loginAccepted",timeout.Token);
            }
        } finally {foreach(var socket in sockets) socket.Dispose();}
    }

    [TestMethod]
    public async Task MessageFloodClosesOnlyOffendingConnectionBeforeCommandDispatch()
    {
        using var data=Configure();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var host=await RaceRecoveryTests.NativeHost.Start(data.Options.DataDirectory,timeout.Token,
            "--RaceServer:Ingress:MessageBurst","3","--RaceServer:Ingress:MessagesPerSecond","1");
        using var noisy=await host.Connect(timeout.Token);using var normal=await host.Connect(timeout.Token);
        await Send(noisy,"login",Login("noisy"),timeout.Token);_=await Receive(noisy,"loginAccepted",timeout.Token);
        await Send(normal,"login",Login("normal"),timeout.Token);_=await Receive(normal,"loginAccepted",timeout.Token);
        for(var i=0;i<3;i++) await Send(noisy,"ping",new {clientMonotonicMilliseconds=i},timeout.Token);
        var error=await Receive(noisy,"error",timeout.Token);
        Assert.AreEqual("rateLimited",error.Payload.GetProperty("code").GetString());
        await AssertClose(noisy,1013,timeout.Token);
        await Send(normal,"ping",new {clientMonotonicMilliseconds=100},timeout.Token);
        _=await Receive(normal,"pong",timeout.Token);
    }

    private static async Task<ClientWebSocket> ConnectEventually(RaceRecoveryTests.NativeHost host,CancellationToken token) {
        using var cleanupTimeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        cleanupTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        while(true) {
            try { return await host.Connect(cleanupTimeout.Token); }
            catch(WebSocketException) { await Task.Delay(20,cleanupTimeout.Token); }
        }
    }

    private static RaceRecoveryTests.TestData Configure() {
        var data=new RaceRecoveryTests.TestData();
        Assert.IsTrue(new RaceServerConfigurationStore(data.Options).ConfigureInitial(new("player-pass","admin-pass","Ingress tests",5,3)).Success);
        return data;
    }
    private static RaceLoginRequest Login(string name)=>new("player-pass",name,"#336699",null,"test",null,null,null,null,null,"team-1");
    private static Task Send<T>(ClientWebSocket socket,string type,T payload,CancellationToken token)=>
        socket.SendAsync(new ArraySegment<byte>(RaceProtocolJson.SerializeToUtf8Bytes(type,1,payload)),WebSocketMessageType.Text,true,token);
    private static async Task<RaceEnvelope> Receive(ClientWebSocket socket,string type,CancellationToken token) {
        var buffer=new byte[RaceProtocol.MaximumMessageBytes];
        while(true) {
            var count=0;WebSocketReceiveResult result;
            do {result=await socket.ReceiveAsync(new ArraySegment<byte>(buffer,count,buffer.Length-count),token);
                if(result.MessageType==WebSocketMessageType.Close) Assert.Fail($"Closed before {type}: {result.CloseStatusDescription}");count+=result.Count;
            } while(!result.EndOfMessage);
            var envelope=RaceProtocolJson.DeserializeEnvelope(buffer.AsSpan(0,count));
            if(envelope.Type==type) return envelope;
            if(envelope.Type=="loginRejected") Assert.Fail(envelope.Payload.GetRawText());
        }
    }
    private static async Task AssertClose(ClientWebSocket socket,int code,CancellationToken token) {
        var result=await socket.ReceiveAsync(new ArraySegment<byte>(new byte[RaceProtocol.MaximumMessageBytes]),token);
        Assert.AreEqual(WebSocketMessageType.Close,result.MessageType);
        Assert.AreEqual(code,(int)result.CloseStatus!);
    }
}
