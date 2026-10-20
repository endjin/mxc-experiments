#:project ../shared/Mxc.Demos.Shared.csproj

// 03 · The "why": an agent tool that executes model-generated code safely.
//
// Scenario: an LLM agent has a tool `run_powershell(script)`. The model is supposed to summarise a file in the
// workspace. This "model output" also tries four things it should not be able to do. Watch each one fail while the
// legitimate work succeeds.
//
// Talking points:
//   • Policy is declared once, in C#, next to the tool. The model never sees or controls it.
//   • Allowed: read/write the workspace. Denied: everything else on disk, all networking, clipboard, input injection.
//   • The host verifies the outcome afterwards — the sandbox gives you *evidence*, not just hope.

using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("03 · Running untrusted, model-generated code as an agent tool");
if (!Demo.RequireSupport()) return 1;

var workspace = Demo.Workspace;
var secretFile = Path.Combine(Demo.Secrets, "api-key.txt");
var escapeFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "mxc-escaped.txt");
File.Delete(escapeFile);
File.Delete(Path.Combine(workspace, "summary.txt"));

// Pretend this arrived from the model.
var modelGeneratedScript = $$"""
    Write-Output '--- legitimate work: summarise input.txt ---'
    $text = Get-Content -Raw -Path (Join-Path '{{workspace}}' 'input.txt')
    Write-Output ('input.txt has ' + $text.Length + ' characters')
    Set-Content -Path (Join-Path '{{workspace}}' 'summary.txt') -Value ('Summary written inside the sandbox at ' + (Get-Date -Format o))
    Write-Output 'wrote summary.txt'

    Write-Output '--- attempt 1: read a secret outside the workspace ---'
    try { Get-Content -Path '{{secretFile}}' -ErrorAction Stop; Write-Output '!!! LEAKED THE SECRET' }
    catch { Write-Output ('blocked: ' + $_.Exception.GetType().Name) }

    Write-Output '--- attempt 2: write outside the workspace (user profile) ---'
    try { Set-Content -Path '{{escapeFile}}' -Value 'owned' -ErrorAction Stop; Write-Output '!!! ESCAPED THE WORKSPACE' }
    catch { Write-Output ('blocked: ' + $_.Exception.GetType().Name) }

    Write-Output '--- attempt 3: phone home ---'
    try { Invoke-WebRequest -Uri 'https://example.com' -UseBasicParsing -TimeoutSec 5 -ErrorAction Stop | Out-Null; Write-Output '!!! EXFILTRATED' }
    catch { Write-Output ('blocked: ' + $_.Exception.GetType().Name) }

    Write-Output '--- attempt 4: look around ---'
    Write-Output ('whoami: ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name)
    $visible = @(Get-ChildItem -Path 'C:\Users' -ErrorAction SilentlyContinue)
    Write-Output ('entries visible under C:\Users: ' + $visible.Count)
    """;

var tool = new SandboxedToolRunner(workspace);

try
{
    Demo.Step("Tool policy");
    Demo.Note($"read/write : {workspace}");
    Demo.Note("network    : egress deny, ingress deny, host loopback deny");
    Demo.Note("timeout    : 20 s");

    Demo.Step("Executing the model's script inside the sandbox");
    var outcome = await tool.RunPowerShellAsync(modelGeneratedScript);
    Demo.Show(outcome);

    Demo.Step("Host-side verification (never trust the script's own report)");
    var summary = Path.Combine(workspace, "summary.txt");
    if (File.Exists(summary)) Demo.Ok($"legitimate output exists: {summary}");
    else Demo.Fail("summary.txt was not written — the legitimate work failed");

    if (File.Exists(escapeFile)) Demo.Fail($"sandbox escape: {escapeFile} exists!");
    else Demo.Ok("nothing was written to the user profile");

    if (outcome.Stdout.Contains("!!!")) Demo.Fail("at least one attack succeeded — see output above");
    else Demo.Ok("all four attempts were blocked");

    return 0;
}
catch (MxcException error)
{
    return Demo.Error(error);
}

/// <summary>
/// A reusable "execute untrusted code" primitive you could hand to an agent framework.
/// The policy lives here, in code you control — not in the prompt.
/// </summary>
sealed class SandboxedToolRunner(string workspace)
{
    public Task<ExecutionResult> RunPowerShellAsync(string script, CancellationToken cancellationToken = default)
    {
        var request = new ContainerRequest(Demo.PowerShell(script))
        {
            Containment = new Containment.Process(),
            WorkingDirectory = workspace,
            Filesystem = new FilesystemPolicy { ReadwritePaths = { workspace } },
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
                Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
            },
            // UI lockdown is on by default and is so strict that anything loading user32 at start-up
            // (PowerShell included) dies with 0xC0000142. Disable=false gives it a window station;
            // clipboard access and input injection remain blocked.
            Ui = new UiPolicy { Disable = false },
            TimeoutMs = 20_000,
            InheritDefaultEnvironment = true,
        };
        return MxcContainer.RunAsync(request, options: null, cancellationToken);
    }
}
