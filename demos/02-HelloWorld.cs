#:project ../shared/Mxc.Demos.Shared.csproj

// 02 · The smallest useful program: run a command in a transient container and capture its output.
//
// Talking points:
//   • ContainerRequest = the workload + the policy. Containment.Process is the host's native backend
//     (ProcessContainer on Windows, Bubblewrap on Linux, Seatbelt on macOS) — same code everywhere.
//   • Nothing is granted unless you ask: no network, no filesystem beyond what the backend needs to run the exe.
//   • Start-up cost is milliseconds, not seconds. We time two runs to show it.

using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("02 · Hello from a transient container");
if (!Demo.RequireSupport()) return 1;

try
{
    var request = new ContainerRequest("cmd.exe /d /s /c \"echo Hello from %GREETING_TARGET% & ver\"")
    {
        Containment = new Containment.Process(),
        TimeoutMs = 30_000,
        Environment = new Dictionary<string, string> { ["GREETING_TARGET"] = "the team" },
        InheritDefaultEnvironment = true,      // layer our variable over the normal environment block
    };

    Demo.Step("Run to completion, capture stdout/stderr (cold start)");
    var sw = Stopwatch.StartNew();
    var first = await MxcContainer.RunAsync(request);
    sw.Stop();
    Demo.Show(first);
    Demo.Note($"wall clock: {sw.ElapsedMilliseconds} ms");

    Demo.Step("Same request again (warm)");
    sw.Restart();
    var second = await MxcContainer.RunAsync(request);
    sw.Stop();
    Demo.Show(second);
    Demo.Note($"wall clock: {sw.ElapsedMilliseconds} ms");

    Demo.Step("Exit codes flow straight through");
    var failing = await MxcContainer.RunAsync(new ContainerRequest("cmd.exe /d /s /c \"echo about to fail 1>&2 & exit /b 42\"")
    {
        TimeoutMs = 10_000,
    });
    Demo.Show(failing);
    if (failing.ExitCode == 42) Demo.Ok("the workload's exit code (42) is reported verbatim");

    return 0;
}
catch (MxcException error)
{
    return Demo.Error(error);
}
