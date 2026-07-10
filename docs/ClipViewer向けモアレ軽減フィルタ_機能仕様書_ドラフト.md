# ClipViewer モアレ軽減フィルタ機能 仕様書（ドラフト）

> **【2026/07/05 実装済み注記】** 本ドラフトの Phase 1・2 は v0.7.0 で実装完了。
> 実装後の正式仕様は `ClipViewer_統合要件定義書.md` §19 を参照。主な相違点:
> - キーバインドは §6 の F2 案ではなく **F9（Stage1）/ F11（Stage2）の独立トグル**で実装（F2 は OpenIniFile に転用）
> - Stage1 は Lanczos-3 のみ実装（Area/Gaussian は Phase 3 で追加予定、ini 上は受理され Lanczos で代替）
> - デコード時縮小は「目標幅の2倍」を確保する方式（2倍ズームまで画質維持）

**関連ドキュメント:** ClipViewer_統合要件定義書.md（v0.5.5）, ClipViewer_実装仕様書.md
**本書の位置づけ:** 本スレッドで実施したリサーチ・すり合わせの結果をもとにした機能仕様ドラフト。Cowork側の実装セッションに引き継ぐことを目的とする。
**バージョン番号:** 未確定（Cowork側での実装フェーズ整理時に既存Phase 6計画との統合可否を含めて確定する）
**作成日:** 2026/07/01

---

## 1. 設計方針（今回のすり合わせ内容の反映）

- 画質の好みは主観（クオリア）に依存するため、**アルゴリズムやプリセットを決め打ちしない**。
- モアレ軽減処理とシャープ化処理を**独立した2段パイプライン**として実装し、それぞれ個別にON/OFF・強度調整が可能な構成とする。
- パラメータ調整は**iniファイルの編集で行える**ことを必須要件とする（GUI上のスライダー等は将来フェーズの任意対応とし、必須ではない）。
- 実装は優先度順にフェーズ分けし、まず効果の大きいStage1（モアレ軽減）から着手する。

---

## 2. 機能構成（2段パイプライン）

```
元画像（デコード時縮小: DecodePixelWidth）
  ↓
[判定] ダウンスケールか？（表示解像度 < ソース解像度 × MoireDownscaleThreshold）
  ├─ No → Stage1をバイパス（等倍・拡大時は素通し、ぼかさない）
  └─ Yes ↓
[Stage 1] モアレ軽減フィルタ
   ・MoireFilterMode（Off / Area / Lanczos / Gaussian）
   ・MoireFilterStrength（強度）
  ↓
[Stage 2] シャープ化（アンシャープマスク）
   ・SharpenEnabled（On/Off）
   ・SharpenRadius / SharpenAmount / SharpenThreshold
  ↓
表示・キャッシュ
```

Stage1・Stage2は独立したトグルであり、両方OFF・片方のみON・両方ONの組み合わせをすべて許容する。

---

## 3. 実装優先順位（フェーズ分け）

| フェーズ | 内容 | 目的 |
|---------|------|------|
| **Phase 1（最優先）** | デコード時縮小（`DecodePixelWidth`）／ダウンスケール判定ゲーティング／Stage1フィルタ（Lanczosを最初に実装、Area平均は共通関数の特殊ケースとして後追加しやすい設計に） | モアレ軽減効果を最速で得る。ここまでで「網点が消える」効果が体感できる状態にする |
| **Phase 2** | Stage2アンシャープマスク追加。リンギング対策として`SharpenThreshold`を同時実装 | 「滑らかだが眠い」画質への対策。Stage1単体運用時の弊害を防ぐ |
| **Phase 3** | Stage1のフィルタ種別追加（Area平均・Gaussian強デスクリーン）。キャッシュ／プリフェッチとの連携最適化 | 選択肢の拡充・パフォーマンスチューニング |
| **Phase 4（将来・任意）** | GPU仕上げ（軽量ShaderEffectでの追加シャープ化）、GUIプリセットボタン、ライブプレビュー | 快適性・UX向上（必須ではない） |

Phase 1・2完了時点で「iniを直接編集すれば全パラメータを弄れる」状態が成立する。GUI化はPhase 4まで不要。

---

## 4. ini拡張仕様：`[Filters]` セクション新設

既存の `ClipViewer.ini` に新セクションを追加する。既存セクション（KeyBindings / Settings / State）とは独立させる。

```ini
[Filters]
; モアレ軽減フィルタ 有効/無効
MoireFilterEnabled=True
; モアレ軽減アルゴリズム（Off / Area / Lanczos / Gaussian）
MoireFilterMode=Lanczos
; モアレ軽減強度（0-100、内部でぼかし半径/フィルタサポート幅にマッピング）
MoireFilterStrength=60
; ダウンスケール判定閾値（表示解像度 ÷ ソース解像度 がこの値未満の場合のみフィルタ適用。1.0=常時、0.95推奨）
MoireDownscaleThreshold=0.95

; アンシャープマスク 有効/無効
SharpenEnabled=True
; シャープ化半径（px）
SharpenRadius=1.0
; シャープ化強度（0-200%）
SharpenAmount=50
; シャープ化閾値（0-255、リンギング防止用。低コントラスト部分をシャープ化対象から除外）
SharpenThreshold=4
```

### 4.1 設定項目詳細

| キー | 型 | デフォルト | 範囲 | 説明 |
|------|----|-----------|------|------|
| `MoireFilterEnabled` | bool | True | — | Stage1（モアレ軽減）全体のON/OFF |
| `MoireFilterMode` | enum | Lanczos | Off / Area / Lanczos / Gaussian | Stage1のアルゴリズム種別（Phase 1ではLanczosのみ実装、他はPhase 3で追加） |
| `MoireFilterStrength` | int | 60 | 0-100 | Stage1強度。内部でアルゴリズムごとのパラメータ（ぼかし半径・フィルタサポート幅）に変換 |
| `MoireDownscaleThreshold` | double | 0.95 | 0.0-1.0 | この値未満の縮小率でのみStage1を適用。1.0にすると常時適用（ズーム時含む）になる |
| `SharpenEnabled` | bool | True | — | Stage2（アンシャープマスク）全体のON/OFF |
| `SharpenRadius` | double | 1.0 | 0.1-5.0 | USMのぼかし半径（px） |
| `SharpenAmount` | int | 50 | 0-200 | USMの強さ（%） |
| `SharpenThreshold` | int | 4 | 0-255 | この値未満のコントラスト差はシャープ化対象外（Stage1が残したぼかし残渣のリンギング化を防止） |

### 4.2 注意事項（後方互換・パース仕様）

- `[Filters]` セクションが存在しない旧iniを読み込んだ場合は、上記デフォルト値で新規補完し、次回保存時に追記する（`LastInfoDisplay`旧キーの後方互換処理と同様の方式）。
- コメント行（`;`）およびセクション行のパース仕様は既存の`IniFileManager`実装を踏襲する。
- 数値レンジ外の値が指定された場合はクランプ（範囲内に丸め込み）した上で通知なく適用する（起動を阻害しない）。

---

## 5. AppSettings拡張案

`AppSettings.cs` に以下のプロパティ群を追加する想定（実装時にCowork側で命名調整可）。

```
enum MoireFilterAlgorithm { Off, Area, Lanczos, Gaussian }

bool MoireFilterEnabled
MoireFilterAlgorithm MoireFilterMode
int MoireFilterStrength
double MoireDownscaleThreshold

bool SharpenEnabled
double SharpenRadius
int SharpenAmount
int SharpenThreshold
```

---

## 6. キーバインド案（任意・Phase 4寄りでも可）

細かい強度調整はini経由が主だが、**フィルタ全体のクイックON/OFF**は閲覧中に欲しくなる操作のため、キーバインドを1つ確保しておくことを推奨する。

| キー（デフォルト案） | 動作 | 備考 |
|---|---|---|
| `F2` | モアレ軽減フィルタ（Stage1+Stage2一括）クイックトグル | F1（情報表示）に隣接し空きキーのため割当。iniで変更可 |

既存キーバインド一覧（F1, F3-F9, F11使用済み）との重複はない。F2・F10・F12が空きキーとして残っている。

---

## 7. リンギング対策（Phase 2で必須実装）

Stage1で強めにぼかした直後にStage2で強いUSMをかけると、消したはずの網点の輪郭がリンギングとして再浮上する可能性がある。対策として：

- `SharpenThreshold` により低コントラスト差の領域をシャープ化対象から除外する。
- 実装時、`SharpenAmount`の上限（200%）はソフトリミットとし、内部的にStage1強度と連動して過剰補正にならないよう緩やかに制限する調整ロジックの検討をCowork側実装時に行う（本書では方向性のみ提示し、具体的な計算式は実装フェーズで詰める）。

---

## 8. Coworksへの引き継ぎメモ

- 本書はリサーチ・要件すり合わせのみを目的としたスレッドの成果物であり、コード実装は別スレッド（Cowork）で行う。
- Phase 1着手時に必要な調査事項：`.clip`/`.psd`を含む全フォーマットに対して`DecodePixelWidth`による事前デコード縮小が適用可能か（`ClipFileReader`/`PsdFileReader`はSQLite/独自パースを経由するため、`BitmapImage`の`DecodePixelWidth`が直接効かない可能性があり、実装時に個別確認が必要）。
- Phase番号は既存のPhase 6計画（AVIF/PSD対応強化・複数ページスキップ再設計・コンテキストメニュー統合）と統合するか、独立したPhaseとして追加するかは未確定。Cowork側セッション開始時に判断する。
