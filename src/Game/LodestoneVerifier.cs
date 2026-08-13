using System.Collections.Concurrent;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace MarketStats.Game
{
    /// <summary>Lodestone で名前を照合した結果。</summary>
    public sealed class NameVerification
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>そのデータセンターに同名のキャラクターが存在したか。</summary>
        public bool Exists { get; set; }

        /// <summary>見つかったキャラクターのワールド名（1 件だけ見つかった場合）。</summary>
        public string WorldName { get; set; } = string.Empty;

        /// <summary>名前とワールドが一致した 1 人のキャラクターページ ID。特定できなければ 0。</summary>
        public long LodestoneId { get; set; }

        public int HitCount { get; set; }

        /// <summary>名前が完全に一致した件数。1 件なら本人を特定できたことになる。</summary>
        public int ExactMatches { get; set; }

        public DateTime CheckedLocal { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// 推定した名前が本当に実在するかを Lodestone で確認する。
    ///
    /// 推定は「マーケットの購入履歴に出てきた名前」を使うので普通は実在するが、
    /// 自分のデータセンターに居ない相手なら、そのリテイナーの持ち主ではあり得ない。
    /// 実在確認とワールドの特定に使い、推定の裏取り（あるいは棄却）に役立てる。
    ///
    /// 外部サイトへの通信になるため既定では無効。
    /// 有効にした場合も、同じ名前は再照会せずキャッシュから返す。
    /// </summary>
    public sealed class LodestoneVerifier : IDisposable
    {
        private static readonly TimeSpan CacheDuration = TimeSpan.FromDays(3);
        private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(5);

        private readonly HttpClient _http;
        private readonly ConcurrentDictionary<string, NameVerification> _cache = new(StringComparer.OrdinalIgnoreCase);
        private DateTime _lastRequestUtc = DateTime.MinValue;

        public LodestoneVerifier()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (compatible; MarketStats-Dalamud/0.6)");
        }

        public int CachedCount => _cache.Count;

        public NameVerification? GetCached(string name) =>
            _cache.TryGetValue(name, out var result) &&
            DateTime.Now - result.CheckedLocal < CacheDuration
                ? result
                : null;

        public async Task<NameVerification> VerifyAsync(string name)
        {
            var cached = GetCached(name);
            if (cached != null) return cached;

            var result = new NameVerification { Name = name, CheckedLocal = DateTime.Now };

            try
            {
                // 連続照会にならないよう間隔を空ける。
                var since = DateTime.UtcNow - _lastRequestUtc;
                if (since < MinimumInterval)
                    await Task.Delay(MinimumInterval - since).ConfigureAwait(false);
                _lastRequestUtc = DateTime.UtcNow;

                // ワールドが分かっていれば、それで絞って検索する（別人が大量に出るのを防ぐ）。
                var world = LodestoneLink.ResolveKnownWorld(name);
                var url = LodestoneLink.BuildSearchUrl(name, world);
                var html = await _http.GetStringAsync(url).ConfigureAwait(false);

                // 検索結果の各項目から「ページ ID・名前・ワールド」を取り出す。
                var entries = Regex.Matches(
                    html,
                    @"/lodestone/character/(?<id>\d+)/""[^>]*>\s*(?<name>[^<]+)</a>.*?entry__world[^>]*>\s*(?<world>[^<（(]+)",
                    RegexOptions.Singleline);

                result.HitCount = entries.Count;
                result.Exists = entries.Count > 0;

                // 名前が完全に一致するものだけを本人候補とする。
                var exact = entries
                    .Select(m => new
                    {
                        Id = long.TryParse(m.Groups["id"].Value, out var v) ? v : 0,
                        Name = m.Groups["name"].Value.Trim(),
                        World = m.Groups["world"].Value.Trim(),
                    })
                    .Where(e => e.Id != 0 &&
                                string.Equals(e.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // ワールドまで分かっていれば、そこで 1 人に絞り込む。
                if (!string.IsNullOrEmpty(world))
                    exact = exact
                        .Where(e => e.World.StartsWith(world, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                result.ExactMatches = exact.Count;

                if (exact.Count >= 1)
                {
                    result.WorldName = exact[0].World;

                    // 候補が 1 人に絞れたときだけ、本人のページとして扱う。
                    if (exact.Count == 1) result.LodestoneId = exact[0].Id;
                }
                else if (entries.Count > 0)
                {
                    result.WorldName = entries[0].Groups["world"].Value.Trim();
                }
            }
            catch (Exception e)
            {
                result.Error = e.Message;
                Plugin.PluginLog.Debug($"Lodestone での照合に失敗しました ({name}): {e.Message}");
            }

            _cache[name] = result;
            return result;
        }

        public void Dispose() => _http.Dispose();
    }
}
