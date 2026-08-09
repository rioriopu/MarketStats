using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Component.GUI;
using MarketStats.Data;

namespace MarketStats.Game
{
    /// <summary>
    /// サーバーから届くリテイナー売却履歴 1 件分のレコード（20 件の配列で送られてくる）。
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 52)]
    internal unsafe struct RetainerHistoryEntry
    {
        [FieldOffset(0)] public uint ItemId;
        [FieldOffset(4)] public uint Price;
        [FieldOffset(8)] public uint UnixTimeSeconds;
        [FieldOffset(12)] public uint Quantity;
        [FieldOffset(16)] public byte IsHq;
        [FieldOffset(17)] public byte Unk17;
        [FieldOffset(18)] public byte IsMannequin;
        [FieldOffset(19)] public fixed byte BuyerNameRaw[32];

        public string BuyerName
        {
            get
            {
                fixed (byte* p = BuyerNameRaw)
                {
                    var len = 0;
                    while (len < 32 && p[len] != 0) len++;
                    return len == 0 ? string.Empty : Encoding.UTF8.GetString(p, len);
                }
            }
        }
    }

    /// <summary>
    /// リテイナーの売却履歴をゲームから取り込む。
    ///
    /// 取り込み経路は 2 つある。
    ///   1. 売却履歴パケットの処理関数をフックして構造体をそのまま読む（正確・既定）
    ///   2. 売却履歴ウィンドウが使う UI 配列（ItemDetail）を読む（フックが効かない時の保険）
    ///
    /// どちらの経路でも、取り込んだレコードはキューに積んで Framework 上で処理する。
    /// </summary>
    public sealed unsafe class RetainerHistoryCapture : IDisposable
    {
        /// <summary>売却履歴ウィンドウのアドオン名。</summary>
        public const string AddonName = "RetainerHistory";

        private const int MaxEntries = 20;

        // ProcessRetainerHistory 相当の関数。第 2 引数の +8 から 52 バイト × 20 件が並ぶ。
        private const string ProcessSignature = "40 53 56 57 41 57 48 83 EC 38 48 8B F1";

        private delegate nint ProcessRetainerHistoryDelegate(nint a1, nint data);

        private Hook<ProcessRetainerHistoryDelegate>? _hook;
        private readonly ConcurrentQueue<List<SaleRecord>> _pending = new();

        // アドオン経由の取り込みは、ウィンドウが開いた直後の数秒だけ間欠的に走らせる。
        private DateTime _addonScanUntilUtc = DateTime.MinValue;
        private DateTime _nextAddonScanUtc = DateTime.MinValue;
        private bool _addonListenerRegistered;

        /// <summary>フックの設置に成功しているか。</summary>
        public bool HookActive { get; private set; }

        /// <summary>フックの設置に失敗した理由（診断表示用）。</summary>
        public string HookStatus { get; private set; } = "未初期化";

        public int HookCaptureCount { get; private set; }
        public int AddonCaptureCount { get; private set; }
        public DateTime LastCaptureLocal { get; private set; } = DateTime.MinValue;

        /// <summary>売却履歴ウィンドウが開かれた時に発火する。</summary>
        public event Action? HistoryWindowOpened;

        public void Initialize()
        {
            TrySetupHook();
            RegisterAddonListener();
        }

        private void TrySetupHook()
        {
            if (!Plugin.Config.EnableHookCapture)
            {
                HookStatus = "設定で無効";
                return;
            }

            try
            {
                _hook = Plugin.GameInterop.HookFromSignature<ProcessRetainerHistoryDelegate>(
                    ProcessSignature, ProcessDetour);
                _hook.Enable();
                HookActive = true;
                HookStatus = "有効";
                Plugin.PluginLog.Information("売却履歴フックを設置しました。");
            }
            catch (Exception e)
            {
                HookActive = false;
                HookStatus = $"設置失敗: {e.Message}";
                Plugin.PluginLog.Warning(
                    $"売却履歴フックを設置できませんでした（履歴ウィンドウ経由の取り込みに切り替えます）: {e.Message}");
            }
        }

        private void RegisterAddonListener()
        {
            if (_addonListenerRegistered) return;
            try
            {
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, AddonName, OnAddonEvent);
                Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, AddonName, OnAddonEvent);
                _addonListenerRegistered = true;
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"売却履歴ウィンドウの監視を登録できませんでした: {e.Message}");
            }
        }

        private void OnAddonEvent(AddonEvent type, AddonArgs args)
        {
            HistoryWindowOpened?.Invoke();

            // フックが効いていれば UI 側から読む必要はない（誤検出を避けるため走らせない）。
            if (HookActive || !Plugin.Config.EnableAddonCapture) return;

            var now = DateTime.UtcNow;
            _nextAddonScanUtc = now;
            _addonScanUntilUtc = now.AddSeconds(3);
        }

        private nint ProcessDetour(nint a1, nint data)
        {
            try
            {
                if (data != nint.Zero)
                    CaptureFromPacket(data);
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Error($"売却履歴の取り込みで例外が発生しました: {e}");
            }

            return _hook!.Original(a1, data);
        }

        private void CaptureFromPacket(nint data)
        {
            var retainer = ResolveRetainerName();
            var owner = ResolveOwner();
            var records = new List<SaleRecord>();

            var rejected = 0;

            for (var i = 0; i < MaxEntries; i++)
            {
                var entry = *(RetainerHistoryEntry*)(data + 8 + sizeof(RetainerHistoryEntry) * i);
                if (entry.ItemId == 0) break;
                if (entry.UnixTimeSeconds == 0) continue;

                // ゲーム側の関数が差し替わっていた場合に備え、内容が売却履歴として
                // 妥当かどうかを確認してから記録する（偽のログを残さないため）。
                if (!IsPlausible(entry))
                {
                    rejected++;
                    continue;
                }

                records.Add(new SaleRecord
                {
                    ItemId = entry.ItemId,
                    Hq = entry.IsHq != 0,
                    Quantity = entry.Quantity,
                    TotalGil = entry.Price,
                    UnixTime = entry.UnixTimeSeconds,
                    BuyerName = entry.BuyerName.Trim(),
                    OnMannequin = entry.IsMannequin != 0,
                    RetainerName = retainer,
                    OwnerContentId = owner.ContentId,
                    OwnerName = owner.Name,
                    OwnerWorld = owner.World,
                });
            }

            if (rejected > 0)
                Plugin.PluginLog.Warning(
                    $"売却履歴として妥当でないレコードを {rejected} 件除外しました。" +
                    "ゲームのアップデートで取り込み方法が合わなくなっている可能性があります。");

            if (records.Count == 0) return;

            HookCaptureCount += records.Count;
            _pending.Enqueue(records);
        }

        /// <summary>読み取ったレコードが売却履歴として妥当かどうか。</summary>
        private static bool IsPlausible(RetainerHistoryEntry entry)
        {
            if (entry.Price == 0) return false;
            if (entry.Quantity is 0 or > 99999) return false;
            if (entry.IsHq > 1 || entry.IsMannequin > 1) return false;

            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (entry.UnixTimeSeconds < EarliestSaleUnix || entry.UnixTimeSeconds > now + 86400) return false;

            // 存在しない ItemId なら読み取り位置がずれている。
            return !Plugin.Items.IsBuilt || !Plugin.Items.GetName(entry.ItemId).StartsWith('#');
        }

        // FFXIV: 新生エオルゼアのサービス開始日。これより古い売却時刻はあり得ない。
        private static readonly long EarliestSaleUnix =
            new DateTimeOffset(2013, 8, 24, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        /// <summary>Framework 上から定期的に呼ぶ。アドオン経由の取り込みを進める。</summary>
        public void Tick()
        {
            if (HookActive || !Plugin.Config.EnableAddonCapture) return;

            var now = DateTime.UtcNow;
            if (now < _nextAddonScanUtc || now > _addonScanUntilUtc) return;
            _nextAddonScanUtc = now.AddMilliseconds(300);

            try
            {
                CaptureFromAddon();
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"売却履歴ウィンドウからの取り込みに失敗しました: {e.Message}");
                _addonScanUntilUtc = DateTime.MinValue;
            }
        }

        /// <summary>
        /// 売却履歴ウィンドウが使う UI 配列（ItemDetail）から履歴を読む。
        /// パケットの構造体と違い型情報が無いため、妥当性チェックを厳しめにしている。
        /// </summary>
        private void CaptureFromAddon()
        {
            var addon = Plugin.GameGui.GetAddonByName(AddonName, 1);
            if (addon.IsNull || !addon.IsVisible) return;

            var stage = AtkStage.Instance();
            if (stage == null) return;

            var numberArray = stage->GetNumberArrayData(NumberArrayType.ItemDetail);
            var stringArray = stage->GetStringArrayData(StringArrayType.ItemDetail);
            if (numberArray == null || stringArray == null) return;

            var numbers = new int[numberArray->Size];
            for (var i = 0; i < numbers.Length; i++) numbers[i] = numberArray->IntArray[i];

            var strings = new string[stringArray->Size];
            for (var i = 0; i < strings.Length; i++)
                strings[i] = ReadCString(stringArray->StringArray[i]);

            var parsed = RetainerHistoryArrayParser.Parse(numbers, strings);
            if (parsed.Count == 0) return;

            var retainer = ResolveRetainerName();
            var owner = ResolveOwner();
            foreach (var r in parsed)
            {
                r.RetainerName = retainer;
                r.OwnerContentId = owner.ContentId;
                r.OwnerName = owner.Name;
                r.OwnerWorld = owner.World;
            }

            AddonCaptureCount += parsed.Count;
            _pending.Enqueue(parsed);
        }

        private static string ReadCString(byte* ptr)
        {
            if (ptr == null) return string.Empty;
            var len = 0;
            while (len < 256 && ptr[len] != 0) len++;
            return len == 0 ? string.Empty : Encoding.UTF8.GetString(ptr, len);
        }

        /// <summary>取り込み済みのレコードを取り出す。</summary>
        public List<List<SaleRecord>> Drain()
        {
            var batches = new List<List<SaleRecord>>();
            while (_pending.TryDequeue(out var batch))
                batches.Add(batch);
            if (batches.Count > 0) LastCaptureLocal = DateTime.Now;
            return batches;
        }

        private static string ResolveRetainerName()
        {
            try
            {
                var manager = RetainerManager.Instance();
                if (manager != null)
                {
                    var active = manager->GetActiveRetainer();
                    if (active != null && active->RetainerId != 0)
                    {
                        var name = active->NameString;
                        if (!string.IsNullOrWhiteSpace(name)) return name;
                    }
                }
            }
            catch
            {
                // ClientStructs 側の変更で読めなくなっても取り込み自体は続行する。
            }

            // アクティブなリテイナーが取れない場合は、話しかけている相手から推定する。
            try
            {
                var obj = Plugin.ObjectTable.FirstOrDefault(
                    o => o.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Retainer);
                if (obj != null) return obj.Name.TextValue;
            }
            catch
            {
                // 同上
            }

            return string.Empty;
        }

        private static (ulong ContentId, string Name, string World) ResolveOwner()
        {
            try
            {
                if (Plugin.PlayerState.IsLoaded)
                {
                    var world = Plugin.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText() ?? string.Empty;
                    return (Plugin.PlayerState.ContentId, Plugin.PlayerState.CharacterName, world);
                }
            }
            catch
            {
                // ログイン直後などで取れないことがある。
            }

            return (0, string.Empty, string.Empty);
        }

        /// <summary>診断用: 現在の ItemDetail 配列の中身をログへ出力する。</summary>
        public void DumpArrays()
        {
            try
            {
                var stage = AtkStage.Instance();
                if (stage == null)
                {
                    Plugin.PluginLog.Information("AtkStage を取得できませんでした。");
                    return;
                }

                var numberArray = stage->GetNumberArrayData(NumberArrayType.ItemDetail);
                var stringArray = stage->GetStringArrayData(StringArrayType.ItemDetail);

                if (numberArray != null)
                {
                    var sb = new StringBuilder("ItemDetail(number): ");
                    for (var i = 0; i < numberArray->Size; i++)
                        sb.Append(i).Append('=').Append(numberArray->IntArray[i]).Append(' ');
                    Plugin.PluginLog.Information(sb.ToString());
                }

                if (stringArray != null)
                {
                    var sb = new StringBuilder("ItemDetail(string): ");
                    for (var i = 0; i < stringArray->Size; i++)
                    {
                        var s = ReadCString(stringArray->StringArray[i]);
                        if (string.IsNullOrEmpty(s)) continue;
                        sb.Append(i).Append("=\"").Append(s).Append("\" ");
                    }
                    Plugin.PluginLog.Information(sb.ToString());
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"配列ダンプに失敗しました: {e.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                _hook?.Disable();
                _hook?.Dispose();
            }
            catch
            {
                // 破棄時の失敗は握りつぶす。
            }

            if (_addonListenerRegistered)
            {
                try
                {
                    Plugin.AddonLifecycle.UnregisterListener(OnAddonEvent);
                }
                catch
                {
                    // 同上
                }
            }
        }
    }
}
