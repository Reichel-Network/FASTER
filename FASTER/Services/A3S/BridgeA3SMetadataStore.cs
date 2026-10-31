using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// (A) Headless metadata-bridge adapter. Spawns
    /// <c>java -jar a3s-metadata-bridge-*-all.jar read|write …</c>
    /// per companion <c>docs/BRIDGE.md</c> (incl. Phase 2 <c>write --kind all</c>).
    /// Operators never configure a product JAR path;
    /// <c>Settings.a3sMetadataBridgePath</c> is Advanced-only (empty = bundled default).
    /// </summary>
    public sealed class BridgeA3SMetadataStore : IA3SMetadataStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNamingPolicy = null
        };

        private static readonly JsonSerializerOptions WriteJsonOptions = new()
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
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
        public async Task<A3SChangelogs> ReadChangelogsAsync(string source, CancellationToken cancellationToken = default)
        {
            var envelope = await InvokeReadAsync(source, "changelogs", cancellationToken).ConfigureAwait(false);
            var changelogs = envelope.Changelogs ?? envelope.Files?.Changelogs;
            if (changelogs == null)
                throw new A3SBridgeException("Bridge response did not contain changelogs.");
            return changelogs;
        }

        /// <inheritdoc />
        public Task WriteMetadataAsync(string contentRoot, string kind, object metadataPayload, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(contentRoot))
                throw new ArgumentException("Content root is required.", nameof(contentRoot));
            if (string.IsNullOrWhiteSpace(kind))
                throw new ArgumentException("Metadata kind is required.", nameof(kind));
            ArgumentNullException.ThrowIfNull(metadataPayload);

            var normalized = kind.Trim().ToLowerInvariant();
            if (normalized is "all")
                throw new ArgumentException("Use WriteAllAsync for kind=all.", nameof(kind));

            var envelope = new JsonObject
            {
                ["bridgeVersion"] = 1,
                ["kind"] = normalized,
                [normalized] = JsonSerializer.SerializeToNode(metadataPayload, metadataPayload.GetType(), WriteJsonOptions)
            };

            return InvokeWriteAsync(contentRoot, normalized, envelope.ToJsonString(), cancellationToken);
        }

        /// <inheritdoc />
        public Task WriteAllAsync(string contentRoot, A3SMetadataWriteSet files, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(contentRoot))
                throw new ArgumentException("Content root is required.", nameof(contentRoot));
            ArgumentNullException.ThrowIfNull(files);

            var filesNode = new JsonObject();
            var any = false;

            if (files.Autoconfig != null)
            {
                filesNode["autoconfig"] = JsonSerializer.SerializeToNode(files.Autoconfig, WriteJsonOptions);
                any = true;
            }

            if (files.Sync != null)
            {
                filesNode["sync"] = JsonSerializer.SerializeToNode(files.Sync, WriteJsonOptions);
                any = true;
            }

            if (files.ServerInfo != null)
            {
                filesNode["serverinfo"] = JsonSerializer.SerializeToNode(files.ServerInfo, WriteJsonOptions);
                any = true;
            }

            if (files.Changelogs != null)
            {
                filesNode["changelogs"] = JsonSerializer.SerializeToNode(files.Changelogs, WriteJsonOptions);
                any = true;
            }

            if (files.Events != null)
            {
                filesNode["events"] = JsonSerializer.SerializeToNode(files.Events, files.Events.GetType(), WriteJsonOptions);
                any = true;
            }
            else
            {
                filesNode["events"] = null;
            }

            if (!any)
                throw new ArgumentException("WriteAllAsync requires at least one non-null metadata payload.", nameof(files));

            var envelope = new JsonObject
            {
                ["bridgeVersion"] = 1,
                ["kind"] = "all",
                ["files"] = filesNode
            };

            return InvokeWriteAsync(contentRoot, "all", envelope.ToJsonString(), cancellationToken);
        }

        private async Task<A3SBridgeEnvelope> InvokeReadAsync(string source, string kind, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Metadata source (URL or path) is required.", nameof(source));

            var (java, jar) = ResolveJavaAndJar();
            var psi = CreateBridgeStartInfo(java, jar);
            psi.ArgumentList.Add("read");
            psi.ArgumentList.Add("--source");
            psi.ArgumentList.Add(source.Trim());
            psi.ArgumentList.Add("--kind");
            psi.ArgumentList.Add(kind);
            psi.ArgumentList.Add("--out");
            psi.ArgumentList.Add("-");

            var (exitCode, stdoutText, stderrText) = await RunBridgeAsync(psi, stdin: null, cancellationToken)
                .ConfigureAwait(false);

            EnsureSuccess(exitCode, kind, stdoutText, stderrText, expectingJson: true);

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

        private async Task InvokeWriteAsync(string contentRoot, string kind, string json, CancellationToken cancellationToken)
        {
            var (java, jar) = ResolveJavaAndJar();
            var psi = CreateBridgeStartInfo(java, jar);
            psi.ArgumentList.Add("write");
            psi.ArgumentList.Add("--target");
            psi.ArgumentList.Add(contentRoot.Trim());
            psi.ArgumentList.Add("--kind");
            psi.ArgumentList.Add(kind);
            psi.ArgumentList.Add("--in");
            psi.ArgumentList.Add("-");

            var (exitCode, stdoutText, stderrText) = await RunBridgeAsync(psi, stdin: json, cancellationToken)
                .ConfigureAwait(false);

            EnsureSuccess(exitCode, kind, stdoutText, stderrText, expectingJson: false);
        }

        private (string Java, string Jar) ResolveJavaAndJar()
        {
            var jar = ResolveBridgeJarPath();
            if (!File.Exists(jar))
                throw new A3SBridgeNotFoundException(jar);

            var java = ResolveJavaExecutable()
                       ?? throw new A3SBridgeException(
                           "Java runtime not found. Install a JRE/JDK (companion bridge targets JDK 25) or set JAVA_HOME.");

            if (Path.IsPathRooted(java) && !File.Exists(java))
            {
                throw new A3SBridgeException(
                    $"Java runtime not found at '{java}'. Install JDK/JRE or set JAVA_HOME.");
            }

            return (java, jar);
        }

        private static ProcessStartInfo CreateBridgeStartInfo(string java, string jar)
        {
            return new ProcessStartInfo
            {
                FileName = java,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                ArgumentList = { "-jar", jar }
            };
        }

        private static async Task<(int ExitCode, string Stdout, string Stderr)> RunBridgeAsync(
            ProcessStartInfo psi,
            string? stdin,
            CancellationToken cancellationToken)
        {
            try
            {
                using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                var stdout = new StringBuilder();
                var stderr = new StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

                if (!process.Start())
                    throw new A3SBridgeException($"Failed to start metadata bridge: {psi.FileName} {string.Join(' ', psi.ArgumentList)}");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (stdin != null)
                {
                    await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    process.StandardInput.Close();
                }

                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return (process.ExitCode, stdout.ToString(), stderr.ToString().Trim());
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

        private static void EnsureSuccess(int exitCode, string kind, string stdoutText, string stderrText, bool expectingJson)
        {
            if (exitCode != 0)
            {
                var detail = string.IsNullOrEmpty(stderrText) ? stdoutText.Trim() : stderrText;
                var hint = exitCode switch
                {
                    1 => "usage/unknown command",
                    2 => "I/O or missing metadata",
                    3 => "JSON/domain mapping error",
                    _ => $"exit {exitCode}"
                };
                throw new A3SBridgeException(
                    $"Metadata bridge failed for kind '{kind}' ({hint}, exit {exitCode}): {detail}");
            }

            if (expectingJson && string.IsNullOrWhiteSpace(stdoutText))
                throw new A3SBridgeException($"Metadata bridge returned empty JSON for kind '{kind}'.");
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
            var changelogs = envelope.Changelogs ?? envelope.Files?.Changelogs;

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
                ServerInfo = serverInfo ?? new A3SServerInfo(),
                Changelogs = changelogs
            };
        }
    }
}
