using System.Text.Json.Nodes;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class RaceControlAccessTests
{
    [TestMethod]
    public void InitialPasswordBecomesSuperAdminAndAccountsSupportMultipleUsersPerRole()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = ConfiguredStore(root);
            var owner = store.AuthenticateControlAccount("owner-password")!;
            Assert.AreEqual(RaceControlRole.SuperAdmin, owner.Role);
            Assert.AreEqual("初始超管", owner.Name);

            var admin = store.CreateControlAccount(new(
                "赛事管理员", RaceControlRole.Administrator, "admin-password"));
            var stewardOne = store.CreateControlAccount(new(
                "一号裁判", RaceControlRole.Steward, "steward-password-1"));
            var stewardTwo = store.CreateControlAccount(new(
                "二号裁判", RaceControlRole.Steward, "steward-password-2"));

            Assert.AreEqual(RaceControlRole.Administrator,
                store.AuthenticateControlAccount("admin-password")!.Role);
            Assert.AreEqual(stewardOne.Id, store.AuthenticateControlAccount("steward-password-1")!.Id);
            Assert.AreEqual(stewardTwo.Id, store.AuthenticateControlAccount("steward-password-2")!.Id);
            Assert.HasCount(4, store.ListControlAccounts());
            Assert.ThrowsExactly<InvalidDataException>(() => store.CreateControlAccount(new(
                "重复密码", RaceControlRole.Steward, "steward-password-1")));
            Assert.ThrowsExactly<InvalidDataException>(() => store.CreateControlAccount(new(
                "房间密码", RaceControlRole.Steward, "room-password")));
            Assert.ThrowsExactly<InvalidDataException>(() => store.CreateControlAccount(new(
                "非法角色", (RaceControlRole)99, "invalid-role-password")));
            Assert.ThrowsExactly<InvalidDataException>(() => store.CreateControlAccount(new(
                "空密码", RaceControlRole.Steward, null!)));

            var updated = store.UpdateControlAccount(admin.Id, new(
                "赛事管理员 A", RaceControlRole.Administrator, "admin-password-new"));
            Assert.AreEqual("赛事管理员 A", updated.Name);
            Assert.IsNull(store.AuthenticateControlAccount("admin-password"));
            Assert.AreEqual(admin.Id, store.AuthenticateControlAccount("admin-password-new")!.Id);

            Assert.ThrowsExactly<InvalidDataException>(() => store.DeleteControlAccount(owner.Id));
            Assert.IsTrue(store.DeleteControlAccount(stewardOne.Id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void LegacyAdminDigestMigratesToInitialSuperAdmin()
    {
        var root = TemporaryDirectory();
        try
        {
            _ = ConfiguredStore(root);
            var path = Path.Combine(root, "server-settings.json");
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document["Version"] = 1;
            document.Remove("ControlAccounts");
            File.WriteAllText(path, document.ToJsonString());

            var migrated = new RaceServerConfigurationStore(new RaceServerOptions { DataDirectory = root });
            Assert.AreEqual(RaceControlRole.SuperAdmin,
                migrated.AuthenticateControlAccount("owner-password")!.Role);
            Assert.HasCount(1, migrated.ListControlAccounts());
            StringAssert.Contains(File.ReadAllText(path), "ControlAccounts");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void SessionsRetainPrincipalAndCanBeRevokedPerAccount()
    {
        var principal = new RaceControlPrincipal(Guid.NewGuid(), "裁判", RaceControlRole.Steward);
        var sessions = new AdminSessionStore(password => password == "steward-password" ? principal : null);
        Assert.AreEqual(principal, sessions.Authenticate("steward-password"));
        Assert.IsNull(sessions.Authenticate("wrong-password"));
        var token = sessions.Create(principal);
        Assert.IsTrue(sessions.TryGetPrincipal(token, out var restored));
        Assert.AreEqual(principal, restored);
        sessions.RevokeAccount(principal.Id);
        Assert.IsFalse(sessions.TryGetPrincipal(token, out _));
    }

    [TestMethod]
    public void PublicTimingTokenIsStoredAsDigestAndCanBeRotatedOrDisabled()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = ConfiguredStore(root);
            Assert.IsFalse(store.PublicTimingStatus().Enabled);

            var generatedAt = new DateTimeOffset(2026, 8, 29, 8, 30, 0, TimeSpan.Zero);
            var first = store.RotatePublicTimingToken(generatedAt);
            Assert.AreEqual(43, first.Token.Length);
            Assert.AreEqual(generatedAt, first.GeneratedAt);
            Assert.IsTrue(store.PublicTimingTokenMatches(first.Token));
            Assert.IsFalse(store.PublicTimingTokenMatches("room-password"));
            Assert.IsFalse(store.PublicTimingTokenMatches("owner-password"));

            var settingsPath = Path.Combine(root, "server-settings.json");
            Assert.IsFalse(File.ReadAllText(settingsPath).Contains(first.Token, StringComparison.Ordinal));

            var reloaded = new RaceServerConfigurationStore(new RaceServerOptions { DataDirectory = root });
            Assert.IsTrue(reloaded.PublicTimingTokenMatches(first.Token));
            Assert.AreEqual(generatedAt, reloaded.PublicTimingStatus().GeneratedAt);

            var second = reloaded.RotatePublicTimingToken(generatedAt.AddMinutes(1));
            Assert.IsFalse(reloaded.PublicTimingTokenMatches(first.Token));
            Assert.IsTrue(reloaded.PublicTimingTokenMatches(second.Token));

            reloaded.DisablePublicTiming();
            Assert.IsFalse(reloaded.PublicTimingStatus().Enabled);
            Assert.IsFalse(reloaded.PublicTimingTokenMatches(second.Token));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void RolesMapToReadRaceAdjudicationAndAccountPermissions()
    {
        Assert.IsTrue(RaceControlAccess.Allows(RaceControlRole.SuperAdmin, RaceControlPermission.ManageControlAccounts));
        Assert.IsFalse(RaceControlAccess.Allows(RaceControlRole.Administrator, RaceControlPermission.ManageControlAccounts));
        Assert.IsTrue(RaceControlAccess.Allows(RaceControlRole.Administrator, RaceControlPermission.ManageRace));
        Assert.IsTrue(RaceControlAccess.Allows(RaceControlRole.Steward, RaceControlPermission.Adjudicate));
        Assert.IsFalse(RaceControlAccess.Allows(RaceControlRole.Steward, RaceControlPermission.ManageRace));
        Assert.AreEqual(RaceControlPermission.View,
            RaceControlAccess.RequiredPermission("GET", "/api/admin/state"));
        Assert.AreEqual(RaceControlPermission.View,
            RaceControlAccess.RequiredPermission("GET", "/api/admin/pre-race-check"));
        Assert.AreEqual(RaceControlPermission.Adjudicate,
            RaceControlAccess.RequiredPermission("POST", "/api/admin/penalty/update"));
        Assert.AreEqual(RaceControlPermission.ManageRace,
            RaceControlAccess.RequiredPermission("POST", "/api/admin/flag"));
        Assert.AreEqual(RaceControlPermission.ManageRace,
            RaceControlAccess.RequiredPermission("POST", "/api/admin/participant"));
        Assert.AreEqual(RaceControlPermission.View,
            RaceControlAccess.RequiredPermission("GET", "/api/admin/public-timing"));
        Assert.AreEqual(RaceControlPermission.ManageRace,
            RaceControlAccess.RequiredPermission("POST", "/api/admin/public-timing"));
        Assert.AreEqual(RaceControlPermission.ManageControlAccounts,
            RaceControlAccess.RequiredPermission("DELETE", "/api/admin/control-accounts/123"));
    }

    private static RaceServerConfigurationStore ConfiguredStore(string root)
    {
        var store = new RaceServerConfigurationStore(new RaceServerOptions { DataDirectory = root });
        var result = store.ConfigureInitial(new RaceServerInitialSetupRequest(
            "room-password", "owner-password", "权限测试", 10, 3));
        Assert.IsTrue(result.Success, result.Error);
        return store;
    }

    private static string TemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), "LazyForza-Race-Control-Test-" + Guid.NewGuid().ToString("N"));
}
