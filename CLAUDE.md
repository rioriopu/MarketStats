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
| `src/Game/RetainerHistoryCapture.cs` | 履歴の取り込み（フック / UI 配列） |
| `src/Game/RetainerHistoryArrayParser.cs` | UI 配列からの復元（保険用） |
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

## 重複排除の考え方

ゲーム側の履歴には取引 ID が無いため、内容一致で重複を判定する（`SaleRecord.DedupeKey`）。
ただし「同じ秒に同じ内容の取引が複数件」（99個 × 10枠の同時購入）が普通に起きるので、
キーごとの **件数** を突き合わせて不足分だけ追加する（`SaleStore.Merge`）。

## 配布

- GitHub: `rioriopu/MarketStats`（MIT / Author 表記は estell）
- 配布リポジトリ: PremiumDevReleaseRepo に `tier: free` で登録（`OPERATIONS.md` の手順 4）
