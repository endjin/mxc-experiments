#:project ../shared/Mxc.Demos.Shared.csproj

// 08 · Container lifecycle: provision → start → run several commands → stop → deprovision.
//
// Talking points:
//   • Transient (02) is one-shot. A *provisioned* container has an identity (ContainerId) and keeps state between
//     executions — think "one container per agent conversation".
//   • IsolationSession hands back metadata: the ephemeral user it created and its workspace path.
//   • It isolates identity (a throw-away local user with its own profile and registry hive), so the real user's
//     profile is unreadable — but world-readable NTFS paths still are. Pair it with ProcessContainer for path policy.
//   • Clean-up is explicit and must be attempted even when a step fails.

using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("08 · Persisted IsolationSession container (lifecycle API)");
if (!OperatingSystem.IsWindows()) { Demo.Fail("IsolationSession is Windows-only."); return 1; }
if (!Demo.RequireBackend(ContainmentBackend.IsolationSession)) return 1;

ContainerId? containerId = null;
var started = false;
var exitCode = 1;
var clock = Stopwatch.StartNew();

try
{
    Demo.Step("Provision");
    var provisioned = MxcLifecycle.ProvisionContainer(new IsolationSessionProvisionRequest(new NetworkPolicy
    {
        Egress = new NetworkEgressPolicy { Default = NetworkAction.Allow },
        Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Allow },
    }));
    containerId = provisioned.ContainerId;
    Console.WriteLine($"  container id: {containerId}   ({clock.ElapsedMilliseconds} ms)");
    if (provisioned.Metadata is IsolationSessionProvisionMetadata meta)
    {
        Console.WriteLine($"  agent user  : {meta.AgentUserName}");
        Console.WriteLine($"  agent SID   : {meta.AgentUserSid}");
        Console.WriteLine($"  workspace   : {meta.EphemeralWorkspacePath}");
    }
    Demo.Warnings(provisioned.Warnings);

    Demo.Step("Start");
    clock.Restart();
    Demo.Warnings(MxcLifecycle.StartContainer(containerId.Value).Warnings);
    started = true;
    Console.WriteLine($"  started ({clock.ElapsedMilliseconds} ms)");

    Demo.Step("Exec 1 · who am I, and leave a note in the ephemeral workspace");
    var first = await Exec(containerId.Value,
        "whoami; $env:USERPROFILE; Set-Content -Path (Join-Path $env:USERPROFILE 'note.txt') -Value 'written by exec 1'; Write-Output 'note written'");
    Demo.Show(first);

    Demo.Step("Exec 2 · a separate execution reads the note back (state persisted)");
    var second = await Exec(containerId.Value, "Get-Content -Path (Join-Path $env:USERPROFILE 'note.txt')");
    Demo.Show(second);
    if (second.Stdout.Contains("written by exec 1")) Demo.Ok("the container kept its state between executions");

    Demo.Step("Exec 3 · identity isolation: the real user's profile is off-limits, world-readable paths are not");
    var hostProfileSecret = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mxc-demo-profile-secret.txt");
    var worldReadable = Path.Combine(Demo.Secrets, "api-key.txt");
    File.WriteAllText(hostProfileSecret, "only the real user can read this");
    try
    {
        var third = await Exec(containerId.Value, $$"""
            try { Get-Content '{{hostProfileSecret}}' -ErrorAction Stop | Out-Null; Write-Output '!!! read a file in the REAL user profile' }
            catch { Write-Output ('real user profile  : blocked (' + $_.Exception.GetType().Name + ') - NTFS ACLs protect it from other users') }
            try { Get-Content '{{worldReadable}}' -ErrorAction Stop | Out-Null; Write-Output 'D:\poc\mxc\secrets : readable - default NTFS ACLs let any local user read it' }
            catch { Write-Output 'D:\poc\mxc\secrets : blocked' }
            """);
        Demo.Show(third);
        Demo.Note("IsolationSession isolates *identity* (user, profile, registry hive, session). It is not a path policy.");
        Demo.Note("Need path-level allow/deny or network filtering? Use ProcessContainer (demos 03-05) or combine both.");
    }
    finally
    {
        File.Delete(hostProfileSecret);
    }

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
        Demo.Step("Stop + deprovision (always, even on failure)");
        try
        {
            if (started) Demo.Warnings(MxcLifecycle.StopContainer(id).Warnings);
            Demo.Warnings(MxcLifecycle.DeprovisionContainer(id).Warnings);
            Demo.Ok("container, agent user and ephemeral workspace removed");
        }
        catch (MxcException error)
        {
            Demo.Error(error);
            exitCode = 1;
        }
    }
}

return exitCode;

static Task<ExecutionResult> Exec(ContainerId id, string script) =>
    MxcLifecycle.RunInContainerAsync(id, new ExecutionRequest(Demo.PowerShell(script)) { TimeoutMs = 30_000 });
