using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Mxc.Sdk.V1;

namespace Mxc.Demos;

/// <summary>
/// Small console helpers shared by every demo so each demo file stays focused on the MXC API.
/// </summary>
public static class Demo
{
    private static string? repoRoot;

    /// <summary>
    /// Repo root (D:\poc\mxc). Resolved from the demo source file that called <see cref="Title"/> (works for
    /// file-based `dotnet run demo.cs` apps, whose binaries build into a temp folder), else from the binary, else cwd.
    /// </summary>
    public static string RepoRoot => repoRoot ??= FindRepoRoot(null);

    /// <summary>A read/write scratch directory that demos are allowed to touch: D:\poc\mxc\workspace.</summary>
    public static string Workspace => Path.Combine(RepoRoot, "workspace");

    /// <summary>
    /// A directory that stands in for "things the sandboxed code must never see": D:\poc\mxc\secrets.
    /// The decoy api-key.txt is created on first use so it never has to be committed.
    /// </summary>
    public static string Secrets
    {
        get
        {
            var dir = Path.Combine(RepoRoot, "secrets");
            var decoy = Path.Combine(dir, "api-key.txt");
            if (!File.Exists(decoy))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(decoy, "sk-live-THIS-IS-A-FAKE-SECRET-the-sandbox-must-never-read-it\n");
            }
            return dir;
        }
    }

    public static void Title(string text, [CallerFilePath] string callerFile = "")
    {
        repoRoot ??= FindRepoRoot(callerFile);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"══ {text} ══");
        Console.ResetColor();
    }

    public static void Step(string text)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"▶ {text}");
        Console.ResetColor();
    }

    public static void Note(string text)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  {text}");
        Console.ResetColor();
    }

    public static void Ok(string text)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✔ {text}");
        Console.ResetColor();
    }

    public static void Fail(string text)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✘ {text}");
        Console.ResetColor();
    }

    /// <summary>Print a captured run: stdout, stderr, warnings, exit code.</summary>
    public static void Show(ExecutionResult result, string? label = null)
    {
        if (label is not null) Console.WriteLine($"── {label} ──");
        if (result.Stdout.Length > 0) Console.Write(Indent(result.Stdout));
        if (result.Stderr.Length > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.Write(Indent(result.Stderr, "  [stderr] "));
            Console.ResetColor();
        }
        Warnings(result.Warnings);
        var colour = result.ExitCode == 0 && !result.TimedOut ? ConsoleColor.Green : ConsoleColor.Red;
        Console.ForegroundColor = colour;
        Console.WriteLine($"  exit={result.ExitCode} timedOut={result.TimedOut}");
        Console.ResetColor();
    }

    public static void Warnings(IEnumerable<string> warnings)
    {
        foreach (var warning in warnings)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"  ⚠ {warning}");
            Console.ResetColor();
        }
    }

    /// <summary>Print an MxcException with everything the SDK gives us, then return a non-zero exit code.</summary>
    public static int Error(MxcException error)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"MXC error [{error.Code}] op={error.Operation ?? "-"} native={error.NativeCode ?? "-"}");
        Console.Error.WriteLine($"  {error.Message}");
        if (error.Remediation is not null) Console.Error.WriteLine($"  remediation: {error.Remediation}");
        Console.ResetColor();
        return 1;
    }

    /// <summary>Fail fast with a readable message when the host cannot run MXC at all.</summary>
    public static bool RequireSupport()
    {
        var support = MxcPlatform.GetPlatformSupport();
        if (!support.IsSupported)
        {
            Fail($"MXC is not supported on this host: {support.Reason ?? "unknown reason"}");
            return false;
        }
        return true;
    }

    /// <summary>Fail fast when a specific backend is not available on this host.</summary>
    public static bool RequireBackend(ContainmentBackend backend)
    {
        if (!RequireSupport()) return false;
        var available = MxcPlatform.GetAvailableBackends().Any(b => b.Backend == backend);
        if (!available)
        {
            Fail($"The {backend} backend is not available on this host.");
            Note("Run 01-CheckSupport to see which backends this machine can use.");
        }
        return available;
    }

    /// <summary>
    /// Build a Windows PowerShell command line for the contained process. The script is passed as
    /// -EncodedCommand (base64 UTF-16LE) so quotes, braces and newlines survive the command-line hop verbatim.
    /// </summary>
    public static string PowerShell(string script)
    {
        // With stderr redirected, Windows PowerShell serialises progress and error records as CLIXML.
        // Silence progress, and turn terminating errors into one plain-text stderr line + exit 1.
        var full = "$ProgressPreference = 'SilentlyContinue'\n"
                 + "trap { [Console]::Error.WriteLine('error: ' + $_.Exception.Message); exit 1 }\n"
                 + script;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
        return $"powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}";
    }

    private static string Indent(string text, string prefix = "  ")
    {
        var lines = text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        return string.Concat(lines.Select(l => prefix + l + Environment.NewLine));
    }

    private static string FindRepoRoot(string? callerFile)
    {
        var starts = new[]
        {
            string.IsNullOrEmpty(callerFile) ? null : Path.GetDirectoryName(callerFile),
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        };
        foreach (var start in starts)
        {
            var dir = start is null ? null : new DirectoryInfo(start);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                dir = dir.Parent;
            }
            if (dir is not null) return dir.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
