using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace MarketStats.Data
{
    /// <summary>マーケットで見かけたリテイナーと、その持ち主についての情報。</summary>
    public sealed class RetainerProfile
    {
        public ulong RetainerId { get; set; }
        public string RetainerName { get; set; } = string.Empty;

        /// <summary>確定しているオーナー名（自分のリテイナー、または名刺等で判明した場合）。</summary>
        public string? OwnerName { get; set; }

        /// <summary>オーナーの ContentId（取得できた場合のみ）。</summary>
        public ulong OwnerContentId { get; set; }

        /// <summary>推定したオーナー名。</summary>
        public string? GuessedOwnerName { get; set; }

        public int GuessScore { get; set; }

        public List<string> GuessReasons { get; set; } = new();

        /// <summary>自分のリテイナーか。</summary>
        public bool IsMine { get; set; }

        /// <summary>持ち主を手動で設定したか。</summary>
        public bool ManuallySet { get; set; }

        public long FirstSeenUnix { get; set; }
        public long LastSeenUnix { get; set; }

        /// <summary>これまでに観測した出品の数。</summary>
        public int ObservedListings { get; set; }

        /// <summary>観測した出品のアイテム（重複なし）。</summary>
        public List<uint> ObservedItems { get; set; } = new();

        /// <summary>チャットでこのリテイナー名に言及した発言。</summary>
        public List<ChatMention> ChatMentions { get; set; } = new();

        /// <summary>観測した出品の製作者署名（ContentId → 件数）。</summary>
        public Dictionary<ulong, int> ArtisanCounts { get; set; } = new();

        /// <summary>最も多く署名されている製作者の ContentId。</summary>
        [JsonIgnore]
        public ulong MainArtisanId =>
            ArtisanCounts.Count == 0 ? 0 : ArtisanCounts.OrderByDescending(kv => kv.Value).First().Key;

        /// <summary>署名付き出品のうち、主な製作者が占める割合。</summary>
        [JsonIgnore]
        public double MainArtisanRatio
        {
            get
            {
                var total = ArtisanCounts.Values.Sum();
                if (total == 0) return 0;
                return (double)ArtisanCounts.Values.Max() / total;
            }
        }

        [JsonIgnore]
        public int SignedListingCount => ArtisanCounts.Values.Sum();

        /// <summary>持ち主を割り出すために集めた手がかり。</summary>
        public List<OwnerEvidence> Evidence { get; set; } = new();

        /// <summary>結論の確度（0〜100）。</summary>
        public int Confidence { get; set; }

        /// <summary>結論を出せなかった理由。</summary>
        public string? InconclusiveReason { get; set; }

        [JsonIgnore]
        public bool HasOwner => !string.IsNullOrEmpty(OwnerName);

        [JsonIgnore]
        public DateTime LastSeenLocal => DateTimeOffset.FromUnixTimeSeconds(LastSeenUnix).LocalDateTime;

        /// <summary>
        /// 表示用のオーナー表記。
        ///
        /// 確定していないものを断定的に見せると誤解を招くため、
        /// 推定は既定では隠し、設定で明示的に有効にしたときだけ候補として見せる。
        /// </summary>
        [JsonIgnore]
        public string DisplayOwner
        {
            get
            {
                if (!string.IsNullOrEmpty(OwnerName)) return OwnerName;
                if (string.IsNullOrEmpty(GuessedOwnerName)) return "不明";

                return Plugin.Config.ShowInferredOwners
                    ? $"{GuessedOwnerName}（候補 {Confidence}）"
                    : "不明（候補あり）";
            }
        }

        /// <summary>持ち主が確定しているか（推定ではないか）。</summary>
        [JsonIgnore]
        public bool IsOwnerCertain => !string.IsNullOrEmpty(OwnerName);
    }

    /// <summary>チャットでリテイナー名に言及した発言。</summary>
    public sealed class ChatMention
    {
        public string SpeakerName { get; set; } = string.Empty;
        public string Channel { get; set; } = string.Empty;
        public long Unix { get; set; }
    }

    /// <summary>
    /// リテイナーの台帳。
    ///
    /// リテイナー名やリテイナー ID から持ち主を引く手段はゲームにも API にも無いため、
    /// ここでは「観測したリテイナー」を蓄積し、確定情報（自分のリテイナー、名刺で判明した相手）と
    /// 推定（購入履歴と出品タイミングの相関）を併せて保持する。
    /// </summary>
    public sealed class RetainerRegistry
    {
        private readonly Dictionary<ulong, RetainerProfile> _byId = new();
        private readonly object _lock = new();
        private bool _dirty;
        private DateTime _lastSaveUtc = DateTime.MinValue;

        private string FilePath =>
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "retainers.json");

        public int Count
        {
            get { lock (_lock) return _byId.Count; }
        }

        public int IdentifiedCount
        {
            get
            {
                lock (_lock)
                    return _byId.Values.Count(p => p.HasOwner || !string.IsNullOrEmpty(p.GuessedOwnerName));
            }
        }

        public List<RetainerProfile> Snapshot()
        {
            lock (_lock) return _byId.Values.ToList();
        }

        /// <summary>観測した出品からリテイナーを記録する。</summary>
        public void Observe(ListingRecord listing)
        {
            if (listing.RetainerId == 0) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            lock (_lock)
            {
                if (!_byId.TryGetValue(listing.RetainerId, out var profile))
                {
                    profile = new RetainerProfile
                    {
                        RetainerId = listing.RetainerId,
                        RetainerName = listing.RetainerName,
                        FirstSeenUnix = now,
                    };
                    _byId[listing.RetainerId] = profile;
                }

                if (!string.IsNullOrEmpty(listing.RetainerName))
                    profile.RetainerName = listing.RetainerName;

                if (listing.OwnerContentId != 0)
                {
                    profile.OwnerContentId = listing.OwnerContentId;
                    var identity = Plugin.Identities.Resolve(listing.OwnerContentId);
                    if (identity is { Source: not IdentitySource.Inferred })
                        profile.OwnerName = identity.Name;
                }

                profile.LastSeenUnix = now;
                profile.ObservedListings++;
                if (!profile.ObservedItems.Contains(listing.ItemId))
                    profile.ObservedItems.Add(listing.ItemId);

                // 製作者署名を数える。同じ製作者に偏るほど「自作品を売っている＝持ち主＝製作者」
                // の可能性が高くなる。
                if (listing.ArtisanContentId != 0)
                {
                    profile.ArtisanCounts.TryGetValue(listing.ArtisanContentId, out var count);
                    profile.ArtisanCounts[listing.ArtisanContentId] = count + 1;
                }

                _dirty = true;
            }
        }

        /// <summary>自分のリテイナーを確定として登録する。</summary>
        public void RegisterOwn(ulong retainerId, string retainerName, string ownerName, ulong ownerContentId)
        {
            if (retainerId == 0) return;

            lock (_lock)
            {
                if (!_byId.TryGetValue(retainerId, out var profile))
                {
                    profile = new RetainerProfile
                    {
                        RetainerId = retainerId,
                        FirstSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    };
                    _byId[retainerId] = profile;
                }

                profile.RetainerName = retainerName;
                profile.OwnerName = ownerName;
                profile.OwnerContentId = ownerContentId;
                profile.IsMine = true;
                profile.LastSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _dirty = true;
            }
        }

        /// <summary>推定結果を書き込む。</summary>
        public void SetGuess(ulong retainerId, string ownerName, int score, IEnumerable<string> reasons)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(retainerId, out var profile)) return;
                if (profile.HasOwner) return;               // 確定情報があるなら触らない
                if (profile.GuessScore > score) return;     // より強い推定があるなら残す

                profile.GuessedOwnerName = ownerName;
                profile.GuessScore = score;
                profile.GuessReasons = reasons.ToList();
                _dirty = true;
            }
        }

        /// <summary>
        /// 条件を満たさなくなった推定を取り下げる。
        /// 誤った名前を出し続けるより「不明」に戻す方が実害が小さい。
        /// </summary>
        public void ClearGuess(ulong retainerId, string reason)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(retainerId, out var profile)) return;
                if (profile.HasOwner) return;
                if (string.IsNullOrEmpty(profile.GuessedOwnerName)) return;

                profile.GuessedOwnerName = null;
                profile.GuessScore = 0;
                profile.GuessReasons = new List<string> { $"推定を取り下げました（{reason}）" };
                _dirty = true;
            }
        }

        /// <summary>チャットでの言及を記録する。</summary>
        public bool AddChatMention(ulong retainerId, string speakerName, string channel)
        {
            if (retainerId == 0 || string.IsNullOrWhiteSpace(speakerName)) return false;

            lock (_lock)
            {
                if (!_byId.TryGetValue(retainerId, out var profile)) return false;

                profile.ChatMentions.Add(new ChatMention
                {
                    SpeakerName = speakerName,
                    Channel = channel,
                    Unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                });

                if (profile.ChatMentions.Count > 20)
                    profile.ChatMentions.RemoveRange(0, profile.ChatMentions.Count - 20);

                _dirty = true;
                return true;
            }
        }

        /// <summary>
        /// 同じ製作者の品を扱っているリテイナーを探す。
        /// 自作品を複数のリテイナーで売っている場合、それらは同じ持ち主である可能性が高い。
        /// </summary>
        public List<RetainerProfile> WithSameArtisan(ulong artisanId, ulong excludeRetainerId = 0)
        {
            if (artisanId == 0) return new List<RetainerProfile>();

            lock (_lock)
                return _byId.Values
                    .Where(p => p.RetainerId != excludeRetainerId && p.ArtisanCounts.ContainsKey(artisanId))
                    .OrderByDescending(p => p.ArtisanCounts[artisanId])
                    .ToList();
        }

        /// <summary>
        /// リテイナー ID が近いものを探す。
        /// 同じ人がまとめて作ったリテイナーは ID が連番に近くなることがあるため、
        /// 同一の持ち主を推測する材料になり得る。
        /// </summary>
        public List<(RetainerProfile Profile, ulong Distance)> WithNearbyId(ulong retainerId, ulong maxDistance)
        {
            if (retainerId == 0) return new List<(RetainerProfile, ulong)>();

            lock (_lock)
                return _byId.Values
                    .Where(p => p.RetainerId != retainerId)
                    .Select(p => (Profile: p, Distance: p.RetainerId > retainerId
                        ? p.RetainerId - retainerId
                        : retainerId - p.RetainerId))
                    .Where(x => x.Distance <= maxDistance)
                    .OrderBy(x => x.Distance)
                    .ToList();
        }

        /// <summary>すべてのリテイナー名を返す（チャット監視の照合用）。</summary>
        public List<(ulong Id, string Name)> AllNames()
        {
            lock (_lock)
                return _byId.Values
                    .Where(p => !string.IsNullOrWhiteSpace(p.RetainerName))
                    .Select(p => (p.RetainerId, p.RetainerName))
                    .ToList();
        }

        /// <summary>手がかりを突き合わせた結論を書き込む。変化があれば true。</summary>
        public bool ApplyConclusion(ulong retainerId, OwnerConclusion conclusion, List<OwnerEvidence> evidence)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(retainerId, out var profile)) return false;

                profile.Evidence = evidence;
                profile.Confidence = conclusion.Confidence;
                profile.InconclusiveReason = conclusion.Inconclusive;

                var changed = false;

                if (conclusion.IsCertain && !string.IsNullOrEmpty(conclusion.OwnerName))
                {
                    if (!string.Equals(profile.OwnerName, conclusion.OwnerName, StringComparison.Ordinal))
                    {
                        profile.OwnerName = conclusion.OwnerName;
                        changed = true;
                    }
                    profile.GuessedOwnerName = null;
                    profile.GuessScore = 0;
                }
                else if (!string.IsNullOrEmpty(conclusion.OwnerName))
                {
                    if (!string.Equals(profile.GuessedOwnerName, conclusion.OwnerName, StringComparison.Ordinal))
                        changed = true;

                    profile.GuessedOwnerName = conclusion.OwnerName;
                    profile.GuessScore = conclusion.Confidence;
                }
                else if (!profile.HasOwner && !string.IsNullOrEmpty(profile.GuessedOwnerName))
                {
                    // 結論が出せなくなったら取り下げる。
                    profile.GuessedOwnerName = null;
                    profile.GuessScore = 0;
                    changed = true;
                }

                profile.GuessReasons = conclusion.Supporting
                    .Select(e => $"[{e.KindLabel}] {e.Description}")
                    .ToList();

                if (conclusion.Inconclusive != null && string.IsNullOrEmpty(conclusion.OwnerName))
                    profile.GuessReasons.Add($"結論を出せません: {conclusion.Inconclusive}");

                _dirty = true;
                return changed;
            }
        }

        /// <summary>
        /// 持ち主を手動で確定させる。何らかの方法で分かった相手を、以後の推定より優先して扱う。
        /// </summary>
        public void SetOwnerManually(ulong retainerId, string ownerName)
        {
            lock (_lock)
            {
                if (!_byId.TryGetValue(retainerId, out var profile)) return;

                if (string.IsNullOrWhiteSpace(ownerName))
                {
                    // 空文字で呼ばれたら手動設定を解除する。
                    if (!profile.IsMine) profile.OwnerName = null;
                    profile.ManuallySet = false;
                }
                else
                {
                    profile.OwnerName = ownerName.Trim();
                    profile.ManuallySet = true;
                    profile.GuessedOwnerName = null;
                    profile.GuessScore = 0;
                    profile.GuessReasons = new List<string> { "手動で設定されました" };
                }

                _dirty = true;
            }
        }

        public RetainerProfile? Resolve(ulong retainerId)
        {
            lock (_lock) return _byId.TryGetValue(retainerId, out var p) ? p : null;
        }

        public RetainerProfile? ResolveByName(string retainerName)
        {
            if (string.IsNullOrWhiteSpace(retainerName)) return null;
            lock (_lock)
                return _byId.Values
                    .Where(p => string.Equals(p.RetainerName, retainerName, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.LastSeenUnix)
                    .FirstOrDefault();
        }

        public int Prune(int retentionDays)
        {
            if (retentionDays <= 0) return 0;
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)retentionDays * 86400L;

            int removed;
            lock (_lock)
            {
                var stale = _byId.Where(kv => !kv.Value.IsMine && !kv.Value.HasOwner &&
                                              kv.Value.LastSeenUnix < cutoff)
                                 .Select(kv => kv.Key).ToList();
                foreach (var key in stale) _byId.Remove(key);
                removed = stale.Count;
                if (removed > 0) _dirty = true;
            }
            return removed;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _byId.Clear();
                _dirty = true;
            }
            Save(force: true);
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var list = JsonConvert.DeserializeObject<List<RetainerProfile>>(File.ReadAllText(FilePath));
                lock (_lock)
                {
                    _byId.Clear();
                    foreach (var p in list ?? new List<RetainerProfile>())
                        if (p.RetainerId != 0) _byId[p.RetainerId] = p;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"リテイナー台帳の読み込みに失敗しました: {e.Message}");
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
                lock (_lock) json = JsonConvert.SerializeObject(_byId.Values.ToList(), Formatting.None);
                File.WriteAllText(FilePath, json);

                lock (_lock)
                {
                    _dirty = false;
                    _lastSaveUtc = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"リテイナー台帳の保存に失敗しました: {e.Message}");
            }
        }
    }
}
