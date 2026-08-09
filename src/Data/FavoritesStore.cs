using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace MarketStats.Data
{
    /// <summary>お気に入り登録した購入者。ログの保持期間が通常より長く設定される。</summary>
    public sealed class FavoriteBuyer
    {
        public string Name { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public long AddedUnix { get; set; }
    }

    /// <summary>お気に入り購入者の永続化。売却ログとは別ファイルに保存する。</summary>
    public sealed class FavoritesStore
    {
        private readonly Dictionary<string, FavoriteBuyer> _favorites =
            new(StringComparer.OrdinalIgnoreCase);

        private string FilePath =>
            Path.Combine(Plugin.PluginInterface.GetPluginConfigDirectory(), "favorites.json");

        public IReadOnlyCollection<FavoriteBuyer> All => _favorites.Values;

        public int Count => _favorites.Count;

        public bool IsFavorite(string? buyerName) =>
            !string.IsNullOrWhiteSpace(buyerName) && _favorites.ContainsKey(buyerName);

        public FavoriteBuyer? Get(string buyerName) =>
            _favorites.TryGetValue(buyerName, out var f) ? f : null;

        /// <summary>お気に入り状態を反転する。反転後の状態を返す。</summary>
        public bool Toggle(string buyerName)
        {
            if (string.IsNullOrWhiteSpace(buyerName)) return false;

            if (_favorites.Remove(buyerName))
            {
                Save();
                return false;
            }

            _favorites[buyerName] = new FavoriteBuyer
            {
                Name = buyerName,
                AddedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            };
            Save();
            return true;
        }

        public void SetNote(string buyerName, string note)
        {
            if (_favorites.TryGetValue(buyerName, out var f))
            {
                f.Note = note;
                Save();
            }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var json = File.ReadAllText(FilePath);
                var list = JsonConvert.DeserializeObject<List<FavoriteBuyer>>(json);
                _favorites.Clear();
                foreach (var f in list ?? new List<FavoriteBuyer>())
                {
                    if (string.IsNullOrWhiteSpace(f.Name)) continue;
                    _favorites[f.Name] = f;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"お気に入りの読み込みに失敗しました: {e.Message}");
            }
        }

        public void Save()
        {
            try
            {
                var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
                Directory.CreateDirectory(dir);
                var json = JsonConvert.SerializeObject(
                    _favorites.Values.OrderBy(f => f.Name).ToList(), Formatting.Indented);
                File.WriteAllText(FilePath, json);
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"お気に入りの保存に失敗しました: {e.Message}");
            }
        }
    }
}
