using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ForgeFlowSetup;

internal static class Program
{
    private const string AppName = "ForgeFlow Lite";
    private const string Publisher = "A.Hatem T-Zone SYR";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ForgeFlow Lite";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        try
        {
            InstallerOptions options = InstallerOptions.Parse(args);
            ExistingInstall? existingInstall = FindExistingInstall(options.WaitPid);
            string installDir = options.InstallDir ?? existingInstall?.InstallDir ?? GetDefaultInstallDir();
            int? waitPid = options.WaitPid ?? existingInstall?.ProcessId;

            using var optionsForm = new InstallOptionsForm(installDir, options.LaunchAfterInstall);
            if (optionsForm.ShowDialog() != DialogResult.OK)
                return;

            installDir = optionsForm.InstallDir;
            bool createDesktopShortcut = optionsForm.CreateDesktopShortcut;
            bool launchAfterInstall = optionsForm.LaunchAfterInstall;

            using var progress = new InstallProgressForm();
            progress.Show();
            progress.SetStatus("Preparing installer...");
            Application.DoEvents();

            if (waitPid.HasValue)
            {
                progress.SetStatus("Waiting for ForgeFlow to close...");
                Application.DoEvents();
                CloseForgeFlowProcess(waitPid.Value, forceIfNeeded: true);
            }

            string tempZip = Path.Combine(Path.GetTempPath(), "ForgeFlow_Lite_payload_" + Guid.NewGuid().ToString("N") + ".zip");
            string tempInstallDir = CreateTempInstallDirectory(installDir);
            ExtractPayloadResource(tempZip);

            progress.SetStatus("Installing files...");
            Application.DoEvents();

            ValidateInstallDirectory(installDir);
            CloseRunningForgeFlowProcesses(installDir);
            CloseAllForgeFlowProcesses();
            CleanupLegacyInstalls(installDir);

            ZipFile.ExtractToDirectory(tempZip, tempInstallDir, overwriteFiles: true);
            TryDelete(tempZip);

            string stagedExePath = Path.Combine(tempInstallDir, "ForgeFlow.exe");
            if (!File.Exists(stagedExePath))
                throw new FileNotFoundException("ForgeFlow.exe was not found in the installer payload.", stagedExePath);

            installDir = InstallStagedPayload(tempInstallDir, installDir, progress);

            string exePath = Path.Combine(installDir, "ForgeFlow.exe");
            if (!File.Exists(exePath))
                throw new FileNotFoundException("ForgeFlow.exe was not installed correctly.", exePath);

            progress.SetStatus("Creating shortcuts...");
            Application.DoEvents();
            CleanupLegacyShortcuts();
            if (createDesktopShortcut)
            {
                CreateShortcut("Desktop", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"), exePath, installDir);
            }
            else
            {
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"));
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ForgeFlow.lnk"));
            }
            string startMenuAppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName);
            CreateShortcut("Start menu", Path.Combine(startMenuAppDir, AppName + ".lnk"), exePath, installDir);

            progress.SetStatus("Registering application...");
            Application.DoEvents();
            string uninstallScript = WriteUninstallScript(installDir);
            CleanupLegacyInstalledApps();
            RegisterInstalledApplication(installDir, exePath, uninstallScript);

            progress.SetStatus("Starting ForgeFlow...");
            Application.DoEvents();
            if (launchAfterInstall)
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }

            progress.Close();
            MessageBox.Show("ForgeFlow Lite has been installed successfully.", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            LogSetupError(ex);
            MessageBox.Show("Could not install ForgeFlow Lite.\n\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Environment.ExitCode = 1;
        }
    }

    private static void LogSetupError(Exception exception)
    {
        try
        {
            string logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ForgeFlow",
                "Logs");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(
                Path.Combine(logDir, "setup.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\r\n\r\n");
        }
        catch
        {
        }
    }

    private static string GetDefaultInstallDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "ForgeFlow Lite");
    }

    private static ExistingInstall? FindExistingInstall(int? preferredProcessId)
    {
        ExistingInstall? processInstall = FindRunningForgeFlow(preferredProcessId);
        if (processInstall != null)
            return processInstall;

        string[] shortcutPaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "ForgeFlow.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "ForgeFlow.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName, AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "ForgeFlow.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", AppName, AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", AppName + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "ForgeFlow.lnk")
        };

        foreach (string shortcutPath in shortcutPaths)
        {
            string? target = TryReadShortcutTarget(shortcutPath);
            if (IsForgeFlowExecutable(target))
                return new ExistingInstall(Path.GetDirectoryName(target!)!, null);
        }

        ExistingInstall? registryInstall = FindExistingInstallFromRegistry();
        if (registryInstall != null)
            return registryInstall;

        string[] knownPaths =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ForgeFlow Lite", "ForgeFlow.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ForgeFlow", "ForgeFlow.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ForgeFlow Lite", "ForgeFlow.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ForgeFlow", "ForgeFlow.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ForgeFlow Lite", "ForgeFlow.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ForgeFlow", "ForgeFlow.exe")
        };

        foreach (string path in knownPaths)
            if (IsForgeFlowExecutable(path))
                return new ExistingInstall(Path.GetDirectoryName(path)!, null);

        return null;
    }

    private static ExistingInstall? FindExistingInstallFromRegistry()
    {
        string[] subKeys =
        {
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ForgeFlow Lite",
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ForgeFlow"
        };

        foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            foreach (string subKey in subKeys)
            {
                try
                {
                    using RegistryKey? key = root.OpenSubKey(subKey);
                    string? installLocation = key?.GetValue("InstallLocation")?.ToString();
                    if (!string.IsNullOrWhiteSpace(installLocation) &&
                        IsForgeFlowExecutable(Path.Combine(installLocation, "ForgeFlow.exe")))
                    {
                        return new ExistingInstall(installLocation, null);
                    }

                    string? displayIcon = key?.GetValue("DisplayIcon")?.ToString();
                    if (IsForgeFlowExecutable(displayIcon))
                        return new ExistingInstall(Path.GetDirectoryName(displayIcon!)!, null);
                }
                catch
                {
                }
            }

            ExistingInstall? scannedInstall = FindExistingInstallFromRegistryRoot(root);
            if (scannedInstall != null)
                return scannedInstall;
        }

        return null;
    }

    private static ExistingInstall? FindExistingInstallFromRegistryRoot(RegistryKey root)
    {
        try
        {
            using RegistryKey? uninstallRoot = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstallRoot == null)
                return null;

            foreach (string subKeyName in uninstallRoot.GetSubKeyNames())
            {
                try
                {
                    using RegistryKey? key = uninstallRoot.OpenSubKey(subKeyName);
                    string displayName = key?.GetValue("DisplayName")?.ToString() ?? "";
                    if (!displayName.Contains("ForgeFlow", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? installLocation = key?.GetValue("InstallLocation")?.ToString();
                    if (!string.IsNullOrWhiteSpace(installLocation) &&
                        IsForgeFlowExecutable(Path.Combine(installLocation, "ForgeFlow.exe")))
                    {
                        return new ExistingInstall(installLocation, null);
                    }

                    string? displayIcon = key?.GetValue("DisplayIcon")?.ToString();
                    if (IsForgeFlowExecutable(displayIcon))
                        return new ExistingInstall(Path.GetDirectoryName(displayIcon!)!, null);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static ExistingInstall? FindRunningForgeFlow(int? preferredProcessId)
    {
        if (preferredProcessId.HasValue)
        {
            ExistingInstall? preferred = TryGetInstallFromProcess(preferredProcessId.Value);
            if (preferred != null)
                return preferred;
        }

        foreach (Process process in Process.GetProcessesByName("ForgeFlow"))
        {
            using (process)
            {
                ExistingInstall? install = TryGetInstallFromProcess(process.Id);
                if (install != null)
                    return install;
            }
        }

        return null;
    }

    private static ExistingInstall? TryGetInstallFromProcess(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            string? exePath = process.MainModule?.FileName;
            if (IsForgeFlowExecutable(exePath))
                return new ExistingInstall(Path.GetDirectoryName(exePath!)!, processId);
        }
        catch
        {
        }

        return null;
    }

    private static bool IsForgeFlowExecutable(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               Path.GetFileName(path).Equals("ForgeFlow.exe", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(path);
    }

    private static string? TryReadShortcutTarget(string shortcutPath)
    {
        if (!File.Exists(shortcutPath))
            return null;

        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                return null;

            object? shell = Activator.CreateInstance(shellType);
            if (shell == null)
                return null;

            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;
            string? target = shortcut.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, shortcut, null)?.ToString();
            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static void CloseRunningForgeFlowProcesses(string installDir)
    {
        string normalizedInstallDir = NormalizeDirectory(installDir);
        foreach (Process process in Process.GetProcessesByName("ForgeFlow"))
        {
            using (process)
            {
                bool shouldClose = false;
                try
                {
                    string? exePath = process.MainModule?.FileName;
                    shouldClose = IsForgeFlowExecutable(exePath) &&
                                  NormalizeDirectory(Path.GetDirectoryName(exePath!)!).Equals(normalizedInstallDir, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    shouldClose = true;
                }

                if (shouldClose)
                    CloseForgeFlowProcess(process.Id, forceIfNeeded: true);
            }
        }
    }

    private static void CloseAllForgeFlowProcesses()
    {
        foreach (Process process in Process.GetProcessesByName("ForgeFlow"))
        {
            using (process)
            {
                try
                {
                    if (IsForgeFlowExecutable(process.MainModule?.FileName))
                        CloseForgeFlowProcess(process.Id, forceIfNeeded: true);
                }
                catch
                {
                    CloseForgeFlowProcess(process.Id, forceIfNeeded: true);
                }
            }
        }
    }

    private static void CloseForgeFlowProcess(int processId, bool forceIfNeeded)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            if (process.HasExited)
                return;

            try { process.CloseMainWindow(); } catch { }
            if (process.WaitForExit(8000))
                return;

            if (!forceIfNeeded)
                return;

            try { process.Kill(entireProcessTree: true); } catch { }
            try { process.WaitForExit(15000); } catch { }
        }
        catch
        {
        }
    }

    private static string CreateTempInstallDirectory(string installDir)
    {
        string fullInstallDir = Path.GetFullPath(installDir);
        string? parent = Path.GetDirectoryName(fullInstallDir);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("The install directory is not valid.");

        Directory.CreateDirectory(parent);
        CleanupOldInstallerWorkDirectories(parent);
        string tempInstallDir = Path.Combine(parent, ".ForgeFlow_Install_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempInstallDir);
        return tempInstallDir;
    }

    private static void CleanupOldInstallerWorkDirectories(string parent)
    {
        try
        {
            foreach (string directory in Directory.GetDirectories(parent, ".ForgeFlow_Install_*")
                         .Concat(Directory.GetDirectories(parent, ".ForgeFlow_Backup_*")))
            {
                var info = new DirectoryInfo(directory);
                if (info.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-6))
                    TryDeleteDirectory(directory);
            }
        }
        catch
        {
        }
    }

    private static void ValidateInstallDirectory(string installDir)
    {
        string fullInstallDir = Path.GetFullPath(installDir);
        string localAppData = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        string programFiles = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        string programFilesX86 = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        bool allowed =
            File.Exists(Path.Combine(fullInstallDir, "ForgeFlow.exe")) ||
            IsSameOrChildPath(fullInstallDir, localAppData) ||
            IsSameOrChildPath(fullInstallDir, programFiles) ||
            IsSameOrChildPath(fullInstallDir, programFilesX86);

        if (!allowed)
            throw new InvalidOperationException("The requested install directory is not trusted: " + fullInstallDir);
    }

    private static string InstallStagedPayload(string tempInstallDir, string requestedInstallDir, InstallProgressForm progress)
    {
        try
        {
            progress.SetStatus("Removing previous install...");
            Application.DoEvents();
            CloseRunningForgeFlowProcesses(requestedInstallDir);
            CloseAllForgeFlowProcesses();
            SwapInstallDirectory(tempInstallDir, requestedInstallDir);
            return requestedInstallDir;
        }
        catch (Exception ex)
        {
            LogSetupError(new InvalidOperationException(
                "Primary install directory is locked. Setup will not create a second copy.",
                ex));

            progress.SetStatus("Previous install is locked.");
            Application.DoEvents();

            TryDeleteDirectory(tempInstallDir);
            throw new IOException(
                "Could not replace the existing ForgeFlow Lite installation because Windows is still locking one of its files. " +
                "Setup already tried to close ForgeFlow and clean the old install. Restart Windows, then run setup again.",
                ex);
        }
    }

    private static void SwapInstallDirectory(string tempInstallDir, string installDir)
    {
        string fullInstallDir = Path.GetFullPath(installDir);
        string? parent = Path.GetDirectoryName(fullInstallDir);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("The install directory is not valid.");

        string backupDir = Path.Combine(parent, ".ForgeFlow_Backup_" + Guid.NewGuid().ToString("N"));
        bool backupCreated = false;

        try
        {
            if (Directory.Exists(fullInstallDir))
            {
                CloseRunningForgeFlowProcesses(fullInstallDir);
                CloseAllForgeFlowProcesses();
                MoveDirectoryWithRetry(fullInstallDir, backupDir);
                backupCreated = true;
            }

            MoveDirectoryWithRetry(tempInstallDir, fullInstallDir);

            if (backupCreated)
                DeleteDirectoryWithRetry(backupDir);
        }
        catch
        {
            TryDeleteDirectory(fullInstallDir);
            if (backupCreated && Directory.Exists(backupDir) && !Directory.Exists(fullInstallDir))
            {
                try { MoveDirectoryWithRetry(backupDir, fullInstallDir); } catch { }
            }

            throw;
        }
    }

    private static string MoveStagedPayloadToFallbackDirectory(string tempInstallDir, string requestedInstallDir)
    {
        string fullInstallDir = Path.GetFullPath(requestedInstallDir);
        string? parent = Path.GetDirectoryName(fullInstallDir);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidOperationException("The install directory is not valid.");

        Directory.CreateDirectory(parent);

        for (int attempt = 0; attempt < 6; attempt++)
        {
            string suffix = attempt == 0 ? "Current" : "Current " + (attempt + 1).ToString();
            string candidate = Path.Combine(parent, "ForgeFlow Lite " + suffix);

            try
            {
                ValidateInstallDirectory(candidate);
                if (Directory.Exists(candidate))
                    TryDeleteDirectory(candidate);

                if (Directory.Exists(candidate))
                    continue;

                MoveDirectoryWithRetry(tempInstallDir, candidate);
                return candidate;
            }
            catch (Exception ex) when (attempt < 5)
            {
                LogSetupError(new InvalidOperationException("Could not use fallback install directory: " + candidate, ex));
            }
        }

        throw new IOException("Could not install ForgeFlow Lite because both the current and fallback install folders are locked.");
    }

    private static void WriteDeferredCleanupNote(string installDir, string lockedInstallDir)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(installDir, "pending-cleanup.txt"),
                "The previous ForgeFlow Lite install folder was locked during setup and can be removed later:\r\n" +
                lockedInstallDir + "\r\n");

            RegisterDeferredCleanup(installDir, lockedInstallDir);
        }
        catch
        {
        }
    }

    private static void RegisterDeferredCleanup(string installDir, string lockedInstallDir)
    {
        if (NormalizeDirectory(installDir).Equals(NormalizeDirectory(lockedInstallDir), StringComparison.OrdinalIgnoreCase))
            return;

        if (!IsSafeDeferredCleanupDirectory(lockedInstallDir))
            return;

        string cleanupScript = Path.Combine(installDir, "Cleanup-Previous-ForgeFlow.ps1");
        string script =
            "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
            "$lockedDir = " + ToPowerShellString(lockedInstallDir) + "\r\n" +
            "$currentDir = " + ToPowerShellString(installDir) + "\r\n" +
            "Start-Sleep -Seconds 8\r\n" +
            "if ((Test-Path -LiteralPath $currentDir) -and (Test-Path -LiteralPath $lockedDir) -and ($lockedDir -ne $currentDir)) {\r\n" +
            "  Remove-Item -LiteralPath $lockedDir -Recurse -Force\r\n" +
            "}\r\n" +
            "Remove-Item -LiteralPath $PSCommandPath -Force\r\n";

        File.WriteAllText(cleanupScript, script);

        try
        {
            using RegistryKey runOnce = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
            runOnce.SetValue("ForgeFlow Lite Cleanup", "powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + Quote(cleanupScript), RegistryValueKind.String);
        }
        catch
        {
        }
    }

    private static void MoveDirectoryWithRetry(string source, string destination)
    {
        const int attempts = 18;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch when (attempt < attempts)
            {
                CloseAllForgeFlowProcesses();
                Thread.Sleep(1000);
            }
        }

        Directory.Move(source, destination);
    }

    private static void DeleteDirectoryWithRetry(string directory)
    {
        const int attempts = 18;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory))
                    return;

                Directory.Delete(directory, recursive: true);
                return;
            }
            catch when (attempt < attempts)
            {
                CloseAllForgeFlowProcesses();
                Thread.Sleep(1000);
            }
        }

        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    private static void CleanupLegacyInstalls(string currentInstallDir)
    {
        string current = NormalizeDirectory(currentInstallDir);
        foreach (string legacyExe in GetKnownForgeFlowExecutablePaths())
        {
            if (!File.Exists(legacyExe))
                continue;

            string? legacyDir = Path.GetDirectoryName(legacyExe);
            if (string.IsNullOrWhiteSpace(legacyDir))
                continue;

            if (NormalizeDirectory(legacyDir).Equals(current, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsSafeForgeFlowInstallDirectory(legacyDir))
                continue;

            try
            {
                CloseRunningForgeFlowProcesses(legacyDir);
                DeleteDirectoryWithRetry(legacyDir);
            }
            catch
            {
            }
        }

        TryRemoveEmptyDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ForgeFlow"));
        TryRemoveEmptyDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ForgeFlow Lite"));
    }

    private static IEnumerable<string> GetKnownForgeFlowExecutablePaths()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ForgeFlow Lite", "ForgeFlow.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ForgeFlow", "ForgeFlow.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ForgeFlow Lite", "ForgeFlow.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ForgeFlow", "ForgeFlow.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ForgeFlow Lite", "ForgeFlow.exe");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "ForgeFlow", "ForgeFlow.exe");
    }

    private static bool IsSafeForgeFlowInstallDirectory(string directory)
    {
        string full = NormalizeDirectory(directory);
        string name = Path.GetFileName(full);
        if (!name.Equals("ForgeFlow", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("ForgeFlow Lite", StringComparison.OrdinalIgnoreCase))
            return false;

        return File.Exists(Path.Combine(full, "ForgeFlow.exe"));
    }

    private static bool IsSafeDeferredCleanupDirectory(string directory)
    {
        string full = NormalizeDirectory(directory);
        string name = Path.GetFileName(full);
        if (!name.Equals("ForgeFlow", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("ForgeFlow Lite", StringComparison.OrdinalIgnoreCase))
            return false;

        if (File.Exists(Path.Combine(full, "ForgeFlow.exe")))
            return true;

        string localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        return IsSameOrChildPath(full, localPrograms) ||
               IsSameOrChildPath(full, programFiles) ||
               IsSameOrChildPath(full, programFilesX86);
    }

    private static string NormalizeDirectory(string directory)
    {
        return Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void TryRemoveEmptyDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }

    private static bool IsSameOrChildPath(string path, string parent)
    {
        if (string.IsNullOrWhiteSpace(parent))
            return false;

        string normalizedParent = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static void ExtractPayloadResource(string outputPath)
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream? resource = assembly.GetManifestResourceStream("payload.zip");
        if (resource == null)
            throw new InvalidOperationException("Installer payload was not embedded.");

        using FileStream output = File.Create(outputPath);
        resource.CopyTo(output);
    }

    private static void CreateShortcut(string label, string shortcutPath, string exePath, string workingDirectory)
    {
        try
        {
            string? directory = Path.GetDirectoryName(shortcutPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null)
                throw new InvalidOperationException("Windows shortcut service is not available.");

            object? shell = Activator.CreateInstance(shellType);
            if (shell == null)
                throw new InvalidOperationException("Could not initialize Windows shortcut service.");

            object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath })!;
            Type shortcutType = shortcut.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { exePath + ",0" });
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { AppName });
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);

            Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);

            if (!File.Exists(shortcutPath))
                throw new IOException("Shortcut was not created: " + shortcutPath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create {label} shortcut. {ex.Message}", ex);
        }
    }

    private static void CleanupLegacyShortcuts()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        string startMenuPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        string commonStartMenuPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        string startMenuAppDir = Path.Combine(startMenuPrograms, AppName);
        string commonStartMenuAppDir = Path.Combine(commonStartMenuPrograms, AppName);

        string[] exactShortcutPaths =
        {
            Path.Combine(desktop, AppName + ".lnk"),
            Path.Combine(desktop, "ForgeFlow.lnk"),
            Path.Combine(commonDesktop, AppName + ".lnk"),
            Path.Combine(commonDesktop, "ForgeFlow.lnk"),
            Path.Combine(startMenuPrograms, AppName + ".lnk"),
            Path.Combine(startMenuPrograms, "ForgeFlow.lnk"),
            Path.Combine(startMenuAppDir, AppName + ".lnk"),
            Path.Combine(startMenuAppDir, "ForgeFlow.lnk"),
            Path.Combine(commonStartMenuPrograms, AppName + ".lnk"),
            Path.Combine(commonStartMenuPrograms, "ForgeFlow.lnk"),
            Path.Combine(commonStartMenuAppDir, AppName + ".lnk"),
            Path.Combine(commonStartMenuAppDir, "ForgeFlow.lnk")
        };

        foreach (string shortcutPath in exactShortcutPaths)
            TryDelete(shortcutPath);

        foreach (string directory in new[] { desktop, commonDesktop, startMenuPrograms, commonStartMenuPrograms, startMenuAppDir, commonStartMenuAppDir })
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (string shortcutPath in Directory.GetFiles(directory, "*.lnk"))
            {
                string name = Path.GetFileNameWithoutExtension(shortcutPath);
                if (name.Contains("ForgeFlow", StringComparison.OrdinalIgnoreCase))
                    TryDelete(shortcutPath);
            }
        }

        TryRemoveEmptyDirectory(startMenuAppDir);
        TryRemoveEmptyDirectory(commonStartMenuAppDir);
    }

    private static string WriteUninstallScript(string installDir)
    {
        string uninstallScript = Path.Combine(installDir, "Uninstall-ForgeFlow.ps1");
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        string startMenuPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs");
        string commonStartMenuPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs");
        string startMenuAppDir = Path.Combine(startMenuPrograms, AppName);
        string commonStartMenuAppDir = Path.Combine(commonStartMenuPrograms, AppName);

        string script =
            "$ErrorActionPreference = 'SilentlyContinue'\r\n" +
            "$installDir = " + ToPowerShellString(installDir) + "\r\n" +
            "$shortcutPaths = @(\r\n" +
            "  " + ToPowerShellString(Path.Combine(desktop, AppName + ".lnk")) + ",\r\n" +
            "  " + ToPowerShellString(Path.Combine(desktop, "ForgeFlow.lnk")) + ",\r\n" +
            "  " + ToPowerShellString(Path.Combine(commonDesktop, AppName + ".lnk")) + ",\r\n" +
            "  " + ToPowerShellString(Path.Combine(commonDesktop, "ForgeFlow.lnk")) + ",\r\n" +
            "  " + ToPowerShellString(Path.Combine(startMenuAppDir, AppName + ".lnk")) + ",\r\n" +
            "  " + ToPowerShellString(Path.Combine(startMenuPrograms, AppName + ".lnk")) + ",\r\n" +
            "  " + ToPowerShellString(Path.Combine(startMenuPrograms, "ForgeFlow.lnk")) + ",\r\n" +
            "  " + ToPowerShellString(Path.Combine(commonStartMenuAppDir, AppName + ".lnk")) + ",\r\n" +
            "  " + ToPowerShellString(Path.Combine(commonStartMenuPrograms, AppName + ".lnk")) + ",\r\n" +
            "  " + ToPowerShellString(Path.Combine(commonStartMenuPrograms, "ForgeFlow.lnk")) + "\r\n" +
            ")\r\n" +
            "Get-Process -Name ForgeFlow -ErrorAction SilentlyContinue | Stop-Process -Force\r\n" +
            "foreach ($shortcut in $shortcutPaths) { if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force } }\r\n" +
            "if (Test-Path -LiteralPath " + ToPowerShellString(startMenuAppDir) + ") { Remove-Item -LiteralPath " + ToPowerShellString(startMenuAppDir) + " -Recurse -Force }\r\n" +
            "if (Test-Path -LiteralPath " + ToPowerShellString(commonStartMenuAppDir) + ") { Remove-Item -LiteralPath " + ToPowerShellString(commonStartMenuAppDir) + " -Recurse -Force }\r\n" +
            "Remove-Item -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ForgeFlow Lite' -Recurse -Force\r\n" +
            "Remove-Item -Path 'HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ForgeFlow' -Recurse -Force\r\n" +
            "Remove-Item -Path 'HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ForgeFlow Lite' -Recurse -Force\r\n" +
            "Remove-Item -Path 'HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\ForgeFlow' -Recurse -Force\r\n" +
            "Start-Process -FilePath 'cmd.exe' -WindowStyle Hidden -ArgumentList '/c timeout /t 2 /nobreak >nul & rmdir /s /q \"' + $installDir + '\"'\r\n";

        File.WriteAllText(uninstallScript, script);
        return uninstallScript;
    }

    private static void RegisterInstalledApplication(string installDir, string exePath, string uninstallScript)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath);
        WriteUninstallRegistryValues(key, installDir, exePath, uninstallScript);

        try
        {
            using RegistryKey machineKey = Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ForgeFlow Lite");
            WriteUninstallRegistryValues(machineKey, installDir, exePath, uninstallScript);
        }
        catch
        {
        }
    }

    private static void CleanupLegacyInstalledApps()
    {
        string[] subKeys =
        {
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ForgeFlow",
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\ForgeFlow Lite"
        };

        foreach (string subKey in subKeys)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); } catch { }
            try { Registry.LocalMachine.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); } catch { }
        }
    }

    private static void WriteUninstallRegistryValues(RegistryKey key, string installDir, string exePath, string uninstallScript)
    {
        key.SetValue("DisplayName", AppName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", GetDisplayVersion(), RegistryValueKind.String);
        key.SetValue("Publisher", Publisher, RegistryValueKind.String);
        key.SetValue("InstallLocation", installDir, RegistryValueKind.String);
        key.SetValue("DisplayIcon", exePath, RegistryValueKind.String);
        key.SetValue("UninstallString", "powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + Quote(uninstallScript), RegistryValueKind.String);
        key.SetValue("QuietUninstallString", "powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + Quote(uninstallScript), RegistryValueKind.String);
        key.SetValue("URLInfoAbout", "https://4tiko.net", RegistryValueKind.String);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", Math.Max(1, GetDirectorySizeKb(installDir)), RegistryValueKind.DWord);
    }

    private static string GetDisplayVersion()
    {
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        return version == null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private static int GetDirectorySizeKb(string directory)
    {
        try
        {
            long bytes = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            return (int)Math.Min(int.MaxValue, Math.Max(1, bytes / 1024));
        }
        catch
        {
            return 1;
        }
    }

    private static string ToPowerShellString(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}

internal sealed class InstallProgressForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(10, 10, 10);
    private static readonly Color PanelColor = Color.FromArgb(28, 28, 30);
    private static readonly Color ForegroundColor = Color.FromArgb(245, 245, 247);
    private static readonly Color MutedColor = Color.FromArgb(160, 160, 166);

    private readonly Label _statusLabel = new();
    private readonly ProgressBar _progressBar = new();

    public InstallProgressForm()
    {
        Text = "ForgeFlow Lite Setup";
        Width = 500;
        Height = 190;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BackgroundColor;
        ForeColor = ForegroundColor;
        Font = new Font("Segoe UI", 9F);
        Icon = InstallerUi.TryLoadAppIcon();

        var logo = new PictureBox
        {
            Left = 22,
            Top = 22,
            Width = 42,
            Height = 42,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = InstallerUi.TryLoadAppIcon()?.ToBitmap()
        };

        var title = new Label
        {
            Left = 78,
            Top = 20,
            Width = 360,
            Height = 26,
            Text = "Installing ForgeFlow Lite",
            ForeColor = ForegroundColor,
            Font = new Font("Segoe UI", 13F, FontStyle.Bold)
        };

        _statusLabel.Left = 78;
        _statusLabel.Top = 50;
        _statusLabel.Width = 360;
        _statusLabel.Height = 24;
        _statusLabel.Text = "Installing...";
        _statusLabel.ForeColor = MutedColor;

        _progressBar.Left = 22;
        _progressBar.Top = 104;
        _progressBar.Width = 438;
        _progressBar.Height = 22;
        _progressBar.Style = ProgressBarStyle.Marquee;

        var panel = new Panel
        {
            Left = 18,
            Top = 94,
            Width = 446,
            Height = 42,
            BackColor = PanelColor
        };

        Controls.Add(logo);
        Controls.Add(title);
        Controls.Add(_statusLabel);
        Controls.Add(panel);
        Controls.Add(_progressBar);
        _progressBar.BringToFront();
    }

    public void SetStatus(string status)
    {
        _statusLabel.Text = status;
    }
}

internal sealed class InstallOptionsForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(10, 10, 10);
    private static readonly Color PanelColor = Color.FromArgb(28, 28, 30);
    private static readonly Color BorderColor = Color.FromArgb(58, 58, 60);
    private static readonly Color ForegroundColor = Color.FromArgb(245, 245, 247);
    private static readonly Color MutedColor = Color.FromArgb(160, 160, 166);
    private static readonly Color AccentColor = Color.FromArgb(10, 132, 255);

    private readonly TextBox _installDirBox = new();
    private readonly CheckBox _desktopShortcutBox = new();
    private readonly CheckBox _launchBox = new();

    public string InstallDir => _installDirBox.Text.Trim();
    public bool CreateDesktopShortcut => _desktopShortcutBox.Checked;
    public bool LaunchAfterInstall => _launchBox.Checked;

    public InstallOptionsForm(string installDir, bool launchAfterInstall)
    {
        Text = "ForgeFlow Lite Setup";
        Width = 620;
        Height = 390;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BackgroundColor;
        ForeColor = ForegroundColor;
        Font = new Font("Segoe UI", 9F);
        Icon = InstallerUi.TryLoadAppIcon();

        var logo = new PictureBox
        {
            Left = 26,
            Top = 24,
            Width = 72,
            Height = 54,
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = InstallerUi.TryLoadAppIcon()?.ToBitmap()
        };

        var title = new Label
        {
            Left = 116,
            Top = 24,
            Width = 430,
            Height = 32,
            Text = "Install ForgeFlow Lite",
            ForeColor = ForegroundColor,
            Font = new Font("Segoe UI", 18F, FontStyle.Bold)
        };

        var subtitle = new Label
        {
            Left = 118,
            Top = 60,
            Width = 440,
            Height = 38,
            Text = "Video 60FPS + TikTok30FPS setup",
            ForeColor = MutedColor
        };

        var card = new Panel
        {
            Left = 24,
            Top = 112,
            Width = 548,
            Height = 160,
            BackColor = PanelColor
        };

        var installLabel = new Label
        {
            Left = 42,
            Top = 130,
            Width = 130,
            Height = 20,
            Text = "Install location",
            ForeColor = ForegroundColor
        };

        _installDirBox.Left = 42;
        _installDirBox.Top = 154;
        _installDirBox.Width = 405;
        _installDirBox.Height = 28;
        _installDirBox.Text = installDir;
        _installDirBox.BackColor = Color.FromArgb(22, 22, 24);
        _installDirBox.ForeColor = ForegroundColor;
        _installDirBox.BorderStyle = BorderStyle.FixedSingle;

        var browseButton = CreateButton("Browse...", primary: false);
        browseButton.SetBounds(458, 152, 86, 32);
        browseButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Choose where ForgeFlow Lite will be installed",
                SelectedPath = Directory.Exists(_installDirBox.Text)
                    ? _installDirBox.Text
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "ForgeFlow Lite"),
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog(this) == DialogResult.OK)
                _installDirBox.Text = dialog.SelectedPath;
        };

        _desktopShortcutBox.Left = 42;
        _desktopShortcutBox.Top = 202;
        _desktopShortcutBox.Width = 270;
        _desktopShortcutBox.Height = 24;
        _desktopShortcutBox.Text = "Create a desktop shortcut";
        _desktopShortcutBox.Checked = true;
        _desktopShortcutBox.ForeColor = ForegroundColor;
        _desktopShortcutBox.BackColor = PanelColor;

        _launchBox.Left = 42;
        _launchBox.Top = 230;
        _launchBox.Width = 270;
        _launchBox.Height = 24;
        _launchBox.Text = "Launch ForgeFlow after setup";
        _launchBox.Checked = launchAfterInstall;
        _launchBox.ForeColor = ForegroundColor;
        _launchBox.BackColor = PanelColor;

        var hint = new Label
        {
            Left = 42,
            Top = 286,
            Width = 330,
            Height = 38,
            Text = "ForgeFlow will also be registered in Windows Installed Apps.",
            ForeColor = MutedColor
        };

        var installButton = CreateButton("Install", primary: true);
        installButton.SetBounds(378, 296, 92, 36);
        installButton.DialogResult = DialogResult.OK;

        var cancelButton = CreateButton("Cancel", primary: false);
        cancelButton.SetBounds(480, 296, 92, 36);
        cancelButton.DialogResult = DialogResult.Cancel;

        AcceptButton = installButton;
        CancelButton = cancelButton;
        Controls.Add(card);
        Controls.Add(logo);
        Controls.Add(title);
        Controls.Add(subtitle);
        Controls.Add(installLabel);
        Controls.Add(_installDirBox);
        Controls.Add(browseButton);
        Controls.Add(_desktopShortcutBox);
        Controls.Add(_launchBox);
        Controls.Add(hint);
        Controls.Add(installButton);
        Controls.Add(cancelButton);

        foreach (Control control in Controls)
            if (control != card)
                control.BringToFront();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (DialogResult == DialogResult.OK && string.IsNullOrWhiteSpace(_installDirBox.Text))
        {
            MessageBox.Show(this, "Choose an install location first.", "ForgeFlow Lite Setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    private static Button CreateButton(string text, bool primary)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? AccentColor : Color.FromArgb(44, 44, 46),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI", 9F, FontStyle.Bold)
        };
        button.FlatAppearance.BorderColor = primary ? AccentColor : BorderColor;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }
}

internal static class InstallerUi
{
    public static Icon? TryLoadAppIcon()
    {
        try
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "ForgeFlow.ico");
            return File.Exists(iconPath) ? new Icon(iconPath) : null;
        }
        catch
        {
            return null;
        }
    }
}

internal sealed class InstallerOptions
{
    public string? InstallDir { get; private init; }
    public int? WaitPid { get; private init; }
    public bool LaunchAfterInstall { get; private init; } = true;

    public static InstallerOptions Parse(string[] args)
    {
        string? installDir = null;
        int? waitPid = null;
        bool launch = true;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.Equals("--install-dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                installDir = args[++i].Trim();
                continue;
            }

            if (arg.Equals("--wait-pid", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int parsedPid) && parsedPid > 0)
                    waitPid = parsedPid;
                continue;
            }

            if (arg.Equals("--no-launch", StringComparison.OrdinalIgnoreCase))
                launch = false;
        }

        return new InstallerOptions
        {
            InstallDir = string.IsNullOrWhiteSpace(installDir) ? null : installDir,
            WaitPid = waitPid,
            LaunchAfterInstall = launch
        };
    }
}

internal sealed record ExistingInstall(string InstallDir, int? ProcessId);
