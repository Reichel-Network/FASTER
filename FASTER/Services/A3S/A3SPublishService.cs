using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Phase 2 publish orchestration: optional Build, then C# upload of changed addon files + <c>.a3s/*</c> metadata.
    /// Does not use Arma3Sync GUI upload.
    /// </summary>
    public class A3SPublishService
    {
        private static readonly string[] MetadataFileNames =
        {
            "sync", "serverinfo", "autoconfig", "changelogs", "events"
        };

        private readonly IA3SMetadataStore _metadataStore;
        private readonly A3SProtocolClient _protocolClient;
        private readonly A3SBuildService _buildService;

        public A3SPublishService(
            IA3SMetadataStore metadataStore,
            A3SProtocolClient? protocolClient = null,
            A3SBuildService? buildService = null)
        {
            _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
            _protocolClient = protocolClient ?? new A3SProtocolClient();
            _buildService = buildService ?? new A3SBuildService(_metadataStore);
        }

        public async Task<A3SUploadResult> PublishAsync(
            A3SRepository repository,
            A3SUploadOptions? options = null,
            IProgress<A3SSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(repository);
            options ??= new A3SUploadOptions();

            A3SBuildResult? build = null;
            if (options.BuildFirst)
            {
                build = await _buildService.BuildAsync(repository, progress: progress, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            var contentRoot = build?.ContentRoot ?? A3SBuildService.ResolvePublishRoot(repository);
            if (!Directory.Exists(contentRoot))
                throw new DirectoryNotFoundException($"Publish content root not found: {contentRoot}");

            progress?.Report(new A3SSyncProgress { Message = "Reading local sync tree for upload…" });
            var localSync = build?.SyncRoot
                            ?? await _metadataStore.ReadSyncTreeAsync(contentRoot, cancellationToken).ConfigureAwait(false);
            var serverInfo = build?.ServerInfo
                             ?? await _metadataStore.ReadServerInfoAsync(contentRoot, cancellationToken).ConfigureAwait(false);

            A3SSyncTreeNode? remoteSync = null;
            if (!options.ForceAllFiles && !string.IsNullOrWhiteSpace(repository.AutoconfigUrl))
            {
                try
                {
                    progress?.Report(new A3SSyncProgress { Message = "Reading remote sync for change detection…" });
                    remoteSync = await _metadataStore.ReadSyncTreeAsync(repository.AutoconfigUrl, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    progress?.Report(new A3SSyncProgress
                    {
                        Message = $"Remote sync unavailable ({ex.Message}); uploading all local files."
                    });
                }
            }

            var localLeaves = localSync.EnumerateLeaves().ToList();
            var endpoint = A3SUrlHelpers.ResolveUploadEndpoint(repository);

            // Local SoT: Build already wrote .a3s under the publish root served by IIS/nginx/etc.
            if (A3SUrlHelpers.IsLocalOnlyUpload(endpoint))
            {
                var localA3sDir = Path.Combine(contentRoot, A3SUrlHelpers.A3SFolderName);
                var metaCount = 0;
                if (Directory.Exists(localA3sDir))
                {
                    foreach (var name in MetadataFileNames)
                    {
                        if (File.Exists(Path.Combine(localA3sDir, name)))
                            metaCount++;
                    }
                }

                repository.LastRevision = serverInfo.Revision;
                repository.LastPublishUtc = DateTime.UtcNow;
                repository.LastCheckUtc = repository.LastPublishUtc;
                repository.ProtocolHint = "HTTP";

                progress?.Report(new A3SSyncProgress
                {
                    Message =
                        $"Local publish complete (rev {serverInfo.Revision}, {localLeaves.Count} file(s) on disk, {metaCount} metadata). No remote upload.",
                    CompletedFiles = localLeaves.Count,
                    TotalFiles = localLeaves.Count
                });

                return new A3SUploadResult
                {
                    Revision = serverInfo.Revision,
                    UploadedFiles = 0,
                    SkippedUnchanged = localLeaves.Count,
                    Failed = 0,
                    UploadedMetadataFiles = metaCount,
                    Errors = Array.Empty<string>()
                };
            }

            var remoteMap = remoteSync?.EnumerateLeaves()
                                .ToDictionary(l => l.RelativePath, l => l, StringComparer.OrdinalIgnoreCase)
                            ?? new Dictionary<string, A3SSyncTreeLeafRef>(StringComparer.OrdinalIgnoreCase);

            var toUpload = new List<A3SSyncTreeLeafRef>();
            var skipped = 0;
            foreach (var leaf in localLeaves)
            {
                if (!options.ForceAllFiles
                    && remoteMap.TryGetValue(leaf.RelativePath, out var remote)
                    && A3SHash.EqualsSha1(remote.Sha1, leaf.Sha1))
                {
                    skipped++;
                    continue;
                }

                toUpload.Add(leaf);
            }

            var errors = new List<string>();
            var uploaded = 0;
            var failed = 0;
            long completedBytes = 0;
            var totalBytes = toUpload.Sum(l => l.Size);

            for (var i = 0; i < toUpload.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leaf = toUpload[i];
                progress?.Report(new A3SSyncProgress
                {
                    Message = $"Uploading {leaf.RelativePath}",
                    CompletedFiles = i,
                    TotalFiles = toUpload.Count,
                    CompletedBytes = completedBytes,
                    TotalBytes = totalBytes
                });

                var relativeOs = leaf.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var localPath = Path.Combine(contentRoot, relativeOs);
                if (!File.Exists(localPath))
                {
                    failed++;
                    errors.Add($"{leaf.RelativePath}: local file missing");
                    continue;
                }

                try
                {
                    var remoteUri = A3SUrlHelpers.GetAddonFileUri(endpoint.ContentRoot, leaf.RelativePath);
                    await _protocolClient.UploadAsync(localPath, remoteUri, endpoint, cancellationToken)
                        .ConfigureAwait(false);
                    uploaded++;
                    completedBytes += leaf.Size;

                    // HTTP mirrors need sibling *.zsync (not listed in sync tree).
                    var localZsync = localPath + A3SBuildService.ZsyncExtension;
                    if (File.Exists(localZsync))
                    {
                        var zsyncRelative = leaf.RelativePath + A3SBuildService.ZsyncExtension;
                        var zsyncUri = A3SUrlHelpers.GetAddonFileUri(endpoint.ContentRoot, zsyncRelative);
                        await _protocolClient.UploadAsync(localZsync, zsyncUri, endpoint, cancellationToken)
                            .ConfigureAwait(false);
                        uploaded++;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add($"{leaf.RelativePath}: {ex.Message}");
                }
            }

            progress?.Report(new A3SSyncProgress
            {
                Message = "Uploading .a3s metadata…",
                CompletedFiles = toUpload.Count,
                TotalFiles = toUpload.Count,
                CompletedBytes = completedBytes,
                TotalBytes = totalBytes
            });

            var metaUploaded = 0;
            var a3sDir = Path.Combine(contentRoot, A3SUrlHelpers.A3SFolderName);
            foreach (var name in MetadataFileNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var localMeta = Path.Combine(a3sDir, name);
                if (!File.Exists(localMeta))
                    continue;

                try
                {
                    var remoteUri = A3SUrlHelpers.GetMetadataUri(endpoint.ContentRoot, name);
                    await _protocolClient.UploadAsync(localMeta, remoteUri, endpoint, cancellationToken)
                        .ConfigureAwait(false);
                    metaUploaded++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add($".a3s/{name}: {ex.Message}");
                }
            }

            repository.LastRevision = serverInfo.Revision;
            repository.LastPublishUtc = DateTime.UtcNow;
            repository.LastCheckUtc = repository.LastPublishUtc;
            if (string.IsNullOrWhiteSpace(repository.ProtocolHint))
                repository.ProtocolHint = endpoint.Protocol;

            progress?.Report(new A3SSyncProgress
            {
                Message =
                    $"Publish finished (uploaded {uploaded} file(s), {metaUploaded} metadata, skipped {skipped}, failed {failed}).",
                CompletedFiles = toUpload.Count,
                TotalFiles = toUpload.Count,
                CompletedBytes = completedBytes,
                TotalBytes = totalBytes
            });

            return new A3SUploadResult
            {
                Revision = serverInfo.Revision,
                UploadedFiles = uploaded,
                SkippedUnchanged = skipped,
                Failed = failed,
                UploadedMetadataFiles = metaUploaded,
                Errors = errors
            };
        }
    }
}
