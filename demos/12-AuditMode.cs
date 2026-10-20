#:project ../shared/Mxc.Demos.Shared.csproj

// 12 · Audit mode: let a workload run unrestricted and record everything it *would* have been denied.
//
// Talking points:
//   • CaptureDenialsMode.Allow is MXC's "permissive learning mode" — the fleet-auditing / profiling flow.
//     Every access check that the policy would refuse is allowed AND logged.
//   • Run 1 shows the problem: under a strict policy with no grants, a normal tool simply fails.
//   • Run 2 shows the audit: same workload, same (empty) policy, it now works — and you get the list of
//     everything it touched that you would need to grant.
//   • Never ship Allow mode. It is a measuring instrument, not a sandbox. Caveat from the docs: in Allow mode
//     capability checks are not recorded, so look for capabilities in a Block-mode run (demo 11/13).

using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("12 · Audit mode: what does this workload need?");
if (!OperatingSystem.IsWindows()) { Demo.Fail("Denial capture is Windows ProcessContainer only."); return 1; }
if (!Demo.RequireBackend(ContainmentBackend.ProcessContainer)) return 1;

var workspace = Demo.Workspace;
var outDir = Path.Combine(workspace, "out");
var reportDir = Path.Combine(Demo.RepoRoot, "denials");
Directory.CreateDirectory(reportDir);
if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true);

// A "real" little build step: read an input, write an artefact, call git and node.
var workload = Demo.PowerShell($$"""
    $text = Get-Content -Raw '{{Path.Combine(workspace, "input.txt")}}' -ErrorAction Stop
    New-Item -ItemType Directory -Force '{{outDir}}' | Out-Null
    Set-Content '{{Path.Combine(outDir, "artefact.txt")}}' ("built from " + $text.Length + " chars") -ErrorAction Stop
    & git --version
    & node -e "console.log('node ' + process.version)"
    Write-Output 'workload finished OK'
    """);

ContainerRequest Request(CaptureDenialsPolicy? capture) => new(workload)
{
    Containment = new Containment.ProcessContainer { CaptureDenials = capture },
    Ui = new UiPolicy { Disable = false },          // PowerShell needs a window station (see demo 03)
    InheritDefaultEnvironment = true,
    TimeoutMs = 60_000,
    // Deliberately NO Filesystem grants: we want to discover them.
};

try
{
    Demo.Step("Run 1 · strict policy, no grants, no capture → the tool just breaks");
    var strict = await MxcContainer.RunAsync(Request(capture: null));
    Demo.Show(strict);
    if (strict.ExitCode != 0 || !strict.Stdout.Contains("workload finished OK"))
        Demo.Note("…and you have no idea *why*. That is what auditing is for.");

    Demo.Step("Run 2 · same policy, CaptureDenials Mode = Allow (audit)");
    var audit = await MxcContainer.RunAsync(Request(new CaptureDenialsPolicy
    {
        Mode = CaptureDenialsMode.Allow,
        OutputPath = Path.Combine(reportDir, "audit.json"),
    }));
    Demo.Show(audit);
    if (audit.Stdout.Contains("workload finished OK") && File.Exists(Path.Combine(outDir, "artefact.txt")))
        Demo.Ok("the workload ran to completion — nothing was blocked, everything was recorded");

    var report = DenialReport.From(audit);
    Demo.Step("What it touched that the policy did not grant");
    report.Print(perType: 10);

    Demo.Step("The interesting bit: file accesses under our own folders");
    foreach (var d in report.OfType("file").Where(d => d.Resource.StartsWith(Demo.RepoRoot, StringComparison.OrdinalIgnoreCase)))
        Console.WriteLine($"      {d.AccessType,-8} {d.Resource}");
    Demo.Note("Everything else is runtime start-up noise (C:\\, profile probes, registry). Demo 13 turns this into a policy.");
    return 0;
}
catch (Exception error) when (error is MxcException or InvalidOperationException)
{
    return error is MxcException mxc ? Demo.Error(mxc) : Fail(error.Message);
}

static int Fail(string message) { Demo.Fail(message); return 1; }
