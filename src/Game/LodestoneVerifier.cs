using System.Collections.Concurrent;
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

        public int HitCount { get; set; }
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

                var url = LodestoneLink.BuildSearchUrl(name);
                var html = await _http.GetStringAsync(url).ConfigureAwait(false);

                // 検索結果の件数表示から実在を判断する。
                var hits = Regex.Matches(html, "entry__link--character|entry__chara__name");
                result.HitCount = hits.Count;
                result.Exists = hits.Count > 0;

                var world = Regex.Match(html, @"entry__world""[^>]*>\s*([^<（(]+)");
                if (world.Success) result.WorldName = world.Groups[1].Value.Trim();
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
