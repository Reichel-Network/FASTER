using System;
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
        private readonly IA3SMetadataStore _metadataStore;

        public A3SSyncService(IA3SMetadataStore metadataStore)
        {
            _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
        }

        /// <summary>Check local tree against remote sync metadata. Implemented in Phase 1.</summary>
        public Task CheckAsync(A3SRepository repository, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("A3SSyncService.CheckAsync is implemented in Phase 1.");
        }

        /// <summary>Sync addon tree from remote repository. Implemented in Phase 1.</summary>
        public Task SyncAsync(A3SRepository repository, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("A3SSyncService.SyncAsync is implemented in Phase 1.");
        }
    }
}
