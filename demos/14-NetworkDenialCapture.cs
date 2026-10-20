#:project ../shared/Mxc.Demos.Shared.csproj

// 14 · Network denial capture: which endpoints did the workload try to reach, and what rule would allow each?
//
// Talking points:
//   • Network denials are recorded only when the request carries an explicit network.egress section. MXC then
//     lets the connect attempt reach the Windows Filtering Platform so the refusal can be logged (fixed in 1.0.1).
//   • Each denial is a numeric endpoint like tcp://8.8.8.8:443. It maps 1:1 to an egress allow rule — /32 and the
//     exact port, never broader.
//   • Needs internet. Uses raw IPs, because with egress denied DNS lookups are blocked too.

using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("14 · Capture blocked network connections and turn them into egress rules");
if (!OperatingSystem.IsWindows()) { Demo.Fail("Denial capture is Windows ProcessContainer only."); return 1; }
if (!Demo.RequireBackend(ContainmentBackend.ProcessContainer)) return 1;

var reportDir = Path.Combine(Demo.RepoRoot, "denials");
Directory.CreateDirectory(reportDir);

string[] targets = ["https://1.1.1.1/", "https://8.8.8.8/", "https://9.9.9.9/"];
var workload = Demo.PowerShell(string.Join("\n", targets.Select(url =>
    $"$c = & curl.exe --silent --max-time 10 --output NUL --write-out '%{{http_code}}' {url} 2>$null; " +
    $"Write-Output ('{url,-20} -> ' + $(if ($c -and $c -ne '000') {{ 'HTTP ' + $c }} else {{ 'blocked' }}))")));

NetworkPolicy Egress(params string[] allowedIps) => new()
{
    Egress = new NetworkEgressPolicy
    {
        Default = NetworkAction.Deny,
        Allow = allowedIps.Select(ip => new NetworkRulePolicy
        {
            To = [new NetworkPeerPolicy($"{ip}/32")],
            Ports = [new NetworkPortPolicy { Protocol = NetworkProtocol.Tcp, Port = 443 }],
        }).ToList(),
    },
    Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
};

ContainerRequest Request(NetworkPolicy network, string reportName) => new(workload)
{
    Containment = new Containment.ProcessContainer
    {
        CaptureDenials = new CaptureDenialsPolicy { Mode = CaptureDenialsMode.Block, OutputPath = Path.Combine(reportDir, reportName) },
    },
    Network = network,
    Ui = new UiPolicy { Disable = false },          // PowerShell needs a window station (see demo 03)
    InheritDefaultEnvironment = true,
    TimeoutMs = 60_000,
};

try
{
    Demo.Step("Run 1 · egress allows only 1.1.1.1:443; the workload calls three resolvers");
    var first = await MxcContainer.RunAsync(Request(Egress("1.1.1.1"), "network-1.json"));
    Demo.Show(first);
    var report = DenialReport.From(first);
    var network = report.OfType("network").ToList();

    Demo.Step("Captured network denials → suggested egress rules");
    IEnumerable<string> endpoints;
    if (network.Count > 0)
    {
        endpoints = network.Select(d => d.Resource);
        Demo.Note($"{report.Denials.Count - network.Count} non-network denials (start-up noise) omitted — see {report.Path}");
    }
    else
    {
        // Hosts whose Learning Mode trace is access-only (no option-aware start export) record no WFP decisions.
        Demo.Fail("this host's denial trace recorded no network decisions");
        Demo.Note("Network denial capture needs the option-aware Learning Mode trace (Microsoft validated it on build 26663+).");
        Demo.Note($"This machine is build {Environment.OSVersion.Version.Build}; file/registry/capability capture still works (demos 11-13).");
        Demo.Note("Fallback for the demo: derive the endpoints from what the workload itself saw blocked.");
        endpoints = targets
            .Where(url => first.Stdout.Contains($"{url,-20} -> blocked"))
            .Select(url => $"tcp://{new Uri(url).Host}:443");
    }
    foreach (var resource in endpoints)
    {
        // resource looks like tcp://8.8.8.8:443  (IPv6: tcp://[2001:db8::1]:443, ICMP has no port)
        var uri = new Uri(resource);
        var ip = uri.Host.Trim('[', ']');
        var cidr = ip.Contains(':') ? $"{ip}/128" : $"{ip}/32";
        Console.WriteLine($"  {resource,-22} → new NetworkRulePolicy {{ To = [new(\"{cidr}\")], Ports = [new() {{ Protocol = NetworkProtocol.{Cap(uri.Scheme)}, Port = {uri.Port} }}] }}");
    }

    Demo.Step("Review: approve 8.8.8.8, leave 9.9.9.9 blocked. Run 2 with the extra rule");
    var second = await MxcContainer.RunAsync(Request(Egress("1.1.1.1", "8.8.8.8"), "network-2.json"));
    Demo.Show(second);
    var after = DenialReport.From(second).OfType("network").Select(d => d.Resource).ToList();
    if (network.Count > 0)
        Console.WriteLine($"  captured network denials before: {network.Count}   after: {after.Count}  [{string.Join(", ", after)}]");
    var eightOk = !second.Stdout.Contains($"{"https://8.8.8.8/",-20} -> blocked");
    var nineBlocked = second.Stdout.Contains($"{"https://9.9.9.9/",-20} -> blocked");
    if (eightOk) Demo.Ok("8.8.8.8 is reachable with the approved rule"); else Demo.Fail("8.8.8.8 is still blocked");
    if (nineBlocked) Demo.Ok("9.9.9.9 was not approved and stays blocked"); else Demo.Fail("9.9.9.9 got through");
    return eightOk && nineBlocked ? 0 : 1;
}
catch (Exception error) when (error is MxcException or InvalidOperationException)
{
    return error is MxcException mxc ? Demo.Error(mxc) : Fail(error.Message);
}

static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
static int Fail(string message) { Demo.Fail(message); return 1; }
