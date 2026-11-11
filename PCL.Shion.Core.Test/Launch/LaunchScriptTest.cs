using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Core.Launch;

namespace PCL.Shion.Core.Test;

[TestClass]
public sealed class LaunchScriptTest
{
    private static readonly string[] TrickyArguments =
    [
        "plain", "with space", "", "quote\"inside", "single'quote", "trailing\\", "C:\\Program Files\\Java\\",
        "a\\\"b", "100%", "$HOME", "`cmd`", "semi;colon", "amp&er", "-Dkey=value with space", "中文路径"
    ];

    private static LaunchPlan CreatePlan(string java, IReadOnlyList<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null, string? wrapper = null) =>
        new(java, arguments, workingDirectory, Path.Combine(workingDirectory, "natives"), [],
            environment ?? new Dictionary<string, string>(), wrapper);

    [TestMethod]
    public void PosixQuotingRoundTripsThroughShellWords()
    {
        foreach (var argument in TrickyArguments)
            CollectionAssert.AreEqual(new[] { argument },
                ShellWords.SplitPosix(LaunchScript.QuotePosix(argument)).ToArray(), argument);
    }

    [TestMethod]
    public void BatchQuotingRoundTripsThroughWindowsRules()
    {
        foreach (var argument in TrickyArguments)
        {
            // cmd 会把 %% 还原为 %，之后才由程序按 CommandLineToArgvW 规则拆分
            var quoted = LaunchScript.QuoteBatch(argument).Replace("%%", "%");
            CollectionAssert.AreEqual(new[] { argument }, ShellWords.SplitWindows(quoted).ToArray(), argument);
        }
    }

    [TestMethod]
    public void MasksAccessTokenEverywhere()
    {
        const string token = "eyJhbGciOiJIUzI1NiJ9.secret-part.signature";
        var plan = CreatePlan("/usr/bin/java", ["--accessToken", token, $"-Dtoken={token}"], "/games/mc");

        var script = LaunchScript.Create(plan, "Test", LaunchScriptKind.PosixShell, secretToMask: token);

        Assert.IsFalse(script.Contains(token, StringComparison.Ordinal));
        StringAssert.Contains(script, LaunchScript.Mask(token));
        Assert.AreEqual("eyJhb", LaunchScript.Mask(token)[..5]);
        Assert.AreEqual("ature", LaunchScript.Mask(token)[^5..]);
        Assert.AreEqual("FFFF", LaunchScript.Mask("abcd"));
    }

    [TestMethod]
    public void BatchScriptContainsDirectoryEnvironmentAndCommand()
    {
        var plan = CreatePlan(@"C:\Java\bin\java.exe", ["-Xmx4096m", "net.minecraft.client.main.Main", "--width", "854"],
            @"C:\Games\.minecraft\versions\My Pack", new Dictionary<string, string> { ["FOO"] = "50%" },
            "prime-run");

        var script = LaunchScript.Create(plan, "My Pack & Co", LaunchScriptKind.WindowsBatch, "echo hi");

        StringAssert.StartsWith(script, "@chcp 65001>nul\r\n@echo off\r\n");
        StringAssert.Contains(script, "title My Pack ^& Co\r\n");
        StringAssert.Contains(script, "cd /D \"C:\\Games\\.minecraft\\versions\\My Pack\"\r\n");
        StringAssert.Contains(script, "set \"FOO=50%%\"\r\n");
        StringAssert.Contains(script, "echo hi\r\nif errorlevel 1 exit /b %errorlevel%\r\n");
        StringAssert.Contains(script,
            "\"prime-run\" \"C:\\Java\\bin\\java.exe\" -Xmx4096m net.minecraft.client.main.Main --width 854\r\n");
        Assert.IsFalse(script.Replace("\r\n", "").Contains('\n'));
    }

    [TestMethod]
    public void PosixScriptRunsAndPassesArgumentsUnchanged()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("需要 /bin/sh");

        var directory = Directory.CreateTempSubdirectory("shion-script-");
        try
        {
            var workDir = Directory.CreateDirectory(Path.Combine(directory.FullName, "game dir 'quoted'")).FullName;
            var output = Path.Combine(directory.FullName, "out.txt");
            var fakeJava = Path.Combine(directory.FullName, "fake java");
            // 假的 Java：把工作目录、环境变量与每个参数各写一行
            File.WriteAllText(fakeJava,
                "#!/bin/sh\npwd > \"$OUT\"\nprintf '%s\\n' \"$SHION_TEST\" >> \"$OUT\"\nfor a in \"$@\"; do printf '[%s]\\n' \"$a\" >> \"$OUT\"; done\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(fakeJava, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var plan = CreatePlan(fakeJava, TrickyArguments, workDir,
                new Dictionary<string, string> { ["OUT"] = output, ["SHION_TEST"] = "it's $fine", ["bad-name"] = "x" });
            var scriptPath = Path.Combine(directory.FullName, "launch.sh");
            File.WriteAllText(scriptPath, LaunchScript.Create(plan, "Test", LaunchScriptKind.PosixShell, "true"));

            using var process = Process.Start(new ProcessStartInfo("/bin/sh", [scriptPath])
            {
                RedirectStandardError = true, UseShellExecute = false
            })!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, error);

            var lines = File.ReadAllLines(output);
            Assert.AreEqual(workDir, lines[0]);
            Assert.AreEqual("it's $fine", lines[1]);
            CollectionAssert.AreEqual(TrickyArguments.Select(a => $"[{a}]").ToArray(), lines[2..]);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [TestMethod]
    public void PosixScriptStopsWhenPreLaunchCommandFails()
    {
        if (OperatingSystem.IsWindows())
            Assert.Inconclusive("需要 /bin/sh");

        var directory = Directory.CreateTempSubdirectory("shion-script-");
        try
        {
            var marker = Path.Combine(directory.FullName, "launched");
            var plan = CreatePlan("touch", [marker], directory.FullName);
            var scriptPath = Path.Combine(directory.FullName, "launch.sh");
            File.WriteAllText(scriptPath, LaunchScript.Create(plan, "Test", LaunchScriptKind.PosixShell, "exit 3"));

            using var process = Process.Start(new ProcessStartInfo("/bin/sh", [scriptPath]) { UseShellExecute = false })!;
            process.WaitForExit();

            Assert.AreEqual(3, process.ExitCode);
            Assert.IsFalse(File.Exists(marker));
        }
        finally
        {
            directory.Delete(true);
        }
    }
}
