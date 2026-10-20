#:project ../shared/Mxc.Demos.Shared.csproj

// 09 · WSLC: run Linux container images from Windows, through the same API.
//
// Talking points:
//   • Containment.Wslc runs an OCI image (Docker Hub, MCR, GHCR, quay, or a local tar) inside a WSL2 utility VM.
//     No Docker Desktop involved — it uses the WSL Container SDK that ships with WSL 2.9.9+.
//   • Same ContainerRequest shape: command, filesystem grants, network posture, timeout, env.
//   • Host paths are mounted at /mnt/<drive>/... ; D:\poc\mxc\workspace → /mnt/d/poc/mxc/workspace.
//   • Networking is all-or-nothing on this backend (bridged or isolated). The first run of an image pulls it,
//     which needs the bridged posture; later runs can be fully isolated.

using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("09 · Linux containers on Windows via WSLC");
if (!OperatingSystem.IsWindows()) { Demo.Fail("WSLC is Windows-only."); return 1; }
if (!Demo.RequireBackend(ContainmentBackend.Wslc)) return 1;

var bridged = new NetworkPolicy
{
    Egress = new NetworkEgressPolicy { Default = NetworkAction.Allow },
    Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Allow },
};
var isolated = new NetworkPolicy
{
    Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
    Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
};

var workspace = Demo.Workspace;
var linuxWorkspace = ToLinuxPath(workspace);
File.Delete(Path.Combine(workspace, "hello-from-linux.txt"));

try
{
    Demo.Step("Run 1 · alpine:latest — what is this thing? (first run pulls the image; allow a minute)");
    var clock = Stopwatch.StartNew();
    var alpine = await Run("uname -a; . /etc/os-release; echo \"$PRETTY_NAME\"; id; echo cpus=$(nproc) mem_mb=$(( $(grep MemTotal /proc/meminfo | awk '{print $2}') / 1024 ))",
        new Containment.Wslc { Image = "alpine:latest" }, bridged);
    Demo.Show(alpine);
    Demo.Note($"wall clock: {clock.ElapsedMilliseconds} ms (includes pull on a cache miss)");

    Demo.Step("Run 2 · alpine again, warm cache, fully ISOLATED networking");
    clock.Restart();
    var offline = await Run("if wget -q -T 3 -O /dev/null http://example.com; then echo '!!! network reachable'; else echo 'network blocked (as intended)'; fi",
        new Containment.Wslc { Image = "alpine:latest" }, isolated);
    Demo.Show(offline);
    Demo.Note($"wall clock: {clock.ElapsedMilliseconds} ms");

    Demo.Step("Run 3 · python:3.12-alpine with 2 CPUs / 1 GiB, writing into the mounted Windows workspace");
    var python = $$"""
        python3 - <<'PY'
        import os, platform, json, pathlib
        info = {"python": platform.python_version(), "cpus": os.cpu_count(), "cwd": os.getcwd()}
        print(json.dumps(info))
        out = pathlib.Path("{{linuxWorkspace}}/hello-from-linux.txt")
        out.write_text("written by python inside a WSLC container\n")
        print("wrote", out)
        PY
        """;
    var py = await Run(python, new Containment.Wslc { Image = "python:3.12-alpine", CpuCount = 2, MemoryMb = 1024 }, bridged,
        filesystem: new FilesystemPolicy { ReadwritePaths = { workspace } }, workingDirectory: workspace);
    Demo.Show(py);

    var marker = Path.Combine(workspace, "hello-from-linux.txt");
    if (File.Exists(marker)) Demo.Ok($"Windows sees the file Linux wrote: {marker}");
    else Demo.Fail("the mounted workspace file did not appear on the Windows side");

    return 0;
}
catch (MxcException error)
{
    return Demo.Error(error);
}

static Task<ExecutionResult> Run(string shellScript, Containment.Wslc wslc, NetworkPolicy network,
    FilesystemPolicy? filesystem = null, string? workingDirectory = null) =>
    MxcContainer.RunAsync(new ContainerRequest(shellScript)       // WSLC runs the command line under the image's /bin/sh -c
    {
        Containment = wslc,
        Network = network,
        Filesystem = filesystem,
        WorkingDirectory = workingDirectory,                      // a Windows path; mapped to /mnt/<drive>/... inside
        InheritDefaultEnvironment = true,                         // keep the image's own ENV (PATH etc.)
        TimeoutMs = 300_000,                                      // generous: a cold image pull counts against it
    });

static string ToLinuxPath(string windowsPath)
{
    var full = Path.GetFullPath(windowsPath);
    var drive = char.ToLowerInvariant(full[0]);
    return "/mnt/" + drive + full[2..].Replace('\\', '/');
}
