# M8 レポート: 貼り付けをウィンドウメッセージ経由にする（キーリマッパに左右されない投入）

## 1. 背景

M7 までの投入は、クリップボードへテキストを置いて SendInput で Ctrl+V を送る方式だった。検証用 Windows に常駐するキーリマッパ（Emacs 風のキーバインド）が、メモ帳に対して Ctrl+V を別のキー（PageDown）へ再定義しており、アプリが送った Ctrl+V が貼り付けにならなかった。アプリ側の不具合ではないが、キー入力経由の貼り付けは利用者のキーリマッパ設定に常に左右される。ウィンドウメッセージはキーボードフックを通らないため、貼り付けを WM_PASTE へ切り替える。

## 2. 経路の設計（`TextInjector`）

貼り付け段を二段構えにした。前後の手順（前景確認 → 修飾キー待ち → 退避 → テキスト設定 → 貼り付け → 復元）は従来どおり。

| 経路 | 内容 | 復元前の待ち |
|---|---|---|
| 主経路 `paste-message` | 前景ウィンドウのスレッドへ `GetGUIThreadInfo` を呼び、フォーカス中コントロール（`hwndFocus`）へ `WM_PASTE` を `SendMessageTimeout`（`SMTO_ABORTIFHUNG`、上限 2 秒）で同期送信。フォーカスが取れなければ前景ウィンドウ自体へ送る | `PasteMessageGrace` = 0（戻り次第すぐ復元） |
| 予備経路 `send-input` | 従来の SendInput による Ctrl+V | 従来の `PasteGrace`（1500 ms） |

- **予備経路へ落ちる条件**（Auto のとき）: 宛先が得られない、または `SendMessageTimeout` が 0 を返す（タイムアウト・対象のハング・失敗）。
- **待ちを 0 にした根拠**: `SendMessage` は同期で、対象はメッセージ処理中にクリップボードを読む。実測（§4）で、新しいメモ帳（宛先クラス `RichEditD2DPT`）でも猶予 0 で復元して文書に投入文字列が入ることを 6 回（経路確認 1 + 反復 5）確認した。`PasteMessageGrace` は定数にしてあり、`--message-grace-ms N` で診断時に変更できる（宛先アプリで復元後の内容が貼られる場合の調整用）。
- **既知の限界**: タイムアウトでメッセージが届いたか不明な場合、対象が後からメッセージを処理して、予備経路の Ctrl+V と二重に貼り付く可能性がある。切り分ける手段がないため仕様として許容した。
- **意味論の維持**: 前景の有無・自プロセス判定（「入力先のウィンドウが見つかりません」）、修飾キー待ち（「修飾キーが押されたままです」）、形式ごとの退避・復元（M6）、貼り付け中に別のコピーがあれば復元しない、既存のエラー文言はそのまま。強制した `paste-message` が失敗した場合のみ「貼り付けメッセージを送れませんでした」を返す（Auto では予備経路へ落ちるので出ない）。

### 投入結果と診断への経路の記録

- `InjectResult` に任意の `Route`（`paste-message` / `send-input`）を追加（既存の呼び出し・実装は無変更で互換）。
- `--inject-probe` は `strategy`、`paste_target`（宛先のクラス名と前景ウィンドウ自体か）、`paste_message`（Delivered / NoTarget / Failed）、`fallback=send-input`（落ちたとき）、`route`、`route_used` を出す。タイトルやクリップボード内容は出さない。
- 診断オプション: `--strategy paste-message|send-input`（経路の強制。強制時はもう一方へ落ちない。不正値は終了コード 2）、`--message-grace-ms N`（paste-message の復元前待ち。不正値は終了コード 2）。`--grace-ms N` は send-input の待ちを引き続き上書きする。

## 3. テスト（Windows ARM64、`dotnet test` 全件）

既存 388 件 + 新規 15 件 = **403 件合格、失敗 0、スキップ 0**。

新規（`PasteMessageTests`）:
- 経路選択: Auto は Delivered なら Ctrl+V を送らない／NoTarget・Failed なら send-input へ落ちる／paste-message 強制は落ちない／send-input 強制はメッセージを送らない／経路名の解析。
- 復元の待ち: paste-message 経路は grace が 5 秒でも待たずに復元し `Route` を返す／send-input 経路は grace を守る／失敗した paste-message は Failed と経路を返し元の内容を復元する。
- **実 `WM_PASTE`**: 別スレッドのメッセージループ上の本物の EDIT コントロールへ送り、文書に入ることと、猶予なしで元のクリップボードへ復元されることを確認。無効なウィンドウへの送信は未配送になる。
- 既存テスト 1 件（trace の順序）は `route` ステップが増えたことに合わせて期待値を更新。

## 4. 実測（Windows ARM64、キーリマッパ ON のまま、対話セッション）

検証用 Windows にはキーリマッパが常駐（フック ON、セッション 1）しており、**終始切っていない**。設定ファイルも変更していない。対話セッション（スケジュールタスク経由）で、空の一時ファイルを開いたメモ帳を前景にして、新しい publish（out-m8 の arm64）の `--inject-probe` を実行し、メモ帳の UIA スナップショットで文書内に投入文字列（`whisparrot-inject-probe-12345`）があるかを確認した。実行前にクリップボードへ目印の文字列を置き、実行後に戻っているかも見た。

| 実行 | 使った経路 | 結果 | 文書に投入文字列 | 実行後のクリップボード |
|---|---|---|---|---|
| 既定（Auto） | paste-message（`paste_message=Delivered`、宛先 `RichEditD2DPT`） | Success、`restored=True` | **あり** | 元の内容 |
| `--strategy paste-message` | paste-message | Success、`restored=True` | **あり** | 元の内容 |
| `--strategy send-input` | send-input | Success（キー送信は成功扱い） | **なし**（リマッパが Ctrl+V を貼り付け以外に変換） | 元の内容 |
| 既定（Auto）を 5 回反復 | 5 回とも paste-message | 5 回とも Success、`restored=True` | **5 回ともあり** | 5 回とも元の内容 |

合格条件（キーリマッパ ON のまま、新しい exe の `--inject-probe` で固定文字列がメモ帳の文書に入る）を満たした。**`send-input` を強制すると貼り付けにならないのは、この環境では期待どおり**（リマッパが Ctrl+V を再定義しているため）で、失敗扱いにしていない。キーボードフックを通る経路と通らない経路の差が実測で分かれた形であり、Auto がキー入力に頼らず主経路で成功することの裏づけになる。

補足: probe 自体は `Success` を返すが、send-input では「キー入力の送信」までしか確認できず貼り付け結果は見えない。これは従来から同じで、文書内容の確認は UIA スナップショットで行った。

## 5. publish（Windows ARM64、`build/publish.ps1 -Arch all -Backend cpu`）

Windows 上の新しいクローン（ブランチ feat/m8-paste-message）から実行。

| RID | exe サイズ |
|---|---|
| win-arm64 | 73,920,245 bytes（70.5 MiB） |
| win-x64 | 77,867,128 bytes（74.3 MiB） |

両 exe で `--clipboard-probe` が終了コード 0、`--strategy` と `--message-grace-ms` の不正値は終了コード 2。非対話セッションの `--inject-probe` は `foreground=none` で「入力先のウィンドウが見つかりません」の失敗（終了コード 1）となり、経路強制時も前景確認が先に働く。

## 6. 未検証・限界

- 実測したのはメモ帳（RichEdit 系）のみ。他のアプリ（ブラウザ、Electron、独自描画のコントロール）が WM_PASTE に応答するかは未検証。応答しないアプリでは、メッセージは Delivered になるが貼り付けにならない可能性があり、その場合は予備経路へ落ちない（落ちる条件は送信自体の失敗に限る）。診断は `--strategy` で切り分けられる。
- x64 は ARM64 上のエミュレーションで、テストは ARM64 のみ。x64 は publish と起動（診断プローブの引数検証）まで。
- 実測の確認に使ったメモ帳のタブ（一時ファイル）は閉じていない。
