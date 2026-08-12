using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using MarketStats.Data;

namespace MarketStats.Game
{
    /// <summary>
    /// マーケットボードで表示された出品を記録する。
    ///
    /// ゲームが保持している出品データ（<see cref="InfoProxyItemSearch"/>）には、
    /// 出品リテイナーのオーナーの ContentId が含まれている。名前は入っていないが、
    /// ContentId は一意なので「同じ人物の出品」をまとめる鍵として使える。
    ///
    /// プラグインから検索を自動で投げることはしない。ユーザーが実際に開いたときのデータだけを記録する。
    /// </summary>
    public sealed unsafe class MarketBoardWatcher : IDisposable
    {
        private bool _pendingRead;
        private DateTime _readAfterUtc = DateTime.MinValue;
        private bool _subscribed;

        /// <summary>直近にパケットから読み取った出品（ListingId をキーに補完へ使う）。</summary>
        private readonly Dictionary<ulong, PacketListing> _packetListings = new();

        public int ObservedListingCount { get; private set; }
        public uint LastObservedItemId { get; private set; }

        /// <summary>診断用: 直近にパケットから読めた出品の一覧。</summary>
        public List<PacketListing> LastPacketListings { get; private set; } = new();

        /// <summary>出品を新たに観測したときに発火する。</summary>
        public event Action<uint>? ListingsObserved;

        public void Initialize()
        {
            try
            {
                Plugin.MarketBoard.OfferingsReceived += OnOfferingsReceived;
                Plugin.MarketBoard.HistoryReceived += OnHistoryReceived;
                _subscribed = true;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"マーケットボードの監視を開始できませんでした: {e.Message}");
            }
        }

        private void OnOfferingsReceived(Dalamud.Game.Network.Structures.IMarketBoardCurrentOfferings offerings)
        {
            // ゲーム内部の一覧に入らない情報（オーナーの ContentId や出品者名）が
            // パケット側には残っていることがあるので、先に控えておく。
            try
            {
                var packet = PacketListingProbe.Read(offerings);
                LastPacketListings = packet;

                foreach (var listing in packet)
                {
                    if (listing.ListingId != 0) _packetListings[listing.ListingId] = listing;

                    // 出品者名がそのまま入っていれば、対応表へ登録する。
                    if (listing.RetainerOwnerId != 0 && !string.IsNullOrWhiteSpace(listing.PlayerName))
                        Plugin.Identities.Record(
                            listing.RetainerOwnerId, listing.PlayerName, 0, Data.IdentitySource.MarketBoard);
                }

                // 古い情報が溜まり続けないように上限を設ける。
                if (_packetListings.Count > 4000) _packetListings.Clear();
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"出品パケットの読み取りに失敗しました: {e.Message}");
            }

            // パケット処理の直後はゲーム側の一覧がまだ更新されていないことがあるため、
            // 少し待ってから読む。
            _pendingRead = true;
            _readAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
        }

        /// <summary>
        /// マーケットの購入履歴を受け取る。
        /// 「誰が買ったか」は公開情報なので、出品者（オーナー）の推定材料として蓄積する。
        /// </summary>
        private void OnHistoryReceived(Dalamud.Game.Network.Structures.IMarketBoardHistory history)
        {
            if (!Plugin.Config.EnableResaleTracking) return;

            try
            {
                var world = LodestoneLink.GetCurrentWorld();
                var purchases = new List<MarketPurchase>();

                foreach (var listing in history.HistoryListings)
                {
                    if (listing == null) continue;

                    purchases.Add(new MarketPurchase
                    {
                        ItemId = history.ItemId,
                        Hq = listing.IsHq,
                        BuyerName = listing.BuyerName ?? string.Empty,
                        Quantity = (long)listing.Quantity,
                        UnitPrice = (long)listing.SalePrice,
                        UnixTime = new DateTimeOffset(listing.PurchaseTime.ToUniversalTime()).ToUnixTimeSeconds(),
                        OnMannequin = listing.OnMannequin,
                        WorldName = world,
                    });
                }

                var added = Plugin.Purchases.Add(purchases);
                if (added > 0) Plugin.Purchases.Save();
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"購入履歴の取り込みに失敗しました: {e.Message}");
            }
        }

        /// <summary>ゲーム内部の一覧に足りない情報を、パケット側の控えで補う。</summary>
        private void Enrich(ListingRecord record)
        {
            if (record.ListingId == 0) return;
            if (!_packetListings.TryGetValue(record.ListingId, out var packet)) return;

            if (record.OwnerContentId == 0 && packet.RetainerOwnerId != 0)
                record.OwnerContentId = packet.RetainerOwnerId;

            if (string.IsNullOrEmpty(record.RetainerName) && !string.IsNullOrEmpty(packet.RetainerName))
                record.RetainerName = packet.RetainerName;

            if (record.ArtisanContentId == 0 && packet.ArtisanId != 0)
                record.ArtisanContentId = packet.ArtisanId;
        }

        public void Tick()
        {
            if (!_pendingRead || DateTime.UtcNow < _readAfterUtc) return;
            _pendingRead = false;

            if (!Plugin.Config.EnableResaleTracking) return;

            try
            {
                ReadListings();
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"出品一覧の読み取りに失敗しました: {e.Message}");
            }
        }

        private void ReadListings()
        {
            var module = InfoModule.Instance();
            if (module == null) return;

            var proxy = (InfoProxyItemSearch*)module->GetInfoProxyById(InfoProxyId.ItemSearch);
            if (proxy == null) return;

            var count = (int)Math.Min(proxy->ListingCount, 100u);
            if (count <= 0) return;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var records = new List<ListingRecord>(count);

            for (var i = 0; i < count; i++)
            {
                ref var listing = ref proxy->Listings[i];
                if (listing.ItemId == 0 || listing.ListingId == 0) continue;

                var record = new ListingRecord
                {
                    ItemId = listing.ItemId,
                    Hq = listing.IsHqItem,
                    ListingId = listing.ListingId,
                    RetainerId = listing.RetainerId,
                    OwnerContentId = listing.ContentId,
                    ArtisanContentId = listing.ArtisanId,
                    RetainerName = listing.CharacterName.ToString(),
                    UnitPrice = listing.UnitPrice,
                    Quantity = listing.Quantity,
                    TownId = listing.TownId,
                    FirstSeenUnix = now,
                    LastSeenUnix = now,
                    Source = "game",
                };
                Enrich(record);
                records.Add(record);
            }

            if (records.Count == 0) return;

            Plugin.Listings.Observe(records);
            foreach (var record in records) Plugin.Retainers.Observe(record);

            // 出品を読むついでに、読み方が合っているかを確かめて覚える。
            // ゲームの更新で位置がずれても、ここで学び直せる。
            Plugin.Layout.Learn();
            ObservedListingCount += records.Count;
            LastObservedItemId = proxy->SearchItemId != 0 ? proxy->SearchItemId : records[0].ItemId;

            ListingsObserved?.Invoke(LastObservedItemId);
        }

        /// <summary>現在ゲームが保持している出品一覧（マーケットボードで最後に見た内容）。</summary>
        public List<ListingRecord> CurrentListings()
        {
            var result = new List<ListingRecord>();

            try
            {
                var module = InfoModule.Instance();
                if (module == null) return result;

                var proxy = (InfoProxyItemSearch*)module->GetInfoProxyById(InfoProxyId.ItemSearch);
                if (proxy == null) return result;

                var count = (int)Math.Min(proxy->ListingCount, 100u);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                for (var i = 0; i < count; i++)
                {
                    ref var listing = ref proxy->Listings[i];
                    if (listing.ItemId == 0) continue;

                    var record = new ListingRecord
                    {
                        ItemId = listing.ItemId,
                        Hq = listing.IsHqItem,
                        ListingId = listing.ListingId,
                        RetainerId = listing.RetainerId,
                        OwnerContentId = listing.ContentId,
                        ArtisanContentId = listing.ArtisanId,
                        RetainerName = listing.CharacterName.ToString(),
                        UnitPrice = listing.UnitPrice,
                        Quantity = listing.Quantity,
                        TownId = listing.TownId,
                        FirstSeenUnix = now,
                        LastSeenUnix = now,
                        Source = "game",
                    };
                    Enrich(record);
                    result.Add(record);
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"出品一覧の取得に失敗しました: {e.Message}");
            }

            return result.OrderBy(r => r.UnitPrice).ToList();
        }

        /// <summary>
        /// 診断用: いまゲームが保持している出品一覧の生の値をログへ出力する。
        /// 出品者を特定できない原因（オーナー ID が送られてきているか）を切り分けるために使う。
        /// </summary>
        public void DumpListings()
        {
            try
            {
                var module = InfoModule.Instance();
                if (module == null)
                {
                    Plugin.ChatGui.PrintError("[Market Stats] ゲーム内部の情報を取得できませんでした。");
                    return;
                }

                var proxy = (InfoProxyItemSearch*)module->GetInfoProxyById(InfoProxyId.ItemSearch);
                if (proxy == null)
                {
                    Plugin.ChatGui.PrintError("[Market Stats] 出品一覧の情報を取得できませんでした。");
                    return;
                }

                var count = (int)Math.Min(proxy->ListingCount, 100u);
                Plugin.PluginLog.Information(
                    $"===== 出品一覧のダンプ: SearchItemId={proxy->SearchItemId} ListingCount={proxy->ListingCount} =====");

                var withOwner = 0;
                var withRetainer = 0;
                var withArtisan = 0;
                var withName = 0;

                for (var i = 0; i < count; i++)
                {
                    ref var l = ref proxy->Listings[i];
                    var name = l.CharacterName.ToString();

                    if (l.ContentId != 0) withOwner++;
                    if (l.RetainerId != 0) withRetainer++;
                    if (l.ArtisanId != 0) withArtisan++;
                    if (!string.IsNullOrEmpty(name)) withName++;

                    Plugin.PluginLog.Information(
                        $"[{i:D2}] item={l.ItemId} listing=0x{l.ListingId:X} retainer=0x{l.RetainerId:X} " +
                        $"content=0x{l.ContentId:X} artisan=0x{l.ArtisanId:X} name='{name}' " +
                        $"price={l.UnitPrice} qty={l.Quantity} hq={l.IsHqItem} mannequin={l.IsMannequin} town={l.TownId}");
                }

                Plugin.PluginLog.Information("===== ダンプここまで =====");

                Plugin.ChatGui.Print(
                    $"[Market Stats] 出品 {count} 件をログへ出力しました。" +
                    $"オーナーID {withOwner} 件 / リテイナーID {withRetainer} 件 / 製作者ID {withArtisan} 件 / 名前 {withName} 件");

                if (count > 0 && withOwner == 0)
                    Plugin.ChatGui.Print(
                        "[Market Stats] オーナーIDが 1 件も入っていません。" +
                        "この状態では出品者のキャラクターを特定できません。");
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Error($"出品一覧のダンプに失敗しました: {e}");
                Plugin.ChatGui.PrintError($"[Market Stats] ダンプに失敗しました: {e.Message}");
            }
        }

        public void Dispose()
        {
            if (!_subscribed) return;
            try
            {
                Plugin.MarketBoard.OfferingsReceived -= OnOfferingsReceived;
                Plugin.MarketBoard.HistoryReceived -= OnHistoryReceived;
            }
            catch
            {
                // 破棄時の失敗は握りつぶす。
            }
        }
    }
}
