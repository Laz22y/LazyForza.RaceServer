using System.Text;

namespace LazyForza.RaceServer.Web;

public interface IRaceServerInitializationConsole
{
    bool IsInteractive { get; }
    void Write(string value);
    void WriteLine(string value = "");
    void WriteErrorLine(string value);
    string? ReadLine();
    string ReadSecret();
}

public sealed class SystemRaceServerInitializationConsole : IRaceServerInitializationConsole
{
    public bool IsInteractive =>
        Environment.UserInteractive &&
        !Console.IsInputRedirected &&
        !Console.IsOutputRedirected;

    public void Write(string value) => Console.Write(value);
    public void WriteLine(string value = "") => Console.WriteLine(value);
    public void WriteErrorLine(string value) => Console.Error.WriteLine(value);
    public string? ReadLine() => Console.ReadLine();

    public string ReadSecret()
    {
        if (!IsInteractive)
            throw new InvalidOperationException("当前进程没有可用的交互式终端。");

        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) return secret.ToString();
            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0) secret.Length--;
                continue;
            }
            if (!char.IsControl(key.KeyChar)) secret.Append(key.KeyChar);
        }
    }
}

public static class RaceServerStartup
{
    public const int InitializationRequiredExitCode = 2;

    public static int ValidateNormalStart(RaceServerConfigurationStore settings, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(error);
        if (settings.IsConfigured) return 0;

        error.WriteLine("RaceServer 尚未完成首次初始化，因此不会监听 HTTP 或 WebSocket 端口。");
        error.WriteLine("请先在可交互的服务器终端运行：LazyForza.RaceServer.Web init");
        error.WriteLine($"初始化配置将保存到：{settings.SettingsPath}");
        return InitializationRequiredExitCode;
    }
}

public static class RaceServerInitializationCommand
{
    public static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Count > 0 && string.Equals(arguments[0], "init", StringComparison.OrdinalIgnoreCase);

    public static int Run(
        RaceServerConfigurationStore settings,
        IRaceServerInitializationConsole terminal)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(terminal);

        if (settings.IsConfigured)
        {
            terminal.WriteErrorLine("RaceServer 已完成初始化。为避免覆盖现有凭据，init 已拒绝执行。");
            terminal.WriteErrorLine($"现有配置：{settings.SettingsPath}");
            return RaceServerStartup.InitializationRequiredExitCode;
        }
        if (!terminal.IsInteractive)
        {
            terminal.WriteErrorLine("RaceServer 尚未初始化，但当前进程没有可用的交互式终端。");
            terminal.WriteErrorLine("请在服务器的可交互终端中重新运行 LazyForza.RaceServer.Web init。");
            return RaceServerStartup.InitializationRequiredExitCode;
        }

        terminal.WriteLine("LazyForza RaceServer 首次初始化");
        terminal.WriteLine("密码输入不会显示在终端，也不会写入日志或明文配置。");

        try
        {
            var defaults = settings.InitialRoomSettings;
            var sessionName = ReadText(terminal, "赛事名称", defaults.SessionName);
            var raceLaps = ReadNumber(terminal, "正赛圈数", defaults.TotalRaceLaps, 1, 999);
            var sectorCount = ReadNumber(terminal, "赛道分段数", defaults.SectorCount, 1, 20);

            var roomPassword = ReadConfirmedSecret(terminal, "房间密码（可留空）");
            if (!roomPassword.Success) return RaceServerStartup.InitializationRequiredExitCode;
            var adminPassword = ReadConfirmedSecret(terminal, "初始超级管理员密码");
            if (!adminPassword.Success) return RaceServerStartup.InitializationRequiredExitCode;

            var result = settings.ConfigureInitial(new RaceServerInitialSetupRequest(
                roomPassword.Value!, adminPassword.Value!, sessionName, raceLaps, sectorCount));
            if (!result.Success)
            {
                terminal.WriteErrorLine($"初始化失败：{result.Error}");
                return RaceServerStartup.InitializationRequiredExitCode;
            }

            terminal.WriteLine("初始化完成。现在可以正常启动 RaceServer 并打开浏览器 Race Control。");
            terminal.WriteLine($"配置已保存到：{settings.SettingsPath}");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            terminal.WriteErrorLine("初始化失败：无法从交互式终端读取完整输入。");
            return RaceServerStartup.InitializationRequiredExitCode;
        }
    }

    private static string ReadText(
        IRaceServerInitializationConsole terminal,
        string label,
        string defaultValue)
    {
        while (true)
        {
            terminal.Write($"{label} [{defaultValue}]：");
            var value = terminal.ReadLine() ?? throw new IOException("终端输入已关闭。");
            value = string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
            terminal.WriteErrorLine($"{label}不能为空。");
        }
    }

    private static int ReadNumber(
        IRaceServerInitializationConsole terminal,
        string label,
        int defaultValue,
        int minimum,
        int maximum)
    {
        while (true)
        {
            terminal.Write($"{label} [{defaultValue}]：");
            var value = terminal.ReadLine() ?? throw new IOException("终端输入已关闭。");
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            if (int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum)
                return parsed;
            terminal.WriteErrorLine($"{label}需要填写 {minimum}–{maximum} 之间的整数。");
        }
    }

    private static (bool Success, string? Value) ReadConfirmedSecret(
        IRaceServerInitializationConsole terminal,
        string label)
    {
        terminal.Write($"{label}：");
        var first = terminal.ReadSecret();
        terminal.WriteLine();
        terminal.Write($"再次输入{label}：");
        var second = terminal.ReadSecret();
        terminal.WriteLine();
        if (string.Equals(first, second, StringComparison.Ordinal)) return (true, first);
        terminal.WriteErrorLine($"初始化失败：两次输入的{label}不一致。");
        return (false, null);
    }
}
