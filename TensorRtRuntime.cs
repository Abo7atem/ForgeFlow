using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace VideoProcessing;

/// <summary>
/// Discovers an installed NVIDIA TensorRT + ONNX Runtime TensorRT provider and
/// prepares the process DLL search path. TensorRT is optional: ForgeFlow keeps
/// DirectML/CPU available when the customer has no NVIDIA runtime installed.
/// </summary>
public sealed record TensorRtRuntimeInfo(
    bool IsAvailable,
    string RootPath,
    string ProviderPath,
    string TensorRtLibraryPath,
    string Version,
    string Message);

public static class TensorRtRuntime
{
    private static readonly object Sync = new();
    private static TensorRtRuntimeInfo? _cached;
    private static readonly Dictionary<string, nint> LoadedLibraries = new(StringComparer.OrdinalIgnoreCase);

    public static TensorRtRuntimeInfo Detect(bool forceRefresh = false)
    {
        lock (Sync)
        {
            if (_cached != null && !forceRefresh)
                return _cached;

            foreach (string root in GetCandidateRoots())
            {
                if (!Directory.Exists(root))
                    continue;

                string? provider = FindFile(root, "onnxruntime_providers_tensorrt.dll");
                string? trt = FindTensorRtLibrary(root);
                if (provider == null || trt == null)
                    continue;

                string[] dllDirs =
                {
                    Path.GetDirectoryName(provider) ?? root,
                    Path.GetDirectoryName(trt) ?? root,
                    Path.Combine(root, "bin"),
                    Path.Combine(root, "lib")
                };
                AddDllDirectories(dllDirs);

                string version = TryReadVersion(trt);
                _cached = new TensorRtRuntimeInfo(
                    true,
                    root,
                    provider,
                    trt,
                    version,
                    $"TensorRT {version} detected ({Path.GetFileName(provider)}). NVIDIA TensorRT execution is available.");
                return _cached;
            }

            _cached = new TensorRtRuntimeInfo(
                false,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                "TensorRT runtime/provider was not found. Install a matching NVIDIA TensorRT Windows runtime or use DirectML/CPU.");
            return _cached;
        }
    }

    /// <summary>
    /// Appends TensorRT to an ONNX Runtime session using the provider options
    /// exposed by the installed ONNX Runtime build. The method is deliberately
    /// reflection-based so a DirectML-only deployment still starts normally.
    /// </summary>
    public static bool TryAppendExecutionProvider(
        SessionOptions options,
        int deviceId,
        string engineCachePath,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(options);
        TensorRtRuntimeInfo info = Detect();
        if (!info.IsAvailable)
        {
            message = info.Message;
            return false;
        }

        try
        {
            // Load the provider explicitly before ORT tries to resolve it. This
            // also makes custom side-by-side TensorRT installations work when
            // the DLLs are not in the global PATH.
            // TensorRT's provider-options bridge queries the CUDA EP interface,
            // so load the CUDA provider from the same self-contained bundle when
            // it is present. It is harmless when the customer only ships TRT.
            string? cudaProvider = FindFile(info.RootPath, "onnxruntime_providers_cuda.dll");
            if (cudaProvider != null)
                EnsureLoaded(cudaProvider);
            EnsureLoaded(info.ProviderPath);
            EnsureLoaded(info.TensorRtLibraryPath);

            MethodInfo? optionsMethod = typeof(SessionOptions)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(method =>
                    string.Equals(method.Name, "AppendExecutionProvider_Tensorrt", StringComparison.OrdinalIgnoreCase)
                    && method.GetParameters().Length == 1
                    && method.GetParameters()[0].ParameterType == typeof(OrtTensorRTProviderOptions));

            if (optionsMethod != null)
            {
                using var providerOptions = new OrtTensorRTProviderOptions();
                string cache = string.IsNullOrWhiteSpace(engineCachePath)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForgeFlow", "TensorRTCache")
                    : engineCachePath;
                Directory.CreateDirectory(cache);
                providerOptions.UpdateOptions(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["device_id"] = Math.Max(0, deviceId).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["trt_fp16_enable"] = "True",
                    ["trt_engine_cache_enable"] = "True",
                    ["trt_engine_cache_path"] = cache,
                    ["trt_timing_cache_enable"] = "True",
                    ["trt_timing_cache_path"] = cache,
                    ["trt_context_memory_sharing_enable"] = "True",
                    ["trt_max_workspace_size"] = (2UL * 1024UL * 1024UL * 1024UL).ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
                optionsMethod.Invoke(options, new object[] { providerOptions });
            }
            else
            {
                MethodInfo? deviceMethod = typeof(SessionOptions)
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                        string.Equals(method.Name, "AppendExecutionProvider_Tensorrt", StringComparison.OrdinalIgnoreCase)
                        && method.GetParameters().Length == 1
                        && method.GetParameters()[0].ParameterType == typeof(int));
                if (deviceMethod == null)
                {
                    message = "The installed ONNX Runtime build does not expose the TensorRT execution provider API.";
                    return false;
                }

                deviceMethod.Invoke(options, new object[] { Math.Max(0, deviceId) });
            }

            message = $"TensorRT EP enabled (FP16, engine cache: {Path.GetFileName(engineCachePath)}).";
            return true;
        }
        catch (TargetInvocationException ex)
        {
            message = "TensorRT provider rejected this GPU/model: " + (ex.InnerException?.Message ?? ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            message = "TensorRT provider could not be loaded: " + ex.Message;
            return false;
        }
    }

    private static IEnumerable<string> GetCandidateRoots()
    {
        string? configured = Environment.GetEnvironmentVariable("FORGEFLOW_TENSORRT_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured;

        yield return Path.Combine(AppContext.BaseDirectory, "Tools", "tensorrt");
        yield return Path.Combine(AppContext.BaseDirectory, "TensorRT");

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            yield return Path.Combine(programFiles, "NVIDIA GPU Computing Toolkit", "TensorRT");

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "ForgeFlow", "TensorRT");
    }

    private static string? FindFile(string root, string fileName)
    {
        try
        {
            return Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static string? FindTensorRtLibrary(string root)
    {
        string[] names = { "nvinfer_11.dll", "nvinfer_10.dll", "nvinfer.dll", "nvinfer_lean_11.dll", "nvinfer_lean.dll" };
        foreach (string name in names)
        {
            string? found = FindFile(root, name);
            if (found != null)
                return found;
        }

        return null;
    }

    private static void AddDllDirectories(IEnumerable<string> directories)
    {
        string current = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var parts = current.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (string directory in directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!parts.Contains(directory, StringComparer.OrdinalIgnoreCase))
                parts.Insert(0, directory);
        }

        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, parts));
    }

    private static void EnsureLoaded(string path)
    {
        if (!File.Exists(path))
            return;

        lock (Sync)
        {
            string fullPath = Path.GetFullPath(path);
            if (LoadedLibraries.ContainsKey(fullPath)) return;

            try
            {
                // Keep one handle per absolute path for the process lifetime.
                // Repeated NativeLibrary.Load calls used to leak handles and
                // made TensorRT provider retries increasingly unreliable.
                LoadedLibraries[fullPath] = NativeLibrary.Load(fullPath);
            }
            catch
            {
            }
        }
    }

    private static string TryReadVersion(string path)
    {
        try
        {
            FileVersionInfo? version = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(version.FileVersion) && version.FileVersion != "0.0.0.0")
                return version.FileVersion;
        }
        catch
        {
        }

        string fileName = Path.GetFileName(path);
        if (fileName.Contains("_11", StringComparison.OrdinalIgnoreCase))
            return "11.x (nvinfer_11)";
        if (fileName.Contains("_10", StringComparison.OrdinalIgnoreCase))
            return "10.x (nvinfer_10)";
        return "unknown";
    }
}
