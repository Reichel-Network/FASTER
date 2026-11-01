using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Services.A3S.Zsync;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Generates classic Arma3Sync <c>*.zsync</c> companions beside publish-root leaves (HTTP clients).
    /// Mirrors Java <c>RepositoryZsyncProcessor</c> / Build “Processing *.zsync files…”.
    /// </summary>
    public static class A3SZsyncService
    {
        /// <summary>
        /// True when clients will fetch over HTTP(S) and therefore need <c>.zsync</c> control files.
        /// Matches Java Build: generate for Http protocol, skip for FTP/SFTP-only.
        /// </summary>
        public static bool ShouldGenerateZsync(A3SRepository repository, A3SBuildOptions? options = null)
        {
            if (options?.SkipZsync == true)
                return false;
            if (options?.ForceZsync == true)
                return true;

            var upload = (repository.UploadProtocol ?? string.Empty).Trim().ToUpperInvariant();
            if (upload is "LOCAL" or "HTTP" or "HTTPS")
                return true;

            if (!string.IsNullOrWhiteSpace(repository.AutoconfigUrl)
                && Uri.TryCreate(repository.AutoconfigUrl.Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                    || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            var hint = (repository.ProtocolHint ?? string.Empty).Trim().ToUpperInvariant();
            return hint is "HTTP" or "HTTPS";
        }

        /// <summary>
        /// Build public HTTP(S) content-root URI used in zsync <c>URL:</c> headers (trailing slash).
        /// </summary>
        public static Uri? TryResolvePublicContentRoot(A3SRepository repository)
        {
            if (!string.IsNullOrWhiteSpace(repository.AutoconfigUrl))
            {
                try
                {
                    return A3SUrlHelpers.GetContentRootUri(repository.AutoconfigUrl);
                }
                catch
                {
                    // fall through
                }
            }

            var upload = (repository.UploadProtocol ?? string.Empty).Trim().ToUpperInvariant();
            if (upload is "HTTP" or "HTTPS" or "LOCAL")
            {
                try
                {
                    var endpoint = A3SUrlHelpers.ResolveUploadEndpoint(repository);
                    if (endpoint.ContentRoot.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                        || endpoint.ContentRoot.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
                    {
                        return endpoint.ContentRoot;
                    }
                }
                catch
                {
                    // fall through
                }
            }

            return null;
        }

        /// <summary>
        /// Ensure each sync leaf has a sibling <c>.zsync</c> (regenerate when missing/empty/updated).
        /// </summary>
        public static async Task<A3SZsyncGenerationResult> GenerateForLeavesAsync(
            string contentRoot,
            A3SSyncTreeNode syncRoot,
            A3SRepository repository,
            A3SSyncTreeNode? previousSync = null,
            IProgress<A3SSyncProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
            ArgumentNullException.ThrowIfNull(syncRoot);
            ArgumentNullException.ThrowIfNull(repository);

            var publicRoot = TryResolvePublicContentRoot(repository)
                             ?? new Uri("http://localhost/");

            var previous = previousSync?.EnumerateLeaves()
                               .ToDictionary(l => l.RelativePath, l => l, StringComparer.OrdinalIgnoreCase)
                           ?? new Dictionary<string, A3SSyncTreeLeafRef>(StringComparer.OrdinalIgnoreCase);

            var leaves = syncRoot.EnumerateLeaves().ToList();
            var toGenerate = new List<A3SSyncTreeLeafRef>();
            foreach (var leaf in leaves)
            {
                var relativeOs = leaf.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var sourcePath = Path.Combine(contentRoot, relativeOs);
                if (!File.Exists(sourcePath))
                    continue;

                var zsyncPath = sourcePath + A3SBuildService.ZsyncExtension;
                var needs = !File.Exists(zsyncPath)
                            || new FileInfo(zsyncPath).Length == 0
                            || !previous.TryGetValue(leaf.RelativePath, out var old)
                            || !A3SHash.EqualsSha1(old.Sha1, leaf.Sha1);
                if (needs)
                    toGenerate.Add(leaf);
            }

            var generated = 0;
            var failed = 0;
            var errors = new List<string>();

            for (var i = 0; i < toGenerate.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var leaf = toGenerate[i];
                progress?.Report(new A3SSyncProgress
                {
                    Message = $"Generating .zsync for {leaf.RelativePath}",
                    CompletedFiles = i,
                    TotalFiles = toGenerate.Count
                });

                var relativeOs = leaf.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var sourcePath = Path.Combine(contentRoot, relativeOs);
                var zsyncPath = sourcePath + A3SBuildService.ZsyncExtension;
                var url = A3SUrlHelpers.GetAddonFileUri(publicRoot, leaf.RelativePath).ToString();

                try
                {
                    await A3SZsyncMetaFileMaker.WriteAsync(
                            sourcePath,
                            zsyncPath,
                            url,
                            leaf.Sha1,
                            cancellationToken)
                        .ConfigureAwait(false);
                    generated++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    errors.Add($"{leaf.RelativePath}.zsync: {ex.Message}");
                }
            }

            progress?.Report(new A3SSyncProgress
            {
                Message = $"zsync complete: generated {generated}, skipped {leaves.Count - toGenerate.Count}, failed {failed}.",
                CompletedFiles = toGenerate.Count,
                TotalFiles = toGenerate.Count
            });

            return new A3SZsyncGenerationResult
            {
                Considered = leaves.Count,
                Generated = generated,
                SkippedUnchanged = leaves.Count - toGenerate.Count,
                Failed = failed,
                Errors = errors
            };
        }
    }

    public sealed class A3SZsyncGenerationResult
    {
        public int Considered { get; init; }
        public int Generated { get; init; }
        public int SkippedUnchanged { get; init; }
        public int Failed { get; init; }
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
    }
}
