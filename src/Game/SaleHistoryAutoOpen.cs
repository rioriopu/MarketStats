using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MarketStats.Game
{
    /// <summary>
    /// リテイナーのメニューから「売却履歴」を自動で開いて取り込み、すぐ閉じる。
    ///
    /// ゲーム内の履歴はリテイナーごと最新 20 件しか残らないため、
    /// リテイナーに用があるたびに履歴を覗いておけば取りこぼしがほぼ無くなる。
    /// AutoRetainer を使っている場合は、その巡回に相乗りして各リテイナーで自動取得する。
    /// </summary>
    public sealed class SaleHistoryAutoOpen : IDisposable
    {
        private const string SelectStringAddon = "SelectString";
        private const string HistoryAddon = "RetainerHistory";

        // 日本語 / 英語クライアントの「売却履歴」項目。部分一致で探す。
        private static readonly string[] EntryNeedles = { "売却履歴", "Sale History", "販売履歴" };

        // SelectString の AtkValues 配置: [3] = 項目数, [7..] = 項目文字列
        private const int EntryCountIndex = 3;
        private const int EntriesStartIndex = 7;

        // AutoRetainer の IPC チャンネル
        private const string ChannelAdditionalTask = "AutoRetainer.OnRetainerAdditionalTask";
        private const string ChannelReadyForPostprocess = "AutoRetainer.OnRetainerReadyForPostprocess";
        private const string ChannelRequestPostprocess = "AutoRetainer.RequestPostprocess";
        private const string ChannelFinishPostprocess = "AutoRetainer.FinishPostprocessRequest";

        private static readonly TimeSpan HistoryWaitTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan MenuWaitTimeout = TimeSpan.FromSeconds(5);

        private readonly string _pluginName;
        private Action<string>? _additionalTaskHandler;
        private Action<string, string>? _readyHandler;
        private bool _ipcSubscribed;
        private bool _addonRegistered;

        private TaskCompletionSource<bool>? _historyTcs;
        private int _busy;
        private DateTime _suppressManualUntilUtc = DateTime.MinValue;

        public int AutoOpenCount { get; private set; }
        public string LastResult { get; private set; } = "未実行";

        public SaleHistoryAutoOpen()
        {
            _pluginName = Plugin.PluginInterface.InternalName;
        }

        public void Initialize()
        {
            TrySubscribeAutoRetainer();

            try
            {
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, SelectStringAddon, OnSelectStringSetup);
                _addonRegistered = true;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"リテイナーメニューの監視を登録できませんでした: {e.Message}");
            }
        }

        /// <summary>売却履歴が取り込まれたときに呼ぶ（待機解除用）。</summary>
        public void NotifyHistoryCaptured() => _historyTcs?.TrySetResult(true);

        // ---- AutoRetainer 連携 ----

        private void TrySubscribeAutoRetainer()
        {
            try
            {
                _additionalTaskHandler = OnRetainerAdditionalTask;
                _readyHandler = OnRetainerReadyForPostprocess;

                Plugin.PluginInterface.GetIpcSubscriber<string, object>(ChannelAdditionalTask)
                    .Subscribe(_additionalTaskHandler);
                Plugin.PluginInterface.GetIpcSubscriber<string, string, object>(ChannelReadyForPostprocess)
                    .Subscribe(_readyHandler);

                _ipcSubscribed = true;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Debug($"AutoRetainer の IPC に接続できませんでした（未導入なら正常）: {e.Message}");
            }
        }

        private void OnRetainerAdditionalTask(string retainerName)
        {
            if (!Plugin.Config.AutoOpenHistoryWithAutoRetainer) return;

            try
            {
                Plugin.PluginInterface.GetIpcSubscriber<string, object>(ChannelRequestPostprocess)
                    .InvokeAction(_pluginName);
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"AutoRetainer への処理要求に失敗しました: {e.Message}");
            }
        }

        private void OnRetainerReadyForPostprocess(string pluginName, string retainerName)
        {
            if (pluginName != _pluginName) return;

            if (!Plugin.Config.AutoOpenHistoryWithAutoRetainer)
            {
                FinishAutoRetainer();
                return;
            }

            if (Interlocked.Exchange(ref _busy, 1) == 1)
            {
                FinishAutoRetainer();
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunFlowAsync(retainerName).ConfigureAwait(false);
                }
                finally
                {
                    FinishAutoRetainer();
                    Interlocked.Exchange(ref _busy, 0);
                }
            });
        }

        private void FinishAutoRetainer()
        {
            try
            {
                Plugin.PluginInterface.GetIpcSubscriber<object>(ChannelFinishPostprocess).InvokeAction();
            }
            catch
            {
                // AutoRetainer が居ない場合は何もしない。
            }
        }

        // ---- 手動でリテイナーに話しかけた場合 ----

        private void OnSelectStringSetup(AddonEvent type, AddonArgs args)
        {
            if (!Plugin.Config.AutoOpenHistoryOnRetainerMenu) return;
            if (DateTime.UtcNow < _suppressManualUntilUtc) return;
            if (_busy == 1) return;

            // AutoRetainer 側の巡回に割り込まないよう、少し待ってから
            // まだメニューが出ていれば実行する。
            _ = Task.Run(async () =>
            {
                await Task.Delay(700).ConfigureAwait(false);

                if (Interlocked.Exchange(ref _busy, 1) == 1) return;
                try
                {
                    var isRetainerMenu = await Plugin.Framework
                        .RunOnFrameworkThread(() => FindHistoryEntryIndex() >= 0)
                        .ConfigureAwait(false);
                    if (!isRetainerMenu) return;

                    await RunFlowAsync(null).ConfigureAwait(false);

                    // 連続発火を防ぐ
                    _suppressManualUntilUtc = DateTime.UtcNow.AddSeconds(5);
                }
                finally
                {
                    Interlocked.Exchange(ref _busy, 0);
                }
            });
        }

        // ---- 共通フロー ----

        private async Task RunFlowAsync(string? retainerName)
        {
            var label = retainerName ?? "リテイナー";

            try
            {
                _historyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                if (!await WaitForMenuAsync().ConfigureAwait(false))
                {
                    LastResult = $"{label}: メニューが見つかりませんでした";
                    return;
                }

                var triggered = await Plugin.Framework
                    .RunOnFrameworkThread(TrySelectHistoryEntry)
                    .ConfigureAwait(false);

                if (!triggered)
                {
                    LastResult = $"{label}: 「売却履歴」項目が見つかりませんでした";
                    return;
                }

                var completed = await Task.WhenAny(_historyTcs.Task, Task.Delay(HistoryWaitTimeout))
                    .ConfigureAwait(false);

                await Plugin.Framework.RunOnFrameworkThread(CloseHistoryAddon).ConfigureAwait(false);

                AutoOpenCount++;
                LastResult = completed == _historyTcs.Task
                    ? $"{label}: 取り込み成功 ({DateTime.Now:HH:mm:ss})"
                    : $"{label}: 応答待ちがタイムアウトしました";
            }
            catch (Exception e)
            {
                LastResult = $"{label}: 失敗 ({e.Message})";
                Plugin.PluginLog.Warning($"売却履歴の自動取得に失敗しました: {e.Message}");
            }
            finally
            {
                _historyTcs = null;
            }
        }

        private static async Task<bool> WaitForMenuAsync()
        {
            var deadline = DateTime.UtcNow + MenuWaitTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var ready = await Plugin.Framework
                    .RunOnFrameworkThread(() => FindHistoryEntryIndex() >= 0)
                    .ConfigureAwait(false);
                if (ready) return true;
                await Task.Delay(200).ConfigureAwait(false);
            }
            return false;
        }

        private static unsafe bool TrySelectHistoryEntry()
        {
            var index = FindHistoryEntryIndex();
            if (index < 0) return false;

            var addon = GetSelectString();
            if (addon == null) return false;

            var values = stackalloc AtkValue[1];
            values[0] = new AtkValue { Type = AtkValueType.Int, Int = index };
            addon->FireCallback(1, values, true);
            return true;
        }

        private static unsafe AtkUnitBase* GetSelectString()
        {
            var ptr = Plugin.GameGui.GetAddonByName(SelectStringAddon, 1);
            if (ptr.IsNull || !ptr.IsVisible) return null;
            return (AtkUnitBase*)ptr.Address;
        }

        /// <summary>リテイナーメニューの中から「売却履歴」項目の番号を探す。無ければ -1。</summary>
        private static unsafe int FindHistoryEntryIndex()
        {
            var addon = GetSelectString();
            if (addon == null) return -1;
            if (addon->AtkValuesCount <= EntryCountIndex) return -1;

            var count = addon->AtkValues[EntryCountIndex].Int;
            for (var i = 0; i < count; i++)
            {
                var valueIndex = EntriesStartIndex + i;
                if (valueIndex >= addon->AtkValuesCount) break;

                var value = addon->AtkValues[valueIndex];
                var ptr = (byte*)value.String.Value;
                if (ptr == null) continue;

                var text = Marshal.PtrToStringUTF8((nint)ptr);
                if (string.IsNullOrEmpty(text)) continue;

                foreach (var needle in EntryNeedles)
                {
                    if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }

            return -1;
        }

        private static unsafe void CloseHistoryAddon()
        {
            var ptr = Plugin.GameGui.GetAddonByName(HistoryAddon, 1);
            if (ptr.IsNull) return;
            var addon = (AtkUnitBase*)ptr.Address;
            if (addon == null) return;
            addon->Close(true);
        }

        public void Dispose()
        {
            if (_addonRegistered)
            {
                try { Plugin.AddonLifecycle.UnregisterListener(OnSelectStringSetup); }
                catch { /* 破棄時の失敗は無視 */ }
            }

            if (!_ipcSubscribed) return;

            try
            {
                if (_additionalTaskHandler != null)
                    Plugin.PluginInterface.GetIpcSubscriber<string, object>(ChannelAdditionalTask)
                        .Unsubscribe(_additionalTaskHandler);
                if (_readyHandler != null)
                    Plugin.PluginInterface.GetIpcSubscriber<string, string, object>(ChannelReadyForPostprocess)
                        .Unsubscribe(_readyHandler);
            }
            catch
            {
                // 破棄時の失敗は握りつぶす。
            }
        }
    }
}
