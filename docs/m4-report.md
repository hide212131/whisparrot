# M4 報告: HTTPS プロキシ対応（T08 拡張）

ブランチ: `feat/m4-https-proxy`（`feat/m3-cleanup` から分岐。マージ・タグ・リリースは未実施）

## 1. 対応内容

| 項目 | 内容 |
| --- | --- |
| プロキシ URL の検証 | `ProxyUrlRules`（設定保存時）と `ProxyTarget.Parse`（実行時）が `http://` に加えて `https://host[:port]` を受け付ける。パス・クエリー・フラグメント・`socks5://` / `ftp://` などは従来どおり拒否。手動 URL の userinfo 拒否も従来どおり |
| 接続 | `SocketsHttpHandler` の標準機能で、プロキシへ TLS 接続したうえで CONNECT する。自前の TLS 実装は追加していない。環境変数モード・手動モードの両方で同じ経路を通る |
| 証明書検証 | OS の信頼ストアのみ。ホスト名の一致も標準の検証に任せる。検証を無効化・緩和する設定や公開 API は作っていない。テスト専用の信頼の入口は既存の `internal` コンストラクター（`tlsTestHook`）のままで、本番の設定・公開 API から到達できない |
| Basic 認証 | CONNECT の `Proxy-Authorization` として TLS の内側でのみ送る。証明書が信頼できない場合は握手で失敗するため、資格情報は送られない（テストで確認）。平文 http プロキシの挙動は変更なし |
| 失敗時 | 証明書の不信・ホスト名不一致・到達不能・認証失敗はすべて `HttpRequestException`。直結へは落ちない |
| 設定ウィンドウ | 案内文を「http://host:port または https://host:port（https:// は TLS で接続し、証明書は OS の信頼ストアで検証）」へ変更 |

## 2. fixture で実測したケース

fixture: ループバックの CONNECT プロキシを TLS で包んだもの（`TestConnectProxy` に証明書を渡すと HTTPS プロキシになる）と、テスト用 CA（`TestCa`）。検証コールバックはテスト用 CA を信頼の起点にし、**ホスト名の不一致は拒否する**（実物と同じ判定）。対象サーバーは M2 と同じ自己署名の TLS サーバー。

| ケース | 結果 |
| --- | --- |
| 手動モード: https プロキシ経由でモデル取得経路へ到達 | 成功。CONNECT 先が対象ホスト:443、TLS 握手の失敗 0 |
| 環境変数モード（`HTTPS_PROXY=https://...`）で同上 | 成功 |
| 整文経路（Cleanup, POST）で同上 | 成功 |
| 認証あり・正しい資格情報 | 成功。Basic の値は TLS 終端後の CONNECT にのみ現れる |
| 認証あり・誤った資格情報 | 失敗（`HttpRequestException`）。対象サーバーへの要求 0 |
| 信頼できない証明書（別 CA の署名）かつ認証あり | 失敗。プロキシに CONNECT が 0 件（資格情報は送られない）、対象への要求 0 |
| 自己署名でテスト用 CA 発行ではない証明書 | 失敗。CONNECT 0 件、対象への要求 0、直結に落ちない |
| ホスト名が証明書と一致しない（SAN が別名のみ） | 失敗。CONNECT 0 件、対象への要求 0 |
| テスト用の信頼を与えない既定の検証 | 失敗（OS の信頼ストアにない証明書を拒否）。CONNECT 0 件 |
| 到達不能な https プロキシ | 接続エラー。対象への要求 0 |
| https プロキシ設定下でもループバック宛ては直結、`NO_PROXY` 一致は直結 | 変更なし（プロキシへの接続 0 件） |
| URL 検証 | `https://proxy.local[:port]` と userinfo 付きは正規化して受理（スキームを保持、userinfo は資格情報へ分離）。`https://...` のパス付き・クエリー付き、`ftp://`、`socks5://` は拒否 |

既存テストへの影響: `CompatiblePolisherTests.An_unusable_proxy_configuration_falls_back_and_never_goes_direct` は「`https://` が不正」という旧仕様に依存していたため、不正の例を `socks5://` に変えた（意図は同じ。不正なプロキシ設定で黙って直結しないこと）。

自動テスト: **357 件合格、失敗 0**（M3 時点 341 件 + 新規 16 件。ARM64 VM、`dotnet test` 全件）。http プロキシ・プロキシなし・`NO_PROXY` の既存テストは変更なしで通る。

## 3. 未検証として残る範囲

- 実在の HTTPS プロキシ製品（Squid 等）との相性。この VM にはない。
- OS の信頼ストアに入れた実証明書（社内 CA 等）での実機確認。fixture はテスト専用の入口で CA を信頼させており、「OS ストアの証明書で成功する」経路そのものは未実測。逆方向（ストアにない証明書の拒否）は既定の検証で実測済み。
- 実 exe を HTTPS プロキシ経由で動かす E2E（上記の理由で、テスト用証明書を実 exe に信頼させる手段を本番に作らない方針のため）。
- NTLM / Kerberos / Digest 認証、PAC、システム proxy（引き続き範囲外）。
- プロキシ越しの実 HF 取得。

## 4. publish（Windows ARM64 VM、`build/publish.ps1 -Arch all`、cpu）

| アーキ | exe サイズ |
| --- | --- |
| win-arm64 | 73,914,535 bytes（70.5 MiB） |
| win-x64 | 77,859,795 bytes（74.3 MiB） |

## 5. 作業上の注記

VM の既存クローン（`dev\whisparrot`）は古いブランチで未コミットの変更を持っていたため、破棄せず別ディレクトリに新規クローンして検証・publish した。
