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
        private static readonly (InfoProxyId Id, IdentitySource Source)[] Proxies =
        {
            (InfoProxyId.PartyMember, IdentitySource.Party),
            (InfoProxyId.FriendList, IdentitySource.Friend),
            (InfoProxyId.FreeCompanyMember, IdentitySource.FreeCompany),
            (InfoProxyId.LinkshellMember, IdentitySource.Linkshell),
            (InfoProxyId.CrossWorldLinkshellMember, IdentitySource.Linkshell),
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
