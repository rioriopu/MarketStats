using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace MarketStats.Data
{
    /// <summary>自分のリテイナーが出している品 1 件。</summary>
    public sealed class OwnListing
    {
        public uint ItemId { get; set; }
        public bool Hq { get; set; }
        public long Quantity { get; set; }
        public long UnitPrice { get; set; }
        public ulong ListingId { get; set; }

        /// <summary>この内容を確認した時刻。</summary>
        public long ObservedUnix { get; set; }

        [JsonIgnore]
        public long Total => Quantity * UnitPrice;

        [JsonIgnore]
        public DateTime ObservedLocal => DateTimeOffset.FromUnixTimeSeconds(ObservedUnix).LocalDateTime;
    }

    /// <summary>自分のリテイナー 1 体分の情報。</summary>
    public sealed class OwnRetainer
    {
        public ulong RetainerId { get; set; }
        public string Name { get; set; } = string.Empty;

        /// <summary>このリテイナーを持っているキャラクター。</summary>
        public ulong OwnerContentId { get; set; }
        public string OwnerName { get; set; } = string.Empty;
        public string OwnerWorld { get; set; } = string.Empty;

        /// <summary>リテイナーが預かっているギル。</summary>
        public uint Gil { get; set; }

        /// <summary>出品している品数（リテイナー一覧から分かる概数）。</summary>
        public int MarketItemCount { get; set; }

        /// <summary>出品期限（この時刻を過ぎると出品が取り下げられる）。</summary>
        public long MarketExpireUnix { get; set; }

        public byte Town { get; set; }

        /// <summary>出品の明細。リテイナーの出品リストを開いたときに更新される。</summary>
        public List<OwnListing> Listings { get; set; } = new();

        public long ListingsUpdatedUnix { get; set; }
        public long LastSeenUnix { get; set; }

        [JsonIgnore]
        public long TotalValue => Listings.Sum(l => l.Total);

        [JsonIgnore]
        public DateTime ListingsUpdatedLocal =>
            DateTimeOffset.FromUnixTimeSeconds(ListingsUpdatedUnix).LocalDateTime;

        [JsonIgnore]
        public DateTime MarketExpireLocal =>
            DateTimeOffset.FromUnixTimeSeconds(MarketExpireUnix).LocalDateTime;

        /// <summary>出品期限までの残り。過ぎていれば負になる。</summary>
        [JsonIgnore]
        public TimeSpan MarketExpireIn =>
            MarketExpireUnix == 0
                ? TimeSpan.Zero
                : DateTimeOffset.FromUnixTimeSeconds(MarketExpireUnix) - DateTimeOffset.UtcNow;

        /// <summary>明細が古くなっていないか（一覧の品数と食い違っていないか）。</summary>
        [JsonIgnore]
        public bool ListingsLookStale =>
            ListingsUpdatedUnix == 0 || (MarketItemCount > 0 && Listings.Count != MarketItemCount);
    }

    /// <summary>キャラクター 1 人分のまとめ。</summary>
    public sealed class OwnCharacter
    {
        public ulong ContentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string World { get; set; } = string.Empty;
        public List<OwnRetainer> Retainers { get; } = new();

        public long TotalValue => Retainers.Sum(r => r.TotalValue);
        public int TotalListings => Retainers.Sum(r => r.Listings.Count);
        public long TotalGil => Retainers.Sum(r => (long)r.Gil);

        public string Display => string.IsNullOrEmpty(World) ? Name : $"{Name} @ {World}";
    }

    /// <summary>
    /// 自分のリテイナーと、その出品内容を保管する。
    ///
    /// 複数のキャラクターを使っている場合、ログインしていないキャラの分は
    /// ゲームから読めないため、一度確認した内容をここに残しておく。
    /// </summary>
    public sealed class OwnListingStore
    {
        private readonly Dictionary<ulong, OwnRetainer> _retainers = new();
        private readonly object _lock = new();
        private bool _dirty;

        private string FilePath =>
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "own_listings.json");

        public int RetainerCount
        {
            get { lock (_lock) return _retainers.Count; }
        }

        public List<OwnRetainer> Snapshot()
        {
            lock (_lock) return _retainers.Values.ToList();
        }

        /// <summary>キャラクターごとにまとめて返す。</summary>
        public List<OwnCharacter> ByCharacter()
        {
            var result = new List<OwnCharacter>();

            lock (_lock)
            {
                foreach (var group in _retainers.Values.GroupBy(r => r.OwnerContentId))
                {
                    var first = group.First();
                    var character = new OwnCharacter
                    {
                        ContentId = group.Key,
                        Name = string.IsNullOrEmpty(first.OwnerName) ? "(不明なキャラクター)" : first.OwnerName,
                        World = first.OwnerWorld,
                    };

                    character.Retainers.AddRange(group.OrderBy(r => r.Name));
                    result.Add(character);
                }
            }

            return result.OrderByDescending(c => c.TotalValue).ToList();
        }

        /// <summary>リテイナー一覧から分かる概要を反映する（出品数・ギル・期限）。</summary>
        public void UpdateSummary(
            ulong retainerId, string name, ulong ownerContentId, string ownerName, string ownerWorld,
            uint gil, int marketItemCount, long marketExpireUnix, byte town)
        {
            if (retainerId == 0) return;

            lock (_lock)
            {
                if (!_retainers.TryGetValue(retainerId, out var retainer))
                {
                    retainer = new OwnRetainer { RetainerId = retainerId };
                    _retainers[retainerId] = retainer;
                }

                retainer.Name = name;
                retainer.OwnerContentId = ownerContentId;
                retainer.OwnerName = ownerName;
                retainer.OwnerWorld = ownerWorld;
                retainer.Gil = gil;
                retainer.MarketItemCount = marketItemCount;
                retainer.MarketExpireUnix = marketExpireUnix;
                retainer.Town = town;
                retainer.LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _dirty = true;
            }
        }

        /// <summary>出品の明細を差し替える。</summary>
        public void UpdateListings(ulong retainerId, List<OwnListing> listings)
        {
            if (retainerId == 0) return;

            lock (_lock)
            {
                if (!_retainers.TryGetValue(retainerId, out var retainer))
                {
                    retainer = new OwnRetainer { RetainerId = retainerId };
                    _retainers[retainerId] = retainer;
                }

                retainer.Listings = listings;
                retainer.ListingsUpdatedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _dirty = true;
            }
        }

        public OwnRetainer? Resolve(ulong retainerId)
        {
            lock (_lock) return _retainers.TryGetValue(retainerId, out var r) ? r : null;
        }

        /// <summary>自分のリテイナーかどうか。</summary>
        public bool IsMine(ulong retainerId)
        {
            lock (_lock) return _retainers.ContainsKey(retainerId);
        }

        public void Remove(ulong retainerId)
        {
            lock (_lock)
            {
                if (_retainers.Remove(retainerId)) _dirty = true;
            }
            Save(force: true);
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var list = JsonConvert.DeserializeObject<List<OwnRetainer>>(File.ReadAllText(FilePath));
                lock (_lock)
                {
                    _retainers.Clear();
                    foreach (var r in list ?? new List<OwnRetainer>())
                        if (r.RetainerId != 0) _retainers[r.RetainerId] = r;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"自分の出品の読み込みに失敗しました: {e.Message}");
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

                string json;
                lock (_lock) json = JsonConvert.SerializeObject(_retainers.Values.ToList(), Formatting.None);
                File.WriteAllText(FilePath, json);

                lock (_lock) _dirty = false;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"自分の出品の保存に失敗しました: {e.Message}");
            }
        }
    }
}
