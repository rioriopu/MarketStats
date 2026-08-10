using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace MarketStats.Data
{
    /// <summary>マーケットの購入履歴 1 件（自分の取引に限らない、公開されている取引履歴）。</summary>
    public sealed class MarketPurchase
    {
        public uint ItemId { get; set; }
        public bool Hq { get; set; }
        public string BuyerName { get; set; } = string.Empty;
        public long Quantity { get; set; }
        public long UnitPrice { get; set; }
        public long UnixTime { get; set; }
        public bool OnMannequin { get; set; }
        public string WorldName { get; set; } = string.Empty;

        [JsonIgnore]
        public string Key => $"{ItemId}|{UnixTime}|{BuyerName}|{Quantity}|{UnitPrice}";

        [JsonIgnore]
        public DateTime LocalTime => DateTimeOffset.FromUnixTimeSeconds(UnixTime).LocalDateTime;
    }

    /// <summary>
    /// マーケットの購入履歴の蓄積。
    ///
    /// 出品側の「誰が売っているか」は公開されていないが、
    /// 購入側の「誰が買ったか」は履歴として公開されている。
    /// この非対称性を利用して、「買った直後に同じ物を出し始めたリテイナー」を突き合わせる。
    /// </summary>
    public sealed class MarketHistoryStore
    {
        private readonly Dictionary<uint, List<MarketPurchase>> _byItem = new();
        private readonly object _lock = new();
        private bool _dirty;
        private DateTime _lastSaveUtc = DateTime.MinValue;

        /// <summary>1 アイテムあたりの保持件数。</summary>
        private const int MaxPerItem = 400;

        private string FilePath =>
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "purchases.json");

        public int Count
        {
            get { lock (_lock) return _byItem.Values.Sum(v => v.Count); }
        }

        public int ItemCount
        {
            get { lock (_lock) return _byItem.Count; }
        }

        public DateTime LastObservedLocal { get; private set; } = DateTime.MinValue;

        /// <summary>購入履歴を取り込む。新規件数を返す。</summary>
        public int Add(IEnumerable<MarketPurchase> purchases)
        {
            var added = 0;

            lock (_lock)
            {
                foreach (var purchase in purchases)
                {
                    if (purchase.ItemId == 0 || purchase.UnixTime == 0) continue;

                    if (!_byItem.TryGetValue(purchase.ItemId, out var list))
                    {
                        list = new List<MarketPurchase>();
                        _byItem[purchase.ItemId] = list;
                    }

                    if (list.Any(p => p.Key == purchase.Key)) continue;

                    list.Add(purchase);
                    added++;
                }

                if (added > 0)
                {
                    foreach (var list in _byItem.Values)
                    {
                        if (list.Count <= MaxPerItem) continue;
                        list.Sort((a, b) => b.UnixTime.CompareTo(a.UnixTime));
                        list.RemoveRange(MaxPerItem, list.Count - MaxPerItem);
                    }
                    _dirty = true;
                }
            }

            if (added > 0) LastObservedLocal = DateTime.Now;
            return added;
        }

        public List<MarketPurchase> ForItem(uint itemId)
        {
            lock (_lock)
                return _byItem.TryGetValue(itemId, out var list)
                    ? list.ToList()
                    : new List<MarketPurchase>();
        }

        public List<MarketPurchase> ByBuyer(string buyerName)
        {
            if (string.IsNullOrWhiteSpace(buyerName)) return new List<MarketPurchase>();
            lock (_lock)
                return _byItem.Values
                    .SelectMany(v => v)
                    .Where(p => string.Equals(p.BuyerName, buyerName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.UnixTime)
                    .ToList();
        }

        public int Prune(int retentionDays)
        {
            if (retentionDays <= 0) return 0;
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)retentionDays * 86400L;

            var removed = 0;
            lock (_lock)
            {
                foreach (var list in _byItem.Values)
                    removed += list.RemoveAll(p => p.UnixTime < cutoff);

                var empty = _byItem.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList();
                foreach (var key in empty) _byItem.Remove(key);

                if (removed > 0) _dirty = true;
            }
            return removed;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _byItem.Clear();
                _dirty = true;
            }
            Save(force: true);
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var list = JsonConvert.DeserializeObject<List<MarketPurchase>>(File.ReadAllText(FilePath));
                lock (_lock)
                {
                    _byItem.Clear();
                    foreach (var p in list ?? new List<MarketPurchase>())
                    {
                        if (!_byItem.TryGetValue(p.ItemId, out var bucket))
                        {
                            bucket = new List<MarketPurchase>();
                            _byItem[p.ItemId] = bucket;
                        }
                        bucket.Add(p);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"購入履歴の読み込みに失敗しました: {e.Message}");
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
                lock (_lock) json = JsonConvert.SerializeObject(_byItem.Values.SelectMany(v => v).ToList(), Formatting.None);
                File.WriteAllText(FilePath, json);

                lock (_lock)
                {
                    _dirty = false;
                    _lastSaveUtc = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"購入履歴の保存に失敗しました: {e.Message}");
            }
        }
    }
}
