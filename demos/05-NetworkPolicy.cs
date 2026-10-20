#:project ../shared/Mxc.Demos.Shared.csproj

// 05 · Network policy: deny everything, allow everything, or allow exactly one destination.
//
// Talking points:
//   • Egress, ingress and host-loopback are three separate toggles. All default to deny.
//   • Egress allow rules are numeric: CIDR + protocol + port. No hostnames — DNS is itself network traffic (UDP 53),
//     so with a deny default the sandbox cannot even resolve names. That is a feature.
//   • Enforcement is Windows Filtering Platform (kernel), not an environment variable the code can ignore.

using System.Net;
using System.Net.Sockets;
using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("05 · Network policy");
if (!Demo.RequireSupport()) return 1;

// A tiny HTTP server on the host's loopback so the first two runs need no internet.
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
var serving = Task.Run(async () =>
{
    while (true)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var response = "HTTP/1.1 200 OK\r\nContent-Length: 20\r\nConnection: close\r\n\r\nhello from the host\n"u8.ToArray();
        await client.GetStream().WriteAsync(response);
    }
});
Demo.Note($"host test server listening on http://127.0.0.1:{port}/");

static string Curl(string url) =>
    $"$r = & curl.exe --silent --show-error --max-time 5 --output NUL --write-out '%{{http_code}}' {url} 2>&1; Write-Output ('{url} -> ' + ($r -join ' '))";

try
{
    Demo.Step("Run 1 · default policy (everything denied) → loopback call must fail");
    Demo.Show(await Run(Curl($"http://127.0.0.1:{port}/"), new NetworkPolicy
    {
        Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
        Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
    }));

    Demo.Step("Run 2 · egress allow + host-loopback allow → the same call succeeds (HTTP 200)");
    Demo.Show(await Run(Curl($"http://127.0.0.1:{port}/"), new NetworkPolicy
    {
        Egress = new NetworkEgressPolicy { Default = NetworkAction.Allow },
        Ingress = new NetworkIngressPolicy { Default = NetworkAction.Allow, HostLoopback = NetworkAction.Allow },
    }));

    Demo.Step("Run 3 · internet allow-list: ONLY 1.1.1.1:443 (needs internet; skip if offline)");
    Demo.Note("expect: 1.1.1.1 answers, 8.8.8.8 is blocked, and example.com cannot even be resolved");
    var script = string.Join("; ", Curl("https://1.1.1.1/"), Curl("https://8.8.8.8/"), Curl("https://example.com/"));
    Demo.Show(await Run(script, new NetworkPolicy
    {
        Egress = new NetworkEgressPolicy
        {
            Default = NetworkAction.Deny,
            Allow =
            [
                new NetworkRulePolicy
                {
                    To = [new NetworkPeerPolicy("1.1.1.1/32")],
                    Ports = [new NetworkPortPolicy { Protocol = NetworkProtocol.Tcp, Port = 443 }],
                },
            ],
        },
        Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
    }));

    return 0;
}
catch (MxcException error)
{
    return Demo.Error(error);
}
finally
{
    listener.Stop();
    _ = serving.ContinueWith(_ => { }, TaskScheduler.Default);
}

static Task<ExecutionResult> Run(string script, NetworkPolicy network) =>
    MxcContainer.RunAsync(new ContainerRequest(Demo.PowerShell(script))
    {
        Containment = new Containment.Process(),
        Network = network,
        Ui = new UiPolicy { Disable = false },      // PowerShell needs a window station to start (see demo 03)
        InheritDefaultEnvironment = true,
        TimeoutMs = 60_000,
    });
