using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Phase 1 ownership: check/sync orchestration against community A3S repos (cancel, progress, exact-match delete).
    /// C# owns download UX; metadata I/O only via <see cref="IA3SMetadataStore"/>.
    /// </summary>
    public class A3SSyncService
    {
        public const string ZipExtension = ".zip";

        private readonly IA3SMetadataStore _metadataStore;
        private readonly A3SProtocolClient _protocolClient;

        public A3SSyncService(IA3SMetadataStore metadataStore, A3SProtocolClient? protocolClient = null)
        {
            _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
            _protocolClient = protocolClient ?? new A3SProtocolClient();
        }

        /// <summary>Check local tree against remote sync metadata without downloading addon files.</summary>
        public async Task<A3SCheckResult> CheckAsync(
            A3SRepository repository,
            IProgress<A3SSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            ValidateRepository(repository);

            var prepared = await PrepareRemoteMetadataAsync(repository, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new A3SSyncProgress { Message = "Comparing local files to remote sync tree…" });

            var comparison = await CompareLocalAsync(repository.LocalPath, prepared.SyncRoot, prepared.ServerInfo, cancellationToken)
                .ConfigureAwait(false);

            repository.LastRevision = prepared.ServerInfo.Revision;
            repository.LastCheckUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(prepared.ProtocolHint))
                repository.ProtocolHint = prepared.ProtocolHint;

            return new A3SCheckResult
            {
                Revision = prepared.ServerInfo.Revision,
                TotalRemoteFiles = comparison.AllLeaves.Count,
                MissingOrOutdated = comparison.ToDownload.Count,
                ExtraLocalFiles = comparison.ExtraLocal.Count,
                DownloadBytes = comparison.ToDownload.Sum(EstimateDownloadSize),
                FilesToDownload = comparison.ToDownload,
                ExtraLocalPaths = comparison.ExtraLocal
            };
        }

        /// <summary>Sync addon tree from remote repository (HTTP(S)).</summary>
        public async Task<A3SSyncResult> SyncAsync(
            A3SRepository repository,
            A3SSyncOptions? options = null,
            IProgress<A3SSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            options ??= new A3SSyncOptions();
            ValidateRepository(repository);

            if (options.ExactMatch && !options.ExactMatchConfirmed)
            {
                throw new InvalidOperationException(
                    "Exact-match delete requires explicit confirmation (ExactMatchConfirmed).");
            }

            Directory.CreateDirectory(repository.LocalPath);

            var prepared = await PrepareRemoteMetadataAsync(repository, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report(new A3SSyncProgress { Message = "Comparing local files to remote sync tree…" });

            var comparison = await CompareLocalAsync(repository.LocalPath, prepared.SyncRoot, prepared.ServerInfo, cancellationToken)
                .ConfigureAwait(false);

            var errors = new List<string>();
            var downloaded = 0;
            var failed = 0;
            var total = comparison.ToDownload.Count;
            long completedBytes = 0;
            var totalBytes = comparison.ToDownload.Sum(EstimateDownloadSize);

            for (var i = 0; i < comparison.ToDownload.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leaf = comparison.ToDownload[i];
                progress?.Report(new A3SSyncProgress
                {
                    Message = $"Downloading {leaf.RelativePath}",
                    CompletedFiles = i,
                    TotalFiles = total,
                    CompletedBytes = completedBytes,
                    TotalBytes = totalBytes
                });

                try
                {
                    await DownloadLeafAsync(prepared.ContentRoot, repository.LocalPath, leaf, prepared.ServerInfo, cancellationToken)
                        .ConfigureAwait(false);
                    downloaded++;
                    completedBytes += EstimateDownloadSize(leaf);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add($"{leaf.RelativePath}: {ex.Message}");
                }
            }

            var deleted = 0;
            if (options.ExactMatch && options.ExactMatchConfirmed && comparison.ExtraLocal.Count > 0)
            {
                progress?.Report(new A3SSyncProgress
                {
                    Message = $"Exact-match: removing {comparison.ExtraLocal.Count} extra local file(s)…",
                    CompletedFiles = total,
                    TotalFiles = total,
                    CompletedBytes = completedBytes,
                    TotalBytes = totalBytes
                });

                foreach (var extra in comparison.ExtraLocal)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (File.Exists(extra))
                        {
                            File.Delete(extra);
                            deleted++;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Delete {extra}: {ex.Message}");
                    }
                }

                CleanupEmptyDirectories(repository.LocalPath, prepared.ServerInfo.HiddenFolderPaths);
            }

            repository.LastRevision = prepared.ServerInfo.Revision;
            repository.LastCheckUtc = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(prepared.ProtocolHint))
                repository.ProtocolHint = prepared.ProtocolHint;

            progress?.Report(new A3SSyncProgress
            {
                Message = $"Sync finished (downloaded {downloaded}, unchanged {comparison.Unchanged}, deleted {deleted}, failed {failed}).",
                CompletedFiles = total,
                TotalFiles = total,
                CompletedBytes = completedBytes,
                TotalBytes = totalBytes
            });

            return new A3SSyncResult
            {
                Revision = prepared.ServerInfo.Revision,
                Downloaded = downloaded,
                Unchanged = comparison.Unchanged,
                Deleted = deleted,
                Failed = failed,
                Errors = errors,
                AddonRelativePaths = prepared.SyncRoot.AddonFolderRelativePaths()
            };
        }

        private async Task<PreparedMetadata> PrepareRemoteMetadataAsync(
            A3SRepository repository,
            IProgress<A3SSyncProgress>? progress,
            CancellationToken cancellationToken)
        {
            progress?.Report(new A3SSyncProgress
            {
                Message = "Reading remote .a3s metadata via IA3SMetadataStore (bridge)…"
            });

            // Bridge accepts autoconfig URL directly (read --source <url> --kind all).
            var bundle = await _metadataStore.ReadAllAsync(repository.AutoconfigUrl, cancellationToken)
                .ConfigureAwait(false);

            var contentRoot = A3SUrlHelpers.GetContentRootUri(repository.AutoconfigUrl);
            var fromProtocol = A3SUrlHelpers.TryBuildContentRootFromProtocol(bundle.Autoconfig.Protocol);
            if (fromProtocol != null)
                contentRoot = fromProtocol;

            var protocolHint = string.IsNullOrWhiteSpace(bundle.Autoconfig.Protocol?.Type)
                ? contentRoot.Scheme.ToUpperInvariant()
                : bundle.Autoconfig.Protocol.Type;

            return new PreparedMetadata(contentRoot, bundle.Autoconfig, bundle.ServerInfo, bundle.SyncRoot, protocolHint);
        }

        private static async Task<ComparisonResult> CompareLocalAsync(
            string localRoot,
            A3SSyncTreeNode syncRoot,
            A3SServerInfo serverInfo,
            CancellationToken cancellationToken)
        {
            var leaves = syncRoot.EnumerateLeaves().ToList();
            var toDownload = new List<A3SSyncTreeLeafRef>();
            var remoteRelative = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unchanged = 0;

            foreach (var leaf in leaves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relativeOs = leaf.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                remoteRelative.Add(relativeOs);
                var localPath = Path.Combine(localRoot, relativeOs);

                if (!File.Exists(localPath))
                {
                    toDownload.Add(leaf);
                    continue;
                }

                var localSha1 = await A3SHash.ComputeSha1HexAsync(localPath, cancellationToken).ConfigureAwait(false);
                if (A3SHash.EqualsSha1(leaf.Sha1, localSha1))
                {
                    unchanged++;
                    continue;
                }

                toDownload.Add(leaf);
            }

            var extra = new List<string>();
            if (Directory.Exists(localRoot))
            {
                var hidden = new HashSet<string>(
                    (serverInfo.HiddenFolderPaths ?? new List<string>())
                    .Select(p => p.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var file in Directory.EnumerateFiles(localRoot, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(localRoot, file);
                    if (relative.StartsWith(A3SUrlHelpers.A3SFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(relative, A3SUrlHelpers.A3SFolderName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (relative.EndsWith(ZipExtension, StringComparison.OrdinalIgnoreCase)
                        || relative.EndsWith(".zsync", StringComparison.OrdinalIgnoreCase)
                        || relative.EndsWith(".faster.download", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsUnderHiddenFolder(relative, hidden))
                        continue;

                    if (!remoteRelative.Contains(relative))
                        extra.Add(file);
                }
            }

            return new ComparisonResult(leaves, toDownload, extra, unchanged);
        }

        private async Task DownloadLeafAsync(
            Uri contentRoot,
            string localRoot,
            A3SSyncTreeLeafRef leaf,
            A3SServerInfo serverInfo,
            CancellationToken cancellationToken)
        {
            var relativeOs = leaf.RelativePath.Replace('/', Path.DirectorySeparatorChar);
            var localPath = Path.Combine(localRoot, relativeOs);
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

            if (leaf.Compressed || serverInfo.CompressedPboFilesOnly)
            {
                var zipRelative = leaf.RelativePath + ZipExtension;
                var zipLocal = localPath + ZipExtension;
                await _protocolClient.DownloadAddonFileAsync(contentRoot, zipRelative, zipLocal, cancellationToken)
                    .ConfigureAwait(false);

                ExtractZipBeside(zipLocal, localPath);
                try { File.Delete(zipLocal); } catch { /* ignore */ }
            }
            else
            {
                await _protocolClient.DownloadAddonFileAsync(contentRoot, leaf.RelativePath, localPath, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!string.IsNullOrWhiteSpace(leaf.Sha1) && File.Exists(localPath))
            {
                var actual = await A3SHash.ComputeSha1HexAsync(localPath, cancellationToken).ConfigureAwait(false);
                if (!A3SHash.EqualsSha1(leaf.Sha1, actual))
                {
                    throw new IOException(
                        $"SHA-1 mismatch for {leaf.RelativePath}: expected {leaf.Sha1}, got {actual}.");
                }
            }
        }

        private static void ExtractZipBeside(string zipPath, string expectedOutputFile)
        {
            var destDir = Path.GetDirectoryName(expectedOutputFile)
                          ?? throw new InvalidOperationException("Missing destination directory.");
            var expectedName = Path.GetFileName(expectedOutputFile);

            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.Entries.FirstOrDefault(e =>
                            string.Equals(e.Name, expectedName, StringComparison.OrdinalIgnoreCase))
                        ?? archive.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Name));

            if (entry == null)
                throw new InvalidDataException($"Zip '{zipPath}' contains no file entries.");

            var outPath = Path.Combine(destDir, expectedName);
            entry.ExtractToFile(outPath, overwrite: true);
        }

        private static long EstimateDownloadSize(A3SSyncTreeLeafRef leaf)
            => leaf.Compressed && leaf.CompressedSize > 0 ? leaf.CompressedSize : leaf.Size;

        private static bool IsUnderHiddenFolder(string relativePath, HashSet<string> hiddenFolderPaths)
        {
            foreach (var hidden in hiddenFolderPaths)
            {
                if (string.IsNullOrEmpty(hidden))
                    continue;
                if (relativePath.Equals(hidden, StringComparison.OrdinalIgnoreCase)
                    || relativePath.StartsWith(hidden + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void CleanupEmptyDirectories(string root, IEnumerable<string>? hiddenFolderPaths)
        {
            var hidden = new HashSet<string>(
                (hiddenFolderPaths ?? Array.Empty<string>())
                .Select(p => p.Replace('/', Path.DirectorySeparatorChar).Trim(Path.DirectorySeparatorChar)),
                StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(root))
                return;

            foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                var relative = Path.GetRelativePath(root, dir);
                if (relative.Equals(A3SUrlHelpers.A3SFolderName, StringComparison.OrdinalIgnoreCase)
                    || relative.StartsWith(A3SUrlHelpers.A3SFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || IsUnderHiddenFolder(relative, hidden))
                {
                    continue;
                }

                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch
                {
                    // ignore locked / non-empty races
                }
            }
        }

        private static void ValidateRepository(A3SRepository repository)
        {
            if (string.IsNullOrWhiteSpace(repository.AutoconfigUrl))
                throw new InvalidOperationException("Repository autoconfig URL is required.");
            if (string.IsNullOrWhiteSpace(repository.LocalPath))
                throw new InvalidOperationException("Repository local path is required.");
        }

        private sealed record PreparedMetadata(
            Uri ContentRoot,
            A3SAutoconfigInfo Autoconfig,
            A3SServerInfo ServerInfo,
            A3SSyncTreeNode SyncRoot,
            string ProtocolHint);

        private sealed record ComparisonResult(
            IReadOnlyList<A3SSyncTreeLeafRef> AllLeaves,
            List<A3SSyncTreeLeafRef> ToDownload,
            List<string> ExtraLocal,
            int Unchanged);
    }
}
