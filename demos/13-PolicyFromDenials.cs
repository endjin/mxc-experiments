#:project ../shared/Mxc.Demos.Shared.csproj

// 13 · Least privilege without guesswork: capture → review → generate policy → enforce → verify.
//
// Talking points:
//   • Step 1 audits the workload (Mode = Allow) with no grants, exactly like demo 12.
//   • Step 2 is the human/automated REVIEW: only accesses under folders we approve become grants. The workload
//     also reads secrets\api-key.txt — the report surfaces it, the reviewer rejects it. Grant nothing blindly.
//   • Step 3 writes the generated policy to denials\generated-policy.json (what you would check in).
//   • Step 4 enforces it (Mode = Block, still capturing): the legitimate work succeeds, the secret read is
//     blocked, and the denial count drops to the residual start-up noise plus the thing we refused.

using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("13 · Generate a least-privilege policy from captured denials");
if (!OperatingSystem.IsWindows()) { Demo.Fail("Denial capture is Windows ProcessContainer only."); return 1; }
if (!Demo.RequireBackend(ContainmentBackend.ProcessContainer)) return 1;

var workspace = Demo.Workspace;
var outDir = Path.Combine(workspace, "out");
var secret = Path.Combine(Demo.Secrets, "api-key.txt");
var reportDir = Path.Combine(Demo.RepoRoot, "denials");
Directory.CreateDirectory(reportDir);
Directory.CreateDirectory(outDir);
File.Delete(Path.Combine(outDir, "report.txt"));

var workload = Demo.PowerShell($$"""
    $text = Get-Content -Raw '{{Path.Combine(workspace, "input.txt")}}' -ErrorAction Stop
    Set-Content '{{Path.Combine(outDir, "report.txt")}}' ('processed ' + $text.Length + ' chars') -ErrorAction Stop
    Write-Output 'legitimate work: OK'
    try { Get-Content '{{secret}}' -ErrorAction Stop | Out-Null; Write-Output 'secret read     : SUCCEEDED' }
    catch { Write-Output 'secret read     : blocked' }
    """);

ContainerRequest Request(FilesystemPolicy? fs, CaptureDenialsMode mode, string reportName) => new(workload)
{
    Containment = new Containment.ProcessContainer
    {
        CaptureDenials = new CaptureDenialsPolicy { Mode = mode, OutputPath = Path.Combine(reportDir, reportName) },
    },
    Filesystem = fs,
    Ui = new UiPolicy { Disable = false },          // PowerShell needs a window station (see demo 03)
    InheritDefaultEnvironment = true,
    TimeoutMs = 60_000,
};

try
{
    Demo.Step("Step 1 · audit (Mode = Allow, no grants)");
    var audit = await MxcContainer.RunAsync(Request(null, CaptureDenialsMode.Allow, "learn.json"));
    Demo.Show(audit);
    var learned = DenialReport.From(audit);
    Console.WriteLine($"  {learned.Denials.Count} denials recorded ({learned.OfType("file").Count()} file)");

    Demo.Step("Step 2 · review: approve only accesses under the workspace");
    string[] approvedRoots = [workspace];
    bool Approved(string path) => approvedRoots.Any(r =>
        path.Equals(r, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    var readonlyPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    var readwritePaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var d in learned.OfType("file"))
    {
        if (!Approved(d.Resource))
        {
            var interesting = d.Resource.StartsWith(Demo.RepoRoot, StringComparison.OrdinalIgnoreCase);
            if (interesting) Demo.Fail($"rejected  {d.AccessType,-8} {d.Resource}   ← outside the approved roots");
            continue;
        }
        Demo.Ok($"approved  {d.AccessType,-8} {d.Resource}");
        if (d.AccessType == "write")
        {
            // A write may create the file, so grant the directory that holds it.
            readwritePaths.Add(Directory.Exists(d.Resource) ? d.Resource : Path.GetDirectoryName(d.Resource)!);
        }
        else
        {
            readonlyPaths.Add(d.Resource);
        }
    }
    readonlyPaths.RemoveWhere(p => readwritePaths.Any(rw => p.Equals(rw, StringComparison.OrdinalIgnoreCase) ||
                                                             p.StartsWith(rw + "\\", StringComparison.OrdinalIgnoreCase)));
    Demo.Note($"{learned.OfType("file").Count(d => !Approved(d.Resource))} other file entries were start-up noise outside our folders and were ignored");

    Demo.Step("Step 3 · generated policy");
    var policy = new FilesystemPolicy();
    policy.ReadonlyPaths.AddRange(readonlyPaths);
    policy.ReadwritePaths.AddRange(readwritePaths);
    foreach (var p in policy.ReadonlyPaths) Console.WriteLine($"      ro  {p}");
    foreach (var p in policy.ReadwritePaths) Console.WriteLine($"      rw  {p}");
    var policyFile = Path.Combine(reportDir, "generated-policy.json");
    var json = new JsonObject
    {
        ["filesystem"] = new JsonObject
        {
            ["readonlyPaths"] = new JsonArray(policy.ReadonlyPaths.Select(p => (JsonNode)p).ToArray()),
            ["readwritePaths"] = new JsonArray(policy.ReadwritePaths.Select(p => (JsonNode)p).ToArray()),
        },
    };
    File.WriteAllText(policyFile, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    Demo.Note($"saved to {policyFile}");

    Demo.Step("Step 4 · enforce the generated policy (Mode = Block, still capturing to verify)");
    var enforced = await MxcContainer.RunAsync(Request(policy, CaptureDenialsMode.Block, "verify.json"));
    Demo.Show(enforced);
    var verify = DenialReport.From(enforced);

    var legit = enforced.Stdout.Contains("legitimate work: OK") && File.Exists(Path.Combine(outDir, "report.txt"));
    var secretBlocked = enforced.Stdout.Contains("secret read     : blocked");
    var stillDeniedOurs = verify.OfType("file").Where(d => d.Resource.StartsWith(Demo.RepoRoot, StringComparison.OrdinalIgnoreCase)).ToList();

    Demo.Step("Verify");
    Console.WriteLine($"  denials before policy : {learned.Denials.Count}");
    Console.WriteLine($"  denials after policy  : {verify.Denials.Count}");
    if (legit) Demo.Ok("legitimate work succeeds under the generated policy"); else Demo.Fail("legitimate work failed — policy too tight");
    if (secretBlocked) Demo.Ok("the secret read is still blocked"); else Demo.Fail("the secret was readable — policy too loose");
    foreach (var d in stillDeniedOurs) Console.WriteLine($"      still denied: {d.AccessType,-8} {d.Resource}");
    Demo.Note("Under our folders only the refused secret and a harmless directory probe of the workspace folder remain; the rest is runtime start-up noise.");
    return legit && secretBlocked ? 0 : 1;
}
catch (Exception error) when (error is MxcException or InvalidOperationException)
{
    return error is MxcException mxc ? Demo.Error(mxc) : Fail(error.Message);
}

static int Fail(string message) { Demo.Fail(message); return 1; }
