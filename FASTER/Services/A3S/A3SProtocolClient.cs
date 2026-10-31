using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Renci.SshNet;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Phase 1–2 ownership: HTTP(S) download + HTTP(S) PUT / SFTP upload in C#.
    /// Does not shell Arma3Sync for transfers.
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
                    $"A3SProtocolClient download supports HTTP(S) only; got '{remoteUri.Scheme}'.");

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

        /// <summary>
        /// Upload a local file to a remote URI.
        /// HTTP(S): PUT (WebDAV-style). SFTP: SSH.NET. Credentials come from <paramref name="endpoint"/>.
        /// </summary>
        public Task UploadAsync(
            string localPath,
            Uri remoteUri,
            A3SUploadEndpoint? endpoint = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(remoteUri);
            if (string.IsNullOrWhiteSpace(localPath))
                throw new ArgumentException("Local path is required.", nameof(localPath));
            if (!File.Exists(localPath))
                throw new FileNotFoundException("Local file to upload was not found.", localPath);

            var scheme = remoteUri.Scheme.ToLowerInvariant();
            return scheme switch
            {
                "http" or "https" => UploadHttpAsync(localPath, remoteUri, endpoint, cancellationToken),
                "sftp" => UploadSftpAsync(localPath, remoteUri, endpoint, cancellationToken),
                _ => throw new NotSupportedException(
                    $"A3SProtocolClient upload supports HTTP(S) and SFTP; got '{remoteUri.Scheme}'.")
            };
        }

        private async Task UploadHttpAsync(
            string localPath,
            Uri remoteUri,
            A3SUploadEndpoint? endpoint,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = stream.Length;

            using var request = new HttpRequestMessage(HttpMethod.Put, remoteUri) { Content = content };
            ApplyBasicAuth(request, endpoint);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"HTTP upload PUT failed ({(int)response.StatusCode} {response.ReasonPhrase}) for {remoteUri}. {TrimBody(body)}");
            }
        }

        private static Task UploadSftpAsync(
            string localPath,
            Uri remoteUri,
            A3SUploadEndpoint? endpoint,
            CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var host = remoteUri.Host;
                var port = remoteUri.IsDefaultPort ? 22 : remoteUri.Port;
                var username = string.IsNullOrWhiteSpace(endpoint?.Login) ? "anonymous" : endpoint!.Login;
                var password = endpoint?.Password ?? string.Empty;
                var remotePath = Uri.UnescapeDataString(remoteUri.AbsolutePath);
                if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
                    throw new InvalidOperationException($"SFTP remote path missing for {remoteUri}.");

                using var client = new SftpClient(host, port, username, password);
                client.ConnectionInfo.Timeout = TimeSpan.FromMinutes(2);
                client.Connect();
                try
                {
                    EnsureSftpDirectories(client, remotePath);
                    using var stream = File.OpenRead(localPath);
                    client.UploadFile(stream, remotePath, canOverride: true);
                }
                finally
                {
                    if (client.IsConnected)
                        client.Disconnect();
                }
            }, cancellationToken);
        }

        private static void EnsureSftpDirectories(SftpClient client, string remoteFilePath)
        {
            var normalized = remoteFilePath.Replace('\\', '/');
            var lastSlash = normalized.LastIndexOf('/');
            if (lastSlash <= 0)
                return;

            var dir = normalized[..lastSlash];
            if (string.IsNullOrEmpty(dir) || client.Exists(dir))
            {
                if (!string.IsNullOrEmpty(dir) && client.Exists(dir))
                    return;
            }

            var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var current = dir.StartsWith('/') ? "/" : string.Empty;
            foreach (var part in parts)
            {
                current = current.EndsWith('/') ? current + part : current + "/" + part;
                if (current == "/")
                    continue;
                if (!client.Exists(current))
                    client.CreateDirectory(current);
            }
        }

        private static void ApplyBasicAuth(HttpRequestMessage request, A3SUploadEndpoint? endpoint)
        {
            if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.Login))
                return;
            if (string.Equals(endpoint.Login, "anonymous", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(endpoint.Password))
            {
                return;
            }

            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{endpoint.Login}:{endpoint.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        }

        private static string TrimBody(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return string.Empty;
            body = body.Trim();
            return body.Length <= 200 ? body : body[..200] + "…";
        }

        public void Dispose()
        {
            if (_ownsHttp)
                _http.Dispose();
        }
    }
}
