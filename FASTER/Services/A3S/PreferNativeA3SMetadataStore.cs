using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Phase 4 composition: prefer <see cref="SerializerA3SMetadataStore"/> for classic ObjectStream reads;
    /// keep <see cref="BridgeA3SMetadataStore"/> for writes and as optional read fallback.
    /// </summary>
    public sealed class PreferNativeA3SMetadataStore : IA3SMetadataStore
    {
        private readonly SerializerA3SMetadataStore _native;
        private readonly BridgeA3SMetadataStore _bridge;
        private readonly bool _preferNativeReads;
        private readonly bool _fallbackToBridgeOnReadError;

        public PreferNativeA3SMetadataStore(
            SerializerA3SMetadataStore native,
            BridgeA3SMetadataStore bridge,
            bool preferNativeReads = true,
            bool fallbackToBridgeOnReadError = true)
        {
            _native = native ?? throw new ArgumentNullException(nameof(native));
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _preferNativeReads = preferNativeReads;
            _fallbackToBridgeOnReadError = fallbackToBridgeOnReadError;
        }

        /// <summary>Which implementation handled the last successful read (for diagnostics/tests).</summary>
        public string? LastReadBackend { get; private set; }

        /// <inheritdoc />
        public Task<A3SMetadataBundle> ReadAllAsync(string source, CancellationToken cancellationToken = default)
            => ReadWithPreferenceAsync(s => s.ReadAllAsync(source, cancellationToken), () => _bridge.ReadAllAsync(source, cancellationToken));

        /// <inheritdoc />
        public Task<A3SAutoconfigInfo> ReadAutoconfigAsync(string source, CancellationToken cancellationToken = default)
            => ReadWithPreferenceAsync(s => s.ReadAutoconfigAsync(source, cancellationToken), () => _bridge.ReadAutoconfigAsync(source, cancellationToken));

        /// <inheritdoc />
        public Task<A3SSyncTreeNode> ReadSyncTreeAsync(string source, CancellationToken cancellationToken = default)
            => ReadWithPreferenceAsync(s => s.ReadSyncTreeAsync(source, cancellationToken), () => _bridge.ReadSyncTreeAsync(source, cancellationToken));

        /// <inheritdoc />
        public Task<A3SServerInfo> ReadServerInfoAsync(string source, CancellationToken cancellationToken = default)
            => ReadWithPreferenceAsync(s => s.ReadServerInfoAsync(source, cancellationToken), () => _bridge.ReadServerInfoAsync(source, cancellationToken));

        /// <inheritdoc />
        public Task<A3SChangelogs> ReadChangelogsAsync(string source, CancellationToken cancellationToken = default)
            => ReadWithPreferenceAsync(s => s.ReadChangelogsAsync(source, cancellationToken), () => _bridge.ReadChangelogsAsync(source, cancellationToken));

        /// <inheritdoc />
        public Task<A3SEvents> ReadEventsAsync(string source, CancellationToken cancellationToken = default)
            => ReadWithPreferenceAsync(s => s.ReadEventsAsync(source, cancellationToken), () => _bridge.ReadEventsAsync(source, cancellationToken));

        /// <inheritdoc />
        public Task WriteMetadataAsync(string contentRoot, string kind, object metadataPayload, CancellationToken cancellationToken = default)
        {
            // Native write is not ready — always bridge for classic ObjectStream publish.
            LastReadBackend = null;
            return _bridge.WriteMetadataAsync(contentRoot, kind, metadataPayload, cancellationToken);
        }

        /// <inheritdoc />
        public Task WriteAllAsync(string contentRoot, A3SMetadataWriteSet files, CancellationToken cancellationToken = default)
        {
            LastReadBackend = null;
            return _bridge.WriteAllAsync(contentRoot, files, cancellationToken);
        }

        private async Task<T> ReadWithPreferenceAsync<T>(
            Func<SerializerA3SMetadataStore, Task<T>> nativeRead,
            Func<Task<T>> bridgeRead)
        {
            if (!_preferNativeReads)
            {
                LastReadBackend = "bridge";
                return await bridgeRead().ConfigureAwait(false);
            }

            try
            {
                var value = await nativeRead(_native).ConfigureAwait(false);
                LastReadBackend = "serializer";
                return value;
            }
            catch (Exception ex) when (_fallbackToBridgeOnReadError && IsFallbackCandidate(ex))
            {
                try
                {
                    var value = await bridgeRead().ConfigureAwait(false);
                    LastReadBackend = "bridge-fallback";
                    return value;
                }
                catch (A3SBridgeNotFoundException)
                {
                    // Surface the native failure — more actionable when no JRE/bridge is installed.
                    throw new A3SBridgeException(
                        "Native ObjectStream read failed and metadata bridge is unavailable. " +
                        $"Native error: {ex.Message}",
                        ex);
                }
                catch (A3SBridgeException)
                {
                    throw new A3SBridgeException(
                        "Native ObjectStream read failed and bridge fallback also failed. " +
                        $"Native error: {ex.Message}",
                        ex);
                }
            }
        }

        private static bool IsFallbackCandidate(Exception ex)
            => ex is InvalidDataException
               or NotSupportedException
               or IOException
               or HttpRequestException
               or FormatException;
    }
}
