using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using MarketStats.Data;

namespace MarketStats.Game
{
    /// <summary>
    /// 自分のリテイナーの出品一覧を監視する。
    ///
    /// 出品リストを開くたびにスナップショットを取り、前回から消えた出品を
    /// 「売れた可能性のある取引」として記録する。購入者名は分からないが、
    /// 売却履歴が 20 件で溢れて取りこぼした分でも「売れた事実と個数」は残せる。
    /// （自分で出品を取り下げた場合も消えるため、確定扱いにはしない）
    /// </summary>
    public sealed unsafe class RetainerSellListWatcher : IDisposable
    {
        private const string AddonName = "RetainerSellList";

        private bool _registered;
        private bool _pendingRead;
        private DateTime _readAfterUtc = DateTime.MinValue;

        public int DetectedCount { get; private set; }
        public DateTime LastReadLocal { get; private set; } = DateTime.MinValue;

        public void Initialize()
        {
            try
            {
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonEvent);
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnAddonEvent);
                _registered = true;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"出品リストの監視を登録できませんでした: {e.Message}");
            }
        }

        private void OnAddonEvent(AddonEvent type, AddonArgs args)
        {
            if (!Plugin.Config.EnableSellListDiff) return;
            _pendingRead = true;
            _readAfterUtc = DateTime.UtcNow.AddMilliseconds(400);
        }

        public void Tick()
        {
            if (!_pendingRead || DateTime.UtcNow < _readAfterUtc) return;
            _pendingRead = false;

            if (!Plugin.Config.EnableSellListDiff) return;

            try
            {
                ReadRetainerListings();
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"出品リストの読み取りに失敗しました: {e.Message}");
            }
        }

        private void ReadRetainerListings()
        {
            var (retainerName, retainerId) = ResolveActiveRetainer();
            if (string.IsNullOrEmpty(retainerName) || retainerId == 0) return;

            var module = InfoModule.Instance();
            if (module == null) return;

            var proxy = (InfoProxyItemSearch*)module->GetInfoProxyById(InfoProxyId.ItemSearch);
            if (proxy == null) return;

            var count = (int)Math.Min(proxy->RetainerListingCount, 20u);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var listings = new List<ListingRecord>(count);
            var mismatched = false;

            for (var i = 0; i < count; i++)
            {
                ref var listing = ref proxy->RetainerListings[i];
                if (listing.ItemId == 0) continue;

                // 別のリテイナーのデータが残っている状態で読んでしまうと、
                // 「出品が全部消えた」と誤検出してしまうため取りやめる。
                if (listing.RetainerId != 0 && listing.RetainerId != retainerId)
                {
                    mismatched = true;
                    break;
                }

                listings.Add(new ListingRecord
                {
                    ItemId = listing.ItemId,
                    Hq = listing.IsHqItem,
                    ListingId = listing.ListingId,
                    RetainerId = listing.RetainerId,
                    OwnerContentId = listing.ContentId,
                    RetainerName = retainerName,
                    UnitPrice = listing.UnitPrice,
                    Quantity = listing.Quantity,
                    TownId = listing.TownId,
                    FirstSeenUnix = now,
                    LastSeenUnix = now,
                    Source = "retainer",
                });
            }

            if (mismatched)
            {
                Plugin.PluginLog.Debug("出品リストが別のリテイナーの内容だったため、今回の読み取りを見送りました。");
                return;
            }

            // 0 件のときは「まだ読み込めていない」場合と区別できないため、誤検出を避けて何もしない。
            if (listings.Count == 0) return;

            var detected = Plugin.Pending.UpdateRetainerSnapshot(retainerName, listings);
            if (detected > 0)
            {
                DetectedCount += detected;
                Plugin.PluginLog.Information(
                    $"{retainerName}: 出品 {detected} 件が一覧から消えました（売却または取り下げ）。");
                Plugin.Pending.Reconcile(Plugin.Store.Snapshot());
                Plugin.Pending.Save();
            }

            LastReadLocal = DateTime.Now;
        }

        private static (string Name, ulong Id) ResolveActiveRetainer()
        {
            try
            {
                var manager = RetainerManager.Instance();
                if (manager == null) return (string.Empty, 0);
                var active = manager->GetActiveRetainer();
                if (active == null || active->RetainerId == 0) return (string.Empty, 0);
                return (active->NameString, active->RetainerId);
            }
            catch
            {
                return (string.Empty, 0);
            }
        }

        public void Dispose()
        {
            if (!_registered) return;
            try
            {
                Plugin.AddonLifecycle.UnregisterListener(OnAddonEvent);
            }
            catch
            {
                // 破棄時の失敗は握りつぶす。
            }
        }
    }
}
