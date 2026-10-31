using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Test / fixture metadata store that reads/writes bridge-shaped JSON envelopes
    /// (not classic ObjectStream binaries). Product default is <see cref="SerializerA3SMetadataStore"/>.
    /// </summary>
    public sealed class JsonA3SMetadataStore : IA3SMetadataStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly string? _fixedEnvelopePath;
        private readonly string? _writeRoot;

        public JsonA3SMetadataStore()
        {
        }

        /// <summary>Always return the given bridge envelope JSON regardless of <c>source</c>.</summary>
        public JsonA3SMetadataStore(string fixedEnvelopePath)
        {
            _fixedEnvelopePath = fixedEnvelopePath;
        }

        /// <summary>
        /// Read from <paramref name="fixedEnvelopePath"/> when set as source-independent fixture,
        /// and persist writes under <paramref name="writeRoot"/>/<c>.a3s-json/</c> for round-trip tests.
        /// </summary>
        public JsonA3SMetadataStore(string? fixedEnvelopePath, string writeRoot)
        {
            _fixedEnvelopePath = fixedEnvelopePath;
            _writeRoot = writeRoot;
        }

        /// <summary>Last multi-write set (tests).</summary>
        public A3SMetadataWriteSet? LastWriteSet { get; private set; }

        /// <inheritdoc />
        public async Task<A3SMetadataBundle> ReadAllAsync(string source, CancellationToken cancellationToken = default)
        {
            var envelope = await ReadEnvelopeAsync(source, "all", cancellationToken).ConfigureAwait(false);
            return BridgeA3SMetadataStore.ToBundle(envelope, requireSync: true, requireServerInfo: true, requireAutoconfig: true);
        }

        /// <inheritdoc />
        public async Task<A3SAutoconfigInfo> ReadAutoconfigAsync(string source, CancellationToken cancellationToken = default)
        {
            var fromWrite = await TryReadWrittenKindAsync(source, "autoconfig", cancellationToken).ConfigureAwait(false);
            if (fromWrite?.Autoconfig != null || fromWrite?.Files?.Autoconfig != null)
                return fromWrite.Autoconfig ?? fromWrite.Files!.Autoconfig!;

            var bundle = await ReadAllAsync(source, cancellationToken).ConfigureAwait(false);
            return bundle.Autoconfig;
        }

        /// <inheritdoc />
        public async Task<A3SSyncTreeNode> ReadSyncTreeAsync(string source, CancellationToken cancellationToken = default)
        {
            // Prefer per-kind write artifact when present (build round-trip).
            var fromWrite = await TryReadWrittenKindAsync(source, "sync", cancellationToken).ConfigureAwait(false);
            if (fromWrite?.Sync != null || fromWrite?.Files?.Sync != null)
                return fromWrite.Sync ?? fromWrite.Files!.Sync!;

            var bundle = await ReadAllAsync(source, cancellationToken).ConfigureAwait(false);
            return bundle.SyncRoot;
        }

        /// <inheritdoc />
        public async Task<A3SServerInfo> ReadServerInfoAsync(string source, CancellationToken cancellationToken = default)
        {
            var fromWrite = await TryReadWrittenKindAsync(source, "serverinfo", cancellationToken).ConfigureAwait(false);
            if (fromWrite?.ServerInfo != null || fromWrite?.Files?.ServerInfo != null)
                return fromWrite.ServerInfo ?? fromWrite.Files!.ServerInfo!;

            var bundle = await ReadAllAsync(source, cancellationToken).ConfigureAwait(false);
            return bundle.ServerInfo;
        }

        /// <inheritdoc />
        public async Task<A3SChangelogs> ReadChangelogsAsync(string source, CancellationToken cancellationToken = default)
        {
            var fromWrite = await TryReadWrittenKindAsync(source, "changelogs", cancellationToken).ConfigureAwait(false);
            var changelogs = fromWrite?.Changelogs ?? fromWrite?.Files?.Changelogs;
            if (changelogs != null)
                return changelogs;

            var envelope = await ReadEnvelopeAsync(source, "all", cancellationToken).ConfigureAwait(false);
            return envelope.Changelogs ?? envelope.Files?.Changelogs ?? new A3SChangelogs();
        }

        /// <inheritdoc />
        public async Task<A3SEvents> ReadEventsAsync(string source, CancellationToken cancellationToken = default)
        {
            var fromWrite = await TryReadWrittenKindAsync(source, "events", cancellationToken).ConfigureAwait(false);
            var events = fromWrite?.Events ?? fromWrite?.Files?.Events;
            if (events != null)
                return events;

            var envelope = await ReadEnvelopeAsync(source, "all", cancellationToken).ConfigureAwait(false);
            return envelope.Events ?? envelope.Files?.Events ?? new A3SEvents();
        }

        /// <inheritdoc />
        public async Task WriteMetadataAsync(string contentRoot, string kind, object metadataPayload, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(metadataPayload);
            var normalized = (kind ?? string.Empty).Trim().ToLowerInvariant();
            var root = ResolveWriteRoot(contentRoot);
            Directory.CreateDirectory(root);

            var envelope = new JsonObject
            {
                ["bridgeVersion"] = 1,
                ["kind"] = normalized,
                [normalized] = JsonSerializer.SerializeToNode(metadataPayload, metadataPayload.GetType(), JsonOptions)
            };

            var path = Path.Combine(root, normalized + ".json");
            await File.WriteAllTextAsync(path, envelope.ToJsonString(), cancellationToken).ConfigureAwait(false);
            MergeIntoAll(root, normalized, metadataPayload);
        }

        /// <inheritdoc />
        public async Task WriteAllAsync(string contentRoot, A3SMetadataWriteSet files, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(files);
            LastWriteSet = files;

            var root = ResolveWriteRoot(contentRoot);
            Directory.CreateDirectory(root);

            var filesNode = new JsonObject();
            if (files.Autoconfig != null)
                filesNode["autoconfig"] = JsonSerializer.SerializeToNode(files.Autoconfig, JsonOptions);
            if (files.Sync != null)
                filesNode["sync"] = JsonSerializer.SerializeToNode(files.Sync, JsonOptions);
            if (files.ServerInfo != null)
                filesNode["serverinfo"] = JsonSerializer.SerializeToNode(files.ServerInfo, JsonOptions);
            if (files.Changelogs != null)
                filesNode["changelogs"] = JsonSerializer.SerializeToNode(files.Changelogs, JsonOptions);
            filesNode["events"] = files.Events == null
                ? null
                : JsonSerializer.SerializeToNode(files.Events, files.Events.GetType(), JsonOptions);

            var envelope = new JsonObject
            {
                ["bridgeVersion"] = 1,
                ["kind"] = "all",
                ["files"] = filesNode
            };

            var allPath = Path.Combine(root, "all.json");
            await File.WriteAllTextAsync(allPath, envelope.ToJsonString(), cancellationToken).ConfigureAwait(false);

            // Also drop classic-looking placeholders so publish can find metadata file names.
            var a3sDir = Path.Combine(contentRoot, A3SUrlHelpers.A3SFolderName);
            Directory.CreateDirectory(a3sDir);
            foreach (var name in new[] { "sync", "serverinfo", "autoconfig", "changelogs" })
            {
                var marker = Path.Combine(a3sDir, name);
                if (!File.Exists(marker))
                    await File.WriteAllTextAsync(marker, $"json-store-placeholder:{name}", cancellationToken).ConfigureAwait(false);
            }
        }

        private string ResolveWriteRoot(string contentRoot)
        {
            if (!string.IsNullOrWhiteSpace(_writeRoot))
                return Path.Combine(_writeRoot, ".a3s-json");
            if (string.IsNullOrWhiteSpace(contentRoot))
                throw new ArgumentException("Content root is required.", nameof(contentRoot));
            return Path.Combine(contentRoot, ".a3s-json");
        }

        private async Task<A3SBridgeEnvelope?> TryReadWrittenKindAsync(string source, string kind, CancellationToken cancellationToken)
        {
            try
            {
                var root = ResolveWriteRoot(source);
                var allPath = Path.Combine(root, "all.json");
                if (File.Exists(allPath))
                    return await DeserializeEnvelopeAsync(allPath, cancellationToken).ConfigureAwait(false);

                var kindPath = Path.Combine(root, kind + ".json");
                if (File.Exists(kindPath))
                    return await DeserializeEnvelopeAsync(kindPath, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // fall through to fixture
            }

            return null;
        }

        private void MergeIntoAll(string root, string kind, object payload)
        {
            var allPath = Path.Combine(root, "all.json");
            // DeepClone so we can attach into a new envelope — JsonNode rejects re-parenting.
            JsonObject filesNode;
            if (File.Exists(allPath)
                && JsonNode.Parse(File.ReadAllText(allPath)) is JsonObject existing
                && existing["files"] is JsonObject existingFiles)
            {
                filesNode = existingFiles.DeepClone().AsObject();
            }
            else
            {
                filesNode = new JsonObject();
            }

            filesNode[kind] = JsonSerializer.SerializeToNode(payload, payload.GetType(), JsonOptions);
            var envelope = new JsonObject
            {
                ["bridgeVersion"] = 1,
                ["kind"] = "all",
                ["files"] = filesNode
            };
            File.WriteAllText(allPath, envelope.ToJsonString());
        }

        private async Task<A3SBridgeEnvelope> ReadEnvelopeAsync(string source, string kind, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrWhiteSpace(_fixedEnvelopePath) && File.Exists(_fixedEnvelopePath))
                return await DeserializeEnvelopeAsync(_fixedEnvelopePath, cancellationToken).ConfigureAwait(false);

            var written = await TryReadWrittenKindAsync(source, kind, cancellationToken).ConfigureAwait(false);
            if (written != null)
                return written;

            var path = source;
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("JSON metadata path is required.", nameof(source));
            if (!File.Exists(path))
                throw new FileNotFoundException($"JSON metadata file not found: {path}", path);

            return await DeserializeEnvelopeAsync(path, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<A3SBridgeEnvelope> DeserializeEnvelopeAsync(string path, CancellationToken cancellationToken)
        {
            await using var stream = File.OpenRead(path);
            var envelope = await JsonSerializer.DeserializeAsync<A3SBridgeEnvelope>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (envelope == null)
                throw new InvalidOperationException($"Failed to deserialize A3S bridge JSON from {path}.");
            return envelope;
        }
    }
}
