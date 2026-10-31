using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Test / fixture metadata store that reads bridge-shaped JSON envelopes (not classic ObjectStream binaries).
    /// Not used as the product default — see <see cref="BridgeA3SMetadataStore"/>.
    /// </summary>
    public sealed class JsonA3SMetadataStore : IA3SMetadataStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        private readonly string? _fixedEnvelopePath;

        public JsonA3SMetadataStore()
        {
        }

        /// <summary>Always return the given bridge envelope JSON regardless of <c>source</c>.</summary>
        public JsonA3SMetadataStore(string fixedEnvelopePath)
        {
            _fixedEnvelopePath = fixedEnvelopePath;
        }

        /// <inheritdoc />
        public async Task<A3SMetadataBundle> ReadAllAsync(string source, CancellationToken cancellationToken = default)
        {
            var envelope = await ReadEnvelopeAsync(source, cancellationToken).ConfigureAwait(false);
            return BridgeA3SMetadataStore.ToBundle(envelope, requireSync: true, requireServerInfo: true, requireAutoconfig: true);
        }

        /// <inheritdoc />
        public async Task<A3SAutoconfigInfo> ReadAutoconfigAsync(string source, CancellationToken cancellationToken = default)
        {
            var bundle = await ReadAllAsync(source, cancellationToken).ConfigureAwait(false);
            return bundle.Autoconfig;
        }

        /// <inheritdoc />
        public async Task<A3SSyncTreeNode> ReadSyncTreeAsync(string source, CancellationToken cancellationToken = default)
        {
            var bundle = await ReadAllAsync(source, cancellationToken).ConfigureAwait(false);
            return bundle.SyncRoot;
        }

        /// <inheritdoc />
        public async Task<A3SServerInfo> ReadServerInfoAsync(string source, CancellationToken cancellationToken = default)
        {
            var bundle = await ReadAllAsync(source, cancellationToken).ConfigureAwait(false);
            return bundle.ServerInfo;
        }

        /// <inheritdoc />
        public Task WriteMetadataAsync(string contentRoot, object metadata, CancellationToken cancellationToken = default)
            => throw new NotImplementedException("JsonA3SMetadataStore is read-only (fixtures / tests).");

        private async Task<A3SBridgeEnvelope> ReadEnvelopeAsync(string source, CancellationToken cancellationToken)
        {
            var path = _fixedEnvelopePath ?? source;
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("JSON metadata path is required.", nameof(source));
            if (!File.Exists(path))
                throw new FileNotFoundException($"JSON metadata file not found: {path}", path);

            await using var stream = File.OpenRead(path);
            var envelope = await JsonSerializer.DeserializeAsync<A3SBridgeEnvelope>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (envelope == null)
                throw new InvalidOperationException($"Failed to deserialize A3S bridge JSON from {path}.");
            return envelope;
        }
    }
}
