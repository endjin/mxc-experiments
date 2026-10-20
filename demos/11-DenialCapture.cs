#:project ../shared/Mxc.Demos.Shared.csproj

// 11 · Denial capture ("learning mode"): find out what a workload *tried* to touch.
//
// Talking points:
//   • Writing least-privilege policy by hand is painful. Run the workload once with CaptureDenials and MXC records
//     every blocked access (files, registry, network, capabilities) to a JSON report — while still blocking them.
//   • Mode = Block (default) keeps the sandbox honest; Mode = Allow is an audit-only run that lets everything through.
//   • Use the report to decide what to grant. Never grant blindly — that's the whole point.

using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("11 · Denial capture: what did the workload try to do?");
if (!OperatingSystem.IsWindows()) { Demo.Fail("Denial capture is Windows ProcessContainer only."); return 1; }
if (!Demo.RequireBackend(ContainmentBackend.ProcessContainer)) return 1;

var reportDir = Path.Combine(Demo.RepoRoot, "denials");
Directory.CreateDirectory(reportDir);
var reportPath = Path.Combine(reportDir, "denials.json");
var secret = Path.Combine(Demo.Secrets, "api-key.txt");
var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

var nosyScript = $$"""
    $ErrorActionPreference = 'SilentlyContinue'
    Get-Content '{{secret}}' | Out-Null
    Get-ChildItem '{{documents}}' | Out-Null
    Set-Content (Join-Path '{{documents}}' 'mxc-should-not-exist.txt') 'x'
    Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' | Out-Null
    & curl.exe --silent --max-time 3 http://example.com/ | Out-Null
    Write-Output 'nosy script finished'
    """;

try
{
    Demo.Step("Run a nosy workload with CaptureDenials (Mode = Block)");
    var result = await MxcContainer.RunAsync(new ContainerRequest(Demo.PowerShell(nosyScript))
    {
        Containment = new Containment.ProcessContainer
        {
            CaptureDenials = new CaptureDenialsPolicy { Mode = CaptureDenialsMode.Block, OutputPath = reportPath },
        },
        Filesystem = new FilesystemPolicy { DeniedPaths = { secret } },
        Ui = new UiPolicy { Disable = false },      // PowerShell needs a window station to start (see demo 03)
        InheritDefaultEnvironment = true,
        TimeoutMs = 60_000,
    });
    Demo.Show(result);

    if (result.OutputMetadata?.CaptureDenialsError is { } captureError)
    {
        Demo.Fail($"capture failed: {captureError.Message} (etl: {captureError.EtlPath})");
        return 1;
    }
    var capture = result.OutputMetadata?.CaptureDenials;
    if (capture is null) { Demo.Fail("no denial report was produced"); return 1; }

    Demo.Step("Report");
    Console.WriteLine($"  report        : {capture.OutputPath}");
    Console.WriteLine($"  total denials : {capture.TotalDenials}{(capture.DeniedResourcesTruncated ? " (truncated)" : "")}");
    if (capture.EtlPath is not null) Console.WriteLine($"  etl trace     : {capture.EtlPath}");

    Demo.Step("What was denied (grouped by resource type, first few of each)");
    using var doc = JsonDocument.Parse(File.ReadAllText(capture.OutputPath));
    if (doc.RootElement.TryGetProperty("denials", out var denials) && denials.ValueKind == JsonValueKind.Array)
    {
        var groups = denials.EnumerateArray()
            .Select(d => (
                type: Str(d, "resourceType"),
                access: Str(d, "accessType"),
                resource: Str(d, "resource")))
            .GroupBy(x => x.type)
            .OrderByDescending(g => g.Count());
        foreach (var g in groups)
        {
            Console.WriteLine($"  {g.Key} ({g.Count()})");
            foreach (var item in g.Take(8)) Console.WriteLine($"      {item.access,-8} {item.resource}");
            if (g.Count() > 8) Console.WriteLine($"      … {g.Count() - 8} more");
        }
    }
    else
    {
        Demo.Note("unexpected report shape; raw document follows");
        Console.WriteLine(doc.RootElement.ToString());
    }

    Demo.Note("Open the report in VS Code for the full records, then turn the ones you *want* into ReadonlyPaths / ReadwritePaths / egress rules.");
    return 0;
}
catch (MxcException error)
{
    return Demo.Error(error);
}

static string Str(JsonElement element, string property) =>
    element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "?";
