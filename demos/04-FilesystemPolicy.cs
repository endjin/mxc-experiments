#:project ../shared/Mxc.Demos.Shared.csproj

// 04 · Filesystem policy: nothing by default, then read-only, read-write, and explicit denies.
//
// Talking points:
//   • Deny-by-default. You *grant* paths; you don't block them.
//   • ReadonlyPaths / ReadwritePaths / DeniedPaths compose. A denied path carves a hole out of a grant.
//   • Policy.Filesystem helpers discover what a dev-tool workload typically needs (tools on PATH, temp, profile)
//     so you can grant "enough to run node/git/dotnet" without hand-listing directories.

using Microsoft.Mxc.Sdk.V1;
using Microsoft.Mxc.Sdk.V1.Policy;
using Mxc.Demos;

Demo.Title("04 · Filesystem policy");
if (!Demo.RequireSupport()) return 1;

var workspace = Demo.Workspace;
var inputFile = Path.Combine(workspace, "input.txt");
var privateDir = Path.Combine(workspace, "private");
var writtenFile = Path.Combine(workspace, "written-by-sandbox.txt");
Directory.CreateDirectory(privateDir);
File.WriteAllText(Path.Combine(privateDir, "notes.txt"), "private notes the sandbox must not read");
File.Delete(writtenFile);

var probeScript = $$"""
    try { $t = Get-Content -Raw '{{inputFile}}' -ErrorAction Stop; Write-Output ('READ  input.txt         : ok (' + $t.Length + ' chars)') }
    catch { Write-Output ('READ  input.txt         : blocked (' + $_.Exception.GetType().Name + ')') }
    try { Set-Content '{{writtenFile}}' 'hi' -ErrorAction Stop; Write-Output 'WRITE workspace\file    : ok' }
    catch { Write-Output ('WRITE workspace\file    : blocked (' + $_.Exception.GetType().Name + ')') }
    try { $n = Get-Content -Raw '{{Path.Combine(privateDir, "notes.txt")}}' -ErrorAction Stop; Write-Output 'READ  private\notes.txt : ok' }
    catch { Write-Output ('READ  private\notes.txt : blocked (' + $_.Exception.GetType().Name + ')') }
    """;

try
{
    Demo.Step("Run 1 · no filesystem grants at all");
    Demo.Show(await Run(probeScript, filesystem: null));

    Demo.Step("Run 2 · workspace granted READ-ONLY");
    Demo.Show(await Run(probeScript, new FilesystemPolicy { ReadonlyPaths = { workspace } }));

    Demo.Step("Run 3 · workspace READ-WRITE, but workspace\\private explicitly DENIED");
    Demo.Show(await Run(probeScript, new FilesystemPolicy
    {
        ReadwritePaths = { workspace },
        DeniedPaths = { privateDir },
    }));
    if (File.Exists(writtenFile)) Demo.Ok("host confirms written-by-sandbox.txt now exists in the workspace");

    Demo.Step("Discovery helpers: what would a developer-tool workload need?");
    var tools = Filesystem.GetAvailableToolsPolicy();
    var profile = Filesystem.GetUserProfilePolicy();
    var temp = Filesystem.GetTemporaryFilesPolicy();
    Print("GetAvailableToolsPolicy  (tool dirs found on PATH etc.)", tools);
    Print("GetUserProfilePolicy     (per-user app dirs tools expect)", profile);
    Print("GetTemporaryFilesPolicy  (temp)", temp);

    Demo.Step("Run 4 · grant those fragments and run real tools: node --version ; git --version");
    var composed = new FilesystemPolicy { ReadwritePaths = { workspace } };
    composed.ReadonlyPaths.AddRange(tools.ReadonlyPaths);
    composed.ReadonlyPaths.AddRange(profile.ReadonlyPaths);
    composed.ReadwritePaths.AddRange(tools.ReadwritePaths);
    composed.ReadwritePaths.AddRange(profile.ReadwritePaths);
    composed.ReadwritePaths.AddRange(temp.ReadwritePaths);
    Demo.Show(await Run("node --version; git --version", composed, inheritEnvironment: true));

    return 0;
}
catch (MxcException error)
{
    return Demo.Error(error);
}

static Task<ExecutionResult> Run(string script, FilesystemPolicy? filesystem, bool inheritEnvironment = false) =>
    MxcContainer.RunAsync(new ContainerRequest(Demo.PowerShell(script))
    {
        Containment = new Containment.Process(),
        Filesystem = filesystem,
        Ui = new UiPolicy { Disable = false },      // PowerShell needs a window station to start (see demo 03)
        InheritDefaultEnvironment = inheritEnvironment,
        TimeoutMs = 30_000,
    });

static void Print(string label, FilesystemPolicyResult fragment)
{
    Console.WriteLine($"  {label}");
    foreach (var p in fragment.ReadonlyPaths) Console.WriteLine($"      ro  {p}");
    foreach (var p in fragment.ReadwritePaths) Console.WriteLine($"      rw  {p}");
    if (fragment.ReadonlyPaths.Count + fragment.ReadwritePaths.Count == 0) Console.WriteLine("      (nothing discovered)");
}
