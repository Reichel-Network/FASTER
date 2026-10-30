using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// (A) Headless metadata-bridge adapter. Spawns
    /// <c>java -jar a3s-metadata-bridge-*-all.jar read --source … --kind … --out -</c>
    /// per companion <c>docs/BRIDGE.md</c>. Operators never configure a product JAR path;
    /// <c>Settings.a3sMetadataBridgePath</c> is Advanced-only (empty = bundled default).
    /// </summary>
    public sealed class BridgeA3SMetadataStore : IA3SMetadataStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        /// <summary>
        /// Optional override path to the headless metadata fat JAR (or a directory containing it).
        /// Empty means use the bundled path when present.
        /// </summary>
        public string BridgePath { get; }

        public BridgeA3SMetadataStore(string? bridgePath = null)
        {
            BridgePath = bridgePath ?? string.Empty;
        }

        /// <summary>Resolve <c>java</c> executable (JAVA_HOME then PATH).</summary>
        public static string? ResolveJavaExecutable()
        {
            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                var candidate = Path.Combine(javaHome.Trim(), "bin",
                    OperatingSystem.IsWindows() ? "java.exe" : "java");
                if (File.Exists(candidate))
                    return candidate;
            }

            return "java";
        }

        /// <summary>Resolve the metadata-bridge fat JAR path (override → bundled glob).</summary>
        public string ResolveBridgeJarPath()
        {
            if (!string.IsNullOrWhiteSpace(BridgePath))
            {
                var expanded = Environment.ExpandEnvironmentVariables(BridgePath.Trim());
                if (File.Exists(expanded) && expanded.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                    return expanded;
                if (Directory.Exists(expanded))
                {
                    var fromDir = FindBridgeJar(expanded);
                    if (fromDir != null)
                        return fromDir;
                }

                return expanded;
            }

            var bundledDir = Path.Combine(AppContext.BaseDirectory, "a3s-bridge");
            return FindBridgeJar(bundledDir) ?? Path.Combine(bundledDir, "a3s-metadata-bridge-all.jar");
        }

        private static string? FindBridgeJar(string directory)
        {
            if (!Directory.Exists(directory))
                return null;

            return Directory.EnumerateFiles(directory, "a3s-metadata-bridge-*-all.jar")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault()
                ?? Directory.EnumerateFiles(directory, "a3s-metadata-bridge*.jar")
                    .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
        }

        /// <inheritdoc />
        public async Task<A3SMetadataBundle> ReadAllAsync(string source, CancellationToken cancellationToken = default)
        {
            var envelope = await InvokeReadAsync(source, "all", cancellationToken).ConfigureAwait(false);
            return ToBundle(envelope, requireSync: true, requireServerInfo: true, requireAutoconfig: true);
        }

        /// <inheritdoc />
        public async Task<A3SAutoconfigInfo> ReadAutoconfigAsync(string source, CancellationToken cancellationToken = default)
        {
            var envelope = await InvokeReadAsync(source, "autoconfig", cancellationToken).ConfigureAwait(false);
            var autoconfig = envelope.Autoconfig ?? envelope.Files?.Autoconfig;
            if (autoconfig == null)
                throw new A3SBridgeException("Bridge response did not contain autoconfig.");
            return autoconfig;
        }

        /// <inheritdoc />
        public async Task<A3SSyncTreeNode> ReadSyncTreeAsync(string source, CancellationToken cancellationToken = default)
        {
            var envelope = await InvokeReadAsync(source, "sync", cancellationToken).ConfigureAwait(false);
            var sync = envelope.Sync ?? envelope.Files?.Sync;
            if (sync == null)
                throw new A3SBridgeException("Bridge response did not contain sync.");
            return sync;
        }

        /// <inheritdoc />
        public async Task<A3SServerInfo> ReadServerInfoAsync(string source, CancellationToken cancellationToken = default)
        {
            var envelope = await InvokeReadAsync(source, "serverinfo", cancellationToken).ConfigureAwait(false);
            var serverInfo = envelope.ServerInfo ?? envelope.Files?.ServerInfo;
            if (serverInfo == null)
                throw new A3SBridgeException("Bridge response did not contain serverinfo.");
            return serverInfo;
        }

        /// <inheritdoc />
        public Task WriteMetadataAsync(string contentRoot, object metadata, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException(
                "BridgeA3SMetadataStore.WriteMetadataAsync is implemented in Phase 2 (bridge write --target/--kind/--in).");
        }

        private async Task<A3SBridgeEnvelope> InvokeReadAsync(string source, string kind, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Metadata source (URL or path) is required.", nameof(source));

            var jar = ResolveBridgeJarPath();
            if (!File.Exists(jar))
                throw new A3SBridgeNotFoundException(jar);

            var java = ResolveJavaExecutable()
                       ?? throw new A3SBridgeException(
                           "Java runtime not found. Install a JRE/JDK (companion bridge targets JDK 25) or set JAVA_HOME.");

            // Probe java existence when absolute; PATH-resolved "java" is validated by Process.Start.
            if (Path.IsPathRooted(java) && !File.Exists(java))
            {
                throw new A3SBridgeException(
                    $"Java runtime not found at '{java}'. Install JDK/JRE or set JAVA_HOME.");
            }

            var psi = new ProcessStartInfo
            {
                FileName = java,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                ArgumentList =
                {
                    "-jar", jar,
                    "read",
                    "--source", source.Trim(),
                    "--kind", kind,
                    "--out", "-"
                }
            };

            try
            {
                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                if (!process.Start())
                    throw new A3SBridgeException($"Failed to start metadata bridge: {java} -jar {jar}");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

                var stdoutText = stdout.ToString();
                var stderrText = stderr.ToString().Trim();

                if (process.ExitCode != 0)
                {
                    var detail = string.IsNullOrEmpty(stderrText) ? stdoutText.Trim() : stderrText;
                    throw new A3SBridgeException(
                        $"Metadata bridge failed for kind '{kind}' (exit {process.ExitCode}): {detail}");
                }

                if (string.IsNullOrWhiteSpace(stdoutText))
                    throw new A3SBridgeException($"Metadata bridge returned empty JSON for kind '{kind}'.");

                A3SBridgeEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<A3SBridgeEnvelope>(stdoutText, JsonOptions);
                }
                catch (JsonException ex)
                {
                    throw new A3SBridgeException($"Metadata bridge JSON for kind '{kind}' could not be parsed.", ex);
                }

                if (envelope == null)
                    throw new A3SBridgeException($"Metadata bridge JSON for kind '{kind}' was null.");

                return envelope;
            }
            catch (A3SBridgeException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                throw new A3SBridgeException(
                    "Java runtime not found or could not be started. Install a JRE/JDK and ensure `java` is on PATH " +
                    $"(companion bridge targets JDK 25). Details: {ex.Message}",
                    ex);
            }
        }

        internal static A3SMetadataBundle ToBundle(
            A3SBridgeEnvelope envelope,
            bool requireSync,
            bool requireServerInfo,
            bool requireAutoconfig)
        {
            var autoconfig = envelope.Autoconfig ?? envelope.Files?.Autoconfig;
            var sync = envelope.Sync ?? envelope.Files?.Sync;
            var serverInfo = envelope.ServerInfo ?? envelope.Files?.ServerInfo;

            if (requireAutoconfig && autoconfig == null)
                throw new A3SBridgeException("Bridge response did not contain autoconfig.");
            if (requireSync && sync == null)
                throw new A3SBridgeException("Bridge response did not contain sync.");
            if (requireServerInfo && serverInfo == null)
                throw new A3SBridgeException("Bridge response did not contain serverinfo.");

            return new A3SMetadataBundle
            {
                Autoconfig = autoconfig ?? new A3SAutoconfigInfo(),
                SyncRoot = sync ?? new A3SSyncTreeNode { Type = "directory", Name = "racine" },
                ServerInfo = serverInfo ?? new A3SServerInfo()
            };
        }
    }
}
