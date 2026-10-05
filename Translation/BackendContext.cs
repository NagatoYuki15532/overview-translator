using System;
using System.Net.Http;
using Emby.Plugin.OverviewTranslator.Configuration;
using MediaBrowser.Model.Logging;

namespace Emby.Plugin.OverviewTranslator.Translation
{
    /// <summary>
    /// Creates the HTTP clients used by the translation backends.
    /// Backends never build a client themselves: proxy handling and timeouts are centralised here.
    /// </summary>
    public interface IHttpClientProvider
    {
        /// <summary>
        /// Creates a client with the configured proxy applied. The caller owns it and must
        /// dispose it. Throws <see cref="BackendConfigurationException"/> when the proxy
        /// settings are unusable (rather than silently going direct).
        /// </summary>
        HttpClient Create(NetworkSettings network, int timeoutSeconds);
    }

    /// <summary>
    /// Everything a backend needs in order to run. Passed to the constructor so backends
    /// stay free of DI knowledge and are trivial to unit test.
    /// </summary>
    public sealed class BackendContext
    {
        /// <summary>Settings scoped to the backend being constructed.</summary>
        public PluginConfiguration Configuration { get; set; }

        /// <summary>Creates HTTP clients.</summary>
        public IHttpClientProvider Http { get; set; }

        /// <summary>Emby's logger. Backends log through this so everything lands in embyserver.txt.</summary>
        public ILogger Logger { get; set; }

        /// <summary>Throws when a required collaborator is missing. Call from the backend constructor.</summary>
        public void Validate()
        {
            if (this.Configuration == null)
            {
                throw new ArgumentNullException("Configuration");
            }

            if (this.Http == null)
            {
                throw new ArgumentNullException("Http");
            }
        }
    }

    /// <summary>
    /// Shared plumbing for backends: null-safe logging and a single place that decides
    /// what "the network layer failed" looks like.
    /// </summary>
    public abstract class BackendBase : ITranslationBackend
    {
        protected BackendContext Context { get; private set; }

        protected ILogger Log
        {
            get { return this.Context == null ? null : this.Context.Logger; }
        }

        protected BackendBase(BackendContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException("context");
            }

            context.Validate();
            this.Context = context;
        }

        public abstract TranslationBackend Kind { get; }

        public abstract bool SupportsBatch { get; }

        public abstract System.Threading.Tasks.Task<System.Collections.Generic.IList<TranslationResult>> TranslateAsync(
            System.Collections.Generic.IList<TranslationItem> items,
            TranslationRequest request,
            System.Threading.CancellationToken cancellationToken);

        public abstract System.Threading.Tasks.Task<string> TestAsync(System.Threading.CancellationToken cancellationToken);

        /// <summary>Writes a line to Emby's log without ever throwing because of logging.</summary>
        protected void LogInfo(string message, params object[] args)
        {
            var logger = this.Log;
            if (logger != null)
            {
                logger.Info(message, args);
            }
        }

        /// <summary>Writes an error line to Emby's log without ever throwing because of logging.</summary>
        protected void LogError(string message, params object[] args)
        {
            var logger = this.Log;
            if (logger != null)
            {
                logger.Error(message, args);
            }
        }
    }
}
