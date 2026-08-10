using System.Linq;
using System.Reflection;
using Dalamud.Game.Network.Structures;

namespace MarketStats.Game
{
    /// <summary>Dalamud がパケットから読み取った出品 1 件（診断・補完用）。</summary>
    public sealed class PacketListing
    {
        public uint ItemId { get; set; }
        public ulong ListingId { get; set; }
        public ulong RetainerId { get; set; }
        public string RetainerName { get; set; } = string.Empty;
        public ulong ArtisanId { get; set; }
        public long PricePerUnit { get; set; }
        public long Quantity { get; set; }
        public bool Hq { get; set; }

        /// <summary>出品リテイナーのオーナーの ContentId。取れなければ 0。</summary>
        public ulong RetainerOwnerId { get; set; }

        /// <summary>出品者のキャラクター名。空のことが多い。</summary>
        public string PlayerName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Dalamud がマーケットのパケットから読み取った出品データを覗く。
    ///
    /// 公開インターフェース（<see cref="IMarketBoardItemListing"/>）にはオーナーの ContentId や
    /// 出品者名が出ていないが、実装クラスにはフィールドが存在する。
    /// ゲーム内部の一覧側でこれらが 0 になっていても、パケット側には入っている可能性があるため、
    /// 反射で読み取って補完に使う（取れなければ何もしない）。
    /// </summary>
    public static class PacketListingProbe
    {
        private static PropertyInfo? _ownerIdProperty;
        private static PropertyInfo? _playerNameProperty;
        private static bool _resolved;

        /// <summary>実装クラスから追加情報を取り出せたか（診断表示用）。</summary>
        public static bool HasOwnerIdProperty => _ownerIdProperty != null;

        public static bool HasPlayerNameProperty => _playerNameProperty != null;

        public static string ImplementationTypeName { get; private set; } = "(未取得)";

        public static List<PacketListing> Read(IMarketBoardCurrentOfferings offerings)
        {
            var result = new List<PacketListing>();
            if (offerings.ItemListings == null) return result;

            foreach (var listing in offerings.ItemListings)
            {
                if (listing == null) continue;
                ResolveProperties(listing);

                result.Add(new PacketListing
                {
                    ItemId = listing.ItemId,
                    ListingId = listing.ListingId,
                    RetainerId = listing.RetainerId,
                    RetainerName = listing.RetainerName ?? string.Empty,
                    ArtisanId = listing.ArtisanId,
                    PricePerUnit = listing.PricePerUnit,
                    Quantity = listing.ItemQuantity,
                    Hq = listing.IsHq,
                    RetainerOwnerId = ReadUInt64(listing, _ownerIdProperty),
                    PlayerName = ReadString(listing, _playerNameProperty),
                });
            }

            return result;
        }

        private static void ResolveProperties(object listing)
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                var type = listing.GetType();
                ImplementationTypeName = type.FullName ?? type.Name;

                const BindingFlags flags =
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

                _ownerIdProperty = type.GetProperty("RetainerOwnerId", flags);
                _playerNameProperty = type.GetProperty("PlayerName", flags);

                Plugin.PluginLog.Information(
                    $"出品データの実装クラス: {ImplementationTypeName} / " +
                    $"RetainerOwnerId={( _ownerIdProperty != null ? "あり" : "なし")} / " +
                    $"PlayerName={(_playerNameProperty != null ? "あり" : "なし")}");

                if (_ownerIdProperty == null || _playerNameProperty == null)
                {
                    var names = string.Join(", ", type.GetProperties(flags).Select(p => p.Name));
                    Plugin.PluginLog.Information($"利用できるプロパティ: {names}");
                }
            }
            catch (Exception e)
            {
                Plugin.PluginLog.Warning($"出品データの構造を調べられませんでした: {e.Message}");
            }
        }

        private static ulong ReadUInt64(object target, PropertyInfo? property)
        {
            if (property == null) return 0;
            try
            {
                return property.GetValue(target) switch
                {
                    ulong u => u,
                    long l => l < 0 ? 0UL : (ulong)l,
                    uint u32 => u32,
                    int i => i < 0 ? 0UL : (ulong)i,
                    _ => 0UL,
                };
            }
            catch
            {
                return 0;
            }
        }

        private static string ReadString(object target, PropertyInfo? property)
        {
            if (property == null) return string.Empty;
            try
            {
                return property.GetValue(target) as string ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
