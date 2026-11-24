using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using PatchNoteMailbox.Models;

namespace PatchNoteMailbox.Services
{
    public class PatchNoteFetcher
    {
        private static readonly HttpClient _http;
        private readonly JavaScriptSerializer _json;

        static PatchNoteFetcher()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };
            _http = new HttpClient(handler);
            _http.Timeout = TimeSpan.FromSeconds(15);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) PatchNoteMailbox/1.0");
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ko-KR,ko;q=0.9,en;q=0.8");
        }

        public PatchNoteFetcher()
        {
            _json = new JavaScriptSerializer();
            _json.MaxJsonLength = 20 * 1024 * 1024;
        }

        public async Task EnsureIconCachedAsync(GameConfig game, StorageService storage)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.IconUrl)) return;
            string localPath = storage.GetLocalIconPath(game.Id);
            if (File.Exists(localPath) && new FileInfo(localPath).Length > 0) return;

            try
            {
                if (File.Exists(game.IconUrl))
                {
                    File.Copy(game.IconUrl, localPath, true);
                    return;
                }
                byte[] bytes = await _http.GetByteArrayAsync(game.IconUrl);
                if (bytes != null && bytes.Length > 0)
                {
                    File.WriteAllBytes(localPath, bytes);
                }
            }
            catch { }
        }

        public async Task<List<PatchNoteItem>> FetchAsync(GameConfig game)
        {
            if (game == null || game.Provider == null || string.IsNullOrWhiteSpace(game.Provider.Url))
                return new List<PatchNoteItem>();

            string type = (game.Provider.Type ?? "html-regex").Trim().ToLowerInvariant();
            string body = await GetStringAsync(game.Provider.Url, game.Provider.Headers);

            List<PatchNoteItem> rawItems;
            if (type == "html-regex")
            {
                rawItems = ParseHtmlRegex(body, game.Provider);
            }
            else if (type == "next-data")
            {
                rawItems = ParseNextData(body, game.Provider);
            }
            else if (type == "json-api")
            {
                rawItems = ParseJsonApi(body, game.Provider);
            }
            else if (type == "rss")
            {
                rawItems = ParseRss(body, game.Provider);
            }
            else
            {
                throw new NotSupportedException("지원하지 않는 수집 어댑터 타입: " + type);
            }

            // Apply common filter + deduplicate + sort latest-first
            var filtered = new List<PatchNoteItem>();
            var seen = new HashSet<string>();

            foreach (var item in rawItems)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Title)) continue;
                if (!PassesFilter(item.Title, game.Provider.Filter)) continue;

                string key = string.IsNullOrWhiteSpace(item.Idx) ? item.Url : item.Idx;
                if (string.IsNullOrWhiteSpace(key) || seen.Contains(key)) continue;
                seen.Add(key);

                item.Url = NormalizeUrl(item.Url, game.Provider.BaseUrl);
                filtered.Add(item);
            }

            filtered.Sort(delegate(PatchNoteItem a, PatchNoteItem b)
            {
                int cmp = b.SortKey.CompareTo(a.SortKey);
                if (cmp != 0) return cmp;
                return string.CompareOrdinal(b.Idx ?? "", a.Idx ?? "");
            });

            return filtered;
        }

        private async Task<string> GetStringAsync(string url, Dictionary<string, string> headers)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (headers != null)
                {
                    foreach (var kv in headers)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Key))
                        {
                            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value ?? "");
                        }
                    }
                }
                using (var res = await _http.SendAsync(req))
                {
                    res.EnsureSuccessStatusCode();
                    byte[] bytes = await res.Content.ReadAsByteArrayAsync();
                    return Encoding.UTF8.GetString(bytes);
                }
            }
        }

        private List<PatchNoteItem> ParseHtmlRegex(string html, ProviderConfig p)
        {
            var result = new List<PatchNoteItem>();
            if (string.IsNullOrWhiteSpace(p.ItemRegex)) return result;

            var regex = new Regex(p.ItemRegex, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var matches = regex.Matches(html);

            string idGrp = string.IsNullOrWhiteSpace(p.IdGroup) ? "idx" : p.IdGroup;
            string titleGrp = string.IsNullOrWhiteSpace(p.TitleGroup) ? "title" : p.TitleGroup;
            string dateGrp = string.IsNullOrWhiteSpace(p.DateGroup) ? "date" : p.DateGroup;
            string urlGrp = string.IsNullOrWhiteSpace(p.UrlGroup) ? "url" : p.UrlGroup;

            foreach (Match m in matches)
            {
                string idx = m.Groups[idGrp].Success ? CleanText(m.Groups[idGrp].Value) : "";
                string title = m.Groups[titleGrp].Success ? CleanText(m.Groups[titleGrp].Value) : "";
                string rawDate = m.Groups[dateGrp].Success ? CleanText(m.Groups[dateGrp].Value) : "";
                string url = m.Groups[urlGrp].Success ? WebUtility.HtmlDecode(m.Groups[urlGrp].Value.Trim()) : "";

                if (string.IsNullOrWhiteSpace(idx) && !string.IsNullOrWhiteSpace(url))
                {
                    idx = ComputeShortHash(url);
                }

                long sortKey;
                string formattedDate = NormalizeDate(rawDate, out sortKey);
                if (sortKey == 0)
                {
                    long numIdx;
                    if (long.TryParse(idx, out numIdx)) sortKey = numIdx;
                }

                result.Add(new PatchNoteItem
                {
                    Idx = idx,
                    Title = title,
                    Date = formattedDate,
                    Url = url,
                    SortKey = sortKey
                });
            }
            return result;
        }

        private List<PatchNoteItem> ParseNextData(string html, ProviderConfig p)
        {
            var m = Regex.Match(html, "<script\\s+id=\"__NEXT_DATA__\"[^>]*>([\\s\\S]*?)</script>", RegexOptions.IgnoreCase);
            if (!m.Success) return new List<PatchNoteItem>();
            return ParseJsonApi(m.Groups[1].Value, p);
        }

        private List<PatchNoteItem> ParseJsonApi(string jsonStr, ProviderConfig p)
        {
            var result = new List<PatchNoteItem>();
            object root = _json.DeserializeObject(jsonStr);
            object target = NavigateJsonPath(root, p.ItemsPath);

            var enumerable = target as IEnumerable;
            if (enumerable == null || target is string) return result;

            int seq = 0;
            foreach (object obj in enumerable)
            {
                seq++;
                string title = CleanText(GetJsonString(obj, p.TitleField));
                string rawDate = CleanText(GetJsonString(obj, p.DateField));
                string url = GetJsonString(obj, p.UrlField).Trim();
                string rawId = GetJsonString(obj, p.IdField).Trim();

                string idx = rawId;
                if (!string.IsNullOrWhiteSpace(p.IdRegex) && !string.IsNullOrWhiteSpace(rawId))
                {
                    var im = Regex.Match(rawId, p.IdRegex, RegexOptions.IgnoreCase);
                    if (im.Success)
                    {
                        idx = im.Groups.Count > 1 ? im.Groups[1].Value : im.Value;
                        idx = idx.Replace("-", ".");
                    }
                }
                if (string.IsNullOrWhiteSpace(idx))
                {
                    idx = string.IsNullOrWhiteSpace(url) ? seq.ToString() : ComputeShortHash(url);
                }

                long sortKey;
                string formattedDate = NormalizeDate(rawDate, out sortKey);

                result.Add(new PatchNoteItem
                {
                    Idx = idx,
                    Title = title,
                    Date = formattedDate,
                    Url = url,
                    SortKey = sortKey
                });
            }
            return result;
        }

        private List<PatchNoteItem> ParseRss(string xmlStr, ProviderConfig p)
        {
            var result = new List<PatchNoteItem>();
            var doc = XDocument.Parse(xmlStr);

            // Support both RSS 2.0 (<item>) and Atom (<entry>)
            var items = doc.Descendants().Where(delegate(XElement e)
            {
                string n = e.Name.LocalName.ToLowerInvariant();
                return n == "item" || n == "entry";
            });

            int seq = 0;
            foreach (var el in items)
            {
                seq++;
                string title = CleanText(GetChildValue(el, "title"));
                string rawDate = CleanText(GetChildValue(el, "pubDate"));
                if (string.IsNullOrWhiteSpace(rawDate)) rawDate = CleanText(GetChildValue(el, "updated"));
                if (string.IsNullOrWhiteSpace(rawDate)) rawDate = CleanText(GetChildValue(el, "published"));

                string url = GetChildValue(el, "link");
                if (string.IsNullOrWhiteSpace(url))
                {
                    var linkEl = el.Elements().FirstOrDefault(delegate(XElement c) { return c.Name.LocalName.ToLowerInvariant() == "link"; });
                    if (linkEl != null && linkEl.Attribute("href") != null)
                        url = linkEl.Attribute("href").Value;
                }

                string guid = GetChildValue(el, "guid");
                if (string.IsNullOrWhiteSpace(guid)) guid = GetChildValue(el, "id");
                string idx = "";
                if (!string.IsNullOrWhiteSpace(p.IdRegex))
                {
                    var im = Regex.Match(guid + " " + url, p.IdRegex, RegexOptions.IgnoreCase);
                    if (im.Success) idx = im.Groups.Count > 1 ? im.Groups[1].Value : im.Value;
                }
                if (string.IsNullOrWhiteSpace(idx))
                {
                    idx = ComputeShortHash(!string.IsNullOrWhiteSpace(guid) ? guid : url + seq);
                }

                long sortKey;
                string formattedDate = NormalizeDate(rawDate, out sortKey);

                result.Add(new PatchNoteItem
                {
                    Idx = idx,
                    Title = title,
                    Date = formattedDate,
                    Url = url,
                    SortKey = sortKey
                });
            }
            return result;
        }

        private static string GetChildValue(XElement parent, string localName)
        {
            var child = parent.Elements().FirstOrDefault(delegate(XElement e)
            {
                return string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase);
            });
            return child != null ? child.Value : "";
        }

        private static object NavigateJsonPath(object root, string path)
        {
            if (root == null || string.IsNullOrWhiteSpace(path) || path.Trim() == "$")
                return root;

            string[] segments = path.Split('.');
            object current = root;

            foreach (string rawSeg in segments)
            {
                if (current == null) return null;
                string seg = rawSeg.Trim();
                if (seg == "$" || seg == "") continue;

                // Check array filter syntax: fieldName[prop=value]
                var fm = Regex.Match(seg, @"^([^\[]+)\[([^=\]]+)=([^\]]+)\]$");
                if (fm.Success)
                {
                    string fieldName = fm.Groups[1].Value;
                    string filterProp = fm.Groups[2].Value;
                    string filterVal = fm.Groups[3].Value;

                    current = GetProperty(current, fieldName);
                    var arr = current as IEnumerable;
                    if (arr == null) return null;

                    object matched = null;
                    foreach (object elem in arr)
                    {
                        string val = GetJsonString(elem, filterProp);
                        if (string.Equals(val, filterVal, StringComparison.OrdinalIgnoreCase))
                        {
                            matched = elem;
                            break;
                        }
                    }
                    current = matched;
                }
                else
                {
                    current = GetProperty(current, seg);
                }
            }
            return current;
        }

        private static object GetProperty(object obj, string propName)
        {
            var dict = obj as IDictionary<string, object>;
            if (dict == null) return null;
            object val;
            if (dict.TryGetValue(propName, out val)) return val;
            foreach (var kv in dict)
            {
                if (string.Equals(kv.Key, propName, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            }
            return null;
        }

        private static string GetJsonString(object obj, string dotPath)
        {
            if (obj == null || string.IsNullOrWhiteSpace(dotPath)) return "";
            object val = NavigateJsonPath(obj, dotPath);
            return val != null ? val.ToString() : "";
        }

        private static bool PassesFilter(string title, FilterConfig filter)
        {
            if (filter == null) return true;

            if (filter.TitleIncludes != null && filter.TitleIncludes.Count > 0)
            {
                bool anyMatch = false;
                foreach (string kw in filter.TitleIncludes)
                {
                    if (string.IsNullOrWhiteSpace(kw)) continue;
                    if (title.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        anyMatch = true;
                        break;
                    }
                }
                if (!anyMatch) return false;
            }

            if (!string.IsNullOrWhiteSpace(filter.TitleRegex))
            {
                if (!Regex.IsMatch(title, filter.TitleRegex, RegexOptions.IgnoreCase))
                    return false;
            }

            return true;
        }

        private static string CleanText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string noTags = Regex.Replace(raw, "<[^>]+>", "");
            string decoded = WebUtility.HtmlDecode(noTags);
            return Regex.Replace(decoded, "\\s+", " ").Trim();
        }

        private static string NormalizeUrl(string url, string baseUrl)
        {
            if (string.IsNullOrWhiteSpace(url)) return "";
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                return baseUrl.TrimEnd('/') + "/" + url.TrimStart('/');
            }
            return url;
        }

        private static string NormalizeDate(string rawDate, out long sortKey)
        {
            sortKey = 0;
            if (string.IsNullOrWhiteSpace(rawDate)) return "-";

            DateTime dt;
            if (DateTime.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dt))
            {
                sortKey = dt.ToUniversalTime().Ticks;
                return dt.ToLocalTime().ToString("yyyy.MM.dd");
            }

            var dm = Regex.Match(rawDate, @"(\d{4})[.\-/](\d{1,2})[.\-/](\d{1,2})");
            if (dm.Success)
            {
                int y = int.Parse(dm.Groups[1].Value);
                int mo = int.Parse(dm.Groups[2].Value);
                int d = int.Parse(dm.Groups[3].Value);
                try
                {
                    dt = new DateTime(y, mo, d);
                    sortKey = dt.Ticks;
                    return string.Format("{0:D4}.{1:D2}.{2:D2}", y, mo, d);
                }
                catch { }
            }

            return rawDate;
        }

        private static string ComputeShortHash(string input)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < input.Length; i++)
                {
                    hash = (hash ^ input[i]) * 16777619;
                }
                return (hash % 100000).ToString("D5");
            }
        }
    }
}
