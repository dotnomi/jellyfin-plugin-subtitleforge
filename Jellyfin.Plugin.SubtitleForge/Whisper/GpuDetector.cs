using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.SubtitleForge.Whisper;

/// <summary>
/// Decides whether whisper.cpp may use Vulkan: only with a real GPU render node (AMD, Intel, NVIDIA) and an installed
/// Vulkan loader. Without that, Vulkan would fall back to Mesa's software renderer (llvmpipe), which is far slower
/// than whisper.cpp's own CPU backend.
/// </summary>
public static class GpuDetector
{
    private static readonly Dictionary<string, string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x1002"] = "AMD",
        ["0x8086"] = "Intel",
        ["0x10de"] = "NVIDIA",
    };

    /// <summary>
    /// Lists GPU render nodes with a supported vendor (Linux).
    /// </summary>
    /// <param name="sysClassDrm">Root of /sys/class/drm (for tests).</param>
    /// <returns>Render node name and vendor.</returns>
    public static List<(string Node, string Vendor)> RenderNodes(string sysClassDrm = "/sys/class/drm")
    {
        var result = new List<(string, string)>();
        if (!Directory.Exists(sysClassDrm))
        {
            return result;
        }

        foreach (var dir in Directory.EnumerateDirectories(sysClassDrm, "renderD*").OrderBy(d => d, StringComparer.Ordinal))
        {
            var vendorFile = Path.Combine(dir, "device", "vendor");
            if (File.Exists(vendorFile) && Vendors.TryGetValue(File.ReadAllText(vendorFile).Trim(), out var vendor)
                && File.Exists(Path.Combine("/dev/dri", Path.GetFileName(dir))))
            {
                result.Add((Path.GetFileName(dir), vendor));
            }
        }

        return result;
    }

    /// <summary>
    /// Whether the Vulkan loader library is available.
    /// </summary>
    /// <returns>True if libvulkan.so.1 can be loaded.</returns>
    public static bool HasVulkanLoader()
    {
        if (!NativeLibrary.TryLoad("libvulkan.so.1", out var handle))
        {
            return false;
        }

        NativeLibrary.Free(handle);
        return true;
    }

    /// <summary>
    /// Describes why GPU acceleration is (not) used, for the log and the settings page.
    /// </summary>
    /// <returns>Usable flag and description.</returns>
    public static (bool Usable, string Description) Check()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return (false, "GPU-Beschleunigung gibt es nur für Linux x64");
        }

        var nodes = RenderNodes();
        if (nodes.Count == 0)
        {
            return (false, "keine AMD-/Intel-/NVIDIA-GPU gefunden (/dev/dri fehlt oder ist nicht freigegeben)");
        }

        if (!HasVulkanLoader())
        {
            return (false, $"GPU gefunden ({string.Join(", ", nodes.Select(n => n.Vendor))}), aber kein Vulkan installiert (Paket libvulkan1 + mesa-vulkan-drivers)");
        }

        return (true, "GPU: " + string.Join(", ", nodes.Select(n => $"{n.Vendor} ({n.Node})")));
    }
}
