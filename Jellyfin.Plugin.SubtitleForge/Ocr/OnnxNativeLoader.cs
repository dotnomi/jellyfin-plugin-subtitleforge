using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Jellyfin.Plugin.SubtitleForge.Ocr;

/// <summary>
/// Plugins are not covered by the host's native library probing, so resolve ONNX Runtime's native
/// library from the plugin folder (runtimes/&lt;rid&gt;/native) ourselves.
/// </summary>
public static class OnnxNativeLoader
{
    private static int _registered;

    /// <summary>
    /// Registers the resolver once.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        var onnxAssembly = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly;
        NativeLibrary.SetDllImportResolver(onnxAssembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!libraryName.Contains("onnxruntime", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        var pluginDir = Path.GetDirectoryName(typeof(OnnxNativeLoader).Assembly.Location) ?? AppContext.BaseDirectory;
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        string rid, file;
        if (OperatingSystem.IsWindows())
        {
            (rid, file) = ("win-" + arch, "onnxruntime.dll");
        }
        else if (OperatingSystem.IsMacOS())
        {
            (rid, file) = ("osx-" + arch, "libonnxruntime.dylib");
        }
        else
        {
            (rid, file) = ("linux-" + arch, "libonnxruntime.so");
        }

        var candidate = Path.Combine(pluginDir, "runtimes", rid, "native", file);
        if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
        {
            return handle;
        }

        // Fall back to default probing (e.g. system-wide install).
        return NativeLibrary.TryLoad(libraryName, assembly, searchPath, out handle) ? handle : IntPtr.Zero;
    }
}
