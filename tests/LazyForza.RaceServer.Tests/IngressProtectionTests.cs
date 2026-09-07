using System.Net;
using LazyForza.RaceServer.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class IngressProtectionTests
{
    [TestMethod]
    public void FailureCooldownDoesNotExtendOnRejectedRequestsAndRecovers()
    {
        long now=0;
        var guard=new IngressProtection(new(),()=>now);
        for(var i=0;i<5;i++) guard.TryLogin("nat","player","alice",out _)!.Dispose();
        Assert.IsNull(guard.TryLogin("nat","player","alice",out var retry));
        Assert.AreEqual(60,retry);
        now=59000;
        Assert.IsNull(guard.TryLogin("nat","player","alice",out retry));
        Assert.AreEqual(1,retry);
        now=60000;
        using var recovered=guard.TryLogin("nat","player","alice",out _);
        Assert.IsNotNull(recovered);
        recovered.Complete(true);
    }
    [TestMethod]
    public void SharedExitPlayersAndAdminHaveSeparateFailureBudgets()
    {
        var guard=new IngressProtection(new());
        for(var i=0;i<5;i++) guard.TryLogin("nat","player","bad",out _)!.Dispose();
        for(var round=0;round<10;round++) for(var i=0;i<24;i++)
        {
            using var login=guard.TryLogin("nat","player",$"player-{i}",out _);
            Assert.IsNotNull(login);
            login.Complete(true);
        }
        using var admin=guard.TryLogin("nat","admin","admin",out _);
        Assert.IsNotNull(admin);
    }
    [TestMethod]
    public void RotatingIdentityIsStillBoundedBySourceAndConcurrentChecksReleaseExactlyOnce()
    {
        var guard=new IngressProtection(new(){LoginFailureLimit=2,SourceLoginFailureLimit=3,MaximumConcurrentLogins=2});
        var first=guard.TryLogin("nat","player","a",out _)!;
        var second=guard.TryLogin("nat","player","b",out _)!;
        Assert.IsNull(guard.TryLogin("other","player","c",out _));
        first.Complete(true); first.Dispose(); second.Dispose();
        guard.TryLogin("nat","player","c",out _)!.Dispose();
        guard.TryLogin("nat","player","d",out _)!.Dispose();
        Assert.IsNull(guard.TryLogin("nat","player","rotated",out _));
        using var other=guard.TryLogin("other","player","normal",out _);
        Assert.IsNotNull(other);
    }
    [TestMethod]
    public void FailureMapHasBoundedCardinalityAndExpires()
    {
        long now=0;
        var guard=new IngressProtection(new(){MaximumFailureBuckets=2},()=>now);
        guard.TryLogin("a","player","one",out _)!.Dispose();
        Assert.IsNull(guard.TryLogin("b","player","two",out _));
        now=60000;
        using var next=guard.TryLogin("b","player","two",out _);
        Assert.IsNotNull(next);
    }
    [TestMethod]
    public void UnauthenticatedLeasesLimitPendingOnlyAndReleaseIdempotently()
    {
        var guard=new IngressProtection(new(){MaximumUnauthenticatedConnections=3,MaximumUnauthenticatedPerSource=2});
        var a=guard.TryAcquireConnection("nat")!;
        var b=guard.TryAcquireConnection("nat")!;
        Assert.IsNull(guard.TryAcquireConnection("nat"));
        using var c=guard.TryAcquireConnection("other");
        Assert.IsNotNull(c);
        Assert.IsNull(guard.TryAcquireConnection("third"));
        a.Dispose();a.Dispose();
        using var afterAuthentication=guard.TryAcquireConnection("nat");
        Assert.IsNotNull(afterAuthentication);
        b.Dispose();
    }
    [TestMethod]
    public void MessageAndByteBudgetsRecoverWithoutAffectingOtherConnections()
    {
        long now=0;
        var options=new IngressOptions(){MessageBurst=2,MessagesPerSecond=2,ByteBurst=100,BytesPerSecond=100};
        var budget=new ConnectionMessageBudget(options,()=>now);
        Assert.IsTrue(budget.TryConsume(40,true,out _));
        Assert.IsTrue(budget.TryConsume(40,true,out _));
        Assert.IsFalse(budget.TryConsume(1,true,out var retry));
        Assert.AreEqual(1,retry);
        Assert.IsTrue(new ConnectionMessageBudget(options).TryConsume(100,true,out _));
        now=1000;
        Assert.IsTrue(budget.TryConsume(100,true,out _));
        Assert.IsFalse(budget.TryConsume(1,false,out _));
    }
    [TestMethod]
    public void ArbitraryProxyHeadersAreIgnoredAndOnlySingleTrustedForwardedAddressIsUsed()
    {
        var context=new DefaultHttpContext();context.Connection.RemoteIpAddress=IPAddress.Parse("192.0.2.1");
        context.Request.Headers["X-Forwarded-For"]="198.51.100.9";
        context.Request.Headers["CF-Connecting-IP"]="203.0.113.9";
        Assert.AreEqual("::ffff:192.0.2.1",new IngressProtection(new()).Source(context));
        var trusted=new IngressProtection(new(){TrustedProxyAddresses=["192.0.2.1"]});
        Assert.AreEqual("::ffff:198.51.100.9",trusted.Source(context));
        context.Request.Headers["X-Forwarded-For"]="198.51.100.9, 203.0.113.9";
        Assert.AreEqual("::ffff:192.0.2.1",trusted.Source(context));
    }
}
