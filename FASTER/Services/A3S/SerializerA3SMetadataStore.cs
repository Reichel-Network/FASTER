using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Services.A3S.Serialization;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// (B) Native C# reader for classic GZIP Java <c>ObjectOutputStream</c> <c>.a3s/*</c> blobs.
    /// Read path covers autoconfig, sync, serverinfo, changelogs, and events.
    /// Write path is not implemented yet — use <see cref="BridgeA3SMetadataStore"/> / <see cref="PreferNativeA3SMetadataStore"/>.
    /// </summary>
    public sealed class SerializerA3SMetadataStore : IA3SMetadataStore
    {
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;

        public SerializerA3SMetadataStore()
            : this(new HttpClient { Timeout = TimeSpan.FromMinutes(5) }, ownsHttpClient: true)
        {
        }

        public SerializerA3SMetadataStore(HttpClient httpClient, bool ownsHttpClient = false)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ownsHttpClient = ownsHttpClient;
        }

        /// <inheritdoc />
        public async Task<A3SMetadataBundle> ReadAllAsync(string source, CancellationToken cancellationToken = default)
        {
            var autoconfig = await ReadAutoconfigAsync(source, cancellationToken).ConfigureAwait(false);
            var sync = await ReadSyncTreeAsync(source, cancellationToken).ConfigureAwait(false);
            var serverInfo = await ReadServerInfoAsync(source, cancellationToken).ConfigureAwait(false);
            A3SChangelogs? changelogs = null;
            A3SEvents? events = null;
            try
            {
                changelogs = await ReadChangelogsAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // optional
            }
            catch (HttpRequestException)
            {
                // optional remote
            }

            try
            {
                events = await ReadEventsAsync(source, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                // optional
            }
            catch (HttpRequestException)
            {
                // optional remote
            }

            return new A3SMetadataBundle
            {
                Autoconfig = autoconfig,
                SyncRoot = sync,
                ServerInfo = serverInfo,
                Changelogs = changelogs,
                Events = events
            };
        }

        /// <inheritdoc />
        public async Task<A3SAutoconfigInfo> ReadAutoconfigAsync(string source, CancellationToken cancellationToken = default)
        {
            var bytes = await ReadKindBytesAsync(source, A3SObjectStreamMapper.KindAutoconfig, cancellationToken)
                .ConfigureAwait(false);
            return A3SObjectStreamMapper.ToAutoconfig(A3SObjectStreamMapper.ReadGzipObjectStream(bytes));
        }

        /// <inheritdoc />
        public async Task<A3SSyncTreeNode> ReadSyncTreeAsync(string source, CancellationToken cancellationToken = default)
        {
            var bytes = await ReadKindBytesAsync(source, A3SObjectStreamMapper.KindSync, cancellationToken)
                .ConfigureAwait(false);
            return A3SObjectStreamMapper.ToSyncTree(A3SObjectStreamMapper.ReadGzipObjectStream(bytes));
        }

        /// <inheritdoc />
        public async Task<A3SServerInfo> ReadServerInfoAsync(string source, CancellationToken cancellationToken = default)
        {
            var bytes = await ReadKindBytesAsync(source, A3SObjectStreamMapper.KindServerInfo, cancellationToken)
                .ConfigureAwait(false);
            return A3SObjectStreamMapper.ToServerInfo(A3SObjectStreamMapper.ReadGzipObjectStream(bytes));
        }

        /// <inheritdoc />
        public async Task<A3SChangelogs> ReadChangelogsAsync(string source, CancellationToken cancellationToken = default)
        {
            var bytes = await ReadKindBytesAsync(source, A3SObjectStreamMapper.KindChangelogs, cancellationToken)
                .ConfigureAwait(false);
            return A3SObjectStreamMapper.ToChangelogs(A3SObjectStreamMapper.ReadGzipObjectStream(bytes));
        }

        /// <summary>Read classic <c>.a3s/events</c> (empty list is valid).</summary>
        public async Task<A3SEvents> ReadEventsAsync(string source, CancellationToken cancellationToken = default)
        {
            var bytes = await ReadKindBytesAsync(source, A3SObjectStreamMapper.KindEvents, cancellationToken)
                .ConfigureAwait(false);
            return A3SObjectStreamMapper.ToEvents(A3SObjectStreamMapper.ReadGzipObjectStream(bytes));
        }

        /// <inheritdoc />
        public Task WriteMetadataAsync(string contentRoot, string kind, object metadataPayload, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "SerializerA3SMetadataStore write path is not implemented yet (Phase 4 read-first). " +
                "Use PreferNativeA3SMetadataStore / BridgeA3SMetadataStore for classic ObjectStream writes.");

        /// <inheritdoc />
        public Task WriteAllAsync(string contentRoot, A3SMetadataWriteSet files, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(
                "SerializerA3SMetadataStore write path is not implemented yet (Phase 4 read-first). " +
                "Use PreferNativeA3SMetadataStore / BridgeA3SMetadataStore for classic ObjectStream writes.");

        internal async Task<byte[]> ReadKindBytesAsync(string source, string kind, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Metadata source (URL or path) is required.", nameof(source));
            if (string.IsNullOrWhiteSpace(kind))
                throw new ArgumentException("Metadata kind is required.", nameof(kind));

            var trimmed = source.Trim();
            if (IsRemoteUrl(trimmed))
                return await DownloadRemoteKindAsync(trimmed, kind, cancellationToken).ConfigureAwait(false);

            return await Task.Run(() => ReadLocalKindBytes(trimmed, kind), cancellationToken).ConfigureAwait(false);
        }

        private static bool IsRemoteUrl(string source)
        {
            return source.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                   || source.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static byte[] ReadLocalKindBytes(string source, string kind)
        {
            var path = Path.GetFullPath(source);
            if (File.Exists(path))
            {
                var fileName = Path.GetFileName(path);
                if (string.Equals(fileName, kind, StringComparison.OrdinalIgnoreCase))
                    return File.ReadAllBytes(path);

                var parentName = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
                if (string.Equals(parentName, A3SUrlHelpers.A3SFolderName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(fileName, kind, StringComparison.OrdinalIgnoreCase))
                    return File.ReadAllBytes(path);

                // Single-file source that is not the requested kind — look beside it in .a3s/
                if (string.Equals(parentName, A3SUrlHelpers.A3SFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    var sibling = Path.Combine(Path.GetDirectoryName(path)!, kind);
                    if (File.Exists(sibling))
                        return File.ReadAllBytes(sibling);
                }
            }

            if (Directory.Exists(path))
            {
                string a3sDir;
                if (string.Equals(Path.GetFileName(path), A3SUrlHelpers.A3SFolderName, StringComparison.OrdinalIgnoreCase))
                    a3sDir = path;
                else
                {
                    a3sDir = Path.Combine(path, A3SUrlHelpers.A3SFolderName);
                    if (!Directory.Exists(a3sDir))
                        throw new DirectoryNotFoundException($"No .a3s directory under content root: {path}");
                }

                var file = Path.Combine(a3sDir, kind);
                if (!File.Exists(file))
                    throw new FileNotFoundException($"Missing classic metadata file '{kind}'.", file);
                return File.ReadAllBytes(file);
            }

            throw new FileNotFoundException($"Local A3S metadata source not found: {source}");
        }

        private async Task<byte[]> DownloadRemoteKindAsync(string source, string kind, CancellationToken cancellationToken)
        {
            var baseUrl = NormalizeRemoteA3sBase(source);
            var uri = new Uri(baseUrl + kind.TrimStart('/'));
            using var response = await _httpClient.GetAsync(uri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"Failed to download {uri} (HTTP {(int)response.StatusCode} {response.ReasonPhrase}).");
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Accepts content-root, <c>…/.a3s</c>, or <c>…/.a3s/autoconfig</c> URLs; returns <c>…/.a3s/</c>.
        /// </summary>
        public static string NormalizeRemoteA3sBase(string url)
        {
            var trimmed = url.Trim().TrimEnd('/');
            var lower = trimmed.ToLowerInvariant();
            string[] kinds =
            {
                A3SObjectStreamMapper.KindAutoconfig,
                A3SObjectStreamMapper.KindSync,
                A3SObjectStreamMapper.KindServerInfo,
                A3SObjectStreamMapper.KindChangelogs,
                A3SObjectStreamMapper.KindEvents
            };

            foreach (var kind in kinds)
            {
                var suffix = "/.a3s/" + kind;
                if (lower.EndsWith(suffix, StringComparison.Ordinal))
                    return trimmed[..^kind.Length];
            }

            if (lower.EndsWith("/.a3s", StringComparison.Ordinal))
                return trimmed + "/";

            return trimmed + "/.a3s/";
        }
    }
}
