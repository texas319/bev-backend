// ============================================================
// FILE        : NodeClassDetector.cs
// STATUS      : Phase 1c-2 — NEXUS visual retrofit + PIN
// LAST UPD    : 2026-05-28 09:00 CST
// PURPOSE     : Determines the node class of THIS machine purely
//               from its CPU, with no reference to licensing.
//                 - Server-grade Xeon silicon  => CUBE
//                 - Consumer i-series / AMD     => SPHERE
//                 - Anything unrecognized       => CUBE (safe default)
//               Reads Win32_Processor.Name via WMI.
// OWNS        : Local node-class determination.
// CALLED BY   : GatewayWorker at provision time.
// ============================================================

using System.Management;
using System.Runtime.Versioning;
using BEVGateway.Shared;
using Microsoft.Extensions.Logging;

namespace BEVGateway.Service.System;

public interface INodeClassDetector
{
    string Detect();
}

[SupportedOSPlatform("windows")]
public sealed class NodeClassDetector : INodeClassDetector
{
    private readonly ILogger<NodeClassDetector> _log;

    public NodeClassDetector(ILogger<NodeClassDetector> log) { _log = log; }

    public string Detect()
    {
        // Node class is now uniform: every node is a CUBE. (Prior logic
        // classed consumer CPUs as SPHERE; per ops decision all nodes are
        // CUBE.) CPU is still read for the diagnostic log line only.
        var cpu = ReadProcessorName();
        _log.LogInformation("Node class: {Class} (cpu='{Cpu}')", GatewayConstants.NodeClassCube, cpu);
        return GatewayConstants.NodeClassCube;
    }

    private static string Classify(string cpuName)
    {
        if (string.IsNullOrWhiteSpace(cpuName))
            return GatewayConstants.NodeClassCube; // safe default

        var c = cpuName.ToUpperInvariant();

        // Server-grade silicon => CUBE.
        if (c.Contains("XEON"))
            return GatewayConstants.NodeClassCube;

        // Consumer silicon => SPHERE.
        if (c.Contains("CORE(TM) I3") || c.Contains("CORE(TM) I5") ||
            c.Contains("CORE(TM) I7") || c.Contains("CORE(TM) I9") ||
            c.Contains("CORE I3") || c.Contains("CORE I5") ||
            c.Contains("CORE I7") || c.Contains("CORE I9") ||
            c.Contains(" I3-") || c.Contains(" I5-") ||
            c.Contains(" I7-") || c.Contains(" I9-") ||
            c.Contains("AMD") || c.Contains("RYZEN") ||
            c.Contains("ATHLON") || c.Contains("EPYC"))
            return GatewayConstants.NodeClassSphere;

        // Unknown => safe default.
        return GatewayConstants.NodeClassCube;
    }

    private string ReadProcessorName()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                var v = obj["Name"];
                if (v is not null) return v.ToString() ?? "";
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read CPU name for node class; defaulting to CUBE.");
        }
        return "";
    }
}
