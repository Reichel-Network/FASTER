using System;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// (A) Headless metadata-bridge adapter. Phase 0 stub — real process spawn against a
    /// bundled/hidden helper lands in Phase 1. Operators never configure a JAR path as product UX;
    /// <c>Settings.a3sMetadataBridgePath</c> is Advanced-only (empty = bundled default later).
    /// </summary>
    public sealed class BridgeA3SMetadataStore : IA3SMetadataStore
    {
        /// <summary>
        /// Optional override path to the headless metadata helper. Empty means use the bundled path when present.
        /// </summary>
        public string BridgePath { get; }

        public BridgeA3SMetadataStore(string? bridgePath = null)
        {
            BridgePath = bridgePath ?? string.Empty;
        }

        /// <inheritdoc />
        public Task<object?> ReadAutoconfigAsync(string source, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException(
                "BridgeA3SMetadataStore.ReadAutoconfigAsync is implemented in Phase 1 (headless metadata bridge).");
        }

        /// <inheritdoc />
        public Task<object?> ReadSyncTreeAsync(string contentRoot, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException(
                "BridgeA3SMetadataStore.ReadSyncTreeAsync is implemented in Phase 1 (headless metadata bridge).");
        }

        /// <inheritdoc />
        public Task<object?> ReadServerInfoAsync(string contentRoot, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException(
                "BridgeA3SMetadataStore.ReadServerInfoAsync is implemented in Phase 1 (headless metadata bridge).");
        }

        /// <inheritdoc />
        public Task WriteMetadataAsync(string contentRoot, object metadata, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException(
                "BridgeA3SMetadataStore.WriteMetadataAsync is implemented in Phase 1 / Phase 2 (headless metadata bridge).");
        }
    }
}
