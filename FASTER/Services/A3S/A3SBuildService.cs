using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Properties;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Phase 2 ownership: C# tree walk + SHA-1, revision/changelog updates, write classic <c>.a3s/*</c>
    /// via <see cref="IA3SMetadataStore"/>. Does not shell Arma3Sync build/upload.
    /// </summary>
    public class A3SBuildService
    {
        public const string RacineName = "racine";
        public const string AddonsFolderName = "addons";
        public const string ZsyncExtension = ".zsync";
        public const string PboZipExtension = ".pbo.zip";

        private readonly IA3SMetadataStore _metadataStore;

        public A3SBuildService(IA3SMetadataStore metadataStore)
        {
            _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
        }

        /// <summary>
        /// Resolve the repository main folder used for Build/Publish:
        /// explicit publish root → Deployment InstallPath (when flagged) → LocalPath.
        /// </summary>
        public static string ResolvePublishRoot(A3SRepository repository, string? deploymentInstallPath = null)
        {
            ArgumentNullException.ThrowIfNull(repository);

            if (!string.IsNullOrWhiteSpace(repository.PublishRoot))
                return repository.PublishRoot.Trim();

            if (repository.UseDeploymentInstallPath)
            {
                var install = deploymentInstallPath;
                if (string.IsNullOrWhiteSpace(install))
                {
                    try { install = Settings.Default.Deployments?.InstallPath; }
                    catch { /* design-time / tests without settings host */ }
                }

                if (!string.IsNullOrWhiteSpace(install))
                    return install.Trim();
            }

            if (!string.IsNullOrWhiteSpace(repository.LocalPath))
                return repository.LocalPath.Trim();

            throw new InvalidOperationException(
                "Repository has no publish root. Set PublishRoot, LocalPath, or UseDeploymentInstallPath.");
        }

        /// <summary>Build <c>.a3s/*</c> metadata for a publish root (walk + SHA-1 + revision/changelogs).</summary>
        public async Task<A3SBuildResult> BuildAsync(
            A3SRepository repository,
            A3SBuildOptions? options = null,
            IProgress<A3SSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            options ??= new A3SBuildOptions();

            var contentRoot = string.IsNullOrWhiteSpace(options.ContentRootOverride)
                ? ResolvePublishRoot(repository)
                : options.ContentRootOverride.Trim();

            if (!Directory.Exists(contentRoot))
                throw new DirectoryNotFoundException($"Publish content root not found: {contentRoot}");

            progress?.Report(new A3SSyncProgress { Message = $"Building A3S repository from {contentRoot}…" });

            A3SSyncTreeNode? previousSync = null;
            A3SServerInfo? previousServerInfo = null;
            A3SChangelogs previousChangelogs = new();

            try
            {
                previousSync = await _metadataStore.ReadSyncTreeAsync(contentRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report(new A3SSyncProgress { Message = "No previous local sync metadata (first build)." });
            }

            try
            {
                previousServerInfo = await _metadataStore.ReadServerInfoAsync(contentRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // first build
            }

            try
            {
                previousChangelogs = await _metadataStore.ReadChangelogsAsync(contentRoot, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                previousChangelogs = new A3SChangelogs();
            }

            progress?.Report(new A3SSyncProgress { Message = "Walking content tree and computing SHA-1…" });
            var syncRoot = await WalkContentTreeAsync(contentRoot, progress, cancellationToken).ConfigureAwait(false);
            var leaves = syncRoot.EnumerateLeaves().ToList();
            var totalBytes = leaves.Sum(l => l.Size);
            var buildDate = DateTime.UtcNow;
            var revision = (previousServerInfo?.Revision ?? repository.LastRevision) + 1;
            if (revision < 1)
                revision = 1;

            var connections = options.NumberOfConnections
                              ?? (repository.NumberOfConnections > 0 ? repository.NumberOfConnections : 4);

            var serverInfo = new A3SServerInfo
            {
                Revision = revision,
                BuildDate = buildDate,
                NumberOfFiles = leaves.Count,
                TotalFilesSize = totalBytes,
                NumberOfConnections = connections,
                NoPartialFileTransfer = previousServerInfo?.NoPartialFileTransfer ?? false,
                CompressedPboFilesOnly = previousServerInfo?.CompressedPboFilesOnly ?? false,
                RepositoryContentUpdated = true,
                HiddenFolderPaths = previousServerInfo?.HiddenFolderPaths?.ToList() ?? new List<string>()
            };

            var changelog = BuildChangelog(revision, buildDate, previousSync, syncRoot);
            var changelogs = previousChangelogs ?? new A3SChangelogs();
            changelogs.List = changelogs.List
                .Where(c => c.Revision != revision)
                .OrderBy(c => c.Revision)
                .ToList();
            changelogs.List.Add(changelog);

            // Keep a bounded history like Arma3Sync (last ~10).
            if (changelogs.List.Count > 10)
                changelogs.List = changelogs.List.OrderByDescending(c => c.Revision).Take(10).OrderBy(c => c.Revision).ToList();

            // HTTP clients request leafName + ".zsync" beside every sync leaf (pbo, bisign, …).
            // Metadata intentionally excludes .zsync from the sync tree (same as Java Build).
            A3SZsyncGenerationResult? zsync = null;
            if (A3SZsyncService.ShouldGenerateZsync(repository, options))
            {
                progress?.Report(new A3SSyncProgress { Message = "Processing *.zsync files…" });
                zsync = await A3SZsyncService.GenerateForLeavesAsync(
                        contentRoot,
                        syncRoot,
                        repository,
                        previousSync,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (zsync.Failed > 0)
                {
                    throw new IOException(
                        $"zsync generation failed for {zsync.Failed} file(s): {string.Join("; ", zsync.Errors.Take(5))}");
                }
            }

            progress?.Report(new A3SSyncProgress
            {
                Message = $"Writing .a3s metadata (revision {revision}, {leaves.Count} files)…",
                CompletedFiles = leaves.Count,
                TotalFiles = leaves.Count,
                CompletedBytes = totalBytes,
                TotalBytes = totalBytes
            });

            // Companion BRIDGE.md Phase 2: single write --kind all from files envelope.
            var writeSet = new A3SMetadataWriteSet
            {
                Sync = syncRoot,
                ServerInfo = serverInfo,
                Changelogs = changelogs,
                Autoconfig = options.SkipAutoconfig ? null : BuildAutoconfig(repository, connections),
                Events = null
            };
            await _metadataStore.WriteAllAsync(contentRoot, writeSet, cancellationToken).ConfigureAwait(false);

            repository.LastRevision = revision;
            repository.LastBuildUtc = buildDate;
            repository.LastCheckUtc = buildDate;
            if (string.IsNullOrWhiteSpace(repository.LocalPath))
                repository.LocalPath = contentRoot;
            if (string.IsNullOrWhiteSpace(repository.ProtocolHint) && !string.IsNullOrWhiteSpace(repository.AutoconfigUrl))
            {
                try
                {
                    repository.ProtocolHint = A3SUrlHelpers.GetContentRootUri(repository.AutoconfigUrl).Scheme.ToUpperInvariant();
                }
                catch
                {
                    // ignore bad URL while building
                }
            }

            progress?.Report(new A3SSyncProgress
            {
                Message = $"Build complete: revision {revision}, {leaves.Count} files.",
                CompletedFiles = leaves.Count,
                TotalFiles = leaves.Count,
                CompletedBytes = totalBytes,
                TotalBytes = totalBytes
            });

            return new A3SBuildResult
            {
                ContentRoot = contentRoot,
                Revision = revision,
                FileCount = leaves.Count,
                TotalBytes = totalBytes,
                NewAddons = changelog.NewAddons.Count,
                UpdatedAddons = changelog.UpdatedAddons.Count,
                DeletedAddons = changelog.DeletedAddons.Count,
                ZsyncGenerated = zsync?.Generated ?? 0,
                ZsyncSkipped = zsync?.SkippedUnchanged ?? 0,
                ZsyncFailed = zsync?.Failed ?? 0,
                SyncRoot = syncRoot,
                ServerInfo = serverInfo,
                Changelogs = changelogs
            };
        }

        /// <summary>Walk publish root into a racine sync tree with SHA-1 leaves (Arma3Sync-compatible rules).</summary>
        public static async Task<A3SSyncTreeNode> WalkContentTreeAsync(
            string contentRoot,
            IProgress<A3SSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            var root = new A3SSyncTreeNode
            {
                Type = "directory",
                Name = RacineName,
                MarkAsAddon = false,
                Children = new List<A3SSyncTreeNode>()
            };

            var entries = Directory.EnumerateFileSystemEntries(contentRoot)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var fileTotal = CountCandidateFiles(contentRoot);
            var completed = 0;

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                // Skip classic .a3s and FASTER fixture sidecar (.a3s-json); not publish content.
                if (A3SUrlHelpers.IsMetadataSidecarDirectory(name))
                    continue;

                if (Directory.Exists(entry))
                {
                    var child = await WalkDirectoryAsync(entry, progress, () =>
                    {
                        completed++;
                        return (completed, fileTotal);
                    }, cancellationToken).ConfigureAwait(false);
                    root.Children.Add(child);
                }
                else if (File.Exists(entry) && ShouldIncludeFile(name))
                {
                    root.Children.Add(await CreateLeafAsync(entry, progress, () =>
                    {
                        completed++;
                        return (completed, fileTotal);
                    }, cancellationToken).ConfigureAwait(false));
                }
            }

            return root;
        }

        private static int CountCandidateFiles(string root)
        {
            var count = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(root, file);
                    if (A3SUrlHelpers.IsUnderMetadataSidecar(relative))
                        continue;

                    if (ShouldIncludeFile(Path.GetFileName(file)))
                        count++;
                }
            }
            catch
            {
                // best-effort for progress
            }

            return count;
        }

        private static async Task<A3SSyncTreeNode> WalkDirectoryAsync(
            string directory,
            IProgress<A3SSyncProgress>? progress,
            Func<(int completed, int total)> bump,
            CancellationToken cancellationToken)
        {
            var node = new A3SSyncTreeNode
            {
                Type = "directory",
                Name = Path.GetFileName(directory),
                Children = new List<A3SSyncTreeNode>()
            };

            string[]? children = null;
            try
            {
                children = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                progress?.Report(new A3SSyncProgress { Message = $"Skipping unreadable directory: {directory} ({ex.Message})" });
                return node;
            }

            foreach (var entry in children.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(entry);
                if (A3SUrlHelpers.IsMetadataSidecarDirectory(name))
                    continue;

                if (Directory.Exists(entry))
                {
                    if (string.Equals(name, AddonsFolderName, StringComparison.OrdinalIgnoreCase))
                        node.MarkAsAddon = true;
                    node.Children.Add(await WalkDirectoryAsync(entry, progress, bump, cancellationToken).ConfigureAwait(false));
                }
                else if (File.Exists(entry) && ShouldIncludeFile(name))
                {
                    node.Children.Add(await CreateLeafAsync(entry, progress, bump, cancellationToken).ConfigureAwait(false));
                }
            }

            return node;
        }

        private static async Task<A3SSyncTreeNode> CreateLeafAsync(
            string filePath,
            IProgress<A3SSyncProgress>? progress,
            Func<(int completed, int total)> bump,
            CancellationToken cancellationToken)
        {
            var info = new FileInfo(filePath);
            var sha1 = await A3SHash.ComputeSha1HexAsync(filePath, cancellationToken).ConfigureAwait(false);
            var (completed, total) = bump();
            progress?.Report(new A3SSyncProgress
            {
                Message = $"Hashed {info.Name}",
                CompletedFiles = completed,
                TotalFiles = Math.Max(total, completed),
                CompletedBytes = 0,
                TotalBytes = 0
            });

            return new A3SSyncTreeNode
            {
                Type = "leaf",
                Name = info.Name,
                Sha1 = sha1,
                Size = info.Length,
                Compressed = false,
                CompressedSize = 0
            };
        }

        private static bool ShouldIncludeFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
                return false;
            if (fileName.Contains(ZsyncExtension, StringComparison.OrdinalIgnoreCase))
                return false;
            if (fileName.Contains(PboZipExtension, StringComparison.OrdinalIgnoreCase))
                return false;
            if (fileName.EndsWith(".faster.download", StringComparison.OrdinalIgnoreCase))
                return false;
            if (fileName.Contains(A3SUrlHelpers.A3SFolderName, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        private static A3SChangelogEntry BuildChangelog(
            long revision,
            DateTime buildDate,
            A3SSyncTreeNode? previousSync,
            A3SSyncTreeNode newSync)
        {
            var newAddons = newSync.AddonFolderRelativePaths()
                .Select(p => p.Replace(Path.DirectorySeparatorChar, '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var oldAddons = (previousSync?.AddonFolderRelativePaths() ?? Array.Empty<string>())
                .Select(p => p.Replace(Path.DirectorySeparatorChar, '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var added = newAddons.Except(oldAddons, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
            var deleted = oldAddons.Except(newAddons, StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();

            var oldLeaves = previousSync?.EnumerateLeaves()
                                 .ToDictionary(l => l.RelativePath, l => l, StringComparer.OrdinalIgnoreCase)
                             ?? new Dictionary<string, A3SSyncTreeLeafRef>(StringComparer.OrdinalIgnoreCase);
            var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var leaf in newSync.EnumerateLeaves())
            {
                if (!oldLeaves.TryGetValue(leaf.RelativePath, out var old))
                    continue;
                if (!A3SHash.EqualsSha1(old.Sha1, leaf.Sha1))
                {
                    var addon = leaf.ParentRelativePath;
                    if (!string.IsNullOrEmpty(addon))
                        updated.Add(addon);
                }
            }

            // Addon folders that gained/lost files also count as updated when still present.
            foreach (var leaf in newSync.EnumerateLeaves())
            {
                if (oldLeaves.ContainsKey(leaf.RelativePath))
                    continue;
                var addon = leaf.ParentRelativePath;
                if (!string.IsNullOrEmpty(addon) && newAddons.Contains(addon) && oldAddons.Contains(addon))
                    updated.Add(addon);
            }

            updated.ExceptWith(added);
            updated.ExceptWith(deleted);

            var contentUpdated = added.Count > 0 || deleted.Count > 0 || updated.Count > 0
                                 || oldLeaves.Count != newSync.EnumerateLeaves().Count();

            return new A3SChangelogEntry
            {
                Revision = (int)revision,
                BuildDate = buildDate,
                ContentUpdated = contentUpdated,
                NewAddons = added,
                UpdatedAddons = updated.OrderBy(s => s).ToList(),
                DeletedAddons = deleted,
                Addons = newAddons.OrderBy(s => s).ToList()
            };
        }

        private static A3SAutoconfigInfo BuildAutoconfig(A3SRepository repository, int connections)
        {
            var protocol = new A3SProtocolInfo
            {
                Type = string.IsNullOrWhiteSpace(repository.ProtocolHint) ? "HTTPS" : repository.ProtocolHint.Trim().ToUpperInvariant(),
                Login = "anonymous",
                Password = string.Empty,
                ValidateSslCertificate = true,
                ConnectionTimeOut = "300000",
                ReadTimeOut = "300000"
            };

            if (!string.IsNullOrWhiteSpace(repository.AutoconfigUrl)
                && Uri.TryCreate(repository.AutoconfigUrl.Trim(), UriKind.Absolute, out var autoUri))
            {
                try
                {
                    var contentRoot = A3SUrlHelpers.GetContentRootUri(repository.AutoconfigUrl);
                    protocol.Type = contentRoot.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "HTTPS" : "HTTP";
                    var host = contentRoot.Host;
                    var path = contentRoot.AbsolutePath.Trim('/');
                    protocol.Url = string.IsNullOrEmpty(path) ? host : $"{host}/{path}";
                    protocol.Port = contentRoot.IsDefaultPort
                        ? (contentRoot.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? "443" : "80")
                        : contentRoot.Port.ToString();
                    repository.ProtocolHint = protocol.Type;
                }
                catch
                {
                    protocol.Url = autoUri.Host;
                    protocol.Port = autoUri.IsDefaultPort ? protocol.Port : autoUri.Port.ToString();
                }
            }
            else if (!string.IsNullOrWhiteSpace(repository.UploadUrl))
            {
                protocol.Type = string.IsNullOrWhiteSpace(repository.UploadProtocol)
                    ? protocol.Type
                    : repository.UploadProtocol.Trim().ToUpperInvariant();
                if (protocol.Type is "SFTP" or "FTP" or "FTPS")
                    protocol.Type = "HTTPS";
                protocol.Url = repository.UploadUrl.Trim().TrimStart('/');
                protocol.Port = string.IsNullOrWhiteSpace(repository.UploadPort) ? "443" : repository.UploadPort.Trim();
            }

            _ = connections; // reserved for future autoconfig connection hints
            return new A3SAutoconfigInfo
            {
                RepositoryName = repository.Name,
                Protocol = protocol
            };
        }
    }
}
