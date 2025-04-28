using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace EdiEnergyExtractor;

internal class CacheForcableHttpClient : IDisposable
{
    private readonly Logger _log = LogManager.GetCurrentClassLogger();
    private static readonly SemaphoreSlim _semaphore = new(1);

    private bool PreferCache { get; }

    private readonly string? _username;
    private readonly string? _password;
    private HttpClient? _httpClient;

    public CacheForcableHttpClient(bool preferCache, string? username, string? password)
    {
        PreferCache = preferCache;
        _username = username;
        _password = password;

        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "cache"));
    }

    private readonly HttpClientHandler _httpClientHandler = new()
    {
        UseProxy = true,
        Proxy = null, // use system proxy
        DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials
    };
    private record AuthenticationResponse
    {
        public string? Token { get; init; }
        public string? DisplayName { get; init; }
        public DateTime? ExpirationDate { get; init; }
        public List<string>? Features { get; init; }
    }

    private async Task<HttpClient> GetHttpClient()
    {
        if (_httpClient != null) return _httpClient;

        var httpClient = new HttpClient(_httpClientHandler);

        if (_username != null && _password != null)
        {
            _log.Debug($"Using credentials for {_username} to login");
            var byteArray = Encoding.ASCII.GetBytes($"{_username}:{_password}");
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

            var loginUri = new Uri("https://www.bdew-mako.de/api/login");
            var response = await httpClient.GetFromJsonAsync<AuthenticationResponse>(loginUri).ConfigureAwait(false);
            if (string.IsNullOrEmpty(response?.Token))
            {
                throw new InvalidOperationException($"Login failed for {_username}.");
            }
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", response.Token);
        }

        _httpClient = httpClient;
        return httpClient;
    }

    public async Task<(MemoryStream content, string filename)> GetAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri, nameof(uri));

        var tempFileBaseName = Path.Combine(AppContext.BaseDirectory, "cache", $"edidocs_{BitConverter.ToString(SHA512.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)))}");
        var tempResponseFileName = tempFileBaseName + ".cachedata";
        var tempFilenameFileName = tempFileBaseName + ".cachename";

        var cacheExists = File.Exists(tempResponseFileName) && File.Exists(tempFilenameFileName);
        if (!cacheExists || !PreferCache)
        {
            //load from web
            byte[] content;
            string filename;
            await _semaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                if (PreferCache)
                {
                    _log.Warn($"PreferCache is set but need loading web ressource anyway (cache miss): {uri}");
                }
                else
                {
                    _log.Debug($"loading web ressource: {uri}");
                }

                using var result = await (await GetHttpClient().ConfigureAwait(false)).GetAsync(uri).ConfigureAwait(false);
                result.EnsureSuccessStatusCode();

                content = await result.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                filename = result.Content.Headers.ContentDisposition?.FileName?.Replace("\"", "", StringComparison.Ordinal) ?? "";
            }
            catch (Exception ex)
            {
                var msg = $"failed to load '{uri}': {ex.GetType().Name}: {ex.Message}";
                _log.Error(ex, msg);
                throw new InvalidOperationException(msg, ex);
            }
            finally
            {
                _semaphore.Release();
            }

            //cache it!
            await File.WriteAllBytesAsync(tempResponseFileName, content).ConfigureAwait(false);
            await File.WriteAllTextAsync(tempFilenameFileName, filename).ConfigureAwait(false);

            return (new MemoryStream(content), filename);
        }
        else
        {

            //read from cache ignoring freshness or cache header!
            var content = await File.ReadAllBytesAsync(tempResponseFileName).ConfigureAwait(false);
            var filename = await File.ReadAllTextAsync(tempFilenameFileName).ConfigureAwait(false);

            return (new MemoryStream(content), filename);
        }
    }

    public void Dispose()
    {
        _httpClientHandler.Dispose();
        _httpClient?.Dispose();
        _httpClient = null;
        _semaphore.Dispose();
    }
}
