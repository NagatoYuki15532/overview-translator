using System;

namespace Emby.Plugin.OverviewTranslator.Configuration
{
    /// <summary>
    /// One entry in the plugin's settings page. The settings UI is plain HTML driven by
    /// the REST API, so this type only describes layout; validation lives on the server.
    /// </summary>
    public sealed class SettingDescriptor
    {
        public string Key { get; set; }

        public string Label { get; set; }

        /// <summary>text | password | number | bool | select | textarea</summary>
        public string Type { get; set; }

        /// <summary>Options for "select".</summary>
        public string[] Options { get; set; }

        public string Help { get; set; }

        /// <summary>Backend this field belongs to, or null when it always applies.</summary>
        public string Backend { get; set; }
    }
}
