using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace MarketStats.Data
{
    /// <summary>
    /// 出品リストから消えた（＝売れたと思われる）出品。購入者名は分からない。
    /// 後から売却履歴で同じ内容が取り込まれたら確定として印を付ける。
    /// </summary>
    public sealed class PendingSale
    {
        public uint ItemId { get; set; }
        public bool Hq { get; set; }
        public long Quantity { get; set; }
        public long UnitPrice { get; set; }
        public string RetainerName { get; set; } = string.Empty;
        public ulong OwnerContentId { get; set; }

        /// <summary>前回この出品を確認できた時刻（この時刻以降に売れたはず）。</summary>
        public long WindowStartUnix { get; set; }

        /// <summary>出品が消えているのを確認した時刻。</summary>
        public long DetectedUnix { get; set; }

        /// <summary>売却履歴側で購入者まで判明したもの。</summary>
        public bool Confirmed { get; set; }

        [JsonIgnore]
        public DateTime DetectedLocal => DateTimeOffset.FromUnixTimeSeconds(DetectedUnix).LocalDateTime;

        [JsonIgnore]
        public long Total => Quantity * UnitPrice;
    }

    /// <summary>ゲーム側の履歴が 20 件で溢れ、取りこぼした可能性がある区間の記録。</summary>
    public sealed class HistoryGap
    {
        public string RetainerName { get; set; } = string.Empty;
        public string OwnerName { get; set; } = string.Empty;

        /// <summary>これまでに記録できていた最新の売却時刻。</summary>
        public long KnownUntilUnix { get; set; }

        /// <summary>今回取り込めた履歴の最も古い売却時刻。</summary>
        public long RecoveredFromUnix { get; set; }

        public long DetectedUnix { get; set; }

        [JsonIgnore]
        public DateTime KnownUntilLocal => DateTimeOffset.FromUnixTimeSeconds(KnownUntilUnix).LocalDateTime;

        [JsonIgnore]
        public DateTime RecoveredFromLocal => DateTimeOffset.FromUnixTimeSeconds(RecoveredFromUnix).LocalDateTime;
    }

    /// <summary>未確定の売却と、取りこぼし区間の保管庫。</summary>
    public sealed class PendingSaleStore
    {
        private readonly List<PendingSale> _pending = new();
        private readonly List<HistoryGap> _gaps = new();
        private readonly Dictionary<string, List<ListingRecord>> _snapshots = new();
        private readonly object _lock = new();
        private bool _dirty;

        private string FilePath =>
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "pending.json");

        public List<PendingSale> PendingSales
        {
            get { lock (_lock) return _pending.ToList(); }
        }

        public List<HistoryGap> Gaps
        {
            get { lock (_lock) return _gaps.ToList(); }
        }

        public int UnconfirmedCount
        {
            get { lock (_lock) return _pending.Count(p => !p.Confirmed); }
        }

        /// <summary>
        /// リテイナーの出品スナップショットを更新し、前回から消えた出品を未確定売却として記録する。
        /// </summary>
        /// <returns>新たに検出した未確定売却の件数。</returns>
        public int UpdateRetainerSnapshot(string retainerName, List<ListingRecord> current)
        {
            if (string.IsNullOrEmpty(retainerName)) return 0;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var detected = 0;

            lock (_lock)
            {
                if (_snapshots.TryGetValue(retainerName, out var previous))
                {
                    var currentIds = current.Select(l => l.ListingId).ToHashSet();
                    var windowStart = previous.Count > 0 ? previous.Max(p => p.LastSeenUnix) : now;

                    foreach (var gone in previous.Where(p => !currentIds.Contains(p.ListingId)))
                    {
                        _pending.Add(new PendingSale
                        {
                            ItemId = gone.ItemId,
                            Hq = gone.Hq,
                            Quantity = gone.Quantity,
                            UnitPrice = gone.UnitPrice,
                            RetainerName = retainerName,
                            OwnerContentId = gone.OwnerContentId,
                            WindowStartUnix = windowStart,
                            DetectedUnix = now,
                        });
                        detected++;
                    }
                }

                _snapshots[retainerName] = current;
                if (detected > 0) _dirty = true;
            }

            return detected;
        }

        /// <summary>
        /// 売却履歴で判明したレコードと突き合わせ、一致する未確定売却に確定の印を付ける。
        /// </summary>
        public int Reconcile(IEnumerable<SaleRecord> sales)
        {
            var list = sales.ToList();
            if (list.Count == 0) return 0;

            var confirmed = 0;
            lock (_lock)
            {
                foreach (var pending in _pending.Where(p => !p.Confirmed))
                {
                    var match = list.FirstOrDefault(s =>
                        s.ItemId == pending.ItemId &&
                        s.Hq == pending.Hq &&
                        s.Quantity == pending.Quantity &&
                        string.Equals(s.RetainerName, pending.RetainerName, StringComparison.OrdinalIgnoreCase) &&
                        s.UnixTime >= pending.WindowStartUnix - 300 &&
                        s.UnixTime <= pending.DetectedUnix + 300);

                    if (match == null) continue;
                    pending.Confirmed = true;
                    confirmed++;
                }

                if (confirmed > 0) _dirty = true;
            }

            return confirmed;
        }

        public void AddGap(HistoryGap gap)
        {
            lock (_lock)
            {
                _gaps.Add(gap);
                if (_gaps.Count > 50) _gaps.RemoveRange(0, _gaps.Count - 50);
                _dirty = true;
            }
        }

        public int Prune(int retentionDays)
        {
            if (retentionDays <= 0) return 0;
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)retentionDays * 86400L;

            int removed;
            lock (_lock)
            {
                removed = _pending.RemoveAll(p => p.DetectedUnix < cutoff);
                removed += _gaps.RemoveAll(g => g.DetectedUnix < cutoff);
                if (removed > 0) _dirty = true;
            }
            return removed;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _pending.Clear();
                _gaps.Clear();
                _snapshots.Clear();
                _dirty = true;
            }
            Save(force: true);
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var payload = JsonConvert.DeserializeObject<StoredPayload>(File.ReadAllText(FilePath));
                if (payload == null) return;

                lock (_lock)
                {
                    _pending.Clear();
                    _pending.AddRange(payload.Pending ?? new List<PendingSale>());
                    _gaps.Clear();
                    _gaps.AddRange(payload.Gaps ?? new List<HistoryGap>());
                    _snapshots.Clear();
                    foreach (var kv in payload.Snapshots ?? new Dictionary<string, List<ListingRecord>>())
                        _snapshots[kv.Key] = kv.Value;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"未確定売却の読み込みに失敗しました: {e.Message}");
            }
        }

        public void Save(bool force = false)
        {
            lock (_lock)
            {
                if (!_dirty && !force) return;
            }

            try
            {
                var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
                Directory.CreateDirectory(dir);

                StoredPayload payload;
                lock (_lock)
                {
                    payload = new StoredPayload
                    {
                        Pending = _pending.ToList(),
                        Gaps = _gaps.ToList(),
                        Snapshots = _snapshots.ToDictionary(kv => kv.Key, kv => kv.Value),
                    };
                }

                File.WriteAllText(FilePath, JsonConvert.SerializeObject(payload, Formatting.None));
                lock (_lock) _dirty = false;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"未確定売却の保存に失敗しました: {e.Message}");
            }
        }

        private sealed class StoredPayload
        {
            public List<PendingSale> Pending { get; set; } = new();
            public List<HistoryGap> Gaps { get; set; } = new();
            public Dictionary<string, List<ListingRecord>> Snapshots { get; set; } = new();
        }
    }
}
