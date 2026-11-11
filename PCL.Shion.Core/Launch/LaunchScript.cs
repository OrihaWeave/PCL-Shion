using System.Text;

namespace PCL.Shion.Core.Launch;

/// <summary>启动脚本的格式。</summary>
public enum LaunchScriptKind
{
    /// <summary>Windows 批处理文件（.bat），由 cmd 执行。</summary>
    WindowsBatch,

    /// <summary>POSIX shell 脚本（.sh），由 /bin/sh 执行。</summary>
    PosixShell
}

/// <summary>
/// 把启动计划写成可以脱离启动器运行的脚本，对应 WPF 版“导出启动脚本”。
/// 脚本依次切换到游戏目录、设置环境变量、执行前置命令，然后用与启动器相同的参数启动 Java。
/// </summary>
public static class LaunchScript
{
    /// <summary>与当前系统匹配的脚本格式。</summary>
    public static LaunchScriptKind DefaultKind =>
        OperatingSystem.IsWindows() ? LaunchScriptKind.WindowsBatch : LaunchScriptKind.PosixShell;

    /// <summary>脚本格式对应的文件扩展名，含点号。</summary>
    public static string ExtensionOf(LaunchScriptKind kind) =>
        kind == LaunchScriptKind.WindowsBatch ? ".bat" : ".sh";

    /// <summary>
    /// 生成脚本内容。换行符按格式使用 CRLF 或 LF。
    /// </summary>
    /// <param name="plan">由 <see cref="LaunchPlanner.Build"/> 生成的启动计划。</param>
    /// <param name="instanceName">实例名称，写入窗口标题与注释。</param>
    /// <param name="kind">脚本格式。</param>
    /// <param name="preLaunchCommand">实例的前置命令，原样写入脚本；为空时省略。</param>
    /// <param name="secretToMask">
    /// 需要在脚本中打码的令牌（通常是正版账户的访问令牌）。保留首尾各 5 个字符，中间替换为 F，
    /// 与 WPF 版一致；导出的脚本因此无法直接用于正版登录。长度不足 10 时整段替换。为 null 或空时不打码。
    /// </param>
    /// <param name="commandEnvironment">前置命令可用的额外环境变量，例如 INST_NAME。</param>
    public static string Create(
        LaunchPlan plan,
        string instanceName,
        LaunchScriptKind kind,
        string? preLaunchCommand = null,
        string? secretToMask = null,
        IReadOnlyDictionary<string, string>? commandEnvironment = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(instanceName);

        List<string> command = plan.WrapperCommand is null or "" ? [] : [.. ShellWords.Split(plan.WrapperCommand)];
        command.Add(plan.JavaExecutable);
        command.AddRange(plan.Arguments);
        if (!string.IsNullOrEmpty(secretToMask))
            for (var i = 0; i < command.Count; i++)
                command[i] = command[i].Replace(secretToMask, Mask(secretToMask), StringComparison.Ordinal);

        var environment = new List<KeyValuePair<string, string>>();
        if (commandEnvironment is not null)
            environment.AddRange(commandEnvironment);
        environment.AddRange(plan.EnvironmentVariables);

        return kind == LaunchScriptKind.WindowsBatch
            ? CreateBatch(plan, instanceName, command, environment, preLaunchCommand)
            : CreatePosix(plan, instanceName, command, environment, preLaunchCommand);
    }

    /// <summary>打码令牌：保留首尾各 5 个字符，中间替换为 F。</summary>
    public static string Mask(string secret)
    {
        if (secret.Length < 10)
            return new string('F', secret.Length);
        return secret[..5] + new string('F', secret.Length - 10) + secret[^5..];
    }

    private static string CreateBatch(LaunchPlan plan, string instanceName, List<string> command,
        List<KeyValuePair<string, string>> environment, string? preLaunchCommand)
    {
        var builder = new StringBuilder();
        // 脚本以 UTF-8 保存，先切换代码页，路径与玩家名中的非 ASCII 字符才不会乱码
        builder.Append("@chcp 65001>nul\r\n");
        builder.Append("@echo off\r\n");
        builder.Append("title ").Append(EscapeBatchText(instanceName)).Append("\r\n");
        builder.Append("cd /D ").Append(QuoteBatch(plan.WorkingDirectory, true)).Append("\r\n");
        foreach (var (name, value) in environment)
            builder.Append("set \"").Append(EscapeBatchPercent(name)).Append('=')
                .Append(EscapeBatchPercent(value)).Append("\"\r\n");
        if (!string.IsNullOrWhiteSpace(preLaunchCommand))
        {
            builder.Append(preLaunchCommand.ReplaceLineEndings(" ")).Append("\r\n");
            builder.Append("if errorlevel 1 exit /b %errorlevel%\r\n");
        }

        // 包装命令与 Java 路径总是加引号，与 WPF 版一致
        var javaIndex = command.IndexOf(plan.JavaExecutable);
        builder.Append(string.Join(' ', command.Select((argument, index) => QuoteBatch(argument, index <= javaIndex))))
            .Append("\r\n");
        builder.Append("pause\r\n");
        return builder.ToString();
    }

    private static string CreatePosix(LaunchPlan plan, string instanceName, List<string> command,
        List<KeyValuePair<string, string>> environment, string? preLaunchCommand)
    {
        var builder = new StringBuilder();
        builder.Append("#!/bin/sh\n");
        builder.Append("# ").Append(instanceName.ReplaceLineEndings(" ")).Append('\n');
        builder.Append("cd ").Append(QuotePosix(plan.WorkingDirectory)).Append(" || exit 1\n");
        foreach (var (name, value) in environment.Where(pair => IsPosixName(pair.Key)))
            builder.Append("export ").Append(name).Append('=').Append(QuotePosix(value)).Append('\n');
        if (!string.IsNullOrWhiteSpace(preLaunchCommand))
            // 与启动器一样在独立的 shell 中执行前置命令，非零退出码时不启动游戏
            builder.Append("( ").Append(preLaunchCommand.ReplaceLineEndings(" ")).Append(" ) || exit $?\n");
        builder.Append("exec ").Append(string.Join(' ', command.Select(QuotePosix))).Append('\n');
        return builder.ToString();
    }

    // shell 变量名只能由字母、数字与下划线组成，且不以数字开头；其他名称无法用 export 设置
    private static bool IsPosixName(string name) =>
        name.Length > 0 && !char.IsAsciiDigit(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    /// <summary>用单引号包住参数，参数中的单引号写成 '\''。</summary>
    internal static string QuotePosix(string argument)
    {
        if (argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || "-_./:=,+@%".Contains(c)))
            return argument;
        return "'" + argument.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// 按 Windows 程序常用的命令行解析规则（CommandLineToArgvW）引用参数，并把 % 写成 %%，避免被 cmd 当成变量展开。
    /// </summary>
    /// <param name="argument">参数。</param>
    /// <param name="forceQuote">即使没有特殊字符也加引号，用于可执行文件路径与目录。</param>
    internal static string QuoteBatch(string argument, bool forceQuote = false)
    {
        var needsQuote = forceQuote || argument.Length == 0 ||
                         argument.Any(c => char.IsWhiteSpace(c) || "\"&|<>^(),;=!".Contains(c));
        if (!needsQuote)
            return EscapeBatchPercent(argument);

        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // 引号前的反斜杠要加倍，引号本身再转义
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        // 结尾的反斜杠加倍，避免把收尾引号转义掉
        builder.Append('\\', backslashes * 2).Append('"');
        return EscapeBatchPercent(builder.ToString());
    }

    private static string EscapeBatchPercent(string text) => text.Replace("%", "%%");

    private static string EscapeBatchText(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.ReplaceLineEndings(" "))
        {
            if ("&|<>^".Contains(c))
                builder.Append('^');
            builder.Append(c);
        }

        return EscapeBatchPercent(builder.ToString());
    }
}
