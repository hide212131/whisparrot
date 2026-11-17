# M5 報告: プロキシ統合認証（既定の資格情報）

ブランチ: `feat/m5-proxy-integrated-auth`（`origin/main` から分岐。マージ・タグ・リリースは未実施）

## 1. 背景

v0.1.0 の実環境試用で、モデル取得が 407（Proxy Authentication Required）で失敗した。そのプロキシはブラウザ・CLI からは資格情報なしで使えており、Windows のログオン資格情報による統合認証（NTLM / Negotiate）を OS の資格情報で自動応答していると推定される。従来のコードは「URL の userinfo」か「手動入力のユーザー名/パスワード」の 2 経路しか資格情報を持たず、どちらも無いと 407 に何も応答しなかった。

## 2. 実装内容

| 項目 | 内容 |
| --- | --- |
| 資格情報ソース | `CredentialCache.DefaultNetworkCredentials`（`ProxyTarget.WindowsLogon`）を追加。プロキシ宛て（`IWebProxy.Credentials`）にだけ使われ、宛先サーバーには渡らない。値は取り出せない（ユーザー名・パスワードとも空） |
| 環境変数モード | URL に userinfo があればそれを使用（従来どおり優先）。無ければ既定の資格情報。`NO_PROXY` 一致・loopback の直結は変更なし |
| 手動モード | 認証方式 `credentials`（従来）/ `windows` を排他で選択。`windows` ではユーザー名・パスワードを使わない。`credentials` で入力したユーザー名があれば従来どおり `NetworkCredential`、無ければ資格情報なし（暗黙に Windows へ切り替えない） |
| 設定ウィンドウ | 手動指定に「認証」の選択肢（ユーザー名とパスワード / Windows のログオン情報を使う）を追加。後者を選ぶとユーザー名・パスワード欄と「保存済みのパスワードを削除」を無効化。環境変数モードの説明文に挙動を追記 |
| 診断 | `RouteDescription.Auth`（`None` / `Credentials` / `WindowsLogon`）を追加し、モデル取得の診断出力に `auth=<種別>` を出す。資格情報・NTLM メッセージの中身はどこにも出さない |
| 失敗時 | 認証に応答できない・拒否された場合は `HttpRequestException`。直結へは落ちない（従来どおり）。Basic だけを提示するプロキシに既定の資格情報で応答することもない（§4） |
| 変更していないもの | 証明書検証、`HttpClient` の生成口（`NetworkContext` のみ）、リダイレクト規則 |

### 設定（config.json）

- 追加キー: `proxy.manual_auth`（`"credentials"` / `"windows"`）。キーが無ければ `credentials`。既存の config.json（新キー無し）はそのまま読める（テストで確認）。未知の値・型違いは `Invalid`、未来の `schema_version` は `UnsupportedSchema` で、どちらも非破壊のまま。
- **`schema_version` は 1 のまま**。根拠: 欠けたキーを既定値で補う読み込みなので後方互換の追加で足りる。移行処理が要らない。
- 書き出しは `windows` のときだけ `manual_auth` を出す（既定値は書かない）。理由: 旧版（M4 以前）は未知キーを `Invalid` として拒否するため、この機能を選ばない利用者の config.json に新キーを増やさないようにした。`windows` を選んだ config.json を旧版に戻すと旧版は読み込みを拒否する（ファイルは変更されない）。
- `ProxySettings` / `ProxyConfig` には末尾の省略可能な引数として追加したので、既存の呼び出し側は変更不要。`windows` のとき、保存済みパスワードの復号は行わない（復号できなくても通信は止まらない）。

## 3. 検証 fixture と実測（Windows 11 ARM64 VM、.NET 10.0.404、SSH の非対話セッション）

fixture: ループバックの CONNECT プロキシ（`TestConnectProxy`）に `IntegratedAuthScript` を差し込み、`Proxy-Authenticate: NTLM` / `Negotiate` / `Basic` で 407 を返す。形式の正しい Type 2 チャレンジを返し、クライアントが送った Type 1 / Type 3 の種別と、Type 3 の各欄の長さだけを記録する。**NTLM の暗号の正しさは検証していない**（Type 3 の中身は読まない。受け入れるかは fixture が決める）。メッセージの流れが証拠。

実測した流れ（環境変数モード、userinfo なし = 既定の資格情報。各行の結果は 200 でモデル取得経路の対象サーバーに到達）:

| チャレンジ | 観測した流れ |
| --- | --- |
| `NTLM` | 資格情報なし → `ntlm:type1` → Type 2 → `ntlm:type3` |
| `Negotiate` | 資格情報なし → `negotiate:type1[bare-ntlmssp]` → Type 2 → `negotiate:type3` |
| `Negotiate` と `NTLM` の併記 | クライアントは Negotiate を選び、上と同じ流れ |
| `Basic` のみ | 何も応答せず失敗（Basic の送信 0 件、対象への要求 0 件） |

- 手動モード `windows`（ユーザー名欄に値が残っていても）で NTLM が通る。Type 3 のユーザー名欄は残っていた値の長さではない。
- 環境変数モードで URL に userinfo（`alice:pw`）があると、Type 3 のユーザー名欄が `alice` の長さ（UTF-16 で 10 バイト）になり、既定の資格情報ではなく userinfo が使われる。
- 資格情報ソースが無い（手動 `credentials` でユーザー名なし）と Type 1 すら送らず 407 のまま失敗。
- Type 3 を fixture が拒否した場合は失敗し、対象サーバーへの要求は 0 件（直結に落ちない）。
- Negotiate のクライアントトークンは、この環境（宛先が IP リテラル、ドメイン非参加）では SPNEGO で包まれない素の NTLMSSP だった。fixture はクライアントの枠組みをそのまま返す（SPNEGO で来た場合に包んで返す実装も入れたが、**SPNEGO で包まれる経路は実測していない**）。Kerberos は未実測。
- ループバック宛ては、既定の資格情報を設定していてもプロキシに何も送らない（直結）。

### 重要な制約: 既定の資格情報はログオンセッションに依存する

同じ試験を、ログオンセッションを変えて 1 回ずつ実行して得た結果:

| 実行方法 | 既定の資格情報の Type 3 |
| --- | --- |
| SSH（非対話。パスワードを持たないセッション） | 送られる。ただし**匿名**（ユーザー名・NT 応答とも長さ 0）。fixture は受け入れるので 200 |
| スケジュールタスク（`/it`、ユーザー指定。セッション種別は未確認） | **Type 1 の後で Type 3 が作られず**、407 のまま `HttpRequestException`。Type 3 が作れないときも直結には落ちない |

つまり、fixture で確認できたのは「統合認証のチャレンジに対しクライアントが SSPI 経由で NTLM / Negotiate のメッセージを返す」ことと「応答できないときは失敗する」ことまで。**実ユーザーが対話デスクトップにログオンしている状態で、実プロキシが受け入れる Type 3（本人の資格情報）を作れるか**は、この環境では確かめられていない。`IntegratedAuthWireTests` の「既定の資格情報で Type 3 まで進む」試験は SSH セッションでの実行を前提にしている（クラスのコメントにも記載）。

## 4. 自動テスト

既存 357 件 + 新規 22 件、**合計 379 件合格、失敗 0**（Windows ARM64 VM、`dotnet test` 全件）。

新規 22 件の内訳:
- `IntegratedAuthRoutingTests`（7）: 資格情報ソースの選択（環境変数・手動・直結・None）、userinfo 優先、手動 `windows` はユーザー名欄を無視、`credentials` は従来どおり、`windows` はパスワード復号不要、既定の資格情報から秘密が取り出せないこと。
- `IntegratedAuthSettingsTests`（6）: キー無し config の読み込み、`windows` の往復、既定値は書き出さない、未知の値（3 通り）の拒否、`schema_version` 据え置きと未来スキーマの拒否。
- `IntegratedAuthWireTests`（9）: 上記 §3 の流れ（NTLM / 手動 `windows` / userinfo 優先 / Negotiate / 併記 / 資格情報ソースなし / Type 3 拒否 / Basic のみ / loopback）。

## 5. 未検証として残る範囲

- 実プロキシ（実環境で 407 を出したもの）に対する実機確認。fixture は実プロキシの代わりにはならない。**今回の問題が解消したかは、利用者の対話ログオン環境での再試行が必要**。
- 対話デスクトップのログオンセッションで、既定の資格情報が本人の資格情報として Type 3 に載ること（上記の制約）。
- Kerberos（ドメイン参加環境の Negotiate）。SPNEGO で包まれたトークンの経路。
- NTLM の暗号の正しさ（Type 3 の検証）。
- HTTPS プロキシ（TLS 越し）＋統合認証の組み合わせ（`https://` の経路は M4 で実測済みだが、統合認証との組合せは未実測）。
- 設定ウィンドウの実 UI での操作（ラジオ選択でユーザー名・パスワード欄が無効になる動作）。ビルドは通ったが、対話セッションでの確認は行っていない。
- PAC、システム proxy、Digest（引き続き範囲外）。

## 6. publish（Windows ARM64 VM、`build/publish.ps1 -Arch all`、cpu）

| アーキ | exe サイズ |
| --- | --- |
| win-arm64 | 73,915,722 bytes（70.5 MiB） |
| win-x64 | 77,861,027 bytes（74.3 MiB） |

## 7. 作業上の注記

- 検証は新規クローン（`dev\whisparrot-m5`）で行い、既存のクローンには触れていない。
- 検証中に作った一時的な試験（メッセージ流れの出力用）とスケジュールタスクは削除済み。
