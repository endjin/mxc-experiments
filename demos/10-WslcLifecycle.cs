#:project ../shared/Mxc.Demos.Shared.csproj

// 10 · WSLC lifecycle: a Linux container that stays warm across many executions.
//
// Talking points:
//   • provision/start once, then exec as many times as you like — no VM boot per command.
//   • A per-user daemon (wxc-wslc-daemon.exe) holds the live WSL session; it idles out after 5 minutes.
//   • Exec working directories are *in-container* paths here (e.g. /mnt/d/poc/mxc/workspace), unlike create-and-run.
//   • Filesystem and network posture are fixed at provision time; exec can only add a cooperative proxy.

using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("10 · Persisted WSLC Linux container (lifecycle API)");
if (!OperatingSystem.IsWindows()) { Demo.Fail("WSLC is Windows-only."); return 1; }
if (!Demo.RequireBackend(ContainmentBackend.Wslc)) return 1;

var workspace = Demo.Workspace;
var linuxWorkspace = "/mnt/" + char.ToLowerInvariant(workspace[0]) + workspace[2..].Replace('\\', '/');
File.Delete(Path.Combine(workspace, "from-lifecycle-exec.txt"));

ContainerId? containerId = null;
var started = false;
var exitCode = 1;
var clock = Stopwatch.StartNew();

try
{
    Demo.Step("Provision python:3.12-alpine with the workspace mounted read-write (bridged networking so it can pull)");
    var provisioned = MxcLifecycle.ProvisionContainer(new WslcProvisionRequest
    {
        Image = "python:3.12-alpine",
        Filesystem = new FilesystemPolicy { ReadwritePaths = { workspace } },
        Network = new NetworkPolicy
        {
            Egress = new NetworkEgressPolicy { Default = NetworkAction.Allow },
            Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Allow },
        },
    });
    containerId = provisioned.ContainerId;
    Console.WriteLine($"  container id: {containerId}   ({clock.ElapsedMilliseconds} ms)");
    Demo.Warnings(provisioned.Warnings);

    Demo.Step("Start");
    clock.Restart();
    Demo.Warnings(MxcLifecycle.StartContainer(containerId.Value).Warnings);
    started = true;
    Console.WriteLine($"  started ({clock.ElapsedMilliseconds} ms)");

    Demo.Step("Exec 1 · install state into the container's own filesystem");
    clock.Restart();
    Demo.Show(await Exec(containerId.Value, "python3 -c \"import platform; print('python', platform.python_version())\"; echo 'state from exec 1' > /tmp/state.txt; echo saved"));
    Demo.Note($"exec round-trip: {clock.ElapsedMilliseconds} ms");

    Demo.Step("Exec 2 · read it back — same container, no re-pull, no re-boot");
    clock.Restart();
    var second = await Exec(containerId.Value, "cat /tmp/state.txt; uptime");
    Demo.Show(second);
    Demo.Note($"exec round-trip: {clock.ElapsedMilliseconds} ms");
    if (second.Stdout.Contains("state from exec 1")) Demo.Ok("container state persisted between executions");

    Demo.Step("Exec 3 · work in the mounted Windows workspace (in-container cwd)");
    Demo.Show(await Exec(containerId.Value,
        "pwd; ls -la | head -5; python3 -c \"open('from-lifecycle-exec.txt','w').write('hello from exec 3\\n')\"; cat from-lifecycle-exec.txt",
        workingDirectory: linuxWorkspace));
    if (File.Exists(Path.Combine(workspace, "from-lifecycle-exec.txt"))) Demo.Ok("Windows sees from-lifecycle-exec.txt in the workspace");

    exitCode = 0;
}
catch (MxcException error)
{
    exitCode = Demo.Error(error);
}
finally
{
    if (containerId is { } id)
    {
        Demo.Step("Stop + deprovision");
        try
        {
            if (started) Demo.Warnings(MxcLifecycle.StopContainer(id).Warnings);
            Demo.Warnings(MxcLifecycle.DeprovisionContainer(id).Warnings);
            Demo.Ok("container deleted (the daemon keeps the WSL session warm for ~5 min, then exits)");
        }
        catch (MxcException error)
        {
            Demo.Error(error);
            exitCode = 1;
        }
    }
}

return exitCode;

static Task<ExecutionResult> Exec(ContainerId id, string shellScript, string? workingDirectory = null) =>
    MxcLifecycle.RunInContainerAsync(id, new ExecutionRequest(shellScript)
    {
        WorkingDirectory = workingDirectory,
        InheritDefaultEnvironment = true,
        TimeoutMs = 120_000,
    });
