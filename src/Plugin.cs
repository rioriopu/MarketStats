global using System;
global using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        [PluginService] internal static IMarketBoard MarketBoard { get; private set; } = null!;
        [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;

        private const string CommandName = "/marketstats";
        private const string CommandShort = "/mstats";
        private const string CommandShorter = "/msts";

        internal static PluginConfig Config { get; private set; } = null!;

        internal static readonly ItemCatalog Items = new();
        internal static readonly SaleStore Store = new();
        internal static readonly FavoritesStore Favorites = new();
        internal static readonly IdentityStore Identities = new();
        internal static readonly ListingStore Listings = new();
        internal static readonly PendingSaleStore Pending = new();
        internal static readonly UniversalisClient Universalis = new();

        internal static RetainerHistoryCapture Capture { get; private set; } = null!;
        internal static IdentityCollector IdentityCollector { get; private set; } = null!;
        internal static MarketBoardWatcher MarketWatcher { get; private set; } = null!;
        internal static RetainerSellListWatcher SellListWatcher { get; private set; } = null!;
        internal static SaleHistoryAutoOpen AutoOpen { get; private set; } = null!;
        internal static CharaCardLookup CharaCard { get; private set; } = null!;
        internal static MarketContextMenu SellerMenu { get; private set; } = null!;

        private static MainWindow? _mainWindow;
        private static WindowSystem? _windowSystem;

        private static DateTime _nextMaintenanceUtc = DateTime.MinValue;
        private static DateTime _nextUniversalisFetchUtc = DateTime.MinValue;
        private static readonly Dictionary<uint, DateTime> _lastTrackedItem = new();

        public Plugin()
        {
            Config = PluginInterface.GetPluginConfig() as PluginConfig ?? new PluginConfig();
            Config.Init(PluginInterface);

            try
            {
                Items.Build(DataManager);
                Favorites.Load();
                Store.Load();
                Identities.Load();
                Listings.Load();
                Pending.Load();

                Store.Prune(Config, Favorites);
                Listings.Prune(Config.ListingRetentionDays);
                Pending.Prune(Config.ListingRetentionDays);
                Store.Save(force: true);
            }
            catch (Exception e)
            {
                PluginLog.Error($"初期化に失敗しました: {e}");
            }

            Store.SalesAdded += OnSalesAdded;

            Capture = new RetainerHistoryCapture();
            Capture.Initialize();
            Capture.HistoryWindowOpened += OnHistoryWindowOpened;

            IdentityCollector = new IdentityCollector();

            MarketWatcher = new MarketBoardWatcher();
            MarketWatcher.Initialize();

            SellListWatcher = new RetainerSellListWatcher();
            SellListWatcher.Initialize();

            AutoOpen = new SaleHistoryAutoOpen();
            AutoOpen.Initialize();

            CharaCard = new CharaCardLookup();

            SellerMenu = new MarketContextMenu();
            SellerMenu.Attach();

            _windowSystem = new WindowSystem("MarketStats");
            _mainWindow = new MainWindow { IsOpen = Config.AutoOpenOnLoad };
            _windowSystem.AddWindow(_mainWindow);

            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "リテイナー販売履歴の購入者別統計を開く（引数: buyers / history / sellers / config）",
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
                IdentityCollector.Tick();
                MarketWatcher.Tick();
                SellListWatcher.Tick();
                CharaCard.Tick();

                DrainCaptures();
                RunMaintenance();
                RunUniversalisTracking();
            }
            catch (Exception e)
            {
                PluginLog.Error($"Framework 更新中に例外が発生しました: {e.Message}");
            }
        }

        /// <summary>取り込んだ売却履歴を蓄積し、取りこぼしの検出も行う。</summary>
        private static void DrainCaptures()
        {
            var batches = Capture.Drain();
            if (batches.Count == 0) return;

            var added = 0;
            foreach (var batch in batches)
            {
                DetectHistoryGap(batch);
                added += Store.Merge(batch);
            }

            // 自動取得フローに「履歴が届いた」ことを知らせる。
            AutoOpen.NotifyHistoryCaptured();

            if (added <= 0) return;

            _mainWindow?.InvalidateCache();
            Pending.Reconcile(Store.Snapshot());
            Store.Save();
            Pending.Save();
        }

        /// <summary>
        /// ゲーム側の履歴は 20 件で溢れる。今回取り込めた最も古い売却が、
        /// これまで記録できていた最新の売却より後なら、その間の取引を取りこぼしている。
        /// </summary>
        private static void DetectHistoryGap(List<SaleRecord> batch)
        {
            if (!Config.WarnHistoryGap || batch.Count < 20) return;

            var retainer = batch[0].RetainerName;
            if (string.IsNullOrEmpty(retainer)) return;

            var known = Store.LatestUnixFor(retainer);
            if (known <= 0) return;

            var oldest = batch.Min(r => r.UnixTime);
            if (oldest <= known) return;

            Pending.AddGap(new HistoryGap
            {
                RetainerName = retainer,
                OwnerName = batch[0].OwnerName,
                KnownUntilUnix = known,
                RecoveredFromUnix = oldest,
                DetectedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            });

            PluginLog.Warning(
                $"{retainer}: 売却履歴が 20 件を超えたため、" +
                $"{DateTimeOffset.FromUnixTimeSeconds(known).LocalDateTime:M/d HH:mm} ～ " +
                $"{DateTimeOffset.FromUnixTimeSeconds(oldest).LocalDateTime:M/d HH:mm} の取引を取りこぼした可能性があります。");
        }

        private static void RunMaintenance()
        {
            var now = DateTime.UtcNow;
            if (now < _nextMaintenanceUtc) return;
            _nextMaintenanceUtc = now.AddMinutes(10);

            if (Store.Prune(Config, Favorites) > 0) _mainWindow?.InvalidateCache();
            Listings.Prune(Config.ListingRetentionDays);
            Pending.Prune(Config.ListingRetentionDays);

            Store.Save();
            Listings.Save();
            Pending.Save();
            Identities.Save();
        }

        /// <summary>
        /// 最近購入されたアイテムの出品状況を Universalis から順に取得して、
        /// 再出品の追跡材料にする。負荷を避けるため 1 回につき 1 アイテムだけ。
        /// </summary>
        private static void RunUniversalisTracking()
        {
            if (!Config.EnableUniversalis || !Config.UniversalisAutoTrack) return;

            var now = DateTime.UtcNow;
            if (now < _nextUniversalisFetchUtc) return;
            _nextUniversalisFetchUtc = now.AddMinutes(Math.Max(5, Config.UniversalisTrackIntervalMinutes));

            var itemId = PickNextTrackedItem();
            if (itemId == 0) return;

            _lastTrackedItem[itemId] = DateTime.UtcNow;
            _ = TrackItemAsync(itemId);
        }

        private static uint PickNextTrackedItem()
        {
            var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 72 * 3600L;

            var recent = Store.Snapshot()
                .Where(r => r.UnixTime >= cutoff && r.HasBuyer)
                .Select(r => r.ItemId)
                .Distinct()
                .ToList();

            if (recent.Count == 0) return 0;

            return recent
                .OrderBy(id => _lastTrackedItem.TryGetValue(id, out var t) ? t : DateTime.MinValue)
                .First();
        }

        private static async Task TrackItemAsync(uint itemId)
        {
            try
            {
                var snapshot = await Universalis.FetchAsync(itemId, force: true).ConfigureAwait(false);
                if (snapshot.Error != null) return;

                var records = UniversalisClient.ToListingRecords(snapshot);
                if (records.Count == 0) return;

                Listings.Observe(records);
                Listings.Save();
            }
            catch (Exception e)
            {
                PluginLog.Warning($"Universalis からの出品取得に失敗しました: {e.Message}");
            }
        }

        /// <summary>新しい売却が取り込まれたときの処理（通知とキャッシュ更新）。</summary>
        private static void OnSalesAdded(IReadOnlyList<SaleRecord> records)
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
                case "sellers":
                    _mainWindow.RequestTab(MainWindow.Tab.Sellers);
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

            Store.SalesAdded -= OnSalesAdded;
            Capture.HistoryWindowOpened -= OnHistoryWindowOpened;

            SellerMenu.Dispose();
            AutoOpen.Dispose();
            SellListWatcher.Dispose();
            MarketWatcher.Dispose();
            Capture.Dispose();
            Universalis.Dispose();

            Store.Save(force: true);
            Favorites.Save();
            Identities.Save(force: true);
            Listings.Save(force: true);
            Pending.Save(force: true);

            _windowSystem?.RemoveAllWindows();
            _windowSystem = null;
            _mainWindow = null;
        }
    }
}
