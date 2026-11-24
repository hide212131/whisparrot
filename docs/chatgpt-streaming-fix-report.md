# ChatGPT 整文のストリーミング対応修正レポート

## 原因
公式 Sign in with ChatGPT（token-sharing）の推論は `stream: true` / `store: false` が必須で、`input` は配列で送る必要がある。
従来の `ChatGptPolisher` は `stream:false` と文字列の `input` を送っており、実アカウントで HTTP 400 になって常に原文へフォールバックしていた。

## 変更点
- `ChatGptPolisher.BuildRequestBody`: 要件どおりの形に変更。文脈付き（`PolishInput`）でも同じ形で、`instructions` は `InstructionsFor`、user content は `UserContentFor`。
- 応答処理を SSE 読み（`ReadEventStreamAsync`）に変更:
  - 非 2xx は従来どおり（401/403 は Auth、それ以外は Http。detail は `HTTP <status>`）。
  - `response.output_text.delta` の `delta` を連結する。UTF-8 で 1 MiB（`CompatiblePolisher.MaxResponseBytes`）を超えたら TooLarge。
  - `response.completed` で成功。連結テキストが空白だけなら Empty。delta が一度も来なかった場合だけ、completed 内の `response` を既存の `ParseResponse` に渡して取り出す。
  - `response.failed` は `PolishFailure.Error`。`response.error.code` / `message` があれば detail に入れる（200 字まで）。応答にない値は入れず、token は一切扱わない。
  - `response.incomplete` は Incomplete。終端イベントなしで終わった場合も Incomplete。読めないイベントが連続 3 回で Malformed（有効なイベントで連続数は戻る。1 件だけなら読み飛ばす）。
  - deadline の `CancellationTokenSource` を維持（利用者取消は例外、タイムアウトは `Fallback(Timeout)`）。
- `ParseResponse` は completed 内 output の抽出用として存続（public シグネチャは変更なし）。公開 API の変更はなく、`SettingsTextPolisher` / `TranscribeCommand` / 診断への影響はない。
- Compatible 経路（`CompatiblePolisher`）は未変更。

## リクエスト / 応答の before / after
| | before | after |
|---|---|---|
| stream | `false` | `true` |
| input | 文字列 | `[{"role":"user","content":"..."}]` |
| store | `false` | `false` |
| 送るフィールド | model / instructions / input / stream / store | 同じ（形だけ変更。非対応フィールドは送らない） |
| 成功判定 | 本文 JSON の `status == completed` | SSE の `response.completed`（delta の連結が本文） |

## テスト
`tests/Whisparrot.Tests/ChatGptTests.cs` を更新・追加した。
- リクエストボディ: stream:true、input が user メッセージ 1 件の配列、キー集合、非対応フィールドが無いこと（文脈付きも同様）。
- モックサーバは SSE（delta 2 件 + completed）を返す形に変更。
- SSE: delta 連結で成功 / completed のみ成功 / completed で output も delta もなければ Empty / 空白だけの delta は Empty / failed（code と message が detail に出る）/ detail の 200 字制限 / error なしの failed / incomplete / 終端なし / イベント途中での切断 / 読めないイベントの連続 / 単発の不正イベントの読み飛ばし / refusal / 上限超過（TooLarge）。
- 既存の `ParseResponse` の表形式テストは維持。

**結果: 未実行。** この環境（macOS）には dotnet SDK がなく、プロジェクトは `net10.0-windows` なので、`dotnet build` / `dotnet test` をここでは実行できていない。コードはレビューのみで、コンパイルも未確認。司令塔側の Windows で `dotnet test`（Whisparrot.Tests）を実行してください。

## 未確認事項
- ビルドとテスト全体の合否（上記）。
- 実アカウントでの動作（cleanup-probe で確認予定）。
- 実サーバの SSE に、想定外のイベント種別（例: `error` イベント単独）が来た場合は、未知のイベントとして読み飛ばし、終端がなければ Incomplete になる。

## 変更ファイル
- `src/Whisparrot.Infrastructure/ChatGpt/ChatGptPolisher.cs`
- `tests/Whisparrot.Tests/ChatGptTests.cs`
- `docs/chatgpt-streaming-fix-report.md`（新規）

コミットはしていない（変更は作業ツリーに残してある）。
