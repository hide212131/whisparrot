# クラウド文字起こし 2/3 報告（T22 / T23）

- ブランチ: `feat/cloud-transcription`（T19～T21 の続き。マージ・タグ・リリースはしていない）。
- 設計: [design-cloud-transcription.md](design-cloud-transcription.md) §3 / §5 / §6 / §7 / §11。D-6～D-8 は承認済みの前提。設定画面（T24）は変更していない。

## T22: WAV 変換と CloudTranscriber

| 単位 | 内容 |
| --- | --- |
| `Infrastructure/Recognition/WavEncoder.cs` | 認識直前の 16 kHz mono float から、メモリ上だけの PCM16 LE WAV（44 byte ヘッダー、追加チャンクなし）を作る。有限値を [-1, 1] に clamp し、負側 32768・正側 32767 の倍率で丸めて飽和変換。NaN / Infinity は例外。上限は WAV 全体 25,000,000 byte（最大 12,499,978 サンプル）。長さ計算は checked の 64 bit で、確保前に判定する。ループ内で取消を確認 |
| `Infrastructure/Recognition/CloudTranscriber.cs` | `POST <endpoint>`（OpenAI 互換 `/audio/transcriptions`）。multipart は `file`（`speech.wav`、`audio/wav`）と `model` だけ。非空キーだけ `Authorization: Bearer`。`NetworkPurpose.Transcription` のみを通し、再送なし、リダイレクトは追わない。全体 60 秒（上限検査・WAV 化・接続・送信・本文受信・解析を一つの期限で守る）。応答は 1 MiB 上限（Content-Length と実バイト数）。結果はトップレベル `text`（文字列）だけ |
| 失敗の固定分類 | `TranscriptionFailure`: Auth（401/403）、PayloadTooLarge（413）、Http（その他の非 2xx。3xx 含む）、Network（接続・TLS・proxy・切断）、Timeout、Malformed（不正 JSON・text 欠損 / null / 非文字列）、ResponseTooLarge、AudioTooLong（送信 0 件）、InvalidAudio（送信 0 件）、Error。利用者向け文言は固定で、応答本文・キー・URL・例外メッセージを含まない。この型はログ出力を持たない |
| 空 text | エラーではなく空の成功として返し、Coordinator が非投入・「音声を認識できませんでした」にする（無音とは断定しない） |
| 取消 | 利用者の取消は `OperationCanceledException`（失敗分類にしない）。期限は Timeout |

## T23: 結線

| 単位 | 内容 |
| --- | --- |
| `Core/RecognitionSetup.cs` | `RecognitionSetup`（`Unconfigured(理由)` / `Ready(Engine, 認識関数, Deadline?)`）、`IRecognitionSource`、既存 `IRecognizer` を包む `FixedRecognitionSource`。`PolishSetup` と同じ流儀 |
| `Infrastructure/Recognition/SettingsRecognizer.cs` | 録音開始時の設定から構えを作る。local は既存の `CheckReady` と認識呼出しへ委譲（期限なし）。cloud は `CloudRecognitionConfig.Prepare`（DPAPI 復号・検証）で宛先・モデル・キーを捕捉し、`CloudTranscriber`（Deadline 60 秒）を構える。準備でネットワーク・ワーカー・ローカルモデルに触れない |
| `Core/SessionCoordinator.cs` | `IRecognitionSource` を受ける入口を追加（既存の `IRecognizer` 入口は残し、診断・既存テストは変更なし）。構えは録音開始時に固定。`Unconfigured` は録音せず固定文言を通知。`Deadline` があるときは、認識側が取消を無視しても期限でセッションを終え、先に取消して要求を止めてから「時間切れ」を通知する。期限後の遅れた成功は投稿されず、別セッション・Idle では ID / 状態の不一致で捨てる。認識失敗は `Failed`（未投入あり）を経由せず Idle、整文は始めない。取消は通知しない。処理中の再押下は従来どおり無視 |
| `App/TrayApp.cs` | `SettingsRecognizer` を Coordinator へ結線。起動時のローカルモデル検査・設定を開く案内は local 選択時だけ（cloud 選択時は検証も案内もしない）。local → cloud へ切り替えたとき待機中のワーカーを解放（モデル変更時の既存処理と同じ） |

ローカル認識（`WhisperRecognizer`）・整文（`SettingsTextPolisher` など）・`NetworkContext` のコードは変更していない。

## テスト結果（Windows ARM64、新しいクローン、.NET SDK 10、`dotnet test Whisparrot.slnx`）

| 範囲 | 合格 / 失敗 |
| --- | --- |
| 既存（T21 までの全件） | 587 / 0 |
| 新規（`WavEncoderTests` / `CloudTranscriberTests` / `CoordinatorRecognitionSetupTests` / `SettingsRecognizerPipelineTests`） | 81 / 0 |
| **全件** | **668 / 0**（スキップ 0）。新規 81 件は連続 3 回実行して全回合格（不安定な試験は見られなかった）。`Whisparrot.slnx` 全体のビルドも成功 |

主な確認内容:

- WAV: ヘッダーの各フィールド、10 / 30 秒の byte 長（320,044 / 960,044）、端値・clamp・飽和、NaN / ±Infinity、上限 N（25,000,000 byte）と N+1、checked のオーバーフロー、取消、録音末尾が WAV に含まれること。
- HTTP（ループバックのモックへの実 HTTP）: multipart が file / model の 2 つだけでファイル名・media type・WAV 本文が一致、入力モデル名がそのまま、Bearer の有無（キーはヘッダーだけ）、base path・末尾 `/`、`text` の成功・追加フィールド無視・空・欠損 / null / 型違い / 不正 JSON / 途中切れ、応答 1 MiB ちょうどと +1、虚偽の Content-Length、401 / 403 / 404 / 413 / 429 / 500 / 503 / 400 の分類と要求 1 回（再送 0・Retry-After でも待たない）、エラー本文のダミーキー・文字列が結果・通知に出ないこと、301 / 302 / 303 / 307 / 308 で転送先への要求 0、接続拒否・切断・非 loopback HTTP、上限超過・NaN で送信 0、ヘッダー待ち・ヘッダー後の本文停止が同じ期限（短い期限を注入）で Timeout、取消が例外になること。
- Coordinator: 成功 / 整文 / 整文失敗時のクラウド原文一回投入、認識失敗の非投入・Failed 非経由・整文 0・RetryInsert 無効、空 text、`Unconfigured` と準備例外で録音 0、構えの録音開始時固定、処理中の連打、取消後の遅延成功の破棄、期限での終了と取消、期限後の遅延成功の破棄、古い完了が次セッションに影響しないこと、期限直前の成功が一回だけ採られること、`IRecognizer` 入口の回帰。
- 結合（実 `SettingsRecognizer` + `CloudTranscriber` + `NetworkContext` + Coordinator、実 DPAPI）: ローカルモデルなし（local が準備不能）でも cloud が成功しローカル認識を呼ばない、local 選択（cloud 設定を残す）でクラウド要求 0 とローカルの準備確認の維持、復号不能キー / 未設定でクラウド要求 0・ローカルへ回らない・暗号文を変更しない、クラウド失敗でローカルへ回らず再送なし、録音中の送信先変更が当該セッションに影響せず次から効くこと、実 HTTP の無応答が期限で終わり次セッションが成功、要求中の取消で非投入・次セッション成功。

## 未実施・制約

- 「50 セッションでハンドル / メモリが増え続けない」は、50 回の連続要求でメモリが大きく増えないことの確認に留まる（ハンドル数の計測はしていない）。
- クラウド未準備時の通知は固定文言のみで、設定画面を自動で開く導線は付けていない（T24 の範囲）。
- 実 exe・診断 probe・実 60 秒の E2E・publish・x64 は T25。HTTPS の外部経路、proxy 経由の実要求、LiteLLM + Azure は未検証（NetworkContext 側の試験は T21 まで）。
- ピル・トレイの「クラウド認識中」表示、設定画面の表示は T24。`BackendReported` には `cloud` が流れるが、設定画面のローカル実行方式欄は `WhisperRecognizer.LastBackend`（ローカル結果のみ）を参照するため `cloud` は出ない。
- 日本語音声の品質・追加指定なし JSON の互換性は実サービスでの確認が必要（未確認）。
