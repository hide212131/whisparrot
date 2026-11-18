# クラウド文字起こし 3/3 報告（T24 / T25）

- ブランチ: `feat/cloud-transcription`（T19～T23 の続き。マージ・タグ・リリースはしていない）。
- 設計: [design-cloud-transcription.md](design-cloud-transcription.md) §9 / §10 / §11。D-6～D-8 は承認済みの前提。

## T24: 設定画面・ピル・トレイ

| 単位 | 内容 |
| --- | --- |
| 設定画面「認識」 | ローカル / クラウドのラジオ選択（初期はローカル）。ローカル選択時は既存の 4 モデル・取得操作。クラウド選択時はベース URL（`/v1` 込みの例と、実際の要求先・送信先ホストを表示）、伏せ字 API キー（DPAPI 保存、空欄なら維持、削除チェック）、モデル名（例 `whisper`、そのまま送る） |
| 説明文 | §9.1 の常時表示文案（転送され得ること、整文 OFF でも音声送信があること、25 MB・60 秒、失敗時は再録音が必要、接続先での保存・ログは各サービスの設定を確認）を認識欄の下に常時表示 |
| コピー / 消去 | 「整文の互換接続からコピー」は保存済みの整文互換接続の URL とキー暗号文だけを複写（モデルは複写しない、以後は連動しない、入力済みなら上書き確認、元が未設定・復号不能なら不成立）。「接続設定を消去」は 3 値を既定へ戻す（保存で確定）。コピーだけでは cloud に切り替わらない |
| 未完了の表示 | cloud 選択中、今の入力で録音を始められない理由（URL 未設定・不正、モデル未設定、キー未設定・復号不能）を欄内に表示。保存は妨げない（URL・モデルの必須は既存の検証が保存時に拒否）。録音開始時は T23 の固定文言で通知 |
| 表示の分離 | 「ローカルの実際の実行方式」は従来どおり最後のローカル結果。「今回の認識: クラウド / ローカル（方式）/ 未実行」を別行に表示（`cloud` が CPU / Vulkan 欄へ出ない） |
| ピル | `SessionSnapshot` に録音開始時固定の認識先を追加。クラウドの認識中だけ「クラウド認識中」（支援技術名も同文言）。停止は無効、× は有効 |
| トレイ | メニューに「認識: クラウド（音声の送信先 host）/ ローカル（音声は送信しません）」、ツールチップに「クラウド認識」。クラウド選択中の失敗通知はクリックで設定を開く（通知は 2.5 秒） |

## T25: probe・モック E2E・publish

- `--write-transcription-config <cfg> [--url --model --key-env --engine]`: 既存設定を読み recognition だけ更新（書込不可の設定は拒否）。キーは環境変数から DPAPI 保存、値は表示しない。
- `--transcription-probe --config <cfg> --wav <wav> [--show-text]`: cloud 選択の設定で実経路（準備 → `CloudTranscriber` → `NetworkContext`）を 1 回だけ通す。出力は状態・`elapsed_ms`・`recognition_outcome`・`failure_kind`・`http_status`・`injected_count`（常に 0。probe は投入しない）・`text_chars` のみ。本文は `--show-text` 指定時だけ。
- `--pipeline-file --use-settings` も製品と同じ認識選択器を使うよう変更（cloud なら実 CloudTranscriber、ローカルワーカー不使用）。cloud のとき発話本文は `--show-text` 指定時だけ出力。録音が始まらない場合（構え未完了）は 5 分待たず `recording_started=no` で終了。`--cancel-first-only` を追加。
- `build/mock-transcription-server.ps1`（開発専用・非同梱）と `build/cloud-transcription-e2e.ps1`: multipart を解析し WAV ヘッダー・サンプル数・ピーク・モデル・認証を検査。実 exe を使い、ローカルモデルなし（exe 隣に models なし）で実行。
- publish: `build/publish.ps1 -Arch all -Version 0.3.0-preview.1`。両 exe の FileVersionInfo は FileVersion `0.3.0.0` / ProductVersion `0.3.0-preview.1+<commit>`。

## 検証結果（Windows ARM64、新しいクローン、.NET SDK 10.0.404）

| 項目 | 結果 |
| --- | --- |
| `dotnet test Whisparrot.slnx` | **685 / 0**（スキップ 0）。内訳: 既存 668 + 新規 17（`CloudConnectionFormTests`、`SessionSnapshotEngineTests`） |
| `--settings-window-probe`（既存 + クラウド 11 項目） | arm64・x64 とも 23 PASS / `settings_window_probe=PASS` |
| 実 exe E2E（60 秒系を含む完全版） | **arm64: 56 件 / 失敗 0、x64（ARM64 上のエミュレーション実行）: 56 件 / 失敗 0** |

E2E の内容: probe の成功と失敗分類 14 種（401・403・404・413・429・500・503・不正 JSON・text 欠損 / null / 型違い・応答 1 MiB 超・307 リダイレクト（転送先 0 件）・エラー本文へのキー反響）と空 text・接続拒否（各 1 要求、再送なし、キー・本文の出力なし）／pipeline の成功（整文 OFF、3 連続、WAV 上限ちょうど 25,000,000 byte）／互換整文モックとの連携（認識結果が整文入力、整文結果のみ投入、整文 500・15 秒期限で認識原文を一度だけ投入）／認識失敗 13 種で投入 0・整文要求 0・Idle／local 選択・未設定・復号不能キー・上限 +1 sample で要求 0 かつローカルへ回らない（復号不能時 config.json 不変）／認識待ち中の取消と次セッション成功（再送なし）／実 60 秒期限（応答なし 60.0 秒、本文途中停止 60.0 秒、probe の途中停止 Timeout）。

## 未実施・未確認（検証済みとは書かない）

- **LiteLLM＋Azure の実地確認（設計 §10.4）は未実施・未確認**。日本語音声の品質、追加指定なしでの JSON 互換性、実サービスでの要求経路は確認していない。
- **T24 の対話デスクトップ確認は未実施**: 100 / 150 / 200% DPI、スクリーンリーダー等の支援技術、非フォーカス表示、ピルの「クラウド認識中」と通知クリックの実表示、実マイク・実貼付け。設定画面は非対話の probe（リフレクションでの操作）だけで確認した。
- ChatGPT 整文をクラウド認識の後段に置く実 exe E2E はしていない（互換整文モックのみ。ChatGPT 経路は既存試験と Coordinator の単体試験の範囲）。
- E2E は loopback HTTP のみ。HTTPS・proxy 経由の実要求・統合認証は T21 までの単体試験に留まる。録音中・アップロード中の取消の実 exe E2E はなく、認識待ち中の取消のみ。
- メモリ・ハンドルの長時間増加は計測していない。
- 旧 exe は新しいキーを含む設定を読めない（設計 §4.1 のとおり）。
- 設定画面は、クラウド選択でキー未設定など未完了のままでも保存できる（理由は欄内表示と録音開始時の通知で示す）。入力欄の前後空白は保存時に除去する。
