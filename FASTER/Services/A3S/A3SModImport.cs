using System;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Phase 1 ownership: after sync, scan <c>@*</c> under the repository local path and upsert into
    /// <c>Settings.Default.armaMods</c>; optionally sync into Deployments.
    /// </summary>
    public class A3SModImport
    {
        /// <summary>Import synced addon folders as local Arma mods. Implemented in Phase 1.</summary>
        public Task ImportFromRepositoryAsync(A3SRepository repository, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("A3SModImport.ImportFromRepositoryAsync is implemented in Phase 1.");
        }
    }
}
