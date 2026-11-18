# T13 レポート: ChatGPT 整文 adapter（公式 Sign in with ChatGPT の直接 OAuth + Responses API）

- 対象: [design-v1.0.md](design-v1.0.md) の T13、§6（設定の整文領域）、§7（D-1(b)）、§13
- ブランチ: `feat/t13-chatgpt-adapter`（origin/main の `4b467db` から分岐。マージ・タグ・リリースはしていない）
- 方式: D-1 は本人指示で (b) 公式 Sign in with ChatGPT の直接 OAuth に確定済み（三案比較のスパイクは省略）。他アプリの client ID・token、Web セッション、非公開の backend-api は使わない。
- 作業環境: macOS で編集し、Windows の新規クローンへ変更ファイルを反映してビルド・試験・publish した（検証は Windows ARM64、x64 は ARM64 上のエミュレーション）。

## 0. 結論

| 区分 | 内容 |
| --- | --- |
| 動いた（実測） | 既存テスト 388 件 + 新規 71 件 = **459 件合格、失敗 0**（Windows ARM64）。実 exe（ARM64 / x64）が、ループバックのモック（認可コールバックの疑似 GET、token エンドポイント、Responses エンドポイント）に対して、ログイン・token 交換・暗号化保存・更新・ログアウト・Responses 整文・各種フォールバックを **E2E 30 項目すべて合格**（両 arch）。M3 の互換エンドポイント E2E（36 項目）と設定ウィンドウ probe は変更後も両 arch で合格（互換整文と OFF の挙動は不変） |
| **未検証（本人の操作が必要）** | **実アカウントでのログインと、実 Responses API での整文は一切行っていない。** 実際の OpenAI 側が (a) 動的クライアント ID を返すか、(b) `chatgpt.tokens.use.direct` を付与するか、(c) 公開 Responses API がその token を受け付けるか、(d) 実際に使えるモデル名は何か、は未確認。手順は §5 |
| 留保 | ブラウザでのサインイン通信そのものは、アプリのプロキシ設定の制御外（§6、設計 U-02） |

## 1. 実装の構成

| 単位 | 役割 |
| --- | --- |
| `Infrastructure/ChatGpt/ChatGptEndpoints.cs` | 接続先の定数（`Official`）。認可 `https://auth.openai.com/api/accounts/authorize`、token `…/oauth/token`、`https://api.openai.com/v1/responses`、resource `https://api.openai.com/v1`、リダイレクト `127.0.0.1:1455`。設定からは変えられない。試験・診断だけがモックへ差し替える |
| `Infrastructure/ChatGpt/ChatGptTokenStore.cs` | 別保存域 `chatgpt-token.json`（exe の隣。パスは `AppPaths.ChatGptTokenPath` の内部定数）。access / refresh token は `ISecretProtector`（DPAPI）で暗号化。発行済みクライアント ID・インストール UUID・期限・scope は平文。一時ファイル → 置換で書く。壊れた／別ユーザーの暗号文は**消さず** Unreadable と報告。`ChatGptTokens.ToString()` は秘密を出さない |
| `Infrastructure/ChatGpt/ChatGptAuthService.cs` | 認可 URL の生成（PKCE S256・state・nonce）、ループバックのコールバック待受、手動貼り付け、token 交換・更新・ログアウト、整文用 access token の取得（期限の 3 分前から更新。同時要求は一度の更新を共有）、接続状態。HTTP は `NetworkContext`（Cleanup）経由のみ（プロキシ設定に従い、自動リダイレクトは追わない） |
| `Infrastructure/ChatGpt/ChatGptPolisher.cs` | `POST /v1/responses`。固定整文指示は `instructions`、認識テキストは `input`、`stream:false`・`store:false`。他の任意パラメーターは送らない。応答は `status=="completed"` の最初の `message` の `output_text` だけを成功とする |
| `Infrastructure/Polish/SettingsTextPolisher.cs` | `provider=chatgpt` の構え。未ログイン・token 無効・scope 不足・モデル未指定は `Unconfigured`（理由つき・通信なし・ブラウザを開かない）、使えれば `Ready`（15 秒の期限） |
| `App/Views/SettingsWindow.cs` | 整文の排他選択に ChatGPT を追加（旧「未対応（T13）」の無効化を解除）。選ぶと同じ領域に接続状態・ログイン / ログアウト・モデル名・手動貼り付け欄を表示 |
| `App/DiagnosticsT13.cs` | 非対話診断 `--chatgpt-login` / `--chatgpt-status` / `--chatgpt-logout`、および `--cleanup-probe` の `--chatgpt-token-file` / `--chatgpt-mock`（モック E2E 用。token の値は出さない） |

config.json は **schema_version=1 のまま**で、キーの追加もない（`cleanup.chatgpt.model` と `provider: "chatgpt"` は初版から schema にあった）。token 類は config.json に入らず、試験で確認している。

### 認可・交換の中身（モックで実測した形）

- 認可 URL のパラメーターは、`client_id=dynamic_agent_client`、`agent_name_hint=whisparrot`、`ext_agent_host_id=urn:uuid:<インストール UUID>`、`response_type=code`、`redirect_uri=http://127.0.0.1:<port>/auth/callback`、`resource`、`scope=openid profile email offline_access resource.invoke chatgpt.tokens.use.direct`、`state`、`code_challenge`（S256）、`code_challenge_method`、`nonce` の 11 個ちょうど。
- コールバックの `client_id`（OpenAI が発行したもの）を保存し、token 交換・更新ではその ID を使う（`dynamic_agent_client` は認可 URL にだけ使う）。`code_verifier` は `code_challenge` と SHA-256 で一致することをテストで確認。
- 付与された scope に `chatgpt.tokens.use.direct` が無ければログイン失敗（何も保存しない）。更新後の scope 欠落は「再ログインが必要」扱い。
- コールバックは `state` が一致するものだけ受け付ける（他ソフトや別ページの先回りリクエストは 400 で、待機中のログインを壊さない）。`/auth/callback` 以外のパス・GET 以外のメソッドは 404 / 405。
- ポート 1455 が使用中なら即座に「別のログインが進行中の可能性があります」のエラー。ログイン完了・取消・タイムアウト（5 分）でポートを解放する。
- 手動貼り付け: リダイレクト後の URL 全体、または `code=…&state=…` のクエリを受ける。別ログインの URL（state 不一致）・無関係な文字列は拒否し、ログインは続行できる。

### フォールバックの意味

どの失敗も例外ではなく `PolishResult.Fallback` で、既存の整文失敗セマンティクス（原文をちょうど一度投入、通知、自動リトライなし、15 秒の全体期限）に乗る。失敗の補足に token・コード・応答本文・URL は入れない。

| 状況 | 結果 |
| --- | --- |
| 未ログイン / token ファイルが壊れている・復号不能 / scope 不足 | `Unconfigured`（通信しない。設定を促す理由を保持） |
| 更新が 400 / 401 / 403 | `Auth`。「再ログインが必要」状態にするが、token ファイルは消さない。Responses は呼ばない |
| 更新が 5xx・接続失敗・時間切れ | `Http` / `Network` / `Timeout`（再ログイン状態にはしない） |
| Responses が 401 / 403 | `Auth`（再更新・再送はしない） |
| Responses が 429 / 5xx / 3xx | `Http`（リダイレクトは追わない） |
| 応答が未完了・失敗・拒否（refusal）・ツール呼出し・空白だけ・JSON でない・1 MiB 超 | `Incomplete` / `Malformed` / `Empty` / `TooLarge` |
| 不正なプロキシ設定 | 直結へ逃げず `Network` |
| 15 秒超 | `Timeout`（token 更新の時間も同じ 15 秒に含む） |
| 利用者の取消 | 失敗扱いにせず取消が伝わる |

## 2. 試験（Windows ARM64）

`dotnet test` 全件: **459 件合格、失敗 0、スキップ 0**（既存 388 + 新規 71。既存の「ChatGPT は未実装」試験 1 件は「未ログインなら送らず設定値を保つ」へ置換）。

新規 71 件（`ChatGptTests.cs`、ループバックのモック HTTP への実通信）の主な内容:

| 区分 | 確認内容 |
| --- | --- |
| 認可 URL | パラメーター 11 個の過不足なし、UUID 形式、公式の接続先定数 |
| コールバック・交換 | 発行済み client_id・PKCE verifier・redirect_uri・resource・form-urlencoded、DPAPI 暗号文（平文の token・コードがファイルに無い）、state 不一致の拒否、他パス / 他メソッド、拒否（`error=`）、client_id 欠落 |
| 手動貼り付け | URL / クエリ / 前後空白、ゴミ・別ログインの拒否後に続行、取消 |
| ポート | 使用中エラー、完了後の解放 |
| scope・拒否 | scope 欠落で保存なし、token エンドポイントの 400 / 401 / 500、refresh token 欠落 |
| 更新 | 期限の十分前は通信なし、3 分以内で更新して新しい対を保存、ローテーションなしで旧 refresh を保持、scope 欠落、拒否と一時失敗の区別（ファイルは残る）、並行呼び出しの更新は 1 回 |
| 保存域 | ログアウトで削除（無くても成功）、再ログインで UUID 安定、壊れたファイルを消さない、別ユーザーの暗号文を消さない、ToString に秘密なし |
| 整文 | リクエスト形（5 キーのみ・Bearer・`/v1/responses`）、期限間近の更新後に新 token、未ログイン・更新失敗では送らない、401 / 403 / 429 / 500 / 302、停止して期限で Timeout、利用者の取消の伝播、不正プロキシ、応答解析 19 ケース、1 MiB 超 |
| 構えと設定 | 選択モデルの使用、モデル未指定・未ログイン・scope 不足は Unconfigured（通信 0）、OFF / 互換はログインに触れない、schema 1 のまま保存・往復 |

## 3. モック E2E の実測（実 exe ↔ ループバックのモック）

`build/t13-chatgpt-e2e.ps1`（モック: `build/mock-chatgpt-server.ps1`）。実 exe の `SettingsTextPolisher` / `SessionCoordinator` / `NetworkContext` / `ChatGptAuthService` を通し、認可コールバックはスクリプトがモックのリダイレクト先へ送る疑似 GET（または貼り付け）。実サービス・ブラウザ・アカウントは使っていない。モックの発行値（token）は出力・ログに出さず、一致 / 不一致だけを記録する。

結果: **ARM64 30 / 30、x64 30 / 30 合格**（`RESULT: ALL PASS`）。

| 区分 | 確認した内容（すべて合格） |
| --- | --- |
| 未ログイン | `provider=chatgpt` で原文を 1 回投入、モックへの要求 0、ログインを促す理由が表示 |
| ログイン | 認可 URL のパラメーター、偽造 state の 400（待機は継続）、正規のコールバック → 交換 → `LoggedIn`、`authorization_code` の POST（発行済み client_id・verifier・redirect_uri・resource・form-urlencoded）、`ext_agent_host_id` が保存された UUID と一致 |
| 保存 | 暗号文（平文の token・コードなし）、client_id 保持、config.json に token なし、status 出力に token 値なし |
| 整文 | Responses の整文結果を 1 回だけ投入、Polishing 経由、保存された access token が Bearer として一致、本文は model + 固定指示 + input（原文そのまま）+ `stream:false` + `store:false` の 5 キー |
| 更新 | 期限が 3 分以内の token → `refresh_token` 付与（発行済み client_id・旧 refresh token が一致・resource）→ 新しい token で Responses → 新しい対が保存され、次回は更新なしで認証される |
| 更新失敗 | `invalid_grant`（400）→ 原文 1 回・警告・Responses 未呼び出し・ファイル残存。503 → 原文 1 回 |
| 手動貼り付け | 別ログインの URL を拒否して待機継続 → 正しい URL で `login=OK` |
| 失敗系 | scope 欠落（保存なし）、認可拒否（交換呼び出しなし）、ポート使用中（即時エラーと「別のログインが進行中」） |
| Responses の失敗 | 500・空・壊れた応答・古い access token の 401 のいずれも原文を 1 回投入し、リトライなし。期限は実測 ARM64 15.02 s（x64 は別走行でも合格） |
| ログアウト | ファイル削除、status が LoggedOut、以後 `provider=chatgpt` は原文投入・要求 0 |
| OFF | 要求 0、`polish_setup=Off` |

補足: 設定ウィンドウ probe（`--settings-window-probe`）に ChatGPT の確認を追加し、両 arch で合格（ChatGPT 選択前はパネル非表示 → 選択で表示、未ログインの表示・ログアウト無効・ログイン試行なし、プロバイダとトリム済みモデルの保存、互換設定の維持）。M3 の互換整文 E2E は「未実装」だった ChatGPT の項目を「未ログイン」へ更新し、ARM64 で全項目合格（互換整文と OFF の挙動に変化なし）。

## 4. publish

`build/publish.ps1 -Arch all`（cpu）、出力 `…\out-t13\`（リポジトリ外）。

| RID | exe サイズ |
| --- | --- |
| win-arm64 | 73,940,211 bytes（70.5 MiB） |
| win-x64 | 77,887,161 bytes（74.3 MiB） |

M7 比で +約 22 KB（ARM64、+21,815 bytes）/ +約 23 KB（x64、+22,871 bytes）。ChatGPT 用の runtime や追加依存はない。

## 5. 未検証と、本人が行う実地確認の手順

実アカウントでのログイン・整文は、アカウント所有者の操作が要るため行っていない。以下を **Windows の対話デスクトップ**で実施してほしい。

1. 公開済みでない版を使うため、`out-t13\win-<arch>\whisparrot.exe` を任意のフォルダへ置いて起動する（token と config.json は exe の隣に作られる）。
2. トレイの「設定」→「整文」で **ChatGPT** を選ぶ。ここで勝手にブラウザは開かず、「未ログイン」の表示とログインボタンが出ることを確認する。
3. 「ログイン」を押す → 既定のブラウザで ChatGPT のサインイン / 認可画面が開く → サインインして許可する。
4. 自動で戻れば表示が「ログイン済み」になる。戻らない場合は、アドレス欄の URL（`http://127.0.0.1:1455/auth/callback?...`）全体を設定画面の貼り付け欄へ貼って「貼り付けた URL で続行」を押す。ポート 1455 を使う他のアプリがある場合は「別のログインが進行中」と出る。
5. **「モデル名」に、そのアカウントで公開 Responses API から使えるモデル名を入力**して保存する（既定のモデル名は置いていない。実際に使えるモデルは本人が確認する）。
6. メモ帳などで Win+J（または既定のホットキー）で発話し、整文された文が入ることを確認する。
7. 次を観察して結果を知らせてほしい: (a) 手順 3 でエラーになる場合の画面の文言（アカウント / workspace の権限不足、公開 API の preview 条件など）、(b) 手順 4 で「scope が付与されなかった」と出ないか、(c) 整文が成功するか・失敗時にどの理由か（ポップアップの通知文）、(d) 数十分後に再発話して access token の更新が効くか、(e) 「ログアウト」でフォルダの `chatgpt-token.json` が消え、以後は原文が入ること。

実サービスで想定と違う可能性がある点（モックでは確認できない）:

- 公式の応答形式が、ここで想定した token 応答（`access_token` / `refresh_token` / `expires_in` / `scope`）・Responses 応答（`status=completed`・`output[].message.content[].output_text`）と異なる場合。その場合は整文が `Malformed` / `Incomplete` で原文復帰し、発話は失われないが整文されない。
- 動的クライアント ID を返さない・scope が付与されない場合は、ログイン失敗（「利用権限が付与されませんでした」等）として表示する。
- 公式の条件（対象アカウント・workspace の権限、preview の条件変更）は設計 U-12 のとおり配布時に再確認が必要。

## 6. 留保・設計判断

- **プロキシ（設計 U-02）**: token 交換・更新・Responses はすべて `NetworkContext`（Cleanup）経由でプロキシ設定に従うが、**ブラウザでのサインイン通信そのものはアプリの制御外**（ブラウザ自身のプロキシ設定に従う）。ブラウザからループバックの 1455 へのリダイレクトは直結。設定画面にもその旨を表示している。
- **インストール UUID**: token ファイルに保存し、再ログイン（ファイルがある間）で安定する。指示どおりログアウトでファイルを削除するため、**ログアウト後の次のログインでは新しい UUID** になる。ログアウトしても UUID を残したい場合は別ファイルが必要（要判断）。
- **モデル名の既定なし**: 実際に使えるモデル名を実サービスで確認できていないため、既定値を推測で置いていない。未指定なら `Unconfigured`（原文投入・設定を促す）。モデル一覧の取得（設計 §9 の付随通信）は実装していない。
- **401 の再試行なし**: Responses が 401 のときは更新して再送せず、そのまま原文復帰（指示の「401 は原文へ」に従う。次の発話で期限間近なら更新される）。
- **`store:false`**: 整文の応答を ChatGPT 側に保存させないために付けている。実サービスで拒否される場合は外す判断が要る。
- **診断フラグ**: `--chatgpt-login` などの診断はモックへの差し替え（`--mock`）を持つが、通常起動（トレイ常駐）は常に公式の接続先で、設定・環境変数からは差し替えられない。
- **Windows 側の確認範囲**: 設定ウィンドウの見た目・操作感・実ブラウザの起動、対話デスクトップでのピル表示は非対話セッション（SSH）のため未確認（M2・M3 と同じ制約）。コードのイベント結線と表示文言のみ probe で確認した。
