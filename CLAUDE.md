# Market Stats — 開発メモ

FFXIV / Dalamud プラグイン。リテイナーの売却履歴を購入者ごとに集計する。

## ビルド

```
dotnet build -c Release
```

- TargetFramework: `net10.0-windows7.0` / Dalamud API Level **15**
- Dalamud 参照は `%AppData%\XIVLauncher\addon\Hooks\dev`
- Release ビルド時に `C:\DevPlugins\MarketStats\` へ自動コピー（devPlugins 用）

## 構成

| パス | 役割 |
|---|---|
| `src/Plugin.cs` | エントリポイント。サービス取得・コマンド・Framework ループ |
| `src/PluginConfig.cs` | 設定 |
| `src/Data/SaleRecord.cs` | 売却レコード 1 件 |
| `src/Data/SaleStore.cs` | 永続化・重複排除・保持期間の適用 |
| `src/Data/FavoritesStore.cs` | お気に入り購入者 |
| `src/Data/SaleAggregator.cs` | 購入者 × アイテムの集計、まとめ買いの束ね |
| `src/Data/IdentityStore.cs` | ContentId ↔ キャラクター名の対応表 |
| `src/Data/ListingStore.cs` | 観測した出品の蓄積 |
| `src/Data/ResaleAnalyzer.cs` | 購入 → 再出品の相関スコアリング |
| `src/Data/PendingSaleStore.cs` | 購入者不明の売却 / 取りこぼし区間 |
| `src/Game/RetainerHistoryCapture.cs` | 履歴の取り込み（フック / UI 配列） |
| `src/Game/RetainerHistoryArrayParser.cs` | UI 配列からの復元（保険用） |
| `src/Game/IdentityCollector.cs` | ObjectTable / InfoProxy から対応表を収集 |
| `src/Game/MarketBoardWatcher.cs` | マーケット出品の記録（オーナー ContentId 付き） |
| `src/Game/RetainerSellListWatcher.cs` | 自分の出品リストの差分監視 |
| `src/Game/SaleHistoryAutoOpen.cs` | 売却履歴の自動オープン（AutoRetainer IPC / 手動） |
| `src/Game/ItemCatalog.cs` | ItemId → 名前 / アイコン |
| `src/Game/LodestoneLink.cs` | Lodestone 検索 URL |
| `src/Game/UniversalisClient.cs` | Universalis API |
| `src/UI/MainWindow*.cs` | UI（タブごとに partial 分割） |

## 取り込みの仕組み

売却履歴はゲーム内でリテイナーごと最新 20 件しか保持されない。
そのため「履歴が読み込まれたタイミング」で取り込んで蓄積する。

1. **フック（主経路）**
   シグネチャ `40 53 56 57 41 57 48 83 EC 38 48 8B F1` の関数をフックし、
   第 2 引数 `+8` から 52 バイト × 20 件のレコード配列を読む。

   ```
   0x00 uint   ItemId
   0x04 uint   Price          （合計金額）
   0x08 uint   UnixTimeSeconds
   0x0C uint   Quantity
   0x10 byte   IsHq
   0x11 byte   (未解析)
   0x12 byte   IsMannequin
   0x13 char[32] BuyerName
   ```

   パッチでシグネチャが壊れる可能性があるため、
   読み取った内容には妥当性チェック（`IsPlausible`）を通してから記録する。

2. **UI 配列（保険）**
   フックが設置できなかった場合のみ、`RetainerHistory` アドオン表示中に
   `ItemDetail` の Number/String 配列をパースする（`RetainerHistoryArrayParser`）。
   型情報が無いので誤検出を避けるため判定は厳しめ。少しでも怪しければ何も返さない。

### パッチ後にやること

1. `dotnet build -c Release` が通るか（ClientStructs / Dalamud API の変更）
2. ゲーム内で売却履歴を開き、設定タブの「診断」で取り込み件数が増えるか
3. 増えない場合はシグネチャが壊れている。実行ファイルに対する AOB スキャンで再取得する

## 出品者の識別

`InfoProxyItemSearch.MarketBoardListing` には出品リテイナーのオーナーの **ContentId**
（`ContentId` フィールド, 0x78）が入っている。Dalamud の公開 API
（`IMarketBoardItemListing`）には出ていないので、ClientStructs から直接読む。

名前は含まれないため、別経路で ContentId ↔ 名前の対応表を作る（`IdentityCollector`）。

| 出所 | 取得元 |
|---|---|
| 周囲のプレイヤー | `Character.ContentId`（0x2358）+ ObjectTable |
| フレンド / FC / LS / パーティ | `InfoProxyCommonList.CharDataSpan`（ContentId + Name + HomeWorld）|
| 推定 | 売却履歴と出品タイミングの相関（`ResaleAnalyzer`）|

出所は `IdentitySource` の値が大きいほど強く、弱い出所で確定情報を塗り潰さない。

## 再出品の推定

`ResaleAnalyzer` が購入イベント（`PurchaseSession`）と観測済みの出品を突き合わせる。
スコアは 時間の近さ（最大 40）＋ 数量一致（25）＋ 買値超え（15）＋ 複数回一致（40 × (n-1)）。
50 点以上を候補として表示し、140 点以上かつ複数回一致で `IdentityStore` に推定として記録する。

出品時刻は Universalis の `lastReviewTime` を優先し、無ければ初観測時刻で代用する。
`lastReviewTime` は価格改定でも更新されるため、数量・価格条件と併せて絞る前提。

## 取りこぼし対策

- `SaleHistoryAutoOpen` — AutoRetainer の IPC（`AutoRetainer.OnRetainerAdditionalTask` /
  `OnRetainerReadyForPostprocess` / `RequestPostprocess` / `FinishPostprocessRequest`）に相乗りし、
  `SelectString` の項目から「売却履歴」を `FireCallback` で選ぶ。
  AtkValues 配置は `[3]`=項目数, `[7..]`=項目文字列。
- `RetainerSellListWatcher` — `InfoProxyItemSearch.RetainerListings` を差分監視。
  読み取った listing の `RetainerId` がアクティブなリテイナーと一致しない場合は
  古いデータの可能性があるので見送る（全消え誤検出の防止）。
- 取りこぼし警告 — 取り込んだ履歴が 20 件ちょうどで、その最古の売却時刻が
  記録済みの最新時刻より後なら、その間を取りこぼしている（`Plugin.DetectHistoryGap`）。

## 重複排除の考え方

ゲーム側の履歴には取引 ID が無いため、内容一致で重複を判定する（`SaleRecord.DedupeKey`）。
ただし「同じ秒に同じ内容の取引が複数件」（99個 × 10枠の同時購入）が普通に起きるので、
キーごとの **件数** を突き合わせて不足分だけ追加する（`SaleStore.Merge`）。

## 配布

- GitHub: `rioriopu/MarketStats`（MIT / Author 表記は estell）
- 配布リポジトリ: PremiumDevReleaseRepo に `tier: free` で登録（`OPERATIONS.md` の手順 4）
