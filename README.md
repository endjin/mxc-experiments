# MXC demos (.NET)

Eleven small, self-contained demos of [Microsoft eXecution Container (MXC)](https://github.com/microsoft/mxc)
driven from C#. MXC runs untrusted code (model output, plugins, tools) inside an OS-enforced sandbox behind one
API, on Windows, Linux and macOS. These demos target Windows and use the `Microsoft.Mxc.Sdk` NuGet package.

Every demo is a single .NET 10 file-based app. No solution, no per-demo project:

```powershell
dotnet run demos\01-CheckSupport.cs
```

## Prerequisites

| Need | Why | Check |
|---|---|---|
| Windows 11 24H2 / 25H2 / 26H2 with the September 2026 update or later (see the [OS table](https://github.com/microsoft/mxc/blob/main/docs/backends/process-container/os-version-support.md)) | ProcessContainer and IsolationSession backends | `winver` → build ≥ 26100.9550 / 26200.9550 / 26300.9550 |
| .NET 10 SDK | file-based apps (`dotnet run file.cs`) | `dotnet --version` |
| WSL 2.9.9 or later (demos 09 and 10 only) | WSLC Linux-container backend | `wsl --version`; update with `wsl --update` |
| Internet (first run of 09/10, run 3 of 05) | pulls `alpine:latest` and `python:3.12-alpine`; reaches 1.1.1.1 | |

Nothing needs admin rights. The NuGet package bundles the native engine (`mxc_ffi.dll`), so there is no separate
runtime to install. Demo 01 tells you what the current machine can do before you show anything else.

## Layout

```
demos\        one .cs file per demo (run with `dotnet run demos\NN-Name.cs`)
shared\       Mxc.Demos.Shared: console helpers (Demo.Title/Step/Show, Demo.PowerShell, Demo.Workspace…)
workspace\    the directory the sandboxed code IS allowed to read/write
secrets\      a fake secret the sandboxed code must NOT read (created on first run, git-ignored)
denials\      JSON reports written by demo 11
run-all.ps1   runs every non-interactive demo in order and prints a summary
```

Each demo starts with `#:project ../shared/Mxc.Demos.Shared.csproj`, so the helper is compiled on demand.
`Directory.Build.props` pins the SDK package version (`MxcSdkVersion`).

## The demos, in suggested order

| # | File | Backend | What it shows | Approx. time |
|---|---|---|---|---|
| 01 | `01-CheckSupport.cs` | all | Platform support, available backends, capabilities, isolation tier, host facts | 1 s |
| 02 | `02-HelloWorld.cs` | ProcessContainer | Smallest run-to-completion request; cold vs warm start; exit codes pass through | 1 s |
| 03 | `03-UntrustedAgentTool.cs` | ProcessContainer | **The headline.** A model-generated script does legit work and tries four escapes; all blocked | 3 s |
| 04 | `04-FilesystemPolicy.cs` | ProcessContainer | Deny-by-default → read-only → read-write with a denied sub-folder; discovery helpers; run node and git inside | 10 s |
| 05 | `05-NetworkPolicy.cs` | ProcessContainer | Deny all → allow loopback → allow-list a single IP:port; DNS is blocked too | 15 s |
| 06 | `06-StreamingAndTimeouts.cs` | ProcessContainer | Live stdout/stderr streaming, engine-enforced timeout, Kill() | 10 s |
| 07 | `07-InteractiveShell.cs` | IsolationSession | **Interactive.** A real PowerShell terminal running as a throw-away Windows user | live |
| 08 | `08-PersistedIsolationSession.cs` | IsolationSession | Lifecycle API: provision → start → exec ×3 → stop → deprovision; state persists; identity vs path isolation | 12 s |
| 09 | `09-WslcLinuxContainer.cs` | WSLC | Linux images (alpine, python) from Windows; isolated networking; Windows↔Linux workspace mount | 30 s first run |
| 10 | `10-WslcLifecycle.cs` | WSLC | A warm Linux container across many execs (~50 ms round trips); in-container cwd on a mounted drive | 15 s |
| 11 | `11-DenialCapture.cs` | ProcessContainer | Learning mode: record everything a workload tried and was denied, as a JSON report | 20 s |
| 12 | `12-AuditMode.cs` | ProcessContainer | **Audit mode** (`Mode = Allow`): a tool that fails under strict policy runs to completion and every needed access is logged | 15 s |
| 13 | `13-PolicyFromDenials.cs` | ProcessContainer | **Least privilege loop:** audit → review (secret rejected) → generate policy JSON → enforce → verify denials drop | 20 s |
| 14 | `14-NetworkDenialCapture.cs` | ProcessContainer | **Network auditing:** blocked endpoints → exact `/32` + port egress rules → approve one, re-run, the other stays blocked | 30 s |

Run them all (except 07) with `.\run-all.ps1`; add `-SkipWslc` on a machine without WSL.

## Talking points per demo

**01 Check support.** MXC is one API over several backends. Discovery is read-only and cheap. On this machine the
ProcessContainer tier is `BaseContainer` (kernel-enforced), and ProcessContainer, WSLC, IsolationSession and
Hyperlight are reported available. Only the first three have a .NET V1 creation API.

**02 Hello world.** `ContainerRequest` = workload + policy. `Containment.Process` is the host's native backend, so the
same C# runs on Linux and macOS too. Expect roughly 170–260 ms per transient container.

**03 Untrusted agent tool.** The policy lives in C#, next to the tool, not in the prompt. Legit work in the workspace
succeeds; reading `secrets\`, writing to the user profile and calling `example.com` all fail with
`UnauthorizedAccessException` / `WebException`; `C:\Users` enumerates as empty. The host then verifies the outcome
from outside the sandbox.

**04 Filesystem.** Nothing is readable until granted. `ReadonlyPaths`, `ReadwritePaths` and `DeniedPaths` compose.
`Policy.Filesystem.GetAvailableToolsPolicy()` and friends discover what dev tools need; with those grants node and
git run inside the sandbox (git warns it cannot read `.gitconfig`, which is exactly right).

**05 Network.** Three toggles: egress, ingress, host loopback. Egress allow rules are CIDR + port. The allow-list
run reaches `1.1.1.1:443` (HTTP 301) while `8.8.8.8` is refused and `example.com` cannot resolve, because DNS
is network traffic too. Enforcement is Windows Filtering Platform in the kernel.

**06 Streaming, timeouts, kill.** `SpawnAsync` gives live streams for a UI or log. `TimeoutMs` is enforced by the
engine and surfaces as `TimedOut`. `Kill()` removes the container and every child process.

**07 Interactive shell.** `SpawnWithPty` plus `Containment.IsolationSession` gives a real terminal running as a
fresh local user (`whoami` shows a random name like `R5-Y5`). `dir C:\Users\<you>` is denied. Note that
`D:\poc\mxc\secrets` is readable here: default NTFS ACLs let any local user read it. Identity isolation is not a
path policy; pair it with ProcessContainer when you need both.

**08 Persisted IsolationSession.** Provision returns the agent user name, SID and ephemeral workspace. A file
written by exec 1 is read by exec 2. Clean-up is explicit and runs even on failure.

**09 WSLC.** `Containment.Wslc { Image = "python:3.12-alpine", CpuCount = 2, MemoryMb = 1024 }`. No Docker
Desktop involved. `D:\poc\mxc\workspace` appears as `/mnt/d/poc/mxc/workspace`. Networking is all-or-nothing
(bridged or isolated); the first pull needs bridged, later runs can be isolated.

**10 WSLC lifecycle.** Provision/start once, then exec many times with ~50 ms round trips. A per-user daemon
(`wxc-wslc-daemon.exe`) holds the WSL session warm and exits after about five idle minutes.

**11 Denial capture.** Run once with `CaptureDenials`, read `denials\*.json`, and turn the entries you actually
want into grants. On this machine the nosy script produced 46 denials across files, registry, UI and a
capability.

**12 Audit mode.** Run 1 shows the pain: no grants, the build step fails with "Access is denied" and no clue why.
Run 2 adds `CaptureDenials { Mode = Allow }`: same empty policy, the workload completes, git and node run, and MXC
emits a loud `SECURITY WARNING` that deny-by-default is off. The report (123 entries here) contains the four
accesses under our own folders that matter. Never ship Allow mode; it is a measuring instrument. Capability checks
are not recorded in Allow mode, so take those from a Block-mode run.

**13 Policy from denials.** The full loop in one demo. Audit with no grants, then a review step approves only paths
under `workspace\` and visibly rejects `secrets\api-key.txt`. The generated policy (one read-only file, one
read-write folder) is saved to `denials\generated-policy.json`, the thing you would check in. Enforced in Block
mode, the legitimate work succeeds, the secret read is blocked, and denials fall from about 120 to about 46. The
remainder is runtime start-up noise plus the refused secret.

**14 Network denial capture.** The request carries an explicit egress section allowing only `1.1.1.1:443`; the
workload calls three resolvers by IP. Each blocked endpoint becomes an exact rule (`8.8.8.8/32`, TCP 443, never
broader). Approve 8.8.8.8, re-run: it answers, 9.9.9.9 stays blocked. On hosts with the option-aware Learning Mode
trace the endpoints come from captured WFP records such as `tcp://8.8.8.8:443`. **This machine (build 26300)
records file, registry and capability denials but no network decisions**, so the demo says so and falls back to
the endpoints the workload saw refused. Microsoft validated network capture on build 26663 and later.

## Things we learned on this machine (worth saying out loud)

- **PowerShell needs `Ui = new UiPolicy { Disable = false }` in ProcessContainer.** UI lockdown is on by default
  and is strict enough that any process which loads user32 at start-up (PowerShell 5.1 and 7 both do) dies with
  exit code `0xC0000142`. `cmd.exe` does not care. With `Disable = false` the process gets a window station;
  clipboard access and input injection stay blocked. The shared `Demo.PowerShell()` helper documents this.
- **Scripts go in as `-EncodedCommand`.** Base64 UTF-16LE avoids every quoting problem on the command-line hop.
- **IsolationSession is identity isolation.** It protects the real user's profile, registry hive and session,
  but world-readable NTFS paths are still readable. ProcessContainer gives path-level and network policy.
- **First IsolationSession provision can fail** with `RPC communication with the isolation proxy failed`
  (`0x80010108`). Re-running succeeds. Keep that in mind before a live demo: run 08 once beforehand.
- **Exit code `-1`** on a timed-out or killed run is the engine's marker, not the workload's.

## Not covered here

- **Hyperlight** (micro-VM guests for Python, Node and .NET source) shows as available but has no .NET V1
  creation API yet; it is reachable through `wxc-exec.exe --experimental` with JSON configs.
- **Windows Sandbox** backend: the optional Windows feature is not installed on this machine and it has no V1 API.
- **Telemetry consent** sample (`MxcTelemetry`): interactive and not very demo-worthy.
- **Linux (Bubblewrap, LXC) and macOS (Seatbelt)** backends: same `Containment.Process` code path, different OS.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `exit=-1073741502` and no output | UI lockdown killed a process that loads user32 | `Ui = new UiPolicy { Disable = false }` |
| `PolicyValidation: process.env ... missing SYSTEMROOT, LOCALAPPDATA` | You set `Environment` without `InheritDefaultEnvironment = true` | set it, or omit `Environment` |
| `WSLC image '...' is not cached, and this sandbox declares no egress` | isolated posture on a cold cache | run once with the bridged posture first |
| `the image store at <path> is already held by another live session` | two WSLC sessions on one store | wait for the other run or the daemon's idle timeout |
| Cannot delete a `bin\` folder containing `wslcsdk.dll` | `wxc-wslc-daemon.exe` still idling | wait five minutes or `Stop-Process -Name wxc-wslc-daemon` |
| `RPC communication with the isolation proxy failed` | first IsolationSession start on this host | re-run |

## References

- Repo and docs: <https://github.com/microsoft/mxc> (see `docs/api-reference/dotnet/v1/` and `docs/backends/`)
- NuGet: <https://www.nuget.org/packages/Microsoft.Mxc.Sdk>
- Release 1.0.1 notes and signed CLI binaries (`wxc-exec.exe`, `wxc-host-prep.exe`): <https://github.com/microsoft/mxc/releases/tag/v1.0.1>
