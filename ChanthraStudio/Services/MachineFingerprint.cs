using System;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace ChanthraStudio.Services;

/// <summary>
/// Generates a stable hardware fingerprint for the current machine.
/// SHA-256(CPU.ProcessorId | BaseBoard.SerialNumber | DiskDrive.SerialNumber)
/// truncated to 32 hex chars to match the server's machine_id length
/// constraint (32..64 chars). Result is cached for the life of the process.
/// </summary>
public static class MachineFingerprint
{
    private static string? _cached;
    private static readonly object _lock = new();

    public static string Get()
    {
        if (_cached is not null) return _cached;
        lock (_lock)
        {
            if (_cached is not null) return _cached;

            var cpu = WmiFirst("SELECT ProcessorId FROM Win32_Processor", "ProcessorId");
            var board = WmiFirst("SELECT SerialNumber FROM Win32_BaseBoard", "SerialNumber");
            var disk = WmiFirst("SELECT SerialNumber FROM Win32_DiskDrive", "SerialNumber");
            var combined = $"{cpu}|{board}|{disk}|{Environment.MachineName}";

            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
            var sb = new StringBuilder(64);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            _cached = sb.ToString().Substring(0, 32);
            return _cached;
        }
    }

    private static string WmiFirst(string query, string property)
    {
        try
        {
            using var s = new ManagementObjectSearcher(query);
            foreach (var obj in s.Get())
            {
                var v = obj[property]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(v)) return v!;
            }
        }
        catch
        {
            // WMI can fail under restricted user contexts. Falling through
            // is fine — the combined string still has MachineName as anchor.
        }
        return "unknown";
    }
}
