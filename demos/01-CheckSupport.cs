#:project ../shared/Mxc.Demos.Shared.csproj

// 01 · Discover what this host can do, without creating a container.
//
// Talking points:
//   • MXC is one API over several containment backends. Which ones exist depends on OS build and features.
//   • Discovery is cheap and read-only. Call it at app start-up and degrade gracefully.
//   • On Windows the ProcessContainer backend picks an *isolation tier*: BaseContainer (kernel-enforced,
//     newest builds) or an AppContainer fallback (older builds; may need one-time host prep).

using System.Runtime.InteropServices;
using Microsoft.Mxc.Sdk.V1;
using Mxc.Demos;

Demo.Title("01 · Which MXC backends can this machine run?");
Demo.Note($"Native engine : {MxcPlatform.NativeVersion}");
Demo.Note($"OS            : {Environment.OSVersion.VersionString} ({RuntimeInformation.OSArchitecture})");

try
{
    Demo.Step("Platform support (can the V1 SDK launch anything here?)");
    var support = MxcPlatform.GetPlatformSupport();
    Console.WriteLine($"  supported        : {support.IsSupported}");
    if (support.Reason is not null) Console.WriteLine($"  reason           : {support.Reason}");
    Console.WriteLine($"  launchable via V1: {string.Join(", ", support.AvailableMethods)}");

    Demo.Step("Host-available backends, tiers and optional capabilities");
    var backends = MxcPlatform.GetAvailableBackends();
    if (backends.Count == 0) Demo.Fail("no containment backends are available on this host");
    foreach (var backend in backends)
    {
        Console.WriteLine($"  {backend.Backend,-18} tier={backend.Tier?.ToString() ?? "n/a"}");
        if (backend.Capabilities.Count > 0)
            Console.WriteLine($"  {"",-18} capabilities: {string.Join(", ", backend.Capabilities)}");
        Demo.Warnings(backend.Warnings);
    }

    if (OperatingSystem.IsWindows())
    {
        Demo.Step("Request probe: which ProcessContainer tier would serve a typical request?");
        var probe = MxcContainer.Probe(new ContainerRequest("cmd.exe /d /s /c \"echo probe\"")
        {
            Network = new NetworkPolicy
            {
                Egress = new NetworkEgressPolicy { Default = NetworkAction.Deny },
                Ingress = new NetworkIngressPolicy { Default = NetworkAction.Deny, HostLoopback = NetworkAction.Deny },
            },
        });
        Console.WriteLine($"  tier                   : {probe.Tier}");
        Console.WriteLine($"  needs DACL augmentation: {probe.NeedsDaclAugmentation}");
        if (probe.Error is not null) Demo.Fail($"probe error: {probe.Error}");
        Demo.Warnings(probe.Warnings);

        var f = probe.Probes;
        Console.WriteLine("  host facts:");
        Console.WriteLine($"    BaseContainer API present        : {f.BaseContainerApiPresent}");
        Console.WriteLine($"    native denial capture            : {f.NativeCaptureAvailable}");
        Console.WriteLine($"    guarded (WPR) denial capture     : {f.GuardedCaptureAvailable}");
        Console.WriteLine($"    BaseContainer deny-paths         : {f.BaseContainerSupportsDenyPaths}");
        Console.WriteLine($"    BaseContainer enumerate-paths    : {f.BaseContainerSupportsEnumeratePaths}");
        Console.WriteLine($"    BaseContainer host-loopback allow: {f.BaseContainerSupportsIngressHostLoopbackAllow}");
        Console.WriteLine($"    identity-less loopback proxy     : {f.BaseContainerSupportsIdentitylessLoopbackProxy}");
        Console.WriteLine($"    IsolationSession available       : {f.IsolationSessionAvailable}");
        Console.WriteLine($"    Hyperlight available             : {f.HyperlightAvailable}");

        var ui = f.UiCapabilities;
        Console.WriteLine("  UI isolation the host can enforce:");
        Console.WriteLine($"    clipboard read/write : {ui.CanBlockClipboardRead}/{ui.CanBlockClipboardWrite}");
        Console.WriteLine($"    input injection      : {ui.CanBlockInputInjection}");
        Console.WriteLine($"    desktop switching    : {ui.CanBlockDesktopSwitching}");
        Console.WriteLine($"    logoff / shutdown    : {ui.CanBlockLogoffOrShutdown}");
        Console.WriteLine($"    display settings     : {ui.CanBlockDisplaySettingsChanges}");

        Demo.Step("How to read the tier");
        Demo.Note("BaseContainer    → kernel-enforced process isolation (PSEC). Best tier; no host prep needed.");
        Demo.Note("AppContainerBfs  → AppContainer + Brokered File System fallback on older builds.");
        Demo.Note("AppContainerDacl → AppContainer + DACL fallback; wxc-host-prep.exe may be recommended in warnings.");
    }

    return support.IsSupported ? 0 : 1;
}
catch (MxcException error)
{
    return Demo.Error(error);
}
