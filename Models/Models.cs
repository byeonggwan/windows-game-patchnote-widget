using System;
using System.Collections.Generic;

namespace PatchNoteMailbox.Models
{
    public class GameConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string IconUrl { get; set; }
        public int PollIntervalMinutes { get; set; }
        public ProviderConfig Provider { get; set; }

        public GameConfig()
        {
            PollIntervalMinutes = 60;
            Provider = new ProviderConfig();
        }
    }

    public class ProviderConfig
    {
        /// <summary>
        /// Supported types: "html-regex", "next-data", "json-api", "rss"
        /// </summary>
        public string Type { get; set; }
        public string Url { get; set; }
        public string BaseUrl { get; set; }
        public Dictionary<string, string> Headers { get; set; }

        // For html-regex
        public string ItemRegex { get; set; }
        public string IdGroup { get; set; }
        public string TitleGroup { get; set; }
        public string DateGroup { get; set; }
        public string UrlGroup { get; set; }

        // For next-data / json-api
        public string ItemsPath { get; set; }
        public string IdField { get; set; }
        public string IdRegex { get; set; }
        public string TitleField { get; set; }
        public string DateField { get; set; }
        public string UrlField { get; set; }

        // Common filtering
        public FilterConfig Filter { get; set; }

        public ProviderConfig()
        {
            Type = "html-regex";
            Headers = new Dictionary<string, string>();
            Filter = new FilterConfig();
        }
    }

    public class FilterConfig
    {
        public List<string> TitleIncludes { get; set; }
        public string TitleRegex { get; set; }

        public FilterConfig()
        {
            TitleIncludes = new List<string>();
        }
    }

    public class PatchNoteItem
    {
        public string Idx { get; set; }
        public string Title { get; set; }
        public string Date { get; set; }
        public string Url { get; set; }
        public long SortKey { get; set; }
    }

    public class GameCacheEntry
    {
        public List<PatchNoteItem> Notes { get; set; }
        public List<string> ReadIds { get; set; }
        public string LastFetchedAt { get; set; }

        /// <summary>UTC time of the last successful fetch, round-trip format ("o"). Drives the stale caption.</summary>
        public string FetchedAtUtc { get; set; }

        public GameCacheEntry()
        {
            Notes = new List<PatchNoteItem>();
            ReadIds = new List<string>();
            LastFetchedAt = "";
            FetchedAtUtc = "";
        }
    }

    public class AppState
    {
        // Window position before the move to BWUI. BWUI keeps the position in settings.txt now;
        // these are only read once, to migrate it (see DockWindow.MigratePosition).
        public double DockLeft { get; set; }
        public double DockTop { get; set; }
        public string ExpandDirection { get; set; }
        public bool IsCollapsed { get; set; }
        public Dictionary<string, GameCacheEntry> Cache { get; set; }

        public AppState()
        {
            DockLeft = -1;
            DockTop = -1;
            ExpandDirection = "up";
            IsCollapsed = false;
            Cache = new Dictionary<string, GameCacheEntry>();
        }
    }
}
