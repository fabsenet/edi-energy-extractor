using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NLog;

namespace EdiEnergyExtractorCore
{
    public class CacheForcableHttpClient
    {
        private readonly ILogger _log = LogManager.GetCurrentClassLogger();
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1);

        private bool PreferCache { get; }

        public CacheForcableHttpClient(bool preferCache=false)
        {
            PreferCache = preferCache;
        }

        public async Task<(Stream content, string filename)> GetAsync(string uri)
        {
            var tempFileBaseName = Path.GetTempPath() + "edidocs_"+ BitConverter.ToString(MD5.Create().ComputeHash(Encoding.UTF8.GetBytes(uri)));
            var tempResponseFileName = tempFileBaseName + ".cache";
            var tempFilenameFileName = tempFileBaseName + ".name";

            var cacheExists = File.Exists(tempResponseFileName) && File.Exists(tempFilenameFileName);
            if (!cacheExists || !PreferCache)
            {
                //load from web
                byte[] content;
                string filename;
                await _semaphore.WaitAsync();
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

                    HttpClient httpClient = new HttpClient(new HttpClientHandler());

                    using (var result = await httpClient.GetAsync(uri))
                    {
                        result.EnsureSuccessStatusCode();

                        content = await result.Content.ReadAsByteArrayAsync();
                        filename = result.Content.Headers.ContentDisposition?.FileName.Replace("\"", "") ?? "";
                    }
                }
                catch (Exception ex)
                {
                    var msg = $"failed to load '{uri}': {ex.GetType().Name}: {ex.Message}";
                    _log.Error(ex, msg);
                    throw new Exception(msg, ex);
                }
                finally
                {
                    _semaphore.Release();
                }

                //cache it!
                await File.WriteAllBytesAsync(tempResponseFileName, content);
                await File.WriteAllTextAsync(tempFilenameFileName, filename);

                return (new MemoryStream(content), filename);
            }
            else
            {

                //read from cache ignoring freshness or cache header!
                var content = await File.ReadAllBytesAsync(tempResponseFileName);
                var filename = await File.ReadAllTextAsync(tempFilenameFileName);

                return (new MemoryStream(content), filename);
            }
        }
    }
}