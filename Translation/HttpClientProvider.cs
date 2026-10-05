using System;
using System.Net;
using System.Net.Http;
using Emby.Plugin.OverviewTranslator.Configuration;
using Emby.Plugin.OverviewTranslator.Services;

namespace Emby.Plugin.OverviewTranslator.Translation
{
    /// <summary>
    /// Builds the HTTP clients every backend uses. Centralised so proxy handling,
    /// timeouts and a bad-proxy error surface identically for all engines.
    /// </summary>
    public sealed class HttpClientProvider : IHttpClientProvider
    {
        public HttpClient Create(NetworkSettings network, int timeoutSeconds)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            if (network != null && network.UseProxy)
            {
                if (string.IsNullOrWhiteSpace(network.ProxyHost) || network.ProxyPort <= 0)
                {
                    throw new BackendConfigurationException(
                        "已启用代理，但代理地址或端口为空。请在「简介翻译」设置页填写代理主机与端口，或取消勾选「使用代理」。");
                }

                var webProxy = new WebProxy(network.ProxyHost, network.ProxyPort);

                // Several local proxies (including the one on this machine) serve both
                // HTTP and HTTPS through the same port, so no bypass list is needed.
                handler.Proxy = webProxy;
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
            }

            var seconds = timeoutSeconds > 0 ? timeoutSeconds : 60;

            var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(seconds)
            };

            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Emby-OverviewTranslator/1.0");

            return client;
        }
    }
}
