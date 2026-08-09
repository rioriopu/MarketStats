global using System;
global using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MarketStats.Data;
using MarketStats.Game;
using MarketStats.UI;

namespace MarketStats
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "Market Stats";

        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
        [PluginService] internal static IFramework Framework { get; private set; } = null!;
        [PluginService] internal static IClientState ClientState { get; private set; } = null!;
        [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
        [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
        [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
        [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
        [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
        [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
        [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
        [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

        private const string CommandName = "/marketstats";
        private const string CommandShort = "/mstats";
        private const string CommandShorter = "/msts";

        internal static PluginConfig Config { get; private set; } = null!;
        internal static readonly ItemCatalog Items = new();
        internal static readonly SaleStore Store = new();
        internal static readonly FavoritesStore Favorites = new();
        internal static readonly UniversalisClient Universalis = new();
        internal static RetainerHistoryCapture Capture { get; private set; } = null!;

        private static MainWindow? _mainWindow;
        private static WindowSystem? _windowSystem;

        // 定期処理のインターバル管理
        private static DateTime _nextMaintenanceUtc = DateTime.MinValue;

        public Plugin()
        {
            Config = PluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
            Config.Init(PluginInterface);

            try
            {
                Items.Build(DataManager);
                Favorites.Load();
                Store.Load();
                Store.Prune(Config, Favorites);
                Store.Save(force: true);
            }
            catch (Exception e)
            {
                PluginLog.Error($"初期化に失敗しました: {e}");
            }

            // 新規レコードの取り込み通知（チャット出力）
            Store.SalesAdded += NotifySales;

            Capture = new RetainerHistoryCapture();
            Capture.Initialize();
            Capture.HistoryWindowOpened += OnHistoryWindowOpened;

            _windowSystem = new WindowSystem("MarketStats");
            _mainWindow = new MainWindow { IsOpen = Config.AutoOpenOnLoad };
            _windowSystem.AddWindow(_mainWindow);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "リテイナー販売履歴の購入者別統計を開く（引数: buyers / history / config）",
            });
            CommandManager.AddHandler(CommandShort, new CommandInfo(OnCommand)
            {
                HelpMessage = "Market Stats（短縮形）",
                ShowInHelp = false,
            });
            CommandManager.AddHandler(CommandShorter, new CommandInfo(OnCommand)
            {
                HelpMessage = "Market Stats（短縮形 msts）",
                ShowInHelp = false,
            });

            PluginInterface.UiBuilder.Draw += OnDrawUi;
            PluginInterface.UiBuilder.OpenConfigUi += OnOpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi += OnOpenMainUi;
            Framework.Update += OnFrameworkUpdate;
        }

        private static void OnHistoryWindowOpened()
        {
            if (Config.OpenOnRetainerHistory && _mainWindow != null)
                _mainWindow.IsOpen = true;
        }

        private static void OnFrameworkUpdate(IFramework framework)
        {
            try
            {
                Capture.Tick();

                var batches = Capture.Drain();
                var added = 0;
                foreach (var batch in batches)
                    added += Store.Merge(batch);

                if (added > 0)
                {
                    _mainWindow?.InvalidateCache();
                    Store.Save();
                }

                var now = DateTime.UtcNow;
                if (now >= _nextMaintenanceUtc)
                {
                    _nextMaintenanceUtc = now.AddMinutes(10);
                    if (Store.Prune(Config, Favorites) > 0)
                        _mainWindow?.InvalidateCache();
                    Store.Save();
                }
            }
            catch (Exception e)
            {
                PluginLog.Error($"Framework 更新中に例外が発生しました: {e.Message}");
            }
        }

        /// <summary>新しい売却が取り込まれたときのチャット通知。</summary>
        internal static void NotifySales(IReadOnlyList<SaleRecord> records)
        {
            if (!Config.NotifyNewSales || records.Count == 0) return;

            foreach (var group in records
                         .GroupBy(r => (r.BuyerName, r.ItemId, r.Hq))
                         .OrderBy(g => g.Min(r => r.UnixTime)))
            {
                var total = group.Sum(r => (long)r.Quantity);
                var gil = group.Aggregate(0UL, (acc, r) => acc + r.TotalGil);
                var buyer = string.IsNullOrEmpty(group.Key.BuyerName)
                    ? SaleAggregator.UnknownBuyer
                    : group.Key.BuyerName;
                var itemName = Items.GetName(group.Key.ItemId) + (group.Key.Hq ? " (HQ)" : string.Empty);

                ChatGui.Print($"[Market Stats] {buyer} が {itemName} を {total:N0}個 購入 ({gil:N0} ギル)");
            }
        }

        private static void OnCommand(string command, string args)
        {
            var arg = args.Trim().ToLowerInvariant();
            if (_mainWindow == null) return;

            switch (arg)
            {
                case "":
                    _mainWindow.Toggle();
                    break;
                case "buyers":
                    _mainWindow.RequestTab(MainWindow.Tab.Buyers);
                    _mainWindow.IsOpen = true;
                    break;
                case "history":
                    _mainWindow.RequestTab(MainWindow.Tab.History);
                    _mainWindow.IsOpen = true;
                    break;
                case "config":
                case "settings":
                    _mainWindow.RequestTab(MainWindow.Tab.Settings);
                    _mainWindow.IsOpen = true;
                    break;
                case "dump":
                    Capture.DumpArrays();
                    ChatGui.Print("[Market Stats] 売却履歴ウィンドウの配列をログへ出力しました。");
                    break;
                default:
                    ChatGui.PrintError($"[Market Stats] 不明な引数です: {arg}");
                    break;
            }
        }

        private static void OnDrawUi() => _windowSystem?.Draw();

        private static void OnOpenConfigUi()
        {
            if (_mainWindow == null) return;
            _mainWindow.RequestTab(MainWindow.Tab.Settings);
            _mainWindow.IsOpen = true;
        }

        private static void OnOpenMainUi()
        {
            if (_mainWindow != null) _mainWindow.IsOpen = true;
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkUpdate;
            PluginInterface.UiBuilder.Draw -= OnDrawUi;
            PluginInterface.UiBuilder.OpenConfigUi -= OnOpenConfigUi;
            PluginInterface.UiBuilder.OpenMainUi -= OnOpenMainUi;

            CommandManager.RemoveHandler(CommandName);
            CommandManager.RemoveHandler(CommandShort);
            CommandManager.RemoveHandler(CommandShorter);

            Store.SalesAdded -= NotifySales;
            Capture.HistoryWindowOpened -= OnHistoryWindowOpened;
            Capture.Dispose();
            Universalis.Dispose();

            Store.Save(force: true);
            Favorites.Save();

            _windowSystem?.RemoveAllWindows();
            _windowSystem = null;
            _mainWindow = null;
        }
    }
}
