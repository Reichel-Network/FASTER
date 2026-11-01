using System;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using FASTER.Models;
using FASTER.Properties;

namespace FASTER.Services.A3S
{
    /// <summary>
    /// Localhost-only token-protected HTTP API for Discord-driven event deploy.
    /// Binds <c>http://127.0.0.1:{port}/</c>. Prefer starting from the FASTER UI process.
    /// </summary>
    public sealed class FasterLocalApiHost : IDisposable
    {
        public const int DefaultPort = 8787;
        public const string EnvEnabled = "FASTER_LOCAL_API_ENABLED";
        public const string EnvToken = "FASTER_LOCAL_API_TOKEN";
        public const string EnvPort = "FASTER_LOCAL_API_PORT";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        private readonly A3SRemoteDeployService _deploy;
        private readonly string _token;
        private readonly int _port;
        private HttpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private bool _listening;

        public FasterLocalApiHost(
            A3SRemoteDeployService deployService,
            string token,
            int port = DefaultPort)
        {
            _deploy = deployService ?? throw new ArgumentNullException(nameof(deployService));
            if (string.IsNullOrWhiteSpace(token))
                throw new ArgumentException("API token is required.", nameof(token));
            if (port is < 1 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(port));

            _token = token.Trim();
            _port = port;
        }

        public int Port => _port;
        public bool IsListening => _listening;

        /// <summary>
        /// Resolve enable/port/token from env (preferred) then user settings.
        /// Returns null when disabled or token missing.
        /// </summary>
        public static FasterLocalApiHost? TryCreateFromSettings(
            Func<ServerProfile, Task>? launchAsync = null)
        {
            var settings = Settings.Default;

            var enabledEnv = Environment.GetEnvironmentVariable(EnvEnabled);
            var enabled = !string.IsNullOrWhiteSpace(enabledEnv)
                ? enabledEnv is "1" or "true" or "TRUE" or "yes" or "YES"
                : settings.a3sLocalApiEnabled;

            if (!enabled)
                return null;

            var token = Environment.GetEnvironmentVariable(EnvToken);
            if (string.IsNullOrWhiteSpace(token))
                token = settings.a3sLocalApiToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                Logger.Log("Local API enabled but token is empty — set FASTER_LOCAL_API_TOKEN or a3sLocalApiToken.");
                return null;
            }

            var port = DefaultPort;
            var portEnv = Environment.GetEnvironmentVariable(EnvPort);
            if (!string.IsNullOrWhiteSpace(portEnv) && int.TryParse(portEnv, out var envPort))
                port = envPort;
            else if (settings.a3sLocalApiPort > 0)
                port = settings.a3sLocalApiPort;

            var deploy = new A3SRemoteDeployService(launchAsync: launchAsync);
            return new FasterLocalApiHost(deploy, token!, port);
        }

        public void Start()
        {
            if (_listener != null)
                return;

            var prefix = $"http://127.0.0.1:{_port}/";
            _listener = new HttpListener();
            _listener.Prefixes.Add(prefix);
            _cts = new CancellationTokenSource();
            try
            {
                _listener.Start();
            }
            catch (Exception ex)
            {
                _listener.Close();
                _listener = null;
                throw new InvalidOperationException(
                    $"Could not bind local API on {prefix}. Is the port in use or URL ACL missing? {ex.Message}",
                    ex);
            }

            _listening = true;
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Logger.Log($"Local A3S API listening on {prefix} (token auth required).");
        }

        public void Stop()
        {
            _listening = false;
            try { _cts?.Cancel(); } catch { /* ignore */ }
            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listener?.Close(); } catch { /* ignore */ }
            _listener = null;
            try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
            _loop = null;
            _cts?.Dispose();
            _cts = null;
        }

        public void Dispose() => Stop();

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Local API accept error: {ex.Message}");
                    continue;
                }

                _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                if (!IsAuthorized(context.Request))
                {
                    await WriteJsonAsync(context.Response, 401, new { error = "Unauthorized" }).ConfigureAwait(false);
                    return;
                }

                var method = context.Request.HttpMethod?.ToUpperInvariant() ?? "GET";
                var path = context.Request.Url?.AbsolutePath?.TrimEnd('/') ?? "";
                if (string.IsNullOrEmpty(path))
                    path = "/";

                if (method == "GET" && path is "/v1/status" or "/status")
                {
                    var status = _deploy.GetStatus(_port, _listening);
                    await WriteJsonAsync(context.Response, 200, status).ConfigureAwait(false);
                    return;
                }

                if (method == "GET" && path is "/v1/events" or "/events")
                {
                    var repo = context.Request.QueryString["repo"];
                    var url = context.Request.QueryString["url"] ?? context.Request.QueryString["autoconfigUrl"];
                    try
                    {
                        var events = await _deploy.ListEventsAsync(repo, url).ConfigureAwait(false);
                        await WriteJsonAsync(context.Response, 200, events).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await WriteJsonAsync(context.Response, 400, new { error = ex.Message }).ConfigureAwait(false);
                    }

                    return;
                }

                if (method == "POST" && path is "/v1/deploy" or "/deploy")
                {
                    A3SRemoteDeployRequest? body;
                    try
                    {
                        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                        var raw = await reader.ReadToEndAsync().ConfigureAwait(false);
                        body = string.IsNullOrWhiteSpace(raw)
                            ? null
                            : JsonSerializer.Deserialize<A3SRemoteDeployRequest>(raw, JsonOptions);
                    }
                    catch (Exception ex)
                    {
                        await WriteJsonAsync(context.Response, 400, new { error = "Invalid JSON: " + ex.Message })
                            .ConfigureAwait(false);
                        return;
                    }

                    if (body == null)
                    {
                        await WriteJsonAsync(context.Response, 400, new { error = "JSON body required." })
                            .ConfigureAwait(false);
                        return;
                    }

                    try
                    {
                        A3SRemoteDeployService.ValidateRequest(body);
                        var result = await _deploy.DeployAsync(body).ConfigureAwait(false);
                        await WriteJsonAsync(context.Response, 200, result).ConfigureAwait(false);
                    }
                    catch (ArgumentException ex)
                    {
                        await WriteJsonAsync(context.Response, 400, new { error = ex.Message }).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException ex)
                    {
                        var code = ex.Message.Contains("already running", StringComparison.OrdinalIgnoreCase) ? 409 : 400;
                        await WriteJsonAsync(context.Response, code, new { error = ex.Message }).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        await WriteJsonAsync(context.Response, 500, new { error = ex.Message }).ConfigureAwait(false);
                    }

                    return;
                }

                if (method == "GET" && path is "/" or "/v1" or "/health")
                {
                    await WriteJsonAsync(context.Response, 200, new
                    {
                        service = "FASTER local A3S API",
                        bind = "127.0.0.1",
                        port = _port,
                        endpoints = new[] { "GET /v1/status", "GET /v1/events", "POST /v1/deploy" }
                    }).ConfigureAwait(false);
                    return;
                }

                await WriteJsonAsync(context.Response, 404, new { error = "Not found" }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try
                {
                    await WriteJsonAsync(context.Response, 500, new { error = ex.Message }).ConfigureAwait(false);
                }
                catch
                {
                    // ignore secondary failures
                }
            }
        }

        private bool IsAuthorized(HttpListenerRequest request)
        {
            var header = request.Headers["Authorization"];
            if (!string.IsNullOrWhiteSpace(header)
                && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var presented = header["Bearer ".Length..].Trim();
                return FixedTimeEquals(presented, _token);
            }

            var queryToken = request.QueryString["token"];
            if (!string.IsNullOrWhiteSpace(queryToken))
                return FixedTimeEquals(queryToken.Trim(), _token);

            return false;
        }

        private static bool FixedTimeEquals(string a, string b)
        {
            var ba = Encoding.UTF8.GetBytes(a);
            var bb = Encoding.UTF8.GetBytes(b);
            if (ba.Length != bb.Length)
                return false;
            var diff = 0;
            for (var i = 0; i < ba.Length; i++)
                diff |= ba[i] ^ bb[i];
            return diff == 0;
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, int status, object payload)
        {
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json);
            response.StatusCode = status;
            response.ContentType = "application/json; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            response.OutputStream.Close();
        }
    }
}
