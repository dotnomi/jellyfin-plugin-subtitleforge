using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.SubtitleForge.Common;

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
    /// Whether the proprietary NVIDIA Vulkan driver is installed (its ICD file exists).
    /// </summary>
    /// <param name="icdDirs">ICD folders to check (default: the standard locations).</param>
    /// <returns>True if found.</returns>
    public static bool HasNvidiaVulkanDriver(params string[] icdDirs)
    {
        var dirs = icdDirs.Length > 0 ? icdDirs : new[] { "/usr/share/vulkan/icd.d", "/etc/vulkan/icd.d", "/usr/local/share/vulkan/icd.d" };
        return dirs.Any(d => Directory.Exists(d) && Directory.EnumerateFiles(d, "nvidia*.json").Any());
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
            return (false, Loc.T("GPU acceleration is only available on Linux x64", "GPU-Beschleunigung gibt es nur für Linux x64"));
        }

        var nodes = RenderNodes();
        if (nodes.Count == 0)
        {
            return (false, Loc.T("no AMD/Intel/NVIDIA GPU found (/dev/dri missing or not passed through)", "keine AMD-/Intel-/NVIDIA-GPU gefunden (/dev/dri fehlt oder ist nicht freigegeben)"));
        }

        if (!HasVulkanLoader())
        {
            return (false, Loc.German
                ? $"GPU gefunden ({string.Join(", ", nodes.Select(n => n.Vendor))}), aber kein Vulkan installiert (Paket libvulkan1 + mesa-vulkan-drivers)"
                : $"GPU found ({string.Join(", ", nodes.Select(n => n.Vendor))}), but Vulkan is not installed (packages libvulkan1 + mesa-vulkan-drivers)");
        }

        // NVIDIA only with the proprietary driver: Mesa's open-source NVIDIA Vulkan driver (NVK/nouveau) hangs on
        // whisper.cpp's compute shaders.
        if (!HasNvidiaVulkanDriver())
        {
            var withoutNvidia = nodes.Where(n => n.Vendor != "NVIDIA").ToList();
            if (withoutNvidia.Count == 0)
            {
                return (false, Loc.T("NVIDIA GPU found, but without the official NVIDIA driver (Vulkan) – Whisper uses the CPU", "NVIDIA-GPU gefunden, aber ohne den offiziellen NVIDIA-Treiber (Vulkan) – Whisper nutzt die CPU"));
            }

            nodes = withoutNvidia;
        }

        return (true, "GPU: " + string.Join(", ", nodes.Select(n => $"{n.Vendor} ({n.Node})")));
    }
}
