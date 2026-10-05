using System;
using System.Collections.Generic;
using Emby.Plugin.OverviewTranslator.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.Plugin.OverviewTranslator
{
    /// <summary>
    /// Plugin entry point. Emby discovers this type by scanning the plugins folder
    /// for a public class deriving from <see cref="BasePlugin{TConfiguration}"/>.
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public const string PluginName = "Overview Translator";

        /// <summary>Stable GUID. Must never change: Emby keys the config file and plugin folder by it.</summary>
        public static readonly Guid PluginGuid = new Guid("7f3c1a26-2d4e-4b58-9c11-6a0e5d8b4f21");

        /// <summary>Set in the constructor. Emby creates exactly one instance.</summary>
        private static Plugin _instance;

        /// <summary>
        /// The live configuration, never null.
        ///
        /// Deliberately NOT named <c>Configuration</c>: that name already belongs to the
        /// inherited instance property on <c>BasePlugin&lt;T&gt;</c>, and hiding it with a
        /// static member makes the base constructor call resolve to the static one.
        ///
        /// Safe to call only after Emby has finished constructing the plugin, because reading
        /// it triggers config-file loading, which needs the assembly path Emby sets afterwards.
        /// </summary>
        public static PluginConfiguration CurrentConfiguration
        {
            get
            {
                if (_instance == null)
                {
                    throw new InvalidOperationException("The Overview Translator plugin has not been constructed yet.");
                }

                var configuration = _instance.Configuration;
                configuration.Normalize();
                return configuration;
            }
        }

        /// <summary>The singleton plugin instance, or null before Emby loads the plugin.</summary>
        public static Plugin Instance
        {
            get { return _instance; }
        }

        private readonly IApplicationPaths _applicationPaths;

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            _instance = this;
            _applicationPaths = applicationPaths;

            // CRITICAL: never touch this.Configuration here.
            //
            // Emby assigns BasePlugin.AssemblyFilePath *after* constructing the plugin, and
            // BasePlugin.LoadConfiguration() composes
            //     Path.Combine(PluginConfigurationsPath, Path.ChangeExtension(AssemblyFileName, ".xml"))
            // from it. Reading Configuration during construction therefore throws
            // ArgumentNullException('path2') and Emby reports "Error creating ... Plugin",
            // leaving the plugin unloaded (verified against Emby 4.10.1.0).
            //
            // This is exactly why the framework's own plugins (and the deployed Bangumi plugin)
            // only assign fields in their constructor. Configuration is normalised lazily by
            // Plugin.CurrentConfiguration instead.
            //
            // Registering the data folder must also wait: it needs PluginConfigurationsPath, and
            // more importantly the translation service must not be built before Emby has finished
            // loading assemblies. ServerEntryPoint.Run() performs both.
        }

        public override string Name
        {
            get { return PluginName; }
        }

        public override string Description
        {
            get { return "Translates item overviews with a pluggable translation engine (DeepSeek, any OpenAI-compatible API, or keyless online translators)."; }
        }

        public override Guid Id
        {
            get { return PluginGuid; }
        }

        /// <summary>Directory for this plugin's own data (cache, log files).</summary>
        public string TranslationDataPath
        {
            get
            {
                return System.IO.Path.Combine(_applicationPaths.PluginConfigurationsPath, "overviewtranslator");
            }
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = "overviewtranslator",
                    EmbeddedResourcePath = this.GetType().Namespace + ".Configuration.configPage.html",
                    EnableInMainMenu = true,
                    DisplayName = "简介翻译"
                },
                new PluginPageInfo
                {
                    // Loaded into the web client on every page; it injects the detail-page button.
                    Name = "overviewtranslator.js",
                    EmbeddedResourcePath = this.GetType().Namespace + ".Web.overviewtranslator.js"
                }
            };
        }
    }
}
