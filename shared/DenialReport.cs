using System.Text.Json;
using Microsoft.Mxc.Sdk.V1;

namespace Mxc.Demos;

/// <summary>One de-duplicated entry from a captureDenials policy report (denials.*.json).</summary>
public sealed record Denial(string Resource, string ResourceType, string AccessType, int Pid);

/// <summary>
/// Reads the actionable report MXC writes after a CaptureDenials run. Shape:
/// { "denials": [ { resource, resourceType, accessType, pid, filetime } ], "summary": { exitCode, totalDenials, … } }
/// resourceType is file | network | capability | ui | other ; accessType is read | write | execute | unknown.
/// </summary>
public sealed class DenialReport
{
    public required string Path { get; init; }
    public required IReadOnlyList<Denial> Denials { get; init; }
    public bool Truncated { get; init; }

    public IEnumerable<Denial> OfType(string resourceType) =>
        Denials.Where(d => d.ResourceType.Equals(resourceType, StringComparison.OrdinalIgnoreCase));

    /// <summary>Pull the report out of an ExecutionResult, failing loudly if capture did not work.</summary>
    public static DenialReport From(ExecutionResult result)
    {
        if (result.OutputMetadata?.CaptureDenialsError is { } error)
            throw new InvalidOperationException($"denial capture failed: {error.Message}");
        var capture = result.OutputMetadata?.CaptureDenials
            ?? throw new InvalidOperationException("the run produced no denial report");
        return Load(capture.OutputPath);
    }

    public static DenialReport Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var list = new List<Denial>();
        if (root.TryGetProperty("denials", out var denials) && denials.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in denials.EnumerateArray())
            {
                list.Add(new Denial(
                    Str(d, "resource"),
                    Str(d, "resourceType"),
                    Str(d, "accessType"),
                    d.TryGetProperty("pid", out var pid) && pid.TryGetInt32(out var p) ? p : 0));
            }
        }
        var truncated = root.TryGetProperty("summary", out var summary)
            && summary.TryGetProperty("deniedResourcesTruncated", out var t)
            && t.ValueKind == JsonValueKind.True;
        return new DenialReport { Path = path, Denials = list, Truncated = truncated };
    }

    /// <summary>Print counts per resource type and the first few entries of each.</summary>
    public void Print(int perType = 6)
    {
        Console.WriteLine($"  report: {Path}");
        Console.WriteLine($"  total : {Denials.Count}{(Truncated ? " (truncated)" : "")}");
        foreach (var group in Denials.GroupBy(d => d.ResourceType).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key} ({group.Count()})");
            foreach (var d in group.Take(perType)) Console.WriteLine($"      {d.AccessType,-8} {d.Resource}");
            if (group.Count() > perType) Console.WriteLine($"      … {group.Count() - perType} more");
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
