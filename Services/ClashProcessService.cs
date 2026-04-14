using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ClashXW.Services
{
    public class ClashProcessService : IDisposable
    {
        private Process? _clashProcess;
        private readonly string _executablePath;

        public ClashProcessService(string executablePath)
        {
            _executablePath = executablePath;
        }

        public void Start(string configPath, params string[] additionalSafePaths)
        {
            if (string.IsNullOrEmpty(_executablePath) || !File.Exists(_executablePath))
            {
                throw new FileNotFoundException($"Clash executable not found at: {_executablePath}");
            }

            EnsureExecutablePermissions();

            try
            {
                var assetsDir = Path.GetDirectoryName(_executablePath);
                var startInfo = new ProcessStartInfo
                {
                    FileName = _executablePath,
                    Arguments = $"-d \"{assetsDir}\" -f \"{configPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                // Add config directory to SAFE_PATHS so Clash accepts config files from there
                var existingSafePaths = Environment.GetEnvironmentVariable("SAFE_PATHS") ?? "";
                var safePaths = BuildSafePaths(existingSafePaths, new[] { ConfigManager.ConfigDir }.Concat(additionalSafePaths).ToArray());
                startInfo.Environment["SAFE_PATHS"] = safePaths;

                _clashProcess = new Process { StartInfo = startInfo };
                _clashProcess.Start();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to start Clash process: {ex.Message}", ex);
            }
        }

        public void Stop()
        {
            if (_clashProcess == null)
            {
                return;
            }

            try
            {
                if (_clashProcess.HasExited)
                {
                    return;
                }

                // Clash runs as a single tracked child process; avoiding tree enumeration
                // keeps shutdown fast and avoids noisy Win32 first-chance exceptions.
                _clashProcess.Kill();
                _clashProcess.WaitForExit(1000);
            }
            catch (InvalidOperationException)
            {
                // Process already exited between checks.
            }
            catch (Win32Exception)
            {
                // Process teardown raced with the debugger/OS; treat as already stopping.
            }
        }

        public bool IsRunning => _clashProcess != null && !_clashProcess.HasExited;

        public static string BuildSafePaths(string existingSafePaths, params string[] requiredPaths)
        {
            var values = new List<string>();

            if (!string.IsNullOrWhiteSpace(existingSafePaths))
            {
                values.AddRange(existingSafePaths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            values.AddRange(requiredPaths.Where(path => !string.IsNullOrWhiteSpace(path)));

            return string.Join(Path.PathSeparator, values.Distinct(StringComparer.OrdinalIgnoreCase));
        }

        public void Dispose()
        {
            Stop();
            _clashProcess?.Dispose();
        }

        private void EnsureExecutablePermissions()
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            try
            {
                const UnixFileMode mode = UnixFileMode.UserRead
                                          | UnixFileMode.UserWrite
                                          | UnixFileMode.UserExecute
                                          | UnixFileMode.GroupRead
                                          | UnixFileMode.GroupExecute
                                          | UnixFileMode.OtherRead
                                          | UnixFileMode.OtherExecute;

                File.SetUnixFileMode(_executablePath, mode);
            }
            catch
            {
                // Ignore permission update failures; process start will report a concrete error.
            }
        }
    }
}
