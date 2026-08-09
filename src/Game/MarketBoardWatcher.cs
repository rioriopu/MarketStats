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

        public int ObservedListingCount { get; private set; }
        public uint LastObservedItemId { get; private set; }

        /// <summary>出品を新たに観測したときに発火する。</summary>
        public event Action<uint>? ListingsObserved;

        public void Initialize()
        {
            try
            {
                Plugin.MarketBoard.OfferingsReceived += OnOfferingsReceived;
                _subscribed = true;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"マーケットボードの監視を開始できませんでした: {e.Message}");
            }
        }

        private void OnOfferingsReceived(Dalamud.Game.Network.Structures.IMarketBoardCurrentOfferings offerings)
        {
            // パケット処理の直後はゲーム側の一覧がまだ更新されていないことがあるため、
            // 少し待ってから読む。
            _pendingRead = true;
            _readAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
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

                records.Add(new ListingRecord
                {
                    ItemId = listing.ItemId,
                    Hq = listing.IsHqItem,
                    ListingId = listing.ListingId,
                    RetainerId = listing.RetainerId,
                    OwnerContentId = listing.ContentId,
                    RetainerName = listing.CharacterName.ToString(),
                    UnitPrice = listing.UnitPrice,
                    Quantity = listing.Quantity,
                    TownId = listing.TownId,
                    FirstSeenUnix = now,
                    LastSeenUnix = now,
                    Source = "game",
                });
            }

            if (records.Count == 0) return;

            Plugin.Listings.Observe(records);
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

                    result.Add(new ListingRecord
                    {
                        ItemId = listing.ItemId,
                        Hq = listing.IsHqItem,
                        ListingId = listing.ListingId,
                        RetainerId = listing.RetainerId,
                        OwnerContentId = listing.ContentId,
                        RetainerName = listing.CharacterName.ToString(),
                        UnitPrice = listing.UnitPrice,
                        Quantity = listing.Quantity,
                        TownId = listing.TownId,
                        FirstSeenUnix = now,
                        LastSeenUnix = now,
                        Source = "game",
                    });
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"出品一覧の取得に失敗しました: {e.Message}");
            }

            return result.OrderBy(r => r.UnitPrice).ToList();
        }

        public void Dispose()
        {
            if (!_subscribed) return;
            try
            {
                Plugin.MarketBoard.OfferingsReceived -= OnOfferingsReceived;
            }
            catch
            {
                // 破棄時の失敗は握りつぶす。
            }
        }
    }
}
