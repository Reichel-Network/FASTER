using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace FASTER.Models
{
    /// <summary>
    /// Shared helpers for resolving the Arma 3 dedicated-server binary and building a reliable
    /// <see cref="ProcessStartInfo"/> (working directory, args, validation).
    /// </summary>
    public static class ServerLaunch
    {
        public static readonly string[] CandidateFileNames =
        {
            "arma3server_x64.exe",
            "arma3serverprofiling_x64.exe",
            "arma3server.exe",
            "arma3serverprofiling.exe"
        };

        public sealed class ValidationResult
        {
            public bool Ok { get; init; }
            public string Message { get; init; } = string.Empty;
            public bool IsWarning { get; init; }

            public static ValidationResult Success() => new() { Ok = true };
            public static ValidationResult Fail(string message) => new() { Ok = false, Message = message };
            public static ValidationResult Warn(string message) => new() { Ok = true, IsWarning = true, Message = message };
        }

        /// <summary>
        /// Prefer an existing profile executable when it is a valid arma3server*.exe;
        /// otherwise locate the best dedicated-server binary under <paramref name="serverPath"/>
        /// or the preferred file's directory.
        /// </summary>
        public static string? ResolveExecutable(string? preferredExecutable, string? serverPath)
        {
            if (IsValidServerExecutable(preferredExecutable) && File.Exists(preferredExecutable))
                return Path.GetFullPath(preferredExecutable!);

            foreach (var root in DistinctExistingDirs(serverPath, Path.GetDirectoryName(preferredExecutable)))
            {
                foreach (var name in CandidateFileNames)
                {
                    var candidate = Path.Combine(root, name);
                    if (File.Exists(candidate))
                        return Path.GetFullPath(candidate);
                }
            }

            return null;
        }

        public static bool IsValidServerExecutable(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return false;

            var fileName = Path.GetFileName(path);
            return fileName.StartsWith("arma3server", StringComparison.OrdinalIgnoreCase);
        }

        public static ProcessStartInfo CreateStartInfo(string executable, string arguments, string? workingDirectory = null)
        {
            if (string.IsNullOrWhiteSpace(executable))
                throw new ArgumentException("Executable path is required.", nameof(executable));

            var workDir = !string.IsNullOrWhiteSpace(workingDirectory)
                ? workingDirectory
                : Path.GetDirectoryName(executable) ?? string.Empty;

            return new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = workDir,
                UseShellExecute = false
            };
        }

        public static ValidationResult ValidateForLaunch(ServerProfile profile, bool requireConfigFiles = true)
        {
            ArgumentNullException.ThrowIfNull(profile);

            if (string.IsNullOrWhiteSpace(profile.Executable) || !IsValidServerExecutable(profile.Executable))
            {
                return ValidationResult.Fail(
                    "No valid Arma 3 dedicated server executable selected. Expected arma3server_x64.exe (or arma3server.exe) under the server install path.");
            }

            if (!File.Exists(profile.Executable))
            {
                return ValidationResult.Fail(
                    $"Arma 3 server executable not found:\n{profile.Executable}\nReselect Server File or run Steam Updater into the correct serverPath.");
            }

            var armaPath = profile.ArmaPath;
            if (string.IsNullOrWhiteSpace(armaPath) || !Directory.Exists(armaPath))
            {
                return ValidationResult.Fail(
                    $"Arma server directory does not exist:\n{armaPath}");
            }

            if (requireConfigFiles)
            {
                var profileDir = Path.Combine(armaPath, "Servers", profile.Id);
                var config = Path.Combine(profileDir, "server_config.cfg");
                var basic = Path.Combine(profileDir, "server_basic.cfg");
                if (!Directory.Exists(profileDir) || !File.Exists(config) || !File.Exists(basic))
                {
                    return ValidationResult.Fail(
                        "The profile does not exist in the game files.\nSave the profile first (writes server_config.cfg / server_basic.cfg).");
                }
            }

            return ValidationResult.Success();
        }

        public static int CountSelectedLaunchMods(ServerProfile profile)
        {
            if (profile?.ProfileMods == null)
                return 0;
            return profile.ProfileMods.Count(m =>
                m.ClientSideChecked || m.ServerSideChecked || m.HeadlessChecked);
        }

        public static IReadOnlyList<string> MissingDeployedMods(ServerProfile profile)
        {
            var missing = new List<string>();
            if (profile?.ProfileMods == null || string.IsNullOrWhiteSpace(profile.ArmaPath))
                return missing;

            if (!Directory.Exists(profile.ArmaPath))
                return missing;

            HashSet<string> linkNames;
            try
            {
                linkNames = Directory.EnumerateDirectories(profile.ArmaPath)
                    .Select(d => new DirectoryInfo(d))
                    .Where(d => d.Attributes.HasFlag(FileAttributes.ReparsePoint) || Directory.Exists(d.FullName))
                    .Select(d => d.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return missing;
            }

            foreach (var mod in profile.ProfileMods.Where(m =>
                         m.ServerSideChecked || m.ClientSideChecked || m.HeadlessChecked || m.OptChecked))
            {
                var folder = $"@{Functions.SafeName(mod.Name)}";
                if (!linkNames.Contains(folder))
                    missing.Add(folder);
            }

            return missing;
        }

        public static Process StartServer(string executable, string arguments, string workingDirectory)
        {
            var psi = CreateStartInfo(executable, arguments, workingDirectory);
            return Process.Start(psi)
                   ?? throw new InvalidOperationException($"Process.Start returned null for \"{executable}\".");
        }

        /// <summary>
        /// Running arma3server* processes whose command line matches this profile
        /// (<c>-profiles=...\Servers\{Id}</c>, HC folders <c>{Id}_hc*</c>, or <c>-port={Port}</c>).
        /// </summary>
        public static IReadOnlyList<ArmaProcessMatch> FindMatchingArmaProcesses(ServerProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            var matches = new List<ArmaProcessMatch>();
            foreach (var proc in EnumerateArmaServerProcesses())
            {
                if (CommandLineMatchesProfile(proc.CommandLine, profile))
                    matches.Add(proc);
            }

            return matches;
        }

        /// <summary>
        /// Kill matching arma3server* (+ HC) processes for the profile. Returns how many were signaled.
        /// </summary>
        public static IReadOnlyList<ArmaProcessMatch> StopMatchingArmaProcesses(ServerProfile profile)
        {
            var matched = FindMatchingArmaProcesses(profile);
            var stopped = new List<ArmaProcessMatch>();
            foreach (var match in matched)
            {
                try
                {
                    using var process = Process.GetProcessById(match.ProcessId);
                    process.Kill(entireProcessTree: true);
                    stopped.Add(match);
                }
                catch (ArgumentException)
                {
                    // Already exited.
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }
            }

            return stopped;
        }

        public static bool CommandLineMatchesProfile(string? commandLine, ServerProfile profile)
        {
            if (string.IsNullOrWhiteSpace(commandLine) || profile == null)
                return false;
            if (string.IsNullOrWhiteSpace(profile.Id))
                return false;

            // Dedicated: ...\Servers\{Id}  HC: ...\Servers\{Id}_hcN
            if (commandLine.Contains($"Servers\\{profile.Id}", StringComparison.OrdinalIgnoreCase)
                || commandLine.Contains($"Servers/{profile.Id}", StringComparison.OrdinalIgnoreCase))
                return true;

            var port = profile.Port <= 0 ? 2302 : profile.Port;
            if (commandLine.Contains($"-port={port}", StringComparison.OrdinalIgnoreCase)
                || commandLine.Contains($"-port {port}", StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }

        public sealed class ArmaProcessMatch
        {
            public int ProcessId { get; init; }
            public string ProcessName { get; init; } = string.Empty;
            public string CommandLine { get; init; } = string.Empty;
        }

        private static IEnumerable<ArmaProcessMatch> EnumerateArmaServerProcesses()
        {
            // WMI is the reliable way to read command lines for already-running processes.
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name LIKE 'arma3server%'");
            foreach (ManagementObject obj in searcher.Get())
            {
                int pid;
                try { pid = Convert.ToInt32(obj["ProcessId"]); }
                catch { continue; }

                var name = obj["Name"]?.ToString() ?? string.Empty;
                var cmd = obj["CommandLine"]?.ToString() ?? string.Empty;
                yield return new ArmaProcessMatch
                {
                    ProcessId = pid,
                    ProcessName = name,
                    CommandLine = cmd
                };
            }
        }

        private static IEnumerable<string> DistinctExistingDirs(params string?[] paths)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path))
                    continue;
                string full;
                try { full = Path.GetFullPath(path); }
                catch { continue; }
                if (!Directory.Exists(full))
                    continue;
                if (seen.Add(full))
                    yield return full;
            }
        }
    }
}
