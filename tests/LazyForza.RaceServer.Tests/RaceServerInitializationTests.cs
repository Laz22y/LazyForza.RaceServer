using System.Text;
using LazyForza.RaceServer.Core;
using LazyForza.RaceServer.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LazyForza.RaceServer.Tests;

[TestClass]
public sealed class RaceServerInitializationTests
{
    [TestMethod]
    public void InitCreatesPersistedRoomCredentialsAndInitialSuperAdmin()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = NewStore(root);
            var terminal = new ScriptedTerminal(
                lines: ["耐力挑战赛", "24", "6"],
                secrets: ["room-password", "room-password", "owner-password", "owner-password"]);

            var exitCode = RaceServerInitializationCommand.Run(store, terminal);

            Assert.AreEqual(0, exitCode);
            Assert.IsTrue(store.IsConfigured);
            Assert.IsTrue(store.PlayerPasswordMatches("room-password"));
            var owner = store.AuthenticateControlAccount("owner-password");
            Assert.IsNotNull(owner);
            Assert.AreEqual(RaceControlRole.SuperAdmin, owner.Role);
            Assert.AreEqual("初始超管", owner.Name);
            Assert.AreEqual("耐力挑战赛", store.InitialRoomSettings.SessionName);
            Assert.AreEqual(24, store.InitialRoomSettings.TotalRaceLaps);
            Assert.AreEqual(6, store.InitialRoomSettings.SectorCount);
            Assert.AreEqual(4, terminal.SecretReadCount);

            var persisted = File.ReadAllText(store.SettingsPath);
            Assert.IsFalse(persisted.Contains("room-password", StringComparison.Ordinal));
            Assert.IsFalse(persisted.Contains("owner-password", StringComparison.Ordinal));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void InitializationKeepsExistingPasswordRules()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = NewStore(root);
            var tooLongRoomPassword = new string('r', 129);
            var shortAdmin = store.ConfigureInitial(new RaceServerInitialSetupRequest(
                "room", "short", "规则测试", 10, 3));
            Assert.IsFalse(shortAdmin.Success);
            StringAssert.Contains(shortAdmin.Error, "8–128");

            var longRoom = store.ConfigureInitial(new RaceServerInitialSetupRequest(
                tooLongRoomPassword, "owner-password", "规则测试", 10, 3));
            Assert.IsFalse(longRoom.Success);
            StringAssert.Contains(longRoom.Error, "128");

            var samePassword = store.ConfigureInitial(new RaceServerInitialSetupRequest(
                "same-password", "same-password", "规则测试", 10, 3));
            Assert.IsFalse(samePassword.Success);
            StringAssert.Contains(samePassword.Error, "不能相同");
            Assert.IsFalse(store.IsConfigured);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void InitRefusesToOverwriteExistingCredentials()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = ConfiguredStore(root);
            var before = File.ReadAllBytes(store.SettingsPath);
            var terminal = new ScriptedTerminal(lines: [], secrets: []);

            var exitCode = RaceServerInitializationCommand.Run(store, terminal);

            Assert.AreEqual(RaceServerStartup.InitializationRequiredExitCode, exitCode);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(store.SettingsPath));
            Assert.IsNotNull(store.AuthenticateControlAccount("owner-password"));
            Assert.AreEqual(0, terminal.SecretReadCount);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void UninitializedNonInteractiveCommandsFailWithoutCreatingDefaultCredentials()
    {
        var root = TemporaryDirectory();
        try
        {
            var store = NewStore(root);
            var terminal = new ScriptedTerminal(interactive: false, lines: [], secrets: []);
            var initExitCode = RaceServerInitializationCommand.Run(store, terminal);
            using var startupError = new StringWriter();
            var startupExitCode = RaceServerStartup.ValidateNormalStart(store, startupError);

            Assert.AreEqual(RaceServerStartup.InitializationRequiredExitCode, initExitCode);
            Assert.AreEqual(RaceServerStartup.InitializationRequiredExitCode, startupExitCode);
            Assert.IsFalse(store.IsConfigured);
            Assert.IsFalse(File.Exists(store.SettingsPath));
            Assert.IsFalse(store.PlayerPasswordMatches("change-me"));
            Assert.IsNull(store.AuthenticateControlAccount("change-admin-me"));
            StringAssert.Contains(startupError.ToString(), "不会监听 HTTP 或 WebSocket");

            var plaintextOptions = new RaceServerConfigurationStore(new RaceServerOptions
            {
                DataDirectory = root,
                PlayerPassword = "configured-room-password",
                AdminPassword = "configured-admin-password"
            });
            Assert.IsFalse(plaintextOptions.IsConfigured,
                "Plaintext configuration options must not bypass terminal initialization.");
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void ExistingPersistedCredentialsStartWithoutReinitialization()
    {
        var root = TemporaryDirectory();
        try
        {
            _ = ConfiguredStore(root);
            var reloaded = NewStore(root);
            using var startupError = new StringWriter();

            Assert.AreEqual(0, RaceServerStartup.ValidateNormalStart(reloaded, startupError));
            Assert.AreEqual(string.Empty, startupError.ToString());
            Assert.IsTrue(reloaded.PlayerPasswordMatches("room-password"));
            Assert.AreEqual(RaceControlRole.SuperAdmin,
                reloaded.AuthenticateControlAccount("owner-password")!.Role);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task NativeSetupEndpointsExposeStatusButNoRemoteInitializationRoute()
    {
        var root = TemporaryDirectory();
        try
        {
            var builder = WebApplication.CreateBuilder();
            builder.Services.AddSingleton(NewStore(root));
            await using var app = builder.Build();
            app.MapNativeSetupEndpoints();

            var endpoints = ((IEndpointRouteBuilder)app).DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .ToArray();
            var status = endpoints.Single(endpoint =>
                endpoint.RoutePattern.RawText == "/api/setup/status");
            CollectionAssert.AreEqual(
                new[] { "GET" },
                status.Metadata.GetRequiredMetadata<HttpMethodMetadata>().HttpMethods.ToArray());
            Assert.IsFalse(endpoints.Any(endpoint => endpoint.RoutePattern.RawText == "/api/setup"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public void NativeRaceControlContainsTerminalNoticeInsteadOfPasswordSetupForm()
    {
        var index = File.ReadAllText(RepositoryFile(
            "src", "LazyForza.RaceServer.Web", "wwwroot", "index.html"));
        var app = File.ReadAllText(RepositoryFile(
            "src", "LazyForza.RaceServer.Web", "wwwroot", "app.js"));
        var program = File.ReadAllText(RepositoryFile(
            "src", "LazyForza.RaceServer.Web", "Program.cs"));

        StringAssert.Contains(index, "请在运行 RaceServer 的服务器终端完成首次设置");
        Assert.IsFalse(index.Contains("id=\"setupForm\"", StringComparison.Ordinal));
        Assert.IsFalse(index.Contains("id=\"setupPlayerPassword\"", StringComparison.Ordinal));
        Assert.IsFalse(index.Contains("id=\"setupAdminPassword\"", StringComparison.Ordinal));
        StringAssert.Contains(app, "status.setupMode==='terminal'");
        StringAssert.Contains(app, "renderRemoteSetup");
        Assert.IsFalse(program.Contains("MapPost(\"/api/setup\"", StringComparison.Ordinal));
    }

    private static RaceServerConfigurationStore ConfiguredStore(string root)
    {
        var store = NewStore(root);
        var result = store.ConfigureInitial(new RaceServerInitialSetupRequest(
            "room-password", "owner-password", "迁移测试", 10, 3));
        Assert.IsTrue(result.Success, result.Error);
        return store;
    }

    private static RaceServerConfigurationStore NewStore(string root) =>
        new(new RaceServerOptions { DataDirectory = root });

    private static string RepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"无法定位仓库文件：{Path.Combine(segments)}");
    }

    private static string TemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), "LazyForza-Race-Initialization-Test-" + Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class ScriptedTerminal : IRaceServerInitializationConsole
    {
        private readonly Queue<string?> lines;
        private readonly Queue<string> secrets;
        private readonly StringBuilder output = new();

        public ScriptedTerminal(
            IEnumerable<string?> lines,
            IEnumerable<string> secrets,
            bool interactive = true)
        {
            this.lines = new Queue<string?>(lines);
            this.secrets = new Queue<string>(secrets);
            IsInteractive = interactive;
        }

        public bool IsInteractive { get; }
        public int SecretReadCount { get; private set; }
        public void Write(string value) => output.Append(value);
        public void WriteLine(string value = "") => output.AppendLine(value);
        public void WriteErrorLine(string value) => output.AppendLine(value);
        public string? ReadLine() => lines.Dequeue();

        public string ReadSecret()
        {
            SecretReadCount++;
            return secrets.Dequeue();
        }
    }
}
