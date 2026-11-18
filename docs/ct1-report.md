# クラウド文字起こし 1/3 報告（T19 / T20 / T21）

- ブランチ: `feat/cloud-transcription`（`origin/main` から分岐）。マージ・タグ・リリースはしていない。
- 設計: [design-cloud-transcription.md](design-cloud-transcription.md) §4 / §8 / §9.2 / §11。今回は基盤のみで、利用者から見える挙動は変えていない（クラウド認識の接続、設定画面、Coordinator 結線は T22 以降）。

## T19: 判断記録と仕様反映

D-6 / D-7 / D-8 は本人が設計の推奨どおり承認済みとして記録した。

| 変更先 | 反映内容 |
| --- | --- |
| `spec-v1.0.md` 冒頭 | 2026-11-18 の改訂注記（D-6～D-8 承認済み、設計への参照） |
| §2 ノンゴール | 独自サーバー不使用、利用者データ送信は明示選択したクラウド認識先への音声と任意の整文先への認識済みテキストに限る（D-6） |
| §4 基本動作 4 | 第1段をローカル / 明示選択のクラウドに |
| §5 第1段 | 見出しを「音声認識（ローカル / クラウド）」へ。排他選択・既定ローカル・音声送信条件。末尾にクラウド設定・認識失敗の非復帰・音声非保存（D-7）、WAV 25,000,000 byte・停止後検査・60 秒（D-8） |
| §5 第2段 失敗原則 | 原文保持の保証は認識済みテキストを得た場合に限る |
| §6 通信 | 3 種 → 4 用途（クラウド文字起こしを追加） |
| §7 設定 | 認識先とクラウド接続（URL・キー・モデル）を整文と独立に追加 |
| §10 受け入れ | クラウド選択時のみ送信、失敗・取消は非投入、既定経路と原文投入の維持 |
| `design-v1.0.md` §1 / §3.3 / §6 / §8 / §9 | 追加設計への参照注記。「送信はテキストだけ」「Purpose は 3 種」に例外が加わることを明記 |
| `design-cloud-transcription.md` | 文書状態・§9.2・§12.1 に承認済みの決定記録を追記（実装・実地検証の完了とは区別） |

LiteLLM＋Azure の実動作は未検証のまま（設計 §10.4 の実地確認まで検証済みと書かない）。

## T20: 認識設定の型と DPAPI 準備契約

- 型（`Core/Settings/AppSettings.cs`）: `RecognitionEngine { Local, Cloud }`、`CloudRecognitionSettings(BaseUrl, ApiKeyDpapi, Model)`（`IsEmpty`）、`RecognitionSettings` に `Engine` / `Cloud` を追加。既存の 2 引数コンストラクターは維持。
- parser / serializer（`SettingsJson.cs`）: schema 1 のまま `recognition.engine` / `recognition.cloud.{base_url, api_key_dpapi, model}`。未知値・null・型違い・重複・未知キーはエラー。`engine=cloud` のときだけ `engine` を書き、`cloud` は全子項目が既定なら object ごと、既定の子項目は個別に省略する。local へ戻しても cloud 接続は保持し、明示消去（三値を既定へ）でのみ object が消える。
- validator（`SettingsValidator.cs`）: 非選択でも URL 構文（既存 `CompatibleEndpointUrl` 共用）、モデルの制御文字、暗号文の Base64 形式、空キー文字列（null へ統一）を検証。cloud 選択時のみ URL・モデル（空白だけ不可）を必須。
- DPAPI 準備契約（`Infrastructure/Recognition/CloudRecognitionConfig.cs`）: `Prepare(RecognitionSettings, ISecretProtector)` → `Ready(Endpoint, Model, ApiKey)` / `Unconfigured(固定文言)`。ネットワーク・ワーカー・ローカルモデルに触れず、設定も変更しない。
  - 外部宛でキー未設定・復号不能・空へ復号 → 準備不能。正確な loopback（`localhost` / `127.0.0.1` / `::1`）だけ空キー可（認証なし）。ただし保存済みキーが復号不能なら loopback でも認証なしへ切り替えない。
  - 復号後キーの制御文字は準備不能。Endpoint は `TrimEnd('/') + "/audio/transcriptions"`（`/v1` の自動付加・除去なし）、モデルは入力そのまま。`Ready.ToString()` はキーを出さず、理由文言に設定値・暗号文を含めない。
  - `CompatibleEndpointUrl.IsExactLoopback` を追加（URL 検証と共用。挙動は不変）。
- 旧 exe への制約: 追加キーは `engine` / `cloud` 配下のみ（テストで固定）。旧 parser の未知キー拒否は同じ実装規則によるが、**旧コミットの実 exe で拒否を実測してはいない**。

## T21: NetworkPurpose.Transcription

`Infrastructure/Network/NetworkContext.cs`:
- `NetworkPurpose` に `Transcription` を追加（4 種）。既定タイムアウト 60 秒（呼出し側の明示指定は優先）。
- 転送許可を `purpose != Cleanup` から明示列挙へ変更: `Update`（GitHub）/ `ModelDownload`（Hugging Face）だけが追う。Cleanup / Transcription は呼出し側が policy を渡しても追わず、3xx はそのまま返る（転送先への要求 0）。
- HTTP 許可を用途ごとに列挙: Cleanup は従来の `RoutingProxy.IsLoopback`（不変）、Transcription は正確な 3 形のみ。`127.0.0.2`・`0.0.0.0`・`::ffff:127.0.0.1`・`localhost.example.com` は拒否。
- proxy の 3 モード・NO_PROXY・手動 / 統合認証・TLS は既存実装に委ねる（コード変更なし）。

## テスト結果（Windows ARM64、新しいクローン、.NET SDK 10、`dotnet test`）

| 範囲 | 合格 / 失敗 |
| --- | --- |
| 既存（変更前から存在） | 484 / 0 |
| 新規 `CloudRecognitionSettingsTests`（T20） | 73 / 0 |
| 新規 `TranscriptionNetworkTests`（T21） | 30 / 0 |
| **全件** | **587 / 0**（スキップ 0）。`Whisparrot.slnx` 全体のビルドも成功 |

主な確認内容:
- T20: 既定のシリアライズが旧キー集合と完全一致、旧設定の読込と通常保存で追加キーなし、cloud の round trip、local 復帰時の接続保持と明示消去、型・null・重複・未知 enum・未知キー・URL・暗号文形式、壊れた設定 / 未来 schema の非破壊拒否（ハッシュ不変）、整文設定との独立、準備契約の各分岐（実 DPAPI の往復、別 entropy・壊れた暗号文は準備不能で暗号文は保持）。
- T21: 既定期限、HTTP の正確な loopback（`localhost` / `127.0.0.1` / `::1`、proxy 設定下でも直結）、301/302/303/307/308 の転送拒否、転送許可が Update / ModelDownload に限られること、環境変数 / 手動 / なしの 3 モード、NO_PROXY、Bearer と proxy 資格情報の非混在、407 / 不達 / 不正設定で直結しないこと、NTLM 統合認証（SSH セッション前提の既存 fixture）、TLS 不信頼証明書（API・HTTPS proxy）の拒否。
- 既存 3 用途: 既存テストが変更なしで全件合格（Cleanup の HTTP 許可・転送なし、Update / ModelDownload の転送を含む）。

## 未実施・制約

- 旧 exe（変更前コミット）での新キー拒否の実測は未実施。
- 実 CloudTranscriber・WAV・Coordinator・設定画面・probe・E2E・publish は T22 以降。実 HTTPS 外部経路・LiteLLM＋Azure は未検証。
- 統合認証の試験は既存 fixture どおり SSH セッション前提で、対話セッションでの実測ではない。
