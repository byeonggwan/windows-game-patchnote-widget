using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using PatchNoteMailbox.Models;

namespace PatchNoteMailbox.Services
{
    public class StorageService
    {
        private readonly string _baseDir;
        private readonly string _dataDir;
        private readonly string _iconsDir;
        private readonly string _gamesPath;
        private readonly string _statePath;
        private readonly JavaScriptSerializer _json;

        public string BaseDir { get { return _baseDir; } }
        public string DataDir { get { return _dataDir; } }
        public string IconsDir { get { return _iconsDir; } }

        public StorageService(string baseDir)
        {
            _baseDir = baseDir;
            _dataDir = Path.Combine(baseDir, "data");
            _iconsDir = Path.Combine(_dataDir, "icons");
            _gamesPath = Path.Combine(baseDir, "games.json");
            _statePath = Path.Combine(_dataDir, "state.json");
            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(_iconsDir);

            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = 10 * 1024 * 1024;
        }

        public List<GameConfig> LoadGames()
        {
            if (!File.Exists(_gamesPath))
            {
                var defaults = CreateDefaultGames();
                SaveGames(defaults);
                return defaults;
            }
            try
            {
                string raw = File.ReadAllText(_gamesPath, Encoding.UTF8);
                var list = _json.Deserialize<List<GameConfig>>(raw);
                if (list != null) return list;
            }
            catch { }
            return CreateDefaultGames();
        }

        public void SaveGames(List<GameConfig> games)
        {
            string raw = FormatJson(_json.Serialize(games));
            File.WriteAllText(_gamesPath, raw, Encoding.UTF8);
        }

        public AppState LoadState()
        {
            if (!File.Exists(_statePath))
            {
                return new AppState();
            }
            try
            {
                string raw = File.ReadAllText(_statePath, Encoding.UTF8);
                var st = _json.Deserialize<AppState>(raw);
                if (st != null)
                {
                    if (st.Cache == null) st.Cache = new Dictionary<string, GameCacheEntry>();
                    return st;
                }
            }
            catch { }
            return new AppState();
        }

        public void SaveState(AppState state)
        {
            string raw = _json.Serialize(state);
            File.WriteAllText(_statePath, raw, Encoding.UTF8);
        }

        public string GetLocalIconPath(string gameId)
        {
            return Path.Combine(_iconsDir, gameId + ".png");
        }

        public static List<GameConfig> CreateDefaultGames()
        {
            var list = new List<GameConfig>();

            // 1. 로스트아크 (API 키 불필요 - 공식 공지사항 HTML 파싱 + '업데이트 내역 안내' 필터)
            var lostArk = new GameConfig
            {
                Id = "lostark",
                Name = "로스트아크",
                IconUrl = "https://cdn-lostark.game.onstove.com/2018/obt/assets/images/common/icon/favicon-192.png",
                PollIntervalMinutes = 60,
                Provider = new ProviderConfig
                {
                    Type = "html-regex",
                    Url = "https://lostark.game.onstove.com/News/Notice/List?searchtype=title&searchtext=%EC%97%85%EB%8D%B0%EC%9D%B4%ED%8A%B8%20%EB%82%B4%EC%97%AD%20%EC%95%88%EB%82%B4",
                    BaseUrl = "https://lostark.game.onstove.com",
                    ItemRegex = "<a href=\"(?<url>/News/Notice/Views/(?<idx>\\d+)[^\"]*)\"[^>]*>[\\s\\S]*?list__title\">(?<title>[\\s\\S]*?)</span>[\\s\\S]*?list__date\"[^>]*>(?<date>[\\s\\S]*?)</div>\\s*</a>",
                    IdGroup = "idx",
                    TitleGroup = "title",
                    DateGroup = "date",
                    UrlGroup = "url",
                    Filter = new FilterConfig
                    {
                        TitleIncludes = new List<string> { "업데이트 내역 안내" }
                    }
                }
            };
            list.Add(lostArk);

            // 2. 리그 오브 레전드 (API 키 불필요 - 공식 KR 페이지 __NEXT_DATA__ JSON 파싱)
            var lol = new GameConfig
            {
                Id = "lol",
                Name = "리그 오브 레전드",
                IconUrl = "https://cmsassets.rgpub.io/sanity/images/dsfx7636/content_organization_live/fe7ae6c859e8c5a66324d6eea597f23bdc1f354a-104x104.png?accountingTag=LoL",
                PollIntervalMinutes = 60,
                Provider = new ProviderConfig
                {
                    Type = "next-data",
                    Url = "https://www.leagueoflegends.com/ko-kr/news/tags/patch-notes/",
                    BaseUrl = "https://www.leagueoflegends.com",
                    ItemsPath = "props.pageProps.page.blades[type=articleCardGrid].items",
                    IdField = "action.payload.url",
                    IdRegex = "patch-(\\d+-\\d+)",
                    TitleField = "title",
                    DateField = "publishedAt",
                    UrlField = "action.payload.url",
                    Filter = new FilterConfig
                    {
                        TitleIncludes = new List<string> { "패치 노트" }
                    }
                }
            };
            list.Add(lol);

            return list;
        }

        private static string FormatJson(string json)
        {
            var sb = new StringBuilder();
            bool inQuotes = false;
            int indent = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '"' && (i == 0 || json[i - 1] != '\\'))
                {
                    inQuotes = !inQuotes;
                    sb.Append(ch);
                }
                else if (!inQuotes)
                {
                    if (ch == '{' || ch == '[')
                    {
                        sb.Append(ch);
                        sb.AppendLine();
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                    }
                    else if (ch == '}' || ch == ']')
                    {
                        sb.AppendLine();
                        indent--;
                        sb.Append(new string(' ', Math.Max(0, indent) * 2));
                        sb.Append(ch);
                    }
                    else if (ch == ',')
                    {
                        sb.Append(ch);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                    }
                    else if (ch == ':')
                    {
                        sb.Append(": ");
                    }
                    else if (!char.IsWhiteSpace(ch))
                    {
                        sb.Append(ch);
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }
    }
}
