using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Phase 1–2 ownership: HTTP(S) download (and later upload) implemented in C#.
    /// Does not shell Arma3Sync for transfers. FTP/SFTP/WebDAV can extend the same surface later.
    /// </summary>
    public class A3SProtocolClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;

        public A3SProtocolClient()
            : this(CreateDefaultClient(), ownsHttp: true)
        {
        }

        public A3SProtocolClient(HttpClient httpClient, bool ownsHttp = false)
        {
            _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ownsHttp = ownsHttp;
        }

        private static HttpClient CreateDefaultClient()
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(30)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FASTER-A3S/1.0");
            return client;
        }

        /// <summary>Download a remote HTTP(S) file to a local path (creates parent directories).</summary>
        public async Task DownloadAsync(Uri remoteUri, string localPath, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(remoteUri);
            if (string.IsNullOrWhiteSpace(localPath))
                throw new ArgumentException("Local path is required.", nameof(localPath));

            if (remoteUri.Scheme is not ("http" or "https"))
                throw new NotSupportedException(
                    $"A3SProtocolClient Phase 1 supports HTTP(S) only; got '{remoteUri.Scheme}'. " +
                    "FTP/SFTP/WebDAV land in a later increment.");

            var directory = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var response = await _http.GetAsync(remoteUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var tempPath = localPath + ".faster.download";
            try
            {
                await using (var remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var local = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await remote.CopyToAsync(local, cancellationToken).ConfigureAwait(false);
                }

                if (File.Exists(localPath))
                    File.Delete(localPath);
                File.Move(tempPath, localPath);
            }
            catch
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
                throw;
            }
        }

        /// <summary>Download classic <c>.a3s/{name}</c> under the repository content root.</summary>
        public Task DownloadMetadataAsync(Uri contentRoot, string metadataFileName, string localPath, CancellationToken cancellationToken = default)
        {
            var uri = A3SUrlHelpers.GetMetadataUri(contentRoot, metadataFileName);
            return DownloadAsync(uri, localPath, cancellationToken);
        }

        /// <summary>Download an addon file by relative sync path (uses <c>/</c> separators).</summary>
        public Task DownloadAddonFileAsync(Uri contentRoot, string relativePathWithSlashes, string localPath, CancellationToken cancellationToken = default)
        {
            var uri = A3SUrlHelpers.GetAddonFileUri(contentRoot, relativePathWithSlashes);
            return DownloadAsync(uri, localPath, cancellationToken);
        }

        /// <summary>Upload a local file to a remote URI. Implemented in Phase 2.</summary>
        public Task UploadAsync(string localPath, Uri remoteUri, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException("A3SProtocolClient.UploadAsync is implemented in Phase 2.");
        }

        public void Dispose()
        {
            if (_ownsHttp)
                _http.Dispose();
        }
    }
}
