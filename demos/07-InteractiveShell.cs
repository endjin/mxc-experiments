#:project ../shared/Mxc.Demos.Shared.csproj

// 07 · An interactive shell inside an IsolationSession (a separate, ephemeral Windows user).
//
// Talking points:
//   • IsolationSession runs the workload as a throw-away *agent user account* with its own profile.
//     Try:  whoami   $env:USERPROFILE   dir C:\Users   dir C:\Users\<your-name>   (the last one is denied)
//     Note D:\poc\mxc\secrets IS readable here: default NTFS ACLs allow any local user. Identity isolation ≠ path policy.
//   • SpawnWithPty gives a real terminal (colours, line editing, Ctrl+C) — the thing to wire into a chat UI.
//   • IsolationSession cannot filter networking, so the API makes you say so explicitly (allow/allow/allow).
//
// Run this one from a real terminal window, not from an IDE output pane.

using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("07 · Interactive PowerShell inside an IsolationSession");
if (!OperatingSystem.IsWindows()) { Demo.Fail("IsolationSession is Windows-only."); return 1; }
if (!Demo.RequireBackend(ContainmentBackend.IsolationSession)) return 1;
if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Demo.Fail("Run this demo from an interactive terminal (stdin/stdout must not be redirected).");
    return 1;
}

Demo.Note("Type `exit` to leave the contained shell. Try: whoami ; $env:USERPROFILE ; dir C:\\Users");
Console.WriteLine();

try
{
    using var terminal = MxcContainer.SpawnWithPty(
        new ContainerRequest("powershell.exe -NoLogo")
        {
            Containment = new Containment.IsolationSession(),
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Allow },
                Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Allow },
            },
        },
        new SpawnWithPtyOptions
        {
            Size = new MxcPtySize((ushort)Math.Max(Console.WindowHeight, 10), (ushort)Math.Max(Console.WindowWidth, 40)),
        });

    using var inputCancellation = new CancellationTokenSource();
    var input = Console.OpenStandardInput().CopyToAsync(terminal.Input, inputCancellation.Token);
    var output = terminal.Output.CopyToAsync(Console.OpenStandardOutput());

    var result = await terminal.WaitAsync();
    await output;
    inputCancellation.Cancel();
    terminal.Input.Close();
    _ = input.ContinueWith(_ => { }, TaskScheduler.Default);   // observe; the relay ends when stdin closes

    Console.WriteLine();
    Demo.Warnings(terminal.Warnings);
    Console.WriteLine($"  shell exit={result.ExitCode} timedOut={result.TimedOut}");
    return result.ExitCode;
}
catch (MxcException error)
{
    return Demo.Error(error);
}
