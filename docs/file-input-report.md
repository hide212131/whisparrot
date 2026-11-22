# ファイル入力（`transcribe` サブコマンド）実装レポート

対象: `~/dev/whisparrot-work/design-file-input.md`。ブランチ `feat/file-input`（`main` から分岐）。push・マージ・タグ・リリースはしていない。GUI と config.json のスキーマは変えていない。

## 結論

- `whisparrot.exe transcribe <入力> [--out <フォルダ>] [--no-polish]` を追加した。WAV（任意のレート・チャンネル・PCM/float）と m4a を 16 kHz モノラルへ変換し、設定どおりの認識→整文を一回通して `.raw.txt` / `.polished.txt` / `.result.json` を書く。
- テスト: Windows ARM64 検証機で **913 件すべて合格**（既存 887 + 新規 26）。m4a のデコードは、テスト内で AAC に符号化した m4a を Media Foundation で読み戻して確認した。
- 実地確認: 標準音声（part1.wav / part1.m4a ほか）で実 exe を回し、**デコードと経路・出力・終了コードを確認した。ただし実エンジン（ローカル Whisper・実クラウド・実整文）では回していない**（下の「確認できたこと・できていないこと」）。

## 再利用した境界（大きな作り替えは不要）

`SessionCoordinator` は録音・投入・Hane・取消を扱う UI 向けの状態機械で、入力源と出力先だけを差し替えるには重い。一方、認識と整文はすでに **録音開始時に固定される「構え」**（`RecognitionSetup.Ready` / `PolishSetup.Ready`、作るのは `IRecognitionSource` / `ITextPolisher`）として切り出されている。ここはマイクにもウィンドウにも依存しない。

そこで状態機械は通さず、新設した `FileTranscriber`（Core）が同じ構えを直接呼ぶ。設定からの組み立ては製品（`TrayApp`）と同じ `SettingsRecognizer` / `SettingsTextPolisher` / `WhisperRecognizer` を使うので、認識経路の選択・モデル解決・キーの復号・プロキシ・期限は製品と同じ。

代替案: `WavFileRecorder` + `SessionCoordinator` で通す（既存の `--pipeline-file` 診断がこの方式）。大きな変更は要らないが、原文と整文後を別々に取り出せず、段ごとの所要時間も測れず、投入先の fake が要る。範囲は小さいが目的に合わないので採らなかった。
トレードオフ: 認識・整文の期限処理（時間切れで打ち切る部分）は `SessionCoordinator` 内にあり、共有化せずに `FileTranscriber` に同等のものを持った（Coordinator の改変を避けるため）。将来、期限処理を共通化する余地がある。

## 実装

| 場所 | 内容 |
| --- | --- |
| `Core/FileInput/TranscribeOptions.cs` | 引数解析（入力は一つ、`--out <dir>`、`--no-polish`、不明なオプションはエラー） |
| `Core/FileInput/FileTranscriber.cs` | 認識→整文。構えの固定、期限、段ごとの所要時間、結果の種別 |
| `Core/FileInput/TranscribeOutput.cs` | 出力フォルダ作成、txt、`result.json` |
| `Infrastructure/Audio/AudioFileDecoder.cs` | WAV（`WaveFileReader`）/ m4a（`MediaFoundationReader`）→ 16 kHz モノラル。チャンネルは平均、リサンプルは `WdlResamplingSampleProvider` |
| `App/TranscribeCommand.cs`, `Program.cs` | サブコマンドの結線。`Program` は他の診断と同じ位置で `transcribe` を先に分岐（トレイ・二重起動 mutex に入らない） |

### 終了コードと理由表示

| コード | 意味 | 出力ファイル |
| --- | --- | --- |
| 0 | 成功 | raw / polished（整文時）/ result |
| 2 | 引数エラー（使い方も表示） | なし |
| 3 | 入力が使えない（無い・未対応形式・壊れている） | なし |
| 4 | 認識を始められない・失敗・空（モデル未取得、設定エラー、時間切れ等） | result.json のみ |
| 5 | 整文を選んでいるが使えない・失敗 | raw と result.json（polished は作らない） |
| 1 | 出力を書けなかった | — |

理由は標準エラーに利用者向けの固定文言で出す（キー・URL・応答本文は出さない）。

### 設計が決めていなかった点で選んだこと

- **整文が未構成・失敗のときは成功にしない（コード 5）**。ライブ録音は原文へ倒して投入するが、ファイル入力は条件と結果の記録が目的で、原文を整文結果と取り違えると比較が汚れるため。原文は書く。
- **入力エラーでは何も書かない**。存在しない入力の隣にフォルダや result.json を作らないため（実地確認で気づいて直した）。
- `--no-polish` は `polish.requested=false`、`result=skipped`。設定で整文 OFF の場合も `result=skipped`（`requested=true`）。
- ライブと同じく Hane・投入は使わない。設定の読み込みは exe の隣の config.json（無ければ既定値）。設定ファイルへは書き込まない。
- 標準エラーの文字コードはコンソール既定（日本語環境は CP932）。ファイルへリダイレクトして UTF-8 として読むと文字化けして見える。

### result.json

```json
{ "app_version": "v0.2.0", "started_at": "2026-11-23T08:41:25+09:00", "success": true, "error": null,
  "input": { "file": "part1.wav", "format": "wav", "duration_seconds": 65.129 },
  "recognition": { "engine": "cloud", "model": "mock-stt", "backend": "cloud", "language": "ja", "ok": true },
  "polish": { "requested": true, "provider": "compatible", "model": "polish-model", "result": "ok" },
  "timings_ms": { "decode": 23, "recognize": 372, "polish": 162, "total": 635 } }
```

モデル名は設定から読む（ローカルは `recognition.model`、クラウドは `recognition.cloud.model`、整文は compatible / chatgpt のモデル）。URL・キーは入れない（テストで確認）。`benchmark-results.md` の記録テンプレートは作業フォルダにもリポジトリにも見当たらなかったため、項目の対応は未確認。転記しやすい形かは、そのテンプレートと突き合わせて調整が要る。

## 確認できたこと・できていないこと（検証機: Windows ARM64、フォルダ `whisparrot-fi`）

検証機に **Whisper のモデル（ggml-*.bin）も config.json も無かった**。実クラウド・実整文の API キーも無い。設定は作っていない。

| 確認 | 結果 |
| --- | --- |
| 全テスト | 913 件合格 |
| 実 exe（設定なし）で part1.wav / part1.m4a | デコード成功（65.129 s / 65.173 s、デコード 35 ms / 385 ms）。認識は「モデル『small』が未取得」で**コード 4**、result.json に所要時間付きで記録。**ローカル Whisper の実認識は未確認** |
| 引数なし入力・存在しない入力・.mp3・不明オプション | コード 2 / 3 / 3 / 2（理由は標準エラー）。入力エラーでファイルは作られない |
| 経路の疎通（ループバックのモック） | 既存の `build/mock-transcription-server.ps1` と `mock-compat-server.ps1`、ダミーキー、exe を別フォルダへコピーして設定を書いたもの（検証機の設定は触っていない）で、part1.wav / part1.m4a を実 exe で流して**コード 0**。raw / polished / result.json が設計どおり出力された。`--no-polish` は polished を作らない。整文モックを 500 にすると**コード 5**で raw と result.json だけが残る。モックが受けた WAV は約 1,042,000 サンプル（≒ 65 s × 16 kHz）で、変換後の 16 kHz モノラルが送られていることを確認。ダミーキーはどの出力にも現れない |
| 実エンジン | **未実施**: ローカル Whisper の認識結果、実クラウド認識、実整文は確認していない。認識テキストの正しさ・実所要時間（ベンチ値）はモックのものであり意味を持たない |

## 注意点・未対応

- **同名ステムの衝突**: 出力名は設計どおり `<名前>`（拡張子除く）なので、`part1.wav` と `part1.m4a` を同じフォルダへ出すと上書きし合う。両方を流すときは `--out` を分ける（実地確認もそうした）。
- WinExe のため、PowerShell から `& whisparrot.exe transcribe ...` と呼ぶと終了を待たない。`Start-Process -Wait -PassThru`（終了コードは `.ExitCode`）か `| Out-Null` で呼ぶ。
- 設計の「ベンチマーク実行」の PowerShell スクリプト（whisparrot-bench）は今回の依頼範囲外として作っていない。上の呼び方の注意が必要。
- 他形式（mp3 等）は未対応のまま（設計どおりエラー）。
