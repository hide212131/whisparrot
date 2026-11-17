# M6 レポート: クリップボード退避（スナップショット）の堅牢化

## 1. 背景

v0.1.1 の実環境試用で、音声入力の投入が常に「クリップボードの内容を取得できないため、入力を中止しました」で失敗した（整文 OFF でも発生、投入先はメモ帳）。

原因は `TextInjector.TrySnapshot`。クリップボード内の全形式を列挙し、1 形式でも次に当たると投入全体を中止していた。

- `GetClipboardData` が NULL を返す形式（遅延描画の所有者が描画しない・終了している等。実環境では普通に起こる）
- GDI ハンドル形式（CF_BITMAP / CF_METAFILEPICT / CF_PALETTE / CF_ENHMETAFILE / 0x80–0x8F）
- `GlobalSize` / `GlobalLock` に失敗する形式

## 2. 実装

方針を「1 形式の失敗で全体を中止しない」に変えた（`src/Whisparrot.App/Windows/TextInjector.cs`）。

| 項目 | 内容 |
|---|---|
| 退避 | 形式ごとに独立して処理。取得できない形式はその形式だけを飛ばす |
| NULL の形式 | 飛ばす前に 30 ms（`RenderRetryDelay`）空けて **1 回だけ**再試行 |
| 複製できない形式 | `GlobalSize` ≤ 0 / `GlobalLock` 失敗 / 2 GiB 超は飛ばす。`GlobalUnlock` は finally で行う |
| GDI ハンドル形式 | バイト複製できないので飛ばす（従来は中止）。**OS が他形式から合成する形式の読み替え規則は従来どおり**（DIB / DIBV5 があれば CF_BITMAP、Unicode テキストがあれば CF_TEXT / CF_OEMTEXT / CF_LOCALE は退避せず、復元後に OS が再合成）。読み替えた形式は「飛ばした形式」に数えない |
| 復元 | 退避できた形式は従来どおり全部復元。飛ばした形式は復元されない（取得自体ができない形式はどのみち失われている、という整理） |
| 中止 | クリップボードを開けない場合など、本当に進めない場合だけ（従来の他の中止条件は不変） |
| 結果 | `InjectResult` は「投入できたか」で判定。形式を飛ばしても失敗にしない。通常利用で通知は出さない |

投入の他の安全策（前景ウィンドウ確認、修飾キーの解放待ち、貼り付け後に別のコピーがあれば復元しない）は変えていない。

### 診断

内容（テキスト等）は出さず、形式 ID・標準形式なら名前・理由だけを出す。

- `--clipboard-probe`（既存の往復プローブ）の出力末尾に `skipped_formats=` を追加。
- 新規 `--clipboard-probe --current`: **今のクリップボード**を退避するとしたら何形式が退避でき、どの形式が飛ばされるかを出す（クリップボードは変更しない）。例 `snapshot_formats=1 skipped_formats=14:CF_ENHMETAFILE(gdi_handle)`、`49374(not_retrievable)`。理由は `not_retrievable` / `not_duplicable` / `gdi_handle`。利用者の環境で実際に何が飛ばされているかを後から確認できる。
- 内部には、投入ごとの飛ばした形式を受け取る任意のコールバック（`InjectClipboard` の `onSkipped`）がある。通常の経路では何も渡さない。

### テストのための変更

投入本体を `TextInjector.InjectClipboard(text, sendPaste, onSkipped)` に切り出し、前景確認・修飾キー待ちは `InjectOnSta` 側に残した。貼り付けのキー入力を差し替えられるので、キー入力なしでクリップボード側を実測できる。テストプロジェクトは自己完結型の App exe を参照できないため、`TextInjector.cs` と `NativeMethods.cs` の 2 ファイルだけをリンクで取り込んでいる。

## 3. 再現と修正の実測（Windows 11 ARM64 VM、.NET 10.0.404、SSH の非対話セッション）

テスト `tests/Whisparrot.Tests/ClipboardSnapshotTests.cs`（実クリップボードを使うので並列実行しない）。遅延描画の再現には、専用スレッドでメッセージを回すメッセージ専用ウィンドウをクリップボードの所有者にして、`SetClipboardData(形式, NULL)` で形式を登録し、`WM_RENDERFORMAT` に何も供給せず応答させた。

### 修正前（コミット 87f8c28 = 切り出しとテストのみ、挙動は従来のまま）

| テスト | 結果 |
|---|---|
| 遅延描画の未描画形式がある（テキスト / HTML / HDROP + 未描画 2 形式） | **失敗**。`クリップボードの内容を取得できないため、入力を中止しました`（利用者の報告と同一）。所有者が描画要求を受けたことも確認済み（`GetClipboardData` が所有者に届いている） |
| 拡張メタファイル（CF_ENHMETAFILE）+ テキスト | **失敗**。`クリップボードの内容を安全に退避できないため、入力を中止しました` |
| 複数形式の往復 / 別アプリのコピーは復元しない / 空のクリップボード / CF_BITMAP のみ | 合格 |

CF_BITMAP のみのクリップボードは修正前でも通る。OS が CF_DIB / CF_DIBV5 を合成して列挙に出すため、CF_BITMAP は読み替え規則で飛ばされ、DIB が退避されるから。GDI ハンドル形式で実際に中止していたのは CF_ENHMETAFILE / CF_METAFILEPICT / CF_PALETTE / CF_DSP* 等。

### 修正後

- 遅延描画テスト: 投入成功。貼り付け時点のクリップボードは投入テキスト。テキスト / HTML / HDROP は**バイト単位で一致して復元**。未描画の 2 形式は復元されず、飛ばした形式として ID と理由が取れる。
- 1 回目は描画せず 2 回目で描画する所有者: 再試行で取得でき、飛ばさずに復元される。
- CF_ENHMETAFILE + テキスト: 投入成功、テキストはバイト一致で復元、`14:CF_ENHMETAFILE(gdi_handle)` が診断に出る。
- CF_BITMAP のみ: 投入成功、飛ばした形式なし、復元後も CF_BITMAP / CF_DIB が使える。
- 従来の往復（複数形式のバイト一致復元、別アプリのコピーは復元しない、空のクリップボード）はすべて維持。

自動テスト: 既存 379 件 + 新規 7 件、**合計 386 件合格、失敗 0**（Windows ARM64 VM、`dotnet test` 全件）。

## 4. publish（Windows ARM64 VM、`build/publish.ps1 -Arch all`、cpu）

Windows 上の新しいクローン（origin/main から clone し、本ブランチを取り込み）で実行。

| RID | exe サイズ |
|---|---|
| win-arm64 | 73,917,529 bytes（70.5 MiB） |
| win-x64 | 77,862,843 bytes（74.3 MiB） |

両 exe で `--clipboard-probe`（`snapshot_formats=3 all_original_formats_restored=True other_app_copy_preserved=True skipped_formats=none`）と `--clipboard-probe --current`（`skipped_formats=none`）が終了コード 0 で動くことを確認（x64 は ARM64 上のエミュレーション）。

## 5. 未検証・限界

- **実アプリへの貼り付けは未確認**。すべて非対話セッション（SSH）での実行で、前景ウィンドウ確認と Ctrl+V の送信は差し替えて通していない。メモ帳等への実投入は、対話デスクトップでの確認が要る。
- 再現は「描画しない所有者」の模擬。実際に失敗した利用者のクリップボードの中身（どのアプリがどの形式を載せていたか）は、この環境にない。利用者側で `--clipboard-probe --current` を実行すれば、飛ばされる形式の ID が分かる。
- 飛ばした形式は復元されない。実際に載っていた GDI 形式（画像のメタファイル等）は、投入後にクリップボードから失われる。DIB 経由で OS が再合成する CF_BITMAP と、Unicode テキスト由来のテキスト系形式は残る。GDI ハンドルの複製（`CopyImage` / `CopyEnhMetaFile`）は今回の対象外。
- 再試行は 1 回・30 ms。それで描画しない遅延形式は飛ばす。応答しない所有者に `GetClipboardData` が長く待たされる場合の時間制限は設けていない（従来どおり）。
- テストの実行は ARM64 のみ（x64 は publish と診断プローブの実行まで）。
