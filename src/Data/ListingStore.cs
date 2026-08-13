using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace MarketStats.Data
{
    /// <summary>マーケットで観測した出品 1 件。</summary>
    public sealed class ListingRecord
    {
        public uint ItemId { get; set; }
        public bool Hq { get; set; }

        /// <summary>出品を一意に識別する ID。</summary>
        public ulong ListingId { get; set; }

        public ulong RetainerId { get; set; }

        /// <summary>出品リテイナーのオーナーの ContentId。Universalis 由来の場合や、サーバーが送っていない場合は 0。</summary>
        public ulong OwnerContentId { get; set; }

        /// <summary>製作者の ContentId（製作者署名のあるアイテムのみ）。出品者とは限らない。</summary>
        public ulong ArtisanContentId { get; set; }

        public string RetainerName { get; set; } = string.Empty;
        public long UnitPrice { get; set; }
        public long Quantity { get; set; }
        public byte TownId { get; set; }
        public string WorldName { get; set; } = string.Empty;

        /// <summary>この出品を最初に観測した時刻。</summary>
        public long FirstSeenUnix { get; set; }

        /// <summary>最後に観測した時刻。</summary>
        public long LastSeenUnix { get; set; }

        /// <summary>出品／価格改定の時刻（Universalis の lastReviewTime）。不明なら 0。</summary>
        public long ListedUnix { get; set; }

        /// <summary>"game" または "universalis"。</summary>
        public string Source { get; set; } = "game";

        /// <summary>出品時刻として最も確からしい値。</summary>
        [JsonIgnore]
        public long EffectiveListedUnix => ListedUnix > 0 ? ListedUnix : FirstSeenUnix;

        [JsonIgnore]
        public long Total => UnitPrice * Quantity;
    }

    /// <summary>
    /// 観測した出品の蓄積。「いつからこの出品があるか」を残すのが目的で、
    /// 購入イベントの直後に現れた出品＝再出品の候補、という推定に使う。
    /// </summary>
    public sealed class ListingStore
    {
        private readonly Dictionary<ulong, ListingRecord> _byListingId = new();
        private readonly object _lock = new();
        private bool _dirty;
        private DateTime _lastSaveUtc = DateTime.MinValue;

        private string FilePath =>
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "listings.json");

        public int Count
        {
            get { lock (_lock) return _byListingId.Count; }
        }

        public DateTime LastObservedLocal { get; private set; } = DateTime.MinValue;

        /// <summary>
        /// 観測した出品を取り込む。既知の出品は最終観測時刻だけ更新し、
        /// 新規に現れた出品を戻り値として返す。
        /// </summary>
        public List<ListingRecord> Observe(IReadOnlyList<ListingRecord> observed)
        {
            var fresh = new List<ListingRecord>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            lock (_lock)
            {
                foreach (var record in observed)
                {
                    if (record.ListingId == 0) continue;

                    if (_byListingId.TryGetValue(record.ListingId, out var existing))
                    {
                        existing.LastSeenUnix = now;
                        existing.UnitPrice = record.UnitPrice;
                        existing.Quantity = record.Quantity;

                        // ゲームから読んだ情報の方が詳しい（オーナー ContentId が入る）。
                        if (existing.OwnerContentId == 0 && record.OwnerContentId != 0)
                        {
                            existing.OwnerContentId = record.OwnerContentId;
                            existing.Source = record.Source;
                        }
                        if (string.IsNullOrEmpty(existing.RetainerName))
                            existing.RetainerName = record.RetainerName;
                        if (existing.ListedUnix == 0 && record.ListedUnix > 0)
                            existing.ListedUnix = record.ListedUnix;

                        _dirty = true;
                        continue;
                    }

                    record.FirstSeenUnix = record.FirstSeenUnix > 0 ? record.FirstSeenUnix : now;
                    record.LastSeenUnix = now;
                    _byListingId[record.ListingId] = record;
                    fresh.Add(record);
                    _dirty = true;
                }
            }

            if (observed.Count > 0) LastObservedLocal = DateTime.Now;
            return fresh;
        }

        /// <summary>
        /// あるアイテムの出品一覧を「今の状態」で置き換える。
        ///
        /// 出品は売れたり取り下げられたりして消える。追加するだけだと古い出品が残り続け、
        /// 同じリテイナーの同じ品が何件も並んでしまう。
        /// 一覧を丸ごと観測できたときは、そこに無かったものを取り除く。
        /// </summary>
        /// <returns>消えていた（＝売れたか取り下げられた）出品。</returns>
        public List<ListingRecord> ReplaceForItem(uint itemId, IReadOnlyList<ListingRecord> observed)
        {
            var vanished = new List<ListingRecord>();
            var seen = observed.Select(l => l.ListingId).ToHashSet();

            lock (_lock)
            {
                var stale = _byListingId.Values
                    .Where(l => l.ItemId == itemId && !seen.Contains(l.ListingId))
                    .ToList();

                foreach (var listing in stale)
                {
                    _byListingId.Remove(listing.ListingId);
                    vanished.Add(listing);
                }

                if (stale.Count > 0) _dirty = true;
            }

            return vanished;
        }

        public List<ListingRecord> ForItem(uint itemId)
        {
            lock (_lock)
                return _byListingId.Values.Where(l => l.ItemId == itemId).ToList();
        }

        public List<ListingRecord> ByOwner(ulong contentId)
        {
            if (contentId == 0) return new List<ListingRecord>();
            lock (_lock)
                return _byListingId.Values.Where(l => l.OwnerContentId == contentId).ToList();
        }

        public List<ListingRecord> Snapshot()
        {
            lock (_lock) return _byListingId.Values.ToList();
        }

        public int Prune(int retentionDays)
        {
            if (retentionDays <= 0) return 0;

            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)retentionDays * 86400L;
            int removed;
            lock (_lock)
            {
                var stale = _byListingId.Where(kv => kv.Value.LastSeenUnix < cutoff)
                                        .Select(kv => kv.Key).ToList();
                foreach (var key in stale) _byListingId.Remove(key);
                removed = stale.Count;
                if (removed > 0) _dirty = true;
            }
            return removed;
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var list = JsonConvert.DeserializeObject<List<ListingRecord>>(File.ReadAllText(FilePath));
                lock (_lock)
                {
                    _byListingId.Clear();
                    foreach (var l in list ?? new List<ListingRecord>())
                        if (l.ListingId != 0) _byListingId[l.ListingId] = l;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"出品履歴の読み込みに失敗しました: {e.Message}");
            }
        }

        public void Save(bool force = false)
        {
            lock (_lock)
            {
                if (!_dirty && !force) return;
                if (!force && (DateTime.UtcNow - _lastSaveUtc).TotalSeconds < 30) return;
            }

            try
            {
                var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
                Directory.CreateDirectory(dir);

                string json;
                lock (_lock) json = JsonConvert.SerializeObject(_byListingId.Values.ToList(), Formatting.None);
                File.WriteAllText(FilePath, json);

                lock (_lock)
                {
                    _dirty = false;
                    _lastSaveUtc = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"出品履歴の保存に失敗しました: {e.Message}");
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _byListingId.Clear();
                _dirty = true;
            }
            Save(force: true);
        }
    }
}
