using System;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Phase 2 ownership: tree walk, SHA-1, zsync/zip generation, revision/changelog updates via <see cref="IA3SMetadataStore"/>.
    /// </summary>
    public class A3SBuildService
    {
        private readonly IA3SMetadataStore _metadataStore;

        public A3SBuildService(IA3SMetadataStore metadataStore)
        {
            _metadataStore = metadataStore ?? throw new ArgumentNullException(nameof(metadataStore));
        }

        /// <summary>Build <c>.a3s/*</c> metadata for a publish root. Implemented in Phase 2.</summary>
        public Task BuildAsync(A3SRepository repository, string contentRoot, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("A3SBuildService.BuildAsync is implemented in Phase 2.");
        }
    }
}
