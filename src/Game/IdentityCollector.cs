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
        // ゲームが保持している「名前と ContentId が両方分かっているリスト」を読む。
        //
        // ここに並べてよいのは InfoProxyCommonList として扱える種類だけ。
        // 型の違うもの（手紙・サークル・クロスワールドパーティ・各種招待など）を混ぜると、
        // 別の意味のフィールドをポインタとして読んでしまい、アクセス違反でゲームごと落ちる。
        private static readonly (InfoProxyId Id, IdentitySource Source)[] Proxies =
        {
            (InfoProxyId.PartyMember, IdentitySource.Party),
            (InfoProxyId.FriendList, IdentitySource.Friend),
            (InfoProxyId.FreeCompanyMember, IdentitySource.FreeCompany),
            (InfoProxyId.LinkshellMember, IdentitySource.Linkshell),
            (InfoProxyId.CrossWorldLinkshellMember, IdentitySource.Linkshell),
            (InfoProxyId.ContentMember, IdentitySource.Party),
            (InfoProxyId.NoviceNetworkMember, IdentitySource.Linkshell),
            (InfoProxyId.Blacklist, IdentitySource.ObjectTable),
        };

        /// <summary>1 リストあたりの妥当な最大人数。これを超える値は壊れているとみなす。</summary>
        private const int MaxEntries = 512;

        /// <summary>
        /// リストの中身を安全に読めるか確かめる。
        /// 件数とポインタの両方を検証してからでないと触らない。
        /// </summary>
        private static bool TryGetEntries(
            InfoProxyCommonList* proxy, out InfoProxyCommonList.CharacterData* data, out int count)
        {
            data = null;
            count = 0;

            if (proxy == null) return false;
            if (!SafeMemory.IsFullyReadable((nint)proxy, sizeof(InfoProxyCommonList))) return false;

            var entryCount = (int)proxy->EntryCount;
            if (entryCount is <= 0 or > MaxEntries) return false;

            var charData = proxy->CharData;
            if (charData == null) return false;

            var size = sizeof(InfoProxyCommonList.CharacterData) * entryCount;
            if (!SafeMemory.IsFullyReadable((nint)charData, size)) return false;

            data = charData;
            count = entryCount;
            return true;
        }

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

                    // アカウントの識別子も一緒に控えておく。
                    // 同じ値を持つキャラクターは同一アカウント＝同じ人の別キャラと分かる。
                    Plugin.Identities.Record(contentId, name, worldId, source, chara->AccountId);
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

                    // 中身が読める状態か確かめてから問い合わせる。
                    if (!TryGetEntries(proxy, out var entries, out var count)) continue;

                    for (var i = 0; i < count; i++)
                    {
                        ref var entry = ref entries[i];
                        if (entry.ContentId != contentId) continue;

                        var found = entry.NameString;
                        if (string.IsNullOrWhiteSpace(found)) continue;

                        name = found;
                        worldId = entry.HomeWorld;
                        Plugin.Identities.Record(contentId, name, worldId, source);
                        return true;
                    }
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
                    if (!TryGetEntries(proxy, out var entries, out var count)) continue;

                    for (var i = 0; i < count; i++)
                    {
                        ref var data = ref entries[i];
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
