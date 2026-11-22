# W-1〜W-2 実装レポート（Hane 連携の最小スライス）

対象: [design-hane-integration.md](design-hane-integration.md) §2（接続方式）、§3（Hane API）、§4.1（取得と文脈の組み立て）、§4.5（状態遷移とフォールバック）、§4.6（設定と診断）、§6 の W-1〜W-2。
ブランチ `feat/hane-link-w1w2`（`origin/main` から分岐）。マージ・タグ・リリース・push はしていない。

## 結論

- W-1（クライアント・設定・読取 probe）と W-2（コーディネーター接続）を実装した。Hane 側（H-1〜H-4）は未着手の前提なので、プロトコルを実装したモックのパイプサーバーをテストプロジェクト内に置き、実際の名前付きパイプ越しに検証した。**実機の Hane との結合は未実施**。
- テスト: Windows ARM64 で **887 件すべて合格**（既存 813 + 新規 74）。Hane 関連の新規テストを 5 回連続で流して不安定さがないことも確認した。
- 設計から逸脱する箇所はない。設計が決めていない項目（ワイヤのフィールド名、保持テキストの期限など）は本実装で選んだ値として下表に列挙した。**Hane 側（H-1〜H-4）と突き合わせる必要があるのは「プロトコルの仮定」の節**。

## 実装した内容

### W-1

| 項目 | 内容 | 場所 |
| --- | --- | --- |
| 記述子の読取 | 録音開始時の窓の PID の `<pid>.json` だけを読む。ファイル形式・パイプ名の固定形式（`Hane.Whisparrot.<pid>.<instanceId>`）・トークン形式を検証し、ディレクトリ／ファイルが再解析ポイントなら拒否。PID とプロセス作成時刻が実プロセスと一致しなければ無効（残骸・PID 再利用）。サイズ上限 16 KiB | `Infrastructure/Hane/HaneDescriptor.cs` |
| パイプ接続・認証・版交渉 | 偽装権限なし（Anonymous）で接続 → `GetNamedPipeServerProcessId` を記述子の PID と照合 → 一致した場合だけ `hello` でトークンと版範囲を送る。major の範囲が重ならなければトークンを送る前に連携不可。合意版と `instanceId`（記述子と一致必須）・機能フラグを受け取る | `HaneConnection.cs` |
| ワイヤ | `u32 LE の長さ + UTF-8 JSON`、フレーム最大 256 KiB、編集テキスト最大 32 KiB（送る前に検証）。未知の任意フィールドは読み飛ばす。エラーコードは固定の識別子形式だけを通し、本文・例外文字列は持ち込まない | `HaneWire.cs`, `HaneJson.cs` |
| 文脈取得 | `context.get`（前 1,200・後 200 文字、整文確定待ちの範囲ハンドル最大 2 件）。結果は `HaneContext`（本文を含むため `ToString` は大きさだけ） | `HaneClient.cs` |
| 設定 | `AppSettings.Hane`（`Enabled` 既定 OFF、`InsertRawFirst` 既定 ON）。`config.json` の `hane.enabled` / `hane.insert_raw_first`。既定のときは書き出さない（旧版が未知キーとして拒否しないため）。設定画面に「Hane 連携」の群を追加 | `AppSettings.cs`, `SettingsJson.cs`, `SettingsWindow.cs` |
| 読取 probe | `--hane-probe [--countdown 秒] [--context]`。前景 → 記述子 → 接続・認証 → 版・機能・処理時間。`--context` で `context.get` を一度だけ。出力は可否・大きさ・エラーコードに限定（本文・タイトル・トークンは出さない）。編集・認識・外部送信はしない | `DiagnosticsHane.cs` |
| 録音開始時の取得 | `BeginRecording` でマイク開始後に非同期で取得。予算 150 ms（接続・認証・前後取得の合計）。録音開始は待たせない | `SessionCoordinator.Hane.cs` |

投入先は `TextInjector.RecordedTarget` が `IWindowTarget`（前景のトップレベル HWND と PID）を併せて公開する形にした。連携は窓と PID だけで相手を選び、パス・タイトル・実行ファイル名は使わない。

### W-2（設計 §4.5 の表との対応）

| 設計の状態・結果 | 実装 |
| --- | --- |
| `Recording → Recognizing` | 録音開始時の窓と設定を保持。認識が空・失敗・取消なら編集しない（既存どおり） |
| 認識成功、連携が未成立 | 文脈が届いていなければ従来経路（整文 → 録音開始時の投入先へ一回投入）。連携 OFF・Hane 不在・非 Markdown・版非対応・予算切れ・窓でない投入先はすべてここ。通知は出さない |
| 文脈あり、先行挿入 ON | 新状態 `InsertingRaw` で原文を `edit.insert`。範囲ハンドルを受けた時点で録音スロットを解放（`Idle`）し、整文は別ジョブとして続ける。整文 OFF なら原文で完了。整文が未構成なら「整文の設定が未完了のため、原文のままです」を通知 |
| 文脈あり、先行挿入 OFF | 整文（失敗時は原文）の後、同じ文脈（対象・版・選択）で `edit.insert` を一度だけ |
| 挿入の明示的拒否 | 未適用が確かな拒否は `Failed`（未投入の一件を保持、「入力を再試行」＝利用者の明示操作で従来入力、取消で破棄）。自動では従来入力へ切り替えない |
| 挿入応答の喪失・タイムアウト | 初回 250 ms。応答が不明なら同じ Hane（同じ `instanceId`）へ同じ操作 ID・期限・内容で一度だけ再送（追加 500 ms）。`IN_PROGRESS` はこの予算内で照会し直す。なお不明なら「入力結果を確認できません。Hane の本文を確認してください」を通知し、`Idle` に戻して原文を保持（コピー・破棄だけ。再試行ボタン・自動投入へは渡さない） |
| `PolishingInserted → Replacing` | 整文ジョブ（既定 15 秒）。完了した非空の異なる本文だけを `edit.replace` で差し替え。整文の失敗・時間切れは原文維持で「整文できなかったため原文を残しました」 |
| 差し替えの拒否・通信不明 | 拒否・失効は「整文結果は反映されませんでした」、応答喪失は同じ ID で一度照会し、なお不明なら「整文の反映を確認できません」。どちらも再送・追加の貼り付け・原文を戻す編集はせず、整文結果を保持してコピー・破棄だけを提供（トレイ） |
| 取消 | 認識完了前・先行挿入 OFF の整文中は従来どおり編集しない。編集要求の発行済み（`InsertingRaw`／`Inserting`）は取消不可。原文確定後（`Idle`）の取消は、整文ジョブだけを取り消す（送出済みの差し替えは取り消さない） |
| 先行挿入後の次の録音 | 録音スロット解放後すぐ次の録音を始められる。ジョブ同時 2 件（満杯時は原文を優先し「整文の処理が混み合っているため、原文のままにしました」）。同じ文書に録音・認識中のセッションがある間は差し替えの送出を保留し、そのセッションの挿入確定後（挿入なしで終了した場合はその時点）に送る。文書が未確定の取得中も同じ文書として待つ。保留でハンドル期限を過ぎたら原文を維持。録音開始と差し替え送出はループスレッド内で直列化し、送出済みの差し替えの確定待ちは次の文脈取得の 150 ms の予算内に収める。次の文脈要求には整文確定待ちのハンドル（最大 2 件）を載せる |

## モック結合の検証（要求された 4 点）

モック（`tests/Whisparrot.Tests/Hane/MockHaneServer.cs`、製品コードには含まれない）は、記述子の公開・トークン認証・版交渉・`context.get` / `edit.insert` / `edit.replace`・操作 ID による再送の識別（`IN_PROGRESS`・`OPERATION_CONFLICT`・記録済み結果の返却）・範囲の追跡（範囲内部・重なりは `RANGE_CHANGED`、先頭への挿入は右へ移動、末尾への挿入は範囲外）・保持した原文との完全一致の確認を、UTF-8 バイト座標の小さな文書モデルの上で行う。応答喪失・要求喪失・処理中・拒否を注入できる。クライアントとコーディネーターは実際の名前付きパイプ越しに接続する。

| 要求された確認 | テスト（`HaneCoordinatorTests` ほか） |
| --- | --- |
| 認証・文脈取得 → 原文の即時挿入 → 差し替え成功 | `Raw_text_is_inserted_at_once_and_replaced_later_by_the_polished_text`。整文をゲートで止めたまま、原文が文書に入り録音スロットが解放済みであることを確認してから整文を通し、差し替わる。フレーム順は `hello, context.get, hello, edit.insert, hello, edit.replace`、従来の貼り付けは 0 回、通知なし |
| 範囲変更時の差し替え拒否で重複投入にならない | `A_range_edited_while_polishing_is_not_replaced_and_nothing_is_inserted_twice`。範囲内を編集 → 差し替えは `RANGE_CHANGED` で拒否 → 本文は利用者の編集のまま、整文結果は文書に現れない、`edit.replace` は 1 回（再試行なし）、貼り付けなし、整文結果を保持して通知 |
| 応答喪失時に同じ操作 ID で再照会 | クライアント: `A_lost_insert_response_is_re_asked_with_the_same_operation_id_and_the_text_lands_once`（操作 ID と `params` が完全一致、文書への挿入は一度）、`A_lost_replace_response_…`、要求自体が届かなかった場合・`IN_PROGRESS` の待ち・再照会も無応答なら不明。コーディネーター: 挿入・差し替えの応答喪失 |
| 連携 OFF・Hane 不在では従来経路 | `With_the_integration_off_…`（Hane へのフレーム 0、録音開始時の投入先で貼る）、`When_hane_is_not_running_…`（記述子なし。通知なし）、非 Markdown（`UNSUPPORTED_DOCUMENT`）、窓でない投入先、リンク自体なし |

そのほか: 認証（誤トークン拒否・更新後の取り込み）、残骸記述子、パイプ名の形式、再解析ポイント、別プロセスが提供するパイプへトークンを送らない（サーバー PID 照合）、major 不一致、32 KiB 超をローカルで拒否、挿入後に別インスタンスになっていれば送らない、カーソル移動で `STALE_CURSOR`（自動投入せず `Failed` に保持、明示再試行で従来入力）、先行挿入 OFF、整文失敗、結果不明の保持テキスト、同じ文書への保留と順序、ハンドル失効、ジョブ上限、文脈の予算切れ、設定の往復と検証、本文が `ToString` に出ないこと。

### テスト件数（Windows ARM64）

| | 件数 |
| --- | ---: |
| 既存（起点 `origin/main`） | 813 |
| 新規: `HaneClientTests`（プロトコル・実パイプ） | 34 |
| 新規: `HaneCoordinatorTests`（モック結合） | 20 |
| 新規: `HaneSchedulingTests`（台本クライアントでタイミング固定） | 10 |
| 新規: `HaneSettingsTests` | 10 |
| **合計** | **887（すべて合格、失敗 0、スキップ 0）** |

## プロトコルの仮定（Hane 側 H-1〜H-4 と突き合わせる）

設計書は構造とエラーの意味を定めているが、JSON のフィールド名は未定。W-1 では設計の用語（camelCase）に沿って次を仮定した。**Hane 側が違う名前・型で実装した場合は、クライアントの該当箇所（`HaneDescriptor.cs` / `HaneConnection.cs` / `HaneClient.cs` の解釈部）だけを直せばよい**ように、解釈は 3 ファイルに閉じている。

- 記述子: `{ pid, processCreationTime（FILETIME 100 ns の十進文字列または数値）, instanceId, pipeName, token, protocol: { min: {major, minor}, max: {major, minor} } }`。作成時刻の形式（FILETIME）と、完全一致で照合する点は特に確認が要る。
- `hello` 要求: `{ token, client: "whisparrot", protocol: { min, max } }`。応答 `result`: `{ protocol: {major, minor}（`version` も可）, instanceId, features: [] }`。認証拒否は `error`（固定コード）後に切断、または即時切断のどちらでも「認証拒否」に分類する。
- フレーム: `{ id: 整数, method, params }` / `{ id, result }` / `{ id, error: { code, effect } }`。`effect` は `not_applied` だけを「未適用が確か」とし、欠落・未知の値は不明として扱う。
- `context.get`: 要求 `{ hwnd（数値）, beforeChars, afterChars, includeSnapshot, pendingRangeHandles }`。応答 `{ documentId, revision（十進文字列）, cursorByte, selection: {anchorByte, activeByte}, title, isUntitled, frontMatter: {status, text}, before, after, beforeTruncated, afterTruncated, beforeStartByte, afterStartByte, pendingRanges: [{rangeHandle, tracked, startByte, endByte}] }`。
- `edit.insert`: 要求 `{ operationId（128 ビットの小文字 16 進）, deadlineTick（GetTickCount64 の十進文字列）, hwnd, documentId, expectedRevision, expectedSelection, text }`。応答 `{ documentId, insertedRevision, range: {startByte, endByte}, rangeHandle, expectedTextHash, expiresInMs }`。
- `edit.replace`: 要求 `{ operationId, deadlineTick, hwnd, documentId, rangeHandle, insertedRevision, expectedTextHash, replacement }`（`expectedTextHash` は挿入応答の値をそのまま返す）。応答 `{ documentId, revision, range, undoMode }`。
- 再送: 同じ `operationId`・`deadlineTick`・`params` の内容で、フレームの `id` だけを変えて送る。期限は変えない（設計「同じ操作 ID・期限・ペイロード」のとおり）。そのため再送が期限後に着いた場合、Hane が「完了済みなら記録した結果、未着手なら `DEADLINE_EXCEEDED`（未適用）、処理中なら `IN_PROGRESS`」を返すことを前提にしている（設計 §3.4 の記述どおりなら成立する）。

## 設計が定めていない箇所で選んだ値・解釈

| 項目 | 選択 | 理由 |
| --- | --- | --- |
| 接続の持ち方 | 操作ごとに接続・認証して閉じる（文脈取得・挿入・差し替えは別の接続） | 設計は「同時に処理する要求は一件」「必要なら接続を分ける」。長い整文の間に接続を持ち続けると、トークン更新・Hane 再起動で状態が古くなる。接続の確立は 1 往復で、実測しやすい。`instanceId` の同一性は各操作で検証する |
| 文脈取得後の従来経路への切替 | 拒否された挿入は自動では従来入力に切り替えず `Failed`（利用者の明示再試行が従来入力） | §4.5 の「すでに Hane の文脈を取れた場合、従来入力に切り替えるには対象と選択の再確認を要する」を、利用者の明示操作を再確認とみなして実装 |
| 保持テキストの期限 | 10 分で自動解放（`SessionCoordinator.HeldTtl`） | 設計は「期限切れで解放」とのみ定める |
| 状態の表現 | 新しいセッション状態は `InsertingRaw` のみ。`PolishingInserted` / `Replacing` は録音スロットとは独立のジョブ段階（整文中・保留中・送出中） | 設計 §4.5 末尾の「録音スロットを解放し、整文は別ジョブ」に合わせた |
| 機能フラグ | 診断表示にのみ使い、動作の分岐には使わない | フラグ名が未定 |
| `hello` 中の切断 | 「認証拒否」に分類 | 設計は認証異常で接続を閉じるとしている |
| 記述子の所有者確認 | クライアント側では行わない | 設計の保護は作成時の明示 DACL（Hane 側）。クライアント側の所有者検査は、昇格プロセスの所有者（管理者グループ）で誤って拒否し得る |
| 起動時のウォームアップ | `HaneClient.WarmUp()`（連携 ON のときだけ。使い捨てのパイプ内で 1 往復） | 下の発見事項のとおり |

## 実装中の発見

- **初回の文脈取得が 150 ms の予算を超える**: プロセス内で最初の Hane 通信はパイプ I/O と JSON の JIT を伴い、予算を超えて Timeout になる（その発話は設計どおり従来経路に倒れた）。起動時（連携が ON のとき、および設定で ON にしたとき）に使い捨てのパイプで一往復するウォームアップを足した。実機・ARM64 での初回遅延の実測は W-6 の対象。
- 既定の `NamedPipeServerStream` は緩衝なしで、書き手は読み手が現れるまで書き込みが完了しない。ウォームアップの最初の版は同一スレッドで書いてから読んだためデッドロックした（読み手を先に待たせて修正）。実際のクライアント・サーバー（別接続・別スレッド）では起きない。
- 全体的に、`context.get` の応答を処理する前に認識が終わる（最速のテスト用フェイクのとき）と、そのセッションは従来経路に倒れる。設計どおりの動作で、テストでは取得を待ってから停止している。

## 未検証・未実装

- **実機の Hane との結合**（H-1〜H-4 は別セッションで実装中）。明示 DACL・`PIPE_REJECT_REMOTE_CLIENTS` はサーバー側の責務で、クライアントからは検証していない。IME・閲覧モード・未保存文書・タブ／窓／インスタンスの切替・絵文字や結合文字を含む Hane 側の編集・Undo の確認（一回の Undo で発話全体が戻るか等）は W-6 の対象。モックは UTF-8 バイト座標・絵文字の前後取得までは確認している。
- `--hane-probe` の実機出力: SSH の非対話セッションでは前景ウィンドウがなく `foreground=none` で終了することだけを確認した。接続・認証・版交渉・`--context` の経路は、同じクライアントコードを実パイプのテストで確認している。対話セッションでの Hane 実機に対する実行は未実施。
- 設定画面の新しい群は、ビルドと設定の往復テストまでで、画面の目視確認はしていない（対話セッションがないため）。
- 範囲外（W-3 以降）: 文脈つき整文、用語抽出、認識ヒント、要約、文書の抜粋・用語の外部送信設定。W-2 の整文は現行の整文（発話のみを渡す）の結果を差し替えに使う。`context.get` が返す前後の抜粋・Front Matter・追跡範囲（`pendingRanges`）はメモリ上に保持するが、整文には渡していない。整文確定待ちの範囲を抜粋から外す処理も W-3。
- 本文・文書テキスト・要約・トークンはログ・診断・通知に出していない（`HaneContext` / `HeldText` / `HaneInserted` / 記述子の `ToString` は本文・トークンを含まない。診断はコードと大きさだけ）。

## 次にやること

1. Hane 側（H-1〜H-4）の実装が出そろったら、上の「プロトコルの仮定」を突き合わせ、フィールド名・型の差をクライアントの解釈部で吸収する。
2. 対話セッションで `--hane-probe --context`、未保存タブへの挿入と差し替え、一回の Undo を実機で確認する（設計 §6 の最小 end-to-end スライス）。
3. W-3（文脈つき整文）。文脈を整文へ渡す配線は `SessionCoordinator.Hane.cs` の `RunJobPolish` と `StartPolish` の二か所（`PolishSetup.Ready` の入力型の変更を含む）。
