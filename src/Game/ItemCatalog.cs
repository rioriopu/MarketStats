using Dalamud.Plugin.Services;
using Sheets = Lumina.Excel.Sheets;

namespace MarketStats.Game
{
    /// <summary>ItemId → 名前 / アイコンID の解決。売却ログには ItemId しか残らないため必須。</summary>
    public sealed class ItemCatalog
    {
        private readonly Dictionary<uint, string> _names = new();
        private readonly Dictionary<uint, ushort> _icons = new();

        public bool IsBuilt { get; private set; }

        public void Build(IDataManager data)
        {
            try
            {
                var sheet = data.GetExcelSheet<Sheets.Item>();
                if (sheet == null) return;

                foreach (var item in sheet)
                {
                    var name = item.Name.ExtractText();
                    if (string.IsNullOrEmpty(name)) continue;
                    _names[item.RowId] = name;
                    _icons[item.RowId] = item.Icon;
                }

                IsBuilt = true;
                Plugin.PluginLog.Information($"アイテム名を {_names.Count} 件読み込みました。");
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Error($"アイテムシートの読み込みに失敗しました: {e.Message}");
            }
        }

        public string GetName(uint itemId) =>
            _names.TryGetValue(itemId, out var n) ? n : $"#{itemId}";

        public ushort GetIconId(uint itemId) =>
            _icons.TryGetValue(itemId, out var i) ? i : (ushort)0;
    }
}
