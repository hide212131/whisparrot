# M7 レポート: 貼り付け後の復元待ちの延長と投入診断

## 1. 背景

M6 でクリップボード退避を堅牢化した後も、利用者の環境ではメモ帳への貼り付けで競合が疑われた。新しい Windows のメモ帳は Ctrl+V 時のクリップボード読み取りが非同期で、Ctrl+V 送信から 300 ms（`PasteGrace`）後に元のクリップボードへ復元すると、貼り付け側が復元後の内容を読む（または何も貼られない）可能性がある。

## 2. 変更内容（`src/Whisparrot.App/Windows/TextInjector.cs` ほか）

| 項目 | 内容 |
|---|---|
| `PasteGrace` | 300 ms → **1500 ms**（設計 §10.1 の「実機で 2 秒まで調整可」の範囲内） |
| `InjectClipboard` | 任意引数 `grace`（待ち時間の上書き）と `trace`（ステップ通知）を追加。通常経路は何も渡さず、挙動は待ち時間以外不変 |
| `--inject-probe [--grace-ms N]` | 5 秒カウントダウン後、本番と同じ `InjectClipboard` 経路で固定文字列（30 文字未満の ASCII）を前景アプリへ投入し、各ステップを標準出力へ出す |

`--inject-probe` の出力項目: `grace_ms`、前景のプロセス名とクラス名（タイトルは出さない）、`sequence_after_set`、`paste_sent`（貼り付け送信の成否）、`sequence_before_restore` と `unchanged`（復元直前に sequence が不変か）、`restored`（復元したか）、`inject_text_length`、`result`。**クリップボードの内容は出さない**。`--grace-ms` に不正値を渡すと終了コード 2。

投入の他の安全策は変えていない: 前景ウィンドウ確認、修飾キーの解放待ち、貼り付け後に別のコピーがあれば復元しない、退避の形式ごとの独立処理（M6）。

## 3. テスト（Windows ARM64、`dotnet test` 全件）

既存 386 件 + 新規 2 件 = **388 件合格、失敗 0、スキップ 0**（想定どおり）。

新規テスト:
- `GraceParameter_IsAccepted_AndTraceReportsSteps`: grace 引数を受け付け、trace が 4 ステップを順に出し、`restored=True`、トレースに投入テキスト・元のテキストが含まれず、元の 3 形式が復元される。
- `Trace_ReportsNotRestored_WhenAnotherCopyHappened`: 貼り付け中に別アプリのコピーがあると `restored=False`。

## 4. publish（Windows ARM64、`build/publish.ps1 -Arch all`、cpu）

Windows 上の新しいクローン（ブランチ feat/m7-paste-timing）で実行。

| RID | exe サイズ |
|---|---|
| win-arm64 | 73,918,396 bytes（70.5 MiB） |
| win-x64 | 77,864,290 bytes（74.3 MiB） |

両 exe で `--clipboard-probe` が終了コード 0。`--inject-probe --grace-ms 0` を非対話セッション（SSH）で実行すると、カウントダウンの後 `foreground=none` / `foreground_external=False` で投入せず終了コード 1（前景確認が働く安全側の失敗）。`--grace-ms x` は終了コード 2。x64 は ARM64 上のエミュレーション。

## 5. 未検証・限界

- **対話セッションでのメモ帳への `--inject-probe` 実投入は未検証**（司令塔の独立検証で実施）。上記の実行はすべて非対話で、Ctrl+V の実送信と貼り付け結果は確認できていない。
- **1500 ms で利用者の実機の競合が解消するかは、本人の再試行待ち**。解消しない場合は `--inject-probe --grace-ms N` で待ち時間ごとの結果を見て調整する（上限の目安は 2 秒）。
- 待ちが延びた分、貼り付け後に復元されるまでの時間が長くなる。この間に利用者が別のコピーをした場合は従来どおり復元しない。
- テストの実行は ARM64 のみ（x64 は publish と診断プローブの実行まで）。
