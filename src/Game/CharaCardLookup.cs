using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketStats.Data;

namespace MarketStats.Game
{
    /// <summary>
    /// 冒険者名刺を使って ContentId からキャラクター名を調べる。
    ///
    /// マーケットの出品には出品者の ContentId しか入っていないが、
    /// 冒険者名刺はサーバーに ContentId で問い合わせできるため、名前とワールドが分かる。
    ///
    /// ただしこれは通信を伴うゲーム内機能そのものなので、
    /// 一覧をまとめて自動照会するようなことはしない。
    /// ユーザーが 1 件ずつ明示的に指示したときだけ実行する（フレンドリストから名刺を開くのと同じ 1 操作）。
    /// </summary>
    public sealed unsafe class CharaCardLookup
    {
        private const string AddonName = "CharaCard";
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(6);

        private ulong _pendingContentId;
        private DateTime _requestedAtUtc = DateTime.MinValue;

        /// <summary>照会中かどうか。</summary>
        public bool IsBusy => _pendingContentId != 0;

        public string LastResult { get; private set; } = string.Empty;

        /// <summary>指定した ContentId の冒険者名刺を開いて名前を読み取る。</summary>
        public bool Request(ulong contentId)
        {
            if (contentId == 0 || IsBusy) return false;

            try
            {
                var agent = GetAgent();
                if (agent == null)
                {
                    LastResult = "名刺の機能を利用できませんでした。";
                    return false;
                }

                agent->OpenCharaCard(contentId);
                _pendingContentId = contentId;
                _requestedAtUtc = DateTime.UtcNow;
                LastResult = "照会中…";
                return true;
            }
            catch (Exception e)
            {
                LastResult = $"照会に失敗しました: {e.Message}";
                Plugin.PluginLog.Warning($"冒険者名刺の照会に失敗しました: {e.Message}");
                return false;
            }
        }

        public void Tick()
        {
            if (_pendingContentId == 0) return;

            if (DateTime.UtcNow - _requestedAtUtc > Timeout)
            {
                LastResult = "応答がありませんでした。";
                _pendingContentId = 0;
                return;
            }

            try
            {
                var agent = GetAgent();
                var data = agent == null ? null : agent->Data;
                if (data == null || data->ContentId != _pendingContentId) return;

                var name = data->Name.ToString();
                if (string.IsNullOrWhiteSpace(name)) return;

                Plugin.Identities.Record(_pendingContentId, name, data->WorldId, IdentitySource.CharaCard);
                Plugin.Identities.Save();

                LastResult = $"{name} と判明しました。";
                Plugin.PluginLog.Information($"冒険者名刺から出品者を特定しました: {name}");

                _pendingContentId = 0;

                if (Plugin.Config.CloseCharaCardAfterLookup)
                    CloseAddon();
            }
            catch (Exception e)
            {
                LastResult = $"読み取りに失敗しました: {e.Message}";
                _pendingContentId = 0;
            }
        }

        private static AgentCharaCard* GetAgent()
        {
            var module = AgentModule.Instance();
            if (module == null) return null;
            return (AgentCharaCard*)module->GetAgentByInternalId(AgentId.CharaCard);
        }

        private static void CloseAddon()
        {
            try
            {
                var ptr = Plugin.GameGui.GetAddonByName(AddonName, 1);
                if (ptr.IsNull) return;
                var addon = (AtkUnitBase*)ptr.Address;
                if (addon != null) addon->Close(true);
            }
            catch
            {
                // 閉じられなくても実害はない。
            }
        }
    }
}
