using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace MarketStats.Data
{
    /// <summary>
    /// 売却ログの保管庫。ゲーム内履歴（リテイナーごと最大20件）から取り込んだレコードを
    /// 重複排除しつつ蓄積し、設定された保持期間で自動的に間引く。
    /// </summary>
    public sealed class SaleStore
    {
        private readonly List<SaleRecord> _records = new();
        private readonly object _lock = new();
        private bool _dirty;
        private DateTime _lastSaveUtc = DateTime.MinValue;

        private string FilePath =>
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "sales.json");

        /// <summary>新しい売却が取り込まれた時に発火する（引数は新規レコード）。</summary>
        public event Action<IReadOnlyList<SaleRecord>>? SalesAdded;

        public int Count
        {
            get { lock (_lock) return _records.Count; }
        }

        /// <summary>スナップショットを返す（呼び出し側でロック不要）。</summary>
        public List<SaleRecord> Snapshot()
        {
            lock (_lock) return _records.ToList();
        }

        /// <summary>
        /// ゲームから読んだ売却履歴スナップショットを取り込む。
        /// 同一内容のレコードが複数件ある場合（99個×10枠の同時購入など）に潰れないよう、
        /// キー単位の「件数」で突き合わせて不足分だけを追加する。
        /// </summary>
        /// <returns>新規に追加された件数。</returns>
        public int Merge(IReadOnlyList<SaleRecord> snapshot)
        {
            if (snapshot.Count == 0) return 0;

            var added = new List<SaleRecord>();

            lock (_lock)
            {
                var stored = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var r in _records)
                {
                    stored.TryGetValue(r.DedupeKey, out var n);
                    stored[r.DedupeKey] = n + 1;
                }

                foreach (var group in snapshot.GroupBy(r => r.DedupeKey, StringComparer.Ordinal))
                {
                    stored.TryGetValue(group.Key, out var have);
                    var incoming = group.Count();
                    if (incoming <= have) continue;

                    foreach (var rec in group.Take(incoming - have))
                    {
                        _records.Add(rec);
                        added.Add(rec);
                    }
                }

                if (added.Count > 0)
                {
                    _records.Sort((a, b) => b.UnixTime.CompareTo(a.UnixTime));
                    _dirty = true;
                }
            }

            if (added.Count > 0)
                SalesAdded?.Invoke(added);

            return added.Count;
        }

        /// <summary>
        /// 保持期間を過ぎたレコードを削除する。お気に入り購入者のレコードは
        /// お気に入り用の（通常より長い）保持期間を適用する。
        /// </summary>
        /// <returns>削除した件数。</returns>
        public int Prune(PluginConfig config, FavoritesStore favorites)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var normalCutoff = config.RetentionDays <= 0
                ? long.MinValue
                : now - (long)config.RetentionDays * 86400L;
            var favoriteCutoff = config.FavoriteRetentionDays <= 0
                ? long.MinValue
                : now - (long)config.FavoriteRetentionDays * 86400L;

            int removed;
            lock (_lock)
            {
                removed = _records.RemoveAll(r =>
                {
                    var cutoff = favorites.IsFavorite(r.BuyerName) ? favoriteCutoff : normalCutoff;
                    return cutoff != long.MinValue && r.UnixTime < cutoff;
                });
                if (removed > 0) _dirty = true;
            }

            return removed;
        }

        /// <summary>指定した購入者のレコードをすべて削除する。</summary>
        public int RemoveBuyer(string buyerName)
        {
            int removed;
            lock (_lock)
            {
                removed = _records.RemoveAll(r =>
                    string.Equals(r.BuyerName, buyerName, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) _dirty = true;
            }
            return removed;
        }

        public void Clear()
        {
            lock (_lock)
            {
                _records.Clear();
                _dirty = true;
            }
            Save(force: true);
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var json = File.ReadAllText(FilePath);
                var list = JsonConvert.DeserializeObject<List<SaleRecord>>(json);
                lock (_lock)
                {
                    _records.Clear();
                    if (list != null) _records.AddRange(list);
                    _records.Sort((a, b) => b.UnixTime.CompareTo(a.UnixTime));
                }
                Plugin.PluginLog.Information($"売却ログを {_records.Count} 件読み込みました。");
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Error($"売却ログの読み込みに失敗しました: {e.Message}");
            }
        }

        /// <summary>変更があれば保存する。force=false のときは最短保存間隔（5秒）を守る。</summary>
        public void Save(bool force = false)
        {
            lock (_lock)
            {
                if (!_dirty && !force) return;
                if (!force && (DateTime.UtcNow - _lastSaveUtc).TotalSeconds < 5) return;
            }

            try
            {
                var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
                Directory.CreateDirectory(dir);

                string json;
                lock (_lock) json = JsonConvert.SerializeObject(_records, Formatting.None);

                // 書き込み途中のクラッシュでログを失わないよう一時ファイル経由で置き換える。
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Copy(tmp, FilePath, overwrite: true);
                File.Delete(tmp);

                lock (_lock)
                {
                    _dirty = false;
                    _lastSaveUtc = DateTime.UtcNow;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Error($"売却ログの保存に失敗しました: {e.Message}");
            }
        }

        /// <summary>CSV へ書き出す。</summary>
        public string ExportCsv()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("日時,購入者,アイテム,HQ,数量,単価,合計,リテイナー,所有キャラ,ワールド,マネキン");
            foreach (var r in Snapshot().OrderByDescending(r => r.UnixTime))
            {
                sb.Append(r.LocalTime.ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
                  .Append(Escape(r.BuyerName)).Append(',')
                  .Append(Escape(Plugin.Items.GetName(r.ItemId))).Append(',')
                  .Append(r.Hq ? "HQ" : string.Empty).Append(',')
                  .Append(r.Quantity).Append(',')
                  .Append(r.UnitPrice).Append(',')
                  .Append(r.TotalGil).Append(',')
                  .Append(Escape(r.RetainerName)).Append(',')
                  .Append(Escape(r.OwnerName)).Append(',')
                  .Append(Escape(r.OwnerWorld)).Append(',')
                  .Append(r.OnMannequin ? "yes" : string.Empty)
                  .AppendLine();
            }
            return sb.ToString();

            static string Escape(string? s)
            {
                s ??= string.Empty;
                return s.Contains(',') || s.Contains('"')
                    ? '"' + s.Replace("\"", "\"\"") + '"'
                    : s;
            }
        }
    }
}
