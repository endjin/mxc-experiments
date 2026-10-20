#:project ../shared/Mxc.Demos.Shared.csproj

// 06 · Live output, timeouts and kill.
//
// Talking points:
//   • Run/RunAsync capture everything; Spawn/SpawnAsync give you live stdout/stderr streams (great for UIs/logs).
//   • TimeoutMs is enforced by the engine. A runaway workload is torn down and reported as TimedOut.
//   • Kill() tears down the container and everything in it — no orphaned child processes.

using System.Diagnostics;
using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("06 · Streaming output, timeouts and kill");
if (!Demo.RequireSupport()) return 1;

try
{
    Demo.Step("Spawn with live streams: progress lines appear as they are written");
    var progress = Demo.PowerShell("1..5 | ForEach-Object { Write-Output ('step ' + $_ + ' of 5'); Start-Sleep -Milliseconds 400 }; [Console]::Error.WriteLine('one line on stderr')");
    var started = Stopwatch.StartNew();
    using (var process = await MxcContainer.SpawnAsync(Ps(progress, timeoutMs: 30_000)))
    {
        var stdout = Pump(process.StandardOutput, "stdout", started);
        var stderr = Pump(process.StandardError, "stderr", started);
        var wait = await process.WaitAsync();
        await Task.WhenAll(stdout, stderr);
        Demo.Warnings(process.Warnings);
        Console.WriteLine($"  exit={wait.ExitCode} timedOut={wait.TimedOut}");
    }

    Demo.Step("Timeout: an infinite loop with TimeoutMs = 3000");
    started.Restart();
    var runaway = await MxcContainer.RunAsync(Ps(Demo.PowerShell("Write-Output 'looping forever...'; while ($true) { Start-Sleep -Seconds 1 }"), timeoutMs: 3_000));
    started.Stop();
    Demo.Show(runaway);
    if (runaway.TimedOut) Demo.Ok($"engine stopped it after {started.ElapsedMilliseconds} ms and reported TimedOut");

    Demo.Step("Kill: start something long-running, then kill it from the host after 1.5 s");
    using (var process = await MxcContainer.SpawnAsync(Ps(Demo.PowerShell("Write-Output ('pid ' + $PID + ' alive'); Start-Sleep -Seconds 60; Write-Output 'should never print'"), timeoutMs: 120_000)))
    {
        var stdout = Pump(process.StandardOutput, "stdout", Stopwatch.StartNew());
        await Task.Delay(1_500);
        process.Kill();
        var wait = await process.WaitAsync();
        await stdout;
        Console.WriteLine($"  exit={wait.ExitCode} timedOut={wait.TimedOut}");
        Demo.Ok("killed; the container and its child processes are gone");
    }

    return 0;
}
catch (MxcException error)
{
    return Demo.Error(error);
}

static ContainerRequest Ps(string commandLine, uint timeoutMs) => new(commandLine)
{
    Containment = new Containment.Process(),
    Ui = new UiPolicy { Disable = false },      // PowerShell needs a window station to start (see demo 03)
    TimeoutMs = timeoutMs,
};

static async Task Pump(Stream? stream, string name, Stopwatch clock)
{
    if (stream is null) return;
    using var reader = new StreamReader(stream);
    while (await reader.ReadLineAsync() is { } line)
    {
        Console.WriteLine($"  [{clock.ElapsedMilliseconds,5} ms] {name}: {line}");
    }
}
