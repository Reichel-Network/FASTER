using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Sole boundary for classic <c>.a3s/*</c> metadata I/O (autoconfig, sync, serverinfo, changelogs, events).
    /// Phase 4 default: <see cref="PreferNativeA3SMetadataStore"/> prefers
    /// <see cref="SerializerA3SMetadataStore"/> for classic ObjectStream <b>reads</b>;
    /// <see cref="BridgeA3SMetadataStore"/> remains for <b>writes</b> and optional read fallback
    /// (JSON contract in BRIDGE.md / Arma3Sync docs/BRIDGE.md). No operator-facing JAR UX.
    /// </summary>
    public interface IA3SMetadataStore
    {
        /// <summary>
        /// Read all available classic metadata from an autoconfig URL, content root, <c>.a3s</c> dir, or file.
        /// Preferred entry (bridge: <c>read --source … --kind all --out -</c>).
        /// </summary>
        Task<A3SMetadataBundle> ReadAllAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>Read autoconfig only (<c>--kind autoconfig</c>).</summary>
        Task<A3SAutoconfigInfo> ReadAutoconfigAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>Read sync tree only (<c>--kind sync</c>).</summary>
        Task<A3SSyncTreeNode> ReadSyncTreeAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>Read serverinfo only (<c>--kind serverinfo</c>).</summary>
        Task<A3SServerInfo> ReadServerInfoAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>Read changelogs only (<c>--kind changelogs</c>). Missing file may throw.</summary>
        Task<A3SChangelogs> ReadChangelogsAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>Read events only (<c>--kind events</c>). Missing file may return empty list or throw per store.</summary>
        Task<A3SEvents> ReadEventsAsync(string source, CancellationToken cancellationToken = default);

        /// <summary>
        /// Write one classic <c>.a3s/{kind}</c> file under <paramref name="contentRoot"/>
        /// (bridge: <c>write --target … --kind … --in …</c>).
        /// <paramref name="metadataPayload"/> is the domain object for that kind.
        /// </summary>
        Task WriteMetadataAsync(string contentRoot, string kind, object metadataPayload, CancellationToken cancellationToken = default);

        /// <summary>
        /// Write several classic metadata files in one bridge invoke
        /// (<c>write --target … --kind all --in</c> files envelope). Null entries are skipped.
        /// Preferred Phase 2 Build path.
        /// </summary>
        Task WriteAllAsync(string contentRoot, A3SMetadataWriteSet files, CancellationToken cancellationToken = default);
    }

    /// <summary>Payload for <see cref="IA3SMetadataStore.WriteAllAsync"/> (bridge <c>files</c> envelope).</summary>
    public sealed class A3SMetadataWriteSet
    {
        public A3SAutoconfigInfo? Autoconfig { get; init; }
        public A3SSyncTreeNode? Sync { get; init; }
        public A3SServerInfo? ServerInfo { get; init; }
        public A3SChangelogs? Changelogs { get; init; }
        public A3SEvents? Events { get; init; }
    }
}
