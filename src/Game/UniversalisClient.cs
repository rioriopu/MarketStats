using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace MarketStats.Game
{
    /// <summary>Universalis から取得した「現在の出品」1 件。</summary>
    public sealed class MarketListing
    {
        public string WorldName { get; set; } = string.Empty;
        public string RetainerName { get; set; } = string.Empty;
        public ulong ListingId { get; set; }
        public ulong RetainerId { get; set; }
        public long PricePerUnit { get; set; }
        public long Quantity { get; set; }
        public long Total { get; set; }
        public bool Hq { get; set; }
        public bool OnMannequin { get; set; }
        public long LastReviewUnix { get; set; }
        public DateTime LastReviewLocal { get; set; }
    }

    /// <summary>Universalis から取得した「販売履歴」1 件。</summary>
    public sealed class MarketHistoryEntry
    {
        public string WorldName { get; set; } = string.Empty;
        public string BuyerName { get; set; } = string.Empty;
        public long PricePerUnit { get; set; }
        public long Quantity { get; set; }
        public bool Hq { get; set; }
        public bool OnMannequin { get; set; }
        public long UnixTime { get; set; }

        public DateTime LocalTime => DateTimeOffset.FromUnixTimeSeconds(UnixTime).LocalDateTime;
    }

    public sealed class MarketSnapshot
    {
        public uint ItemId { get; set; }
        public string Scope { get; set; } = string.Empty;
        public List<MarketListing> Listings { get; } = new();
        public List<MarketHistoryEntry> History { get; } = new();
        public DateTime FetchedLocal { get; set; }
        public string? Error { get; set; }
    }

    /// <summary>
    /// Universalis（コミュニティ運営のマーケット情報 API）へのアクセス。
    ///
    /// 「購入者が買った物を再出品しているか」を確定する手段は存在しない。
    /// 出品側の情報として公開されるのはリテイナー名だけで、
    /// キャラクター名との対応は API からもゲームからも取得できないため。
    /// ここでは参考情報として「そのアイテムの現在の出品状況」と
    /// 「同名の購入者が他ワールドでも買っているか」を提供する。
    /// </summary>
    public sealed class UniversalisClient : IDisposable
    {
        private const string BaseUrl = "https://universalis.app/api/v2";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private readonly HttpClient _http;
        private readonly Dictionary<string, MarketSnapshot> _cache = new();
        private readonly object _lock = new();

        public UniversalisClient()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            _http.DefaultRequestHeaders.Add("User-Agent", "MarketStats-Dalamud/0.1");
        }

        /// <summary>照会範囲（データセンター名）。設定が空ならログイン中のデータセンター。</summary>
        public string ResolveScope()
        {
            var configured = Plugin.Config.UniversalisScope;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.Trim();

            var dc = LodestoneLink.GetCurrentDataCenter();
            if (!string.IsNullOrEmpty(dc)) return dc;

            var world = LodestoneLink.GetCurrentWorld();
            return string.IsNullOrEmpty(world) ? "Japan" : world;
        }

        public MarketSnapshot? GetCached(uint itemId)
        {
            lock (_lock)
            {
                var key = CacheKey(itemId);
                if (!_cache.TryGetValue(key, out var snap)) return null;
                if (DateTime.Now - snap.FetchedLocal > CacheDuration) return null;
                return snap;
            }
        }

        /// <summary>キャッシュが有効ならそれを返し、無ければ取得する。</summary>
        public async Task<MarketSnapshot> FetchAsync(uint itemId, bool force = false)
        {
            if (!force)
            {
                var cached = GetCached(itemId);
                if (cached != null) return cached;
            }

            var scope = ResolveScope();
            var snapshot = new MarketSnapshot
            {
                ItemId = itemId,
                Scope = scope,
                FetchedLocal = DateTime.Now,
            };

            try
            {
                var listingsJson = await _http.GetStringAsync(
                    $"{BaseUrl}/{Uri.EscapeDataString(scope)}/{itemId}?listings=30&entries=0")
                    .ConfigureAwait(false);
                ParseListings(listingsJson, snapshot);

                var historyJson = await _http.GetStringAsync(
                    $"{BaseUrl}/history/{Uri.EscapeDataString(scope)}/{itemId}?entriesToReturn=200")
                    .ConfigureAwait(false);
                ParseHistory(historyJson, snapshot);
            }
            catch (Exception e)
            {
                snapshot.Error = e.Message;
                Plugin.PluginLog.Warning($"Universalis の取得に失敗しました (item {itemId}): {e.Message}");
            }

            lock (_lock) _cache[CacheKey(itemId)] = snapshot;
            return snapshot;
        }

        private static void ParseListings(string json, MarketSnapshot snapshot)
        {
            var root = JObject.Parse(json);
            var listings = root["listings"] as JArray;
            if (listings == null) return;

            foreach (var l in listings)
            {
                var lastReview = (long?)l["lastReviewTime"] ?? 0;
                snapshot.Listings.Add(new MarketListing
                {
                    WorldName = (string?)l["worldName"] ?? snapshot.Scope,
                    RetainerName = (string?)l["retainerName"] ?? string.Empty,
                    ListingId = ParseId((string?)l["listingID"]),
                    RetainerId = ParseId((string?)l["retainerID"]),
                    PricePerUnit = (long?)l["pricePerUnit"] ?? 0,
                    Quantity = (long?)l["quantity"] ?? 0,
                    Total = (long?)l["total"] ?? 0,
                    Hq = (bool?)l["hq"] ?? false,
                    OnMannequin = (bool?)l["onMannequin"] ?? false,
                    LastReviewUnix = lastReview,
                    LastReviewLocal = DateTimeOffset.FromUnixTimeSeconds(lastReview).LocalDateTime,
                });
            }

            snapshot.Listings.Sort((a, b) => a.PricePerUnit.CompareTo(b.PricePerUnit));
        }

        private static void ParseHistory(string json, MarketSnapshot snapshot)
        {
            var root = JObject.Parse(json);
            var entries = root["entries"] as JArray;
            if (entries == null) return;

            foreach (var e in entries)
            {
                snapshot.History.Add(new MarketHistoryEntry
                {
                    WorldName = (string?)e["worldName"] ?? snapshot.Scope,
                    BuyerName = (string?)e["buyerName"] ?? string.Empty,
                    PricePerUnit = (long?)e["pricePerUnit"] ?? 0,
                    Quantity = (long?)e["quantity"] ?? 0,
                    Hq = (bool?)e["hq"] ?? false,
                    OnMannequin = (bool?)e["onMannequin"] ?? false,
                    UnixTime = (long?)e["timestamp"] ?? 0,
                });
            }

            snapshot.History.Sort((a, b) => b.UnixTime.CompareTo(a.UnixTime));
        }

        /// <summary>
        /// Universalis の listingID / retainerID は 64bit 整数を文字列で表したもの。
        /// 解釈できない場合は 0（＝不明）とする。
        /// </summary>
        private static ulong ParseId(string? value) =>
            ulong.TryParse(value, out var id) ? id : 0UL;

        /// <summary>取得した出品を、再出品追跡用のレコードへ変換する。</summary>
        public static List<Data.ListingRecord> ToListingRecords(MarketSnapshot snapshot)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var result = new List<Data.ListingRecord>(snapshot.Listings.Count);

            foreach (var l in snapshot.Listings)
            {
                if (l.ListingId == 0) continue;

                result.Add(new Data.ListingRecord
                {
                    ItemId = snapshot.ItemId,
                    Hq = l.Hq,
                    ListingId = l.ListingId,
                    RetainerId = l.RetainerId,
                    OwnerContentId = 0,
                    RetainerName = l.RetainerName,
                    UnitPrice = l.PricePerUnit,
                    Quantity = l.Quantity,
                    WorldName = l.WorldName,
                    ListedUnix = l.LastReviewUnix,
                    FirstSeenUnix = now,
                    LastSeenUnix = now,
                    Source = "universalis",
                });
            }

            return result;
        }

        /// <summary>指定した購入者名に一致する履歴だけを抜き出す。</summary>
        public static List<MarketHistoryEntry> FilterByBuyer(MarketSnapshot snapshot, string buyerName) =>
            snapshot.History
                .Where(h => string.Equals(h.BuyerName, buyerName, StringComparison.OrdinalIgnoreCase))
                .ToList();

        private string CacheKey(uint itemId) => $"{ResolveScope()}:{itemId}";

        public void Dispose() => _http.Dispose();
    }
}
