using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace EdiEnergyExtractor;

public class CacheForcableHttpClient
{
    private readonly Logger _log = LogManager.GetCurrentClassLogger();
    private static readonly SemaphoreSlim _semaphore = new(1);

    private bool PreferCache { get; }

    public CacheForcableHttpClient(bool preferCache = false)
    {
        PreferCache = preferCache;
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "cache"));
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

                using var handler = new HttpClientHandler
                {
                    UseProxy = true,
                    Proxy = null, // use system proxy
                    DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials
                };
                using var httpClient = new HttpClient(handler);

                using var result = await httpClient.GetAsync(uri).ConfigureAwait(false);
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
}
