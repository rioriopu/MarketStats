using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace MarketStats.Data
{
    /// <summary>身元情報の出所。値が大きいほど信頼できる（上書きの優先度になる）。</summary>
    public enum IdentitySource
    {
        Unknown = 0,

        /// <summary>売却履歴と出品の相関から推定したもの（確定ではない）。</summary>
        Inferred = 1,

        /// <summary>周囲に見えていたプレイヤーから取得。</summary>
        ObjectTable = 2,

        Party = 3,
        Linkshell = 4,
        FreeCompany = 5,
        Friend = 6,

        /// <summary>自分自身。</summary>
        Self = 7,

        /// <summary>マーケットの出品データに出品者名が入っていた場合。</summary>
        MarketBoard = 8,

        /// <summary>冒険者名刺から直接取得（最も確実）。</summary>
        CharaCard = 9,
    }

    /// <summary>ContentId とキャラクター名の対応。</summary>
    public sealed class OwnerIdentity
    {
        public ulong ContentId { get; set; }
        public string Name { get; set; } = string.Empty;
        public ushort WorldId { get; set; }

        /// <summary>
        /// アカウントの識別子。同じ値を持つキャラクターは同一アカウント＝同じ人の別キャラ。
        /// 取得できた場合のみ入る。
        /// </summary>
        public ulong AccountId { get; set; }

        /// <summary>
        /// Lodestone のキャラクターページ ID。
        /// 分かっていれば、検索を挟まずに本人のページを直接開ける。
        /// </summary>
        public long LodestoneId { get; set; }
        public IdentitySource Source { get; set; }
        public long LastSeenUnix { get; set; }

        /// <summary>推定の場合の信頼度スコア。確定情報なら 0。</summary>
        public int InferenceScore { get; set; }

        /// <summary>推定ではなく、ゲームから直接取得できた情報か。</summary>
        [JsonIgnore]
        public bool IsConfirmed => Source >= IdentitySource.ObjectTable;
    }

    /// <summary>
    /// ContentId ↔ キャラクター名の対応表。
    ///
    /// マーケットの出品データにはオーナーの ContentId は含まれるが名前は含まれない。
    /// そこで、周囲に見えたプレイヤーやフレンドリスト等から集めた対応表で名前を解決する。
    /// 解決できない場合でも ContentId 自体は一意なので「同じ人物の出品」はまとめられる。
    /// </summary>
    public sealed class IdentityStore
    {
        private readonly Dictionary<ulong, OwnerIdentity> _map = new();
        private readonly object _lock = new();
        private bool _dirty;
        private DateTime _lastSaveUtc = DateTime.MinValue;

        /// <summary>保持する最大件数。超えたら古いものから捨てる。</summary>
        private const int MaxEntries = 20000;

        private string FilePath =>
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "identities.json");

        public int Count
        {
            get { lock (_lock) return _map.Count; }
        }

        public int ConfirmedCount
        {
            get { lock (_lock) return _map.Values.Count(v => v.IsConfirmed); }
        }

        /// <summary>ゲームから直接得られた身元情報を記録する。</summary>
        public void Record(
            ulong contentId, string name, ushort worldId, IdentitySource source, ulong accountId = 0)
        {
            if (contentId == 0 || string.IsNullOrWhiteSpace(name)) return;

            lock (_lock)
            {
                if (_map.TryGetValue(contentId, out var existing))
                {
                    // アカウント識別子は後から分かることがあるので、取れたら足す。
                    if (accountId != 0) existing.AccountId = accountId;

                    // より弱い出所で確定情報を塗り潰さない。
                    if (existing.Source > source)
                    {
                        existing.LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        _dirty = true;
                        return;
                    }

                    accountId = accountId != 0 ? accountId : existing.AccountId;
                }

                _map[contentId] = new OwnerIdentity
                {
                    ContentId = contentId,
                    Name = name,
                    WorldId = worldId,
                    AccountId = accountId,
                    Source = source,
                    LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                _dirty = true;
            }
        }

        /// <summary>
        /// 同じアカウントの別キャラクターを返す。
        /// 買い物用のサブキャラから本体へ辿るために使う。
        /// </summary>
        public List<OwnerIdentity> SameAccount(ulong accountId, ulong excludeContentId = 0)
        {
            if (accountId == 0) return new List<OwnerIdentity>();

            lock (_lock)
                return _map.Values
                    .Where(v => v.AccountId == accountId && v.ContentId != excludeContentId)
                    .OrderBy(v => v.Name)
                    .ToList();
        }

        /// <summary>登録されているすべての対応（書き出し用）。</summary>
        public List<OwnerIdentity> All
        {
            get { lock (_lock) return _map.Values.ToList(); }
        }

        /// <summary>アカウント識別子が判明している人数。</summary>
        public int AccountKnownCount
        {
            get { lock (_lock) return _map.Values.Count(v => v.AccountId != 0); }
        }

        /// <summary>
        /// 売却履歴と出品の相関から推定した対応を記録する。
        /// 確定情報が既にある場合は上書きしない。
        /// </summary>
        public void RecordInference(ulong contentId, string name, int score)
        {
            if (contentId == 0 || string.IsNullOrWhiteSpace(name)) return;

            lock (_lock)
            {
                if (_map.TryGetValue(contentId, out var existing))
                {
                    if (existing.IsConfirmed) return;
                    if (existing.InferenceScore >= score &&
                        string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase))
                        return;
                }

                _map[contentId] = new OwnerIdentity
                {
                    ContentId = contentId,
                    Name = name,
                    Source = IdentitySource.Inferred,
                    InferenceScore = score,
                    LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                _dirty = true;
            }
        }

        public OwnerIdentity? Resolve(ulong contentId)
        {
            if (contentId == 0) return null;
            lock (_lock) return _map.TryGetValue(contentId, out var v) ? v : null;
        }

        /// <summary>名前からの逆引き（同名が複数いる場合は最後に見たもの）。</summary>
        public OwnerIdentity? ResolveByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            lock (_lock)
            {
                return _map.Values
                    .Where(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v.Source)
                    .ThenByDescending(v => v.LastSeenUnix)
                    .FirstOrDefault();
            }
        }

        /// <summary>名前の一部で探す（表記ゆれや、うろ覚えの名前から辿るため）。</summary>
        public List<OwnerIdentity> SearchByName(string fragment)
        {
            if (string.IsNullOrWhiteSpace(fragment)) return new List<OwnerIdentity>();

            lock (_lock)
                return _map.Values
                    .Where(v => v.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v.Source)
                    .ThenByDescending(v => v.LastSeenUnix)
                    .ToList();
        }

        /// <summary>表示用の名前。解決できない場合は ContentId の短縮表記を返す。</summary>
        public string DisplayName(ulong contentId)
        {
            var identity = Resolve(contentId);
            if (identity == null) return contentId == 0 ? "(不明)" : $"ID:{contentId & 0xFFFFFF:X6}";
            return identity.Source == IdentitySource.Inferred ? $"{identity.Name}?" : identity.Name;
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var list = JsonConvert.DeserializeObject<List<OwnerIdentity>>(File.ReadAllText(FilePath));
                lock (_lock)
                {
                    _map.Clear();
                    foreach (var e in list ?? new List<OwnerIdentity>())
                        if (e.ContentId != 0) _map[e.ContentId] = e;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"身元情報の読み込みに失敗しました: {e.Message}");
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

                List<OwnerIdentity> list;
                lock (_lock)
                {
                    if (_map.Count > MaxEntries)
                    {
                        // 確定情報を優先して残し、古い順に捨てる。
                        var keep = _map.Values
                            .OrderByDescending(v => v.IsConfirmed)
                            .ThenByDescending(v => v.LastSeenUnix)
                            .Take(MaxEntries)
                            .ToList();
                        _map.Clear();
                        foreach (var e in keep) _map[e.ContentId] = e;
                    }

                    list = _map.Values.ToList();
                }

                File.WriteAllText(FilePath, JsonConvert.SerializeObject(list, Formatting.None));

                lock (_lock)
                {
                    _dirty = false;
                    _lastSaveUtc = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"身元情報の保存に失敗しました: {e.Message}");
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _map.Clear();
                _dirty = true;
            }
            Save(force: true);
        }
    }
}
