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
        internal static readonly MarketHistoryStore Purchases = new();
        internal static readonly RetainerRegistry Retainers = new();
        internal static readonly OwnListingStore OwnListings = new();
        internal static readonly UniversalisClient Universalis = new();
        internal static readonly LodestoneVerifier NameVerifier = new();

        internal static RetainerHistoryCapture Capture { get; private set; } = null!;
        internal static IdentityCollector IdentityCollector { get; private set; } = null!;
        internal static MarketBoardWatcher MarketWatcher { get; private set; } = null!;
        internal static RetainerSellListWatcher SellListWatcher { get; private set; } = null!;
        internal static SaleHistoryAutoOpen AutoOpen { get; private set; } = null!;
        internal static CharaCardLookup CharaCard { get; private set; } = null!;
        internal static MarketContextMenu SellerMenu { get; private set; } = null!;
        internal static ChatRetainerWatcher ChatWatcher { get; private set; } = null!;
        internal static MarketTaps Taps { get; private set; } = null!;
        internal static CrafterNameHarvester CrafterNames { get; private set; } = null!;
        internal static readonly LayoutLearner Layout = new();

        private static MainWindow? _mainWindow;
        private static SellerOverlayWindow? _sellerOverlay;
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
                Purchases.Load();
                Retainers.Load();
                OwnListings.Load();
                Layout.Load();

                Store.Prune(Config, Favorites);
                Listings.Prune(Config.ListingRetentionDays);
                Pending.Prune(Config.ListingRetentionDays);
                Purchases.Prune(Config.ListingRetentionDays);
                Retainers.Prune(Config.ListingRetentionDays * 3);
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

            ChatWatcher = new ChatRetainerWatcher();
            ChatWatcher.Initialize();

            Taps = new MarketTaps();
            Taps.Initialize();

            CrafterNames = new CrafterNameHarvester();
            CrafterNames.Initialize();

            _windowSystem = new WindowSystem("MarketStats");
            _mainWindow = new MainWindow { IsOpen = Config.AutoOpenOnLoad };
            _windowSystem.AddWindow(_mainWindow);

            // 出品一覧を開いている間だけ横に出る小窓。IsOpen は常に true で、
            // 実際に描くかどうかは DrawConditions() が判定する。
            _sellerOverlay = new SellerOverlayWindow { IsOpen = true };
            _windowSystem.AddWindow(_sellerOverlay);

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

            Purchases.Prune(Config.ListingRetentionDays);
            Retainers.Prune(Config.ListingRetentionDays * 3);
            RegisterOwnRetainers();
            UpdateOwnerGuesses();

            Store.Save();
            Listings.Save();
            Pending.Save();
            Identities.Save();
            Purchases.Save();
            Retainers.Save();
        }

        /// <summary>自分のリテイナーを台帳に確定登録する（自分の出品を他人と混同しないため）。</summary>
        private static unsafe void RegisterOwnRetainers()
        {
            try
            {
                if (!PlayerState.IsLoaded) return;

                var manager = FFXIVClientStructs.FFXIV.Client.Game.RetainerManager.Instance();
                if (manager == null || !manager->IsReady) return;

                var owner = PlayerState.CharacterName;
                var ownerId = PlayerState.ContentId;
                var count = manager->GetRetainerCount();

                var world = Game.LodestoneLink.GetCurrentWorld();

                for (uint i = 0; i < count; i++)
                {
                    var retainer = manager->GetRetainerBySortedIndex(i);
                    if (retainer == null || retainer->RetainerId == 0) continue;

                    var name = retainer->NameString;
                    Retainers.RegisterOwn(retainer->RetainerId, name, owner, ownerId);

                    // キャラクター別・リテイナー別の一覧に使う概要も控えておく。
                    OwnListings.UpdateSummary(
                        retainer->RetainerId, name, ownerId, owner, world,
                        retainer->Gil, retainer->MarketItemCount, retainer->MarketExpire, (byte)retainer->Town);
                }

                OwnListings.Save();
            }
            catch (Exception e)
            {
                PluginLog.Debug($"自分のリテイナーを登録できませんでした: {e.Message}");
            }
        }

        /// <summary>観測した出品と購入履歴を突き合わせ、リテイナーの持ち主を推定する。</summary>
        private static void UpdateOwnerGuesses()
        {
            if (!Config.EnableResaleTracking || !Config.EnableOwnerInference) return;

            try
            {
                var updated = OwnerResolver.Update(
                    Listings, Purchases, Store, Retainers, Identities, Config.ResaleWindowHours);

                if (updated > 0)
                {
                    PluginLog.Debug($"リテイナー {updated} 件について持ち主の推定を更新しました。");
                    _mainWindow?.InvalidateCache();
                }
            }
            catch (Exception e)
            {
                PluginLog.Warning($"持ち主の推定に失敗しました: {e.Message}");
            }
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
                if (records.Count > 0)
                {
                    Listings.Observe(records);
                    foreach (var record in records) Retainers.Observe(record);
                    Listings.Save();
                }

                // 購入履歴も取り込む。買い手の名前は公開されているので、
                // 「誰が大量に買っているか」の材料になる。
                var purchases = UniversalisClient.ToPurchases(snapshot);
                if (purchases.Count > 0)
                {
                    Purchases.Add(purchases);
                    Purchases.Save();
                }
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
                case "own":
                case "mylistings":
                    _mainWindow.RequestTab(MainWindow.Tab.OwnListings);
                    _mainWindow.IsOpen = true;
                    break;
                case "search":
                case "find":
                    _mainWindow.RequestTab(MainWindow.Tab.Search);
                    _mainWindow.IsOpen = true;
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
                case "market":
                case "buyers2":
                    _mainWindow.RequestTab(MainWindow.Tab.MarketBuyers);
                    _mainWindow.IsOpen = true;
                    break;
                case "retainers":
                    _mainWindow.RequestTab(MainWindow.Tab.Retainers);
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
                case "listings":
                    MarketWatcher.DumpListings();
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

            CrafterNames.Dispose();
            Taps.Dispose();
            ChatWatcher.Dispose();
            SellerMenu.Dispose();
            AutoOpen.Dispose();
            SellListWatcher.Dispose();
            MarketWatcher.Dispose();
            Capture.Dispose();
            Universalis.Dispose();
            NameVerifier.Dispose();

            Store.Save(force: true);
            Favorites.Save();
            Identities.Save(force: true);
            Purchases.Save(force: true);
            Retainers.Save(force: true);
            OwnListings.Save(force: true);
            Listings.Save(force: true);
            Pending.Save(force: true);

            _windowSystem?.RemoveAllWindows();
            _windowSystem = null;
            _mainWindow = null;
            _sellerOverlay = null;
        }
    }
}
