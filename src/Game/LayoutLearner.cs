using System.IO;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Newtonsoft.Json;

namespace MarketStats.Game
{
    /// <summary>学習したフィールドの位置。</summary>
    public sealed class LearnedField
    {
        public string Name { get; set; } = string.Empty;

        /// <summary>出品 1 件の先頭からの位置。</summary>
        public int Offset { get; set; }

        /// <summary>この位置で正しく読めた回数。</summary>
        public int Confirmations { get; set; }

        /// <summary>この位置で読めなかった回数。</summary>
        public int Mismatches { get; set; }

        public long LearnedUnix { get; set; }

        /// <summary>学習したときのゲームのバージョン。</summary>
        public string GameVersion { get; set; } = string.Empty;

        [JsonIgnore]
        public bool IsReliable => Confirmations >= 2 && Confirmations > Mismatches;
    }

    /// <summary>
    /// 出品データの読み方を、自分で確かめて覚える。
    ///
    /// 構造体の定義は決め打ちなので、ゲームの更新でずれることがある。
    /// そこで「答えが分かっているもの」＝自分のリテイナーの出品を使い、
    /// 自分のリテイナー ID やアイテム ID が実際にどの位置にあるかを探して記録する。
    ///
    /// 一度学習すれば、定義がずれていても正しい位置から読める。
    /// 学習した位置は毎回検算し、合わなくなったら学び直す。
    /// </summary>
    public sealed unsafe class LayoutLearner
    {
        /// <summary>出品 1 件のサイズ。</summary>
        private const int ListingSize = 0xB8;

        /// <summary>出品配列の開始位置。</summary>
        private const int ListingsOffset = 0x30;

        private readonly Dictionary<string, LearnedField> _fields = new();
        private readonly object _lock = new();

        public string LastResult { get; private set; } = "未学習";
        public DateTime LastLearnedLocal { get; private set; } = DateTime.MinValue;

        private string FilePath =>
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "layout.json");

        public List<LearnedField> Snapshot()
        {
            lock (_lock) return _fields.Values.ToList();
        }

        /// <summary>学習済みの位置を返す。信用できるものが無ければ null。</summary>
        public int? GetOffset(string field)
        {
            lock (_lock)
                return _fields.TryGetValue(field, out var learned) && learned.IsReliable
                    ? learned.Offset
                    : null;
        }

        /// <summary>
        /// いま表示されている出品一覧を使って、読み方を確かめる。
        ///
        /// 自分のリテイナーの出品が含まれていれば答え合わせができるが、
        /// 含まれていなくても「アイテム ID」「価格」など照合できるものはある。
        /// </summary>
        public void Learn()
        {
            try
            {
                var module = InfoModule.Instance();
                if (module == null)
                {
                    LastResult = "ゲーム内部の情報を取得できませんでした。";
                    return;
                }

                var proxy = (InfoProxyItemSearch*)module->GetInfoProxyById(InfoProxyId.ItemSearch);
                if (proxy == null)
                {
                    LastResult = "出品データを取得できませんでした。";
                    return;
                }

                var count = (int)Math.Min(proxy->ListingCount, 100u);
                if (count == 0)
                {
                    LastResult = "出品がありません。マーケットでアイテムを開いてからお試しください。";
                    return;
                }

                var basePointer = (byte*)proxy;
                if (!SafeMemory.IsFullyReadable((nint)basePointer, ListingsOffset + ListingSize * count))
                {
                    LastResult = "出品データを読み取れませんでした。";
                    return;
                }

                var self = SelfRetainerProbe.Read();
                var learned = 0;
                var confirmed = 0;

                // 1. アイテム ID は必ず照合できる（検索中のアイテムと一致するはず）。
                if (proxy->SearchItemId != 0)
                    if (LearnField("item", basePointer, count, 4,
                            value => value == proxy->SearchItemId, ref learned, ref confirmed))
                        LastResult = "アイテム ID の位置を確認しました。";

                // 2. 自分のリテイナーが出品していれば、リテイナー ID とオーナー ID を照合できる。
                foreach (var retainer in self.Retainers)
                {
                    var id = retainer.RetainerId;
                    if (LearnField("retainer", basePointer, count, 8, value => value == id,
                            ref learned, ref confirmed))
                        break;
                }

                if (self.ContentId != 0)
                    LearnField("owner", basePointer, count, 8,
                        value => value == self.ContentId, ref learned, ref confirmed);

                LastLearnedLocal = DateTime.Now;
                LastResult =
                    $"{count} 件の出品を調べ、{confirmed} 個の位置を確認しました" +
                    (learned > 0 ? $"（うち {learned} 個は新たに学習）。" : "。") +
                    (self.Retainers.Count == 0
                        ? " 自分のリテイナーの出品があると、もっと詳しく確かめられます。"
                        : string.Empty);

                Save();
            }
            catch (Exception e)
            {
                LastResult = $"学習に失敗しました: {e.Message}";
                Plugin.PluginLog.Warning($"読み方の学習に失敗: {e.Message}");
            }
        }

        /// <summary>
        /// 出品全件を走査して、指定した条件に合う値が同じ位置に現れるかを調べる。
        /// 複数の出品で同じ位置なら、そこがそのフィールドの位置だと判断できる。
        /// </summary>
        private bool LearnField(
            string name, byte* basePointer, int listingCount, int width,
            Func<ulong, bool> matches, ref int learned, ref int confirmed)
        {
            var votes = new Dictionary<int, int>();

            for (var index = 0; index < listingCount; index++)
            {
                var listing = basePointer + ListingsOffset + ListingSize * index;

                for (var offset = 0; offset + width <= ListingSize; offset += 4)
                {
                    var value = width == 8
                        ? *(ulong*)(listing + offset)
                        : *(uint*)(listing + offset);

                    if (value == 0 || !matches(value)) continue;

                    votes.TryGetValue(offset, out var current);
                    votes[offset] = current + 1;
                }
            }

            if (votes.Count == 0) return false;

            var best = votes.OrderByDescending(kv => kv.Value).First();

            lock (_lock)
            {
                if (_fields.TryGetValue(name, out var existing))
                {
                    if (existing.Offset == best.Key)
                    {
                        existing.Confirmations++;
                        confirmed++;
                    }
                    else
                    {
                        existing.Mismatches++;

                        // 以前の位置が続けて外れるなら、新しい位置に乗り換える。
                        if (existing.Mismatches > existing.Confirmations)
                        {
                            existing.Offset = best.Key;
                            existing.Confirmations = 1;
                            existing.Mismatches = 0;
                            existing.LearnedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            learned++;

                            Plugin.PluginLog.Information(
                                $"読み方を学び直しました: {name} → +0x{best.Key:X2}");
                        }
                    }

                    return true;
                }

                _fields[name] = new LearnedField
                {
                    Name = name,
                    Offset = best.Key,
                    Confirmations = 1,
                    LearnedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    GameVersion = GetGameVersion(),
                };

                learned++;
                confirmed++;
                Plugin.PluginLog.Information($"読み方を学習しました: {name} → +0x{best.Key:X2}");
                return true;
            }
        }

        /// <summary>
        /// 学習した位置と、決め打ちの定義がずれていないかを説明する。
        /// ずれていれば、定義の方が古い（＝学習した位置から読むべき）と分かる。
        /// </summary>
        public string DescribeDivergence()
        {
            var expected = new Dictionary<string, int>
            {
                ["item"] = 0x94,
                ["retainer"] = 0x70,
                ["owner"] = 0x78,
            };

            var lines = new List<string>();

            lock (_lock)
            {
                foreach (var (name, field) in _fields)
                {
                    if (!expected.TryGetValue(name, out var defined)) continue;

                    lines.Add(field.Offset == defined
                        ? $"{name}: +0x{field.Offset:X2}（定義どおり / 確認 {field.Confirmations} 回）"
                        : $"{name}: +0x{field.Offset:X2} ← 定義は +0x{defined:X2}。読み方がずれています。");
                }
            }

            return lines.Count == 0 ? "まだ学習していません。" : string.Join("\n", lines);
        }

        private static string GetGameVersion()
        {
            try
            {
                var repo = Plugin.DataManager.GameData.Repositories.Values.FirstOrDefault();
                return repo?.Version ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var list = JsonConvert.DeserializeObject<List<LearnedField>>(File.ReadAllText(FilePath));
                lock (_lock)
                {
                    _fields.Clear();
                    foreach (var field in list ?? new List<LearnedField>())
                        if (!string.IsNullOrEmpty(field.Name)) _fields[field.Name] = field;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"学習結果の読み込みに失敗しました: {e.Message}");
            }
        }

        public void Save()
        {
            try
            {
                var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
                Directory.CreateDirectory(dir);

                List<LearnedField> list;
                lock (_lock) list = _fields.Values.ToList();

                File.WriteAllText(FilePath, JsonConvert.SerializeObject(list, Formatting.Indented));
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"学習結果の保存に失敗しました: {e.Message}");
            }
        }

        public void Clear()
        {
            lock (_lock) _fields.Clear();
            LastResult = "学習結果を消去しました。";
            Save();
        }
    }
}
