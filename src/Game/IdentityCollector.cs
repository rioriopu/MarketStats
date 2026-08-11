using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using MarketStats.Data;
using CSCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;

namespace MarketStats.Game
{
    /// <summary>
    /// ContentId ↔ キャラクター名の対応表を集める。
    ///
    /// マーケットの出品データにはオーナーの ContentId しか無いため、名前を出すには
    /// 別経路で対応表を作っておく必要がある。ここでは
    ///   ・周囲に見えているプレイヤー（Character.ContentId）
    ///   ・フレンド / FC / リンクシェル / パーティのメンバーリスト
    /// を定期的に読み取って蓄積する。すべてローカル保存のみで外部送信はしない。
    /// </summary>
    public sealed unsafe class IdentityCollector
    {
        // ゲームが保持している「名前と ContentId が両方分かっているリスト」を片端から読む。
        // どれか 1 つにでも載っていれば、その相手の名前を出せるようになる。
        private static readonly (InfoProxyId Id, IdentitySource Source)[] Proxies =
        {
            (InfoProxyId.PartyMember, IdentitySource.Party),
            (InfoProxyId.CrossRealmParty, IdentitySource.Party),
            (InfoProxyId.PartyInvite, IdentitySource.Party),
            (InfoProxyId.FriendList, IdentitySource.Friend),
            (InfoProxyId.FreeCompanyMember, IdentitySource.FreeCompany),
            (InfoProxyId.FreeCompanyInvite, IdentitySource.FreeCompany),
            (InfoProxyId.LinkshellMember, IdentitySource.Linkshell),
            (InfoProxyId.CrossWorldLinkshellMember, IdentitySource.Linkshell),
            (InfoProxyId.ContentMember, IdentitySource.Party),
            (InfoProxyId.NoviceNetworkMember, IdentitySource.Linkshell),
            (InfoProxyId.Blacklist, IdentitySource.ObjectTable),
            (InfoProxyId.Letter, IdentitySource.Friend),
            (InfoProxyId.CircleList, IdentitySource.Linkshell),
            (InfoProxyId.Circle, IdentitySource.Linkshell),
        };

        private DateTime _nextObjectScanUtc = DateTime.MinValue;
        private DateTime _nextProxyScanUtc = DateTime.MinValue;

        public int LastObjectScanCount { get; private set; }

        public void Tick()
        {
            if (!Plugin.Config.EnableIdentityCollection) return;

            var now = DateTime.UtcNow;

            if (now >= _nextObjectScanUtc)
            {
                _nextObjectScanUtc = now.AddSeconds(5);
                TryScanObjectTable();
            }

            if (now >= _nextProxyScanUtc)
            {
                _nextProxyScanUtc = now.AddSeconds(60);
                TryScanInfoProxies();
            }
        }

        private void TryScanObjectTable()
        {
            try
            {
                var found = 0;
                foreach (var obj in Plugin.ObjectTable)
                {
                    if (obj is not IPlayerCharacter player) continue;

                    var chara = (CSCharacter*)obj.Address;
                    if (chara == null) continue;

                    var contentId = chara->ContentId;
                    if (contentId == 0) continue;

                    var name = obj.Name.TextValue;
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    var worldId = (ushort)player.HomeWorld.RowId;

                    var source = Plugin.PlayerState.IsLoaded && contentId == Plugin.PlayerState.ContentId
                        ? IdentitySource.Self
                        : IdentitySource.ObjectTable;

                    Plugin.Identities.Record(contentId, name, worldId, source);
                    found++;
                }

                LastObjectScanCount = found;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"周囲のプレイヤーの読み取りに失敗しました: {e.Message}");
            }
        }

        /// <summary>
        /// ContentId を指定して、ゲームが持っている各リストから直接その人物を引く。
        /// リストに載っている相手なら、対応表を待たずにその場で名前が分かる。
        /// </summary>
        public static bool TryLookupByContentId(ulong contentId, out string name, out ushort worldId)
        {
            name = string.Empty;
            worldId = 0;
            if (contentId == 0) return false;

            try
            {
                var module = InfoModule.Instance();
                if (module == null) return false;

                foreach (var (id, source) in Proxies)
                {
                    var proxy = (InfoProxyCommonList*)module->GetInfoProxyById(id);
                    if (proxy == null) continue;

                    var entry = proxy->GetEntryByContentId(contentId);
                    if (entry == null) continue;

                    var found = entry->NameString;
                    if (string.IsNullOrWhiteSpace(found)) continue;

                    name = found;
                    worldId = entry->HomeWorld;
                    Plugin.Identities.Record(contentId, name, worldId, source);
                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Debug($"識別子からの照会に失敗しました: {e.Message}");
            }

            return false;
        }

        private void TryScanInfoProxies()
        {
            try
            {
                var module = InfoModule.Instance();
                if (module == null) return;

                foreach (var (id, source) in Proxies)
                {
                    var proxy = (InfoProxyCommonList*)module->GetInfoProxyById(id);
                    if (proxy == null) continue;

                    foreach (ref readonly var data in proxy->CharDataSpan)
                    {
                        if (data.ContentId == 0) continue;
                        var name = data.NameString;
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        Plugin.Identities.Record(data.ContentId, name, data.HomeWorld, source);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"メンバーリストの読み取りに失敗しました: {e.Message}");
            }
        }
    }
}
