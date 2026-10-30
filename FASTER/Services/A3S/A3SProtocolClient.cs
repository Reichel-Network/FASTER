using System;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Phase 1–2 ownership: HTTP(S), FTP/FTPS, SFTP, WebDAV download/upload implemented in C#.
    /// Does not shell Arma3Sync for transfers.
    /// </summary>
    public class A3SProtocolClient
    {
        /// <summary>Download a remote file to a local path. Implemented in Phase 1.</summary>
        public Task DownloadAsync(Uri remoteUri, string localPath, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("A3SProtocolClient.DownloadAsync is implemented in Phase 1.");
        }

        /// <summary>Upload a local file to a remote URI. Implemented in Phase 2.</summary>
        public Task UploadAsync(string localPath, Uri remoteUri, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("A3SProtocolClient.UploadAsync is implemented in Phase 2.");
        }
    }
}
