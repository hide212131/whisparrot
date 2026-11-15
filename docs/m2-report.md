# M2 実装報告（T07〜T09）

- 対象: 設計書 [design-v1.0.md](design-v1.0.md) のタスク T07（設定の永続化）・T08（通信とプロキシ基盤）・T09（モデル管理）
- ブランチ: `feat/m2-settings-models`（`feat/m1-core` から作成。`feat/m1-core` と `main` へはマージしていない）
- 実施日: 2026-11-15
- 検証環境: Windows 11 ARM64 VM（2 vCPU / RAM 8 GB / 仮想 GPU のみ）、.NET SDK 10.0.404。**すべて非対話セッション（SSH、セッション 0）での実行**。対話デスクトップを要する確認は行っておらず、下表で「未確認」と明記した。
- 前提とした確定事項: D-4 は Win+J で確定（司令塔が対話デスクトップで登録成功・ピル表示・認識まで実測済み。本フェーズでは再実測していない）。CUDA は初版対象外。バックエンドは CPU と x64 Vulkan（本実装は T10）。

## 0. 結論

| 区分 | 内容 |
| --- | --- |
| 動いた（実測） | 自動テスト 235 件合格（M1 の 49 件 + 新規 186 件、失敗 0）。設定の往復・DPAPI・未来 schema と壊れた JSON の非破壊拒否・自動起動の Run キー ON/OFF（`reg.exe` で別経路から確認）を ARM64 / x64 の実 exe で確認。**`base` と `small` を実際に Hugging Face から取得**し、SHA-256 を catalog・`Get-FileHash`（独立経路）の両方と照合して配置。`config.json` の `recognition.model` を切り替えると、検証済みモデルで日本語認識が通ること、**1 バイト改ざんした `small` は認識に渡らず拒否**されることを実 exe で確認。**4 モデルすべての catalog 値を HF の公開メタデータ（tree API の `lfs.oid` / `size` と resolve の `x-linked-etag` / `x-linked-size` / `x-repo-commit`）と照合して一致**（`build/verify-catalog.ps1`）。設定ウィンドウを実際に生成して「保存」までを通す診断（ARM64 / x64）も合格 |
| 未確認 | 対話デスクトップが要るもの全般: 設定ウィンドウの見た目・操作、トレイからの設定の開閉、**Win+J 採用結果の永続化（対話セッションでの実走）**、ホットキー変更の即時反映の目視、設定画面からのモデル取得ボタンの実クリック、`medium` / `large-v3-turbo` の実ダウンロードとロード、実プロキシ環境、HTTPS プロキシ（未対応として拒否）。詳細は §5 |
| 次に必要なこと | 対話デスクトップでの §5 の手順。その後 T10（GPU・D-3）、T11（整文クライアント。本フェーズで保存と DPAPI は成立済み） |

## 1. タスク別の完了条件への対応

### T07: JsonSettingsStore / SecretProtector / 自動起動

完了条件（設計タスク表）: §8 の全項目、未来 schema 非破壊拒否、atomic save、CurrentUser DPAPI、Run キー ON/OFF が検証済み。

| 条件 | 結果 | 根拠 |
| --- | --- | --- |
| §8 の全項目を `config.json` に保存 | **実測** | 20 キー（`schema_version` から `updates.*` まで）を全て書き出し・読み戻し。`SettingsTests.Serialized_defaults_contain_every_key_of_the_schema` / `Round_trip_preserves_every_field`、実 exe の `--settings-probe`。保存先は §8.1 どおり exe の隣の `config.json`（`AppPaths.ConfigPath`）。書き込めない場所へ別保存先に切り替えることはしない（保存が例外になり、設定画面にエラー表示） |
| 型・値の検証 | **実測** | 未知のキー・重複キー・null のオブジェクト・型違い・未知の enum・範囲外を **JSON パス付き**のエラーにする（22 パターンの Theory）。`schema_version` 以外の欠損は既定値で補う |
| **未来 schema の非破壊拒否** | **実測** | `schema_version > 1` は他の検証（未知のキー等）より先に判定し `UnsupportedSchema`。ファイルは**バイト単位で不変**（unit と実 exe）。保存・退避・初期化は拒否。保存直前にもディスク上の schema を再確認する。欠損・0・負数・文字列は推測移行せずエラー。アプリを起動すると「設定エラー」を通知し、**ホットキーを有効化せず**、`config.json` にも触れない（実 exe で確認） |
| 壊れた JSON からの復旧 | **実測** | 壊れた JSON / 不正値は無断で初期化せず、ファイルに触れない。復旧は**利用者の明示操作**（トレイの「壊れた設定ファイルを退避して初期化」）で、`config.json.corrupt-<UTC日時>.bak` へ移動（上書き・削除はしない）。未来 schema は退避もできない（新しい版の設定を失わせないため）。保存途中のクラッシュで残る `config.json.<nonce>.tmp` は次回起動時に掃除する |
| atomic save | **実測** | 同階層のランダム名 `.tmp` に書き、`Flush(true)` 後に既存なら `File.Replace`（= `ReplaceFileW`）、新規なら `File.Move`。置換が失敗（別プロセスが開いている状況を再現）した場合、元ファイルは不変で `.tmp` も残らない |
| CurrentUser DPAPI | **実測（同一ユーザー）** | `DpapiSecretProtector`: UTF-8 → `ProtectedData`（CurrentUser、固定の識別子を entropy）→ Base64。空は null に統一。復号失敗（破損・entropy 違い）は例外にせず `false`、暗号文は消さない。**別ユーザーでの復号失敗は実機で確認していない**（entropy 違いの暗号文を代用して「復号できない」経路だけを確認） |
| 平文を出さない | **実測** | API キー・プロキシのパスワードは設定モデルが**暗号文しか持たない**（平文のプロパティがない）。`config.json` に平文が無いことを unit・実 exe・設定ウィンドウ診断で確認。`AppSettings.ToString()` にも出ない。ログ・標準出力へは秘密を出す経路を作っていない（`Console.WriteLine` は URL のホストと状態のみ） |
| 自動起動 | **実測（実レジストリ）** | `AutostartService`: `HKCU\...\Run\whisparrot` に引用符付きの絶対 exe パス。実 exe の `--autostart-probe` で ON → **`reg.exe query` で別経路から読んで一致** → OFF → 値が消えることを ARM64 / x64 で確認（終了後に残値なし）。unit は専用テスト用サブキーで、日本語・空白を含むパス、他アプリの値を消さないこと、exe 移動後の修復、OFF の冪等性を確認。起動時に設定どおりへ同期（exe 移動後の修復を含む）。書き込み失敗時は保存を成功にしない（設定ウィンドウ側で自動起動 → 保存の順に行い、後段が失敗したら前段を戻す） |
| M1 の暫定境界の差し替え | 実装 | `IAdoptedHotkeyStore` → `SettingsHotkeyStore`（交渉の結果が設定のキーと違うときだけ保存。全滅・設定読み込み失敗中は何も書かない）。モデル選択 → `recognition.model` を `WhisperRecognizer` の `ModelResolver` が毎回参照。マイク → `WasapiRecorder` が `microphone_id` の endpoint ID を開始時に解決（見つからなければ別デバイスへ切り替えず `DeviceUnavailable`）。整文まわり → 保存と DPAPI のみ |

#### 設定スキーマの実装状況（§8.1）

| キー | 保存・検証 | 実際に効くか |
| --- | --- | --- |
| `schema_version` | 実装 | — |
| `hotkey.modifiers` / `hotkey.key` | 実装（修飾キー 1 つ以上・重複禁止・順序正規化・対応キーのみ） | 起動時の交渉（設定のキーを先頭に、既定候補順）。設定画面の保存時に**即時**（新しい登録に成功してから旧登録を解除。失敗時は保存しない） |
| `microphone_id` | 実装 | 次の録音から（開始時に解決） |
| `recognition.model` | 実装（4 ID のみ） | 次の認識から。待機中の認識子は保存時に解放。認識中の保存は次の認識で子を作り直す |
| `recognition.language` | 実装（`ja` 固定） | M1 から固定 |
| `cleanup.provider` / `timeout_seconds` | 実装（15 固定。`chatgpt` は値を保持し UI では選べない） | **効かない（T11）**。保存のみ |
| `cleanup.compatible.base_url` / `api_key_dpapi` / `model` | 実装（§7.2 の URL 規則を `CompatibleEndpointUrl` で検証、DPAPI） | **効かない（T11）**。保存のみ |
| `cleanup.chatgpt.model` | 実装 | **効かない（T13）** |
| `proxy.*` | 実装（`manual` 時は `http://host:port` を検証、パスワードは DPAPI） | クライアント生成ごとに設定を読むので**次の通信から**（再起動不要） |
| `autostart` | 実装 | 保存時に即時（Run キー）。起動時にも同期 |
| `insertion.method` | 実装（`sendinput` も保存可） | **`sendinput` は効かない（T14）**。常にクリップボード方式。設定画面では選べない |
| `updates.*` | 実装（RFC3339 UTC。将来時刻はエラー） | 使うのは T15 |

### T08: NetworkContext と proxy fixture

完了条件: 3 モード、NO_PROXY、認証、TLS / redirect の規則が単体・結合で成立。通信箇所が共通生成口を使う。

| 条件 | 結果 | 根拠 |
| --- | --- | --- |
| 共通生成口 | **実測（網羅確認）** | `NetworkContext.CreateClient(NetworkPurpose)`（`Cleanup` / `Update` / `ModelDownload`）。リポジトリ内で `new HttpClient` が出てくるのは `NetworkContext` 内だけ、`CreateClient` の呼び出しは `ModelManager`（モデル取得）だけ（grep で確認。整文・更新確認は T11 / T15 がここを通る） |
| 3 モード | **実測（fixture）** | 環境変数（`HTTPS_PROXY` / `https_proxy` の大文字小文字無視、空文字は未指定、`HTTP_PROXY` / `ALL_PROXY` は使わない）、手動（環境変数を混ぜない。環境側プロキシに 0 接続）、なし（環境変数があっても直結。システム proxy / PAC も見ない）。クライアント生成時に設定を読むので、設定変更が次の生成から効くことも確認 |
| NO_PROXY | **実測** | `*`、ホスト完全一致、先頭ドット（そのドメイン自身とサブドメイン）、IP リテラル、`[IPv6]`、`:port`、空要素・空白、ドメイン境界（`.example.com` は `notexample.com` / `example.com.evil.net` に一致しない）。CIDR・ワイルドカード・正規表現・括弧なし IPv6 は**設定エラー**。loopback は設定にかかわらず直結。一致時は直結になりプロキシに来ないことを fixture で確認 |
| 認証付きプロキシ | **実測（fixture）** | 手動（ユーザー名 + DPAPI のパスワード）と、環境変数 URL の userinfo（パーセントデコード。URL から分離して credential にし、config へコピーしない）で Basic 認証が通る。誤ったパスワード・認証情報なしは失敗し、**サーバーへの到達は 0**（直結へ逃げない） |
| 不正なプロキシ | **実測** | 不正な URL・`https://` / `socks5://`・パス付き・空・不正な NO_PROXY は `NetworkConfigurationException`（黙って直結にしない）。到達不能なプロキシも接続エラーで、直結に落ちない |
| TLS | **実測（fixture）** | 本番の入口は OS の信頼ストアのみで、無効化の選択肢はない。自己署名の fixture 証明書は `AuthenticationException` で拒否され、プロキシへの CONNECT までは到達していることを確認。成功系のテストだけ、テスト専用の `internal` コンストラクターで fixture 証明書を受け入れる（設定・公開 API からは到達できない） |
| redirect | **実測（fixture）** | `Cleanup` は追わない（3xx をそのまま返す。転送先へ 0 リクエスト）。`Update` / `ModelDownload` は GET/HEAD の HTTPS だけ最大 5 hop、許可ホスト（`ModelDownload`: `huggingface.co` / `hf.co` とサブドメイン、`Update`: `github.com` / `githubusercontent.com`）のみ。**各 hop で経路を再計算**（1 hop 目はプロキシ経由、2 hop 目の loopback は直結になることを確認）。許可外ホスト・HTTP への転送・6 hop 目は `NetworkPolicyException`。**別ホストへの転送では `Authorization` を渡さない**。スキームは HTTPS のみ（`Cleanup` のローカルホスト HTTP を除く） |
| 実通信での確認 | **実測** | 実 exe で HF から取得（環境変数なし・直結）。HF の resolve が `*.cdn.hf.co` へ 302 するのを許可ホスト規則で追えて、`base` / `small` の取得が成功 |

#### fixture で測れた範囲 / 測れていない範囲

- 測れた: CONNECT プロキシ経由の HTTPS、認証（Basic）、経路規則、NO_PROXY、redirect の各 hop、TLS 拒否、直結へ黙って落ちないこと。fixture はループバックの自作 CONNECT プロキシと自己署名 TLS サーバー（ホスト名は fixture の対応表でループバックへ解決）。
- **測れていない**: 実在のプロキシ製品（Squid 等の実装）との相性、NTLM / Kerberos / Digest 認証（Basic のみ確認）、PAC / システム proxy（使わない方針）、**HTTPS プロキシ**（プロキシ自体への TLS。`https://` のプロキシ URL は**未検証のため設定エラーとして拒否**する。設計 §9.1 の「保存時に理由を示す」に従った）、プロキシ越しの実 HF 取得（この VM にプロキシがない。実 HF 取得は直結）、OS ブラウザーの認証通信（T12/U-02）、MITM 証明書をストアへ入れた環境。

### T09: ModelManager と固定 catalog

完了条件: 4 モデルの取得・hash・途中失敗・切替、既存モデル保持。catalog の revision / hash が実ファイルに一致。

| 条件 | 結果 | 根拠 |
| --- | --- | --- |
| 固定 catalog（4 モデル） | **実測** | `Models/model-catalog.json`（exe に埋め込み）。リビジョン `5359861c739e955e79d9a303bcbc70fb988958b1`（`ggerganov/whisper.cpp` の取得時点の HEAD。URL は `.../resolve/<リビジョン>/<ファイル>`）。パーサーが可変の `main`・短い revision・HTTP・大文字 / 不正な SHA・サイズ 0・`.en`・パス付きファイル名を拒否 |
| catalog は推測でない | **実測** | 出典は HF の公開メタデータ（tree API の LFS `oid` = SHA-256 と `size`）。`build/verify-catalog.ps1` で 4 モデルすべてについて tree API と resolve ヘッダー（`x-linked-etag` / `x-linked-size` / `x-repo-commit`）が catalog と**一致**（`catalog_verify=PASS`）。`small` のサイズは M1 の既存定数（487,601,967）とも一致 |
| 取得: 一時ファイル → ハッシュ → 原子的配置 | **実測** | `models/<name>.bin.part` へストリーム保存し、サイズ・SHA-256 を検証してから `File.Move(overwrite)`。Content-Length 不一致は書き込み前に拒否、catalog 超過は中断、空き容量（必要量 + 64 MiB）を事前確認 |
| 途中失敗・中断 | **実測（fixture）** | 通信切断（本文が途中で終わる）・SHA 不一致（同サイズで 1 バイト違い）・HTTP 503・取消のいずれも、`.part` を残さず正式ファイルへ昇格しない。`.part` や壊れたファイルは `CheckReady` が「使えない」を返し、認識に渡らない（`ModelResolver` 経由。unit で、モデルが無い状態の `RecognizeAsync` が認識子を起動せず例外になることも確認） |
| 既存モデルの保持 | **実測** | 検証済みの既存モデルは**再取得しない**（リクエスト 0、mtime も不変）。壊れた既存は、取得失敗・取消でそのまま残り、検証済みの新ファイルができて初めて置換。認識子が読み込み中のモデル（`LoadedModelPath`）と、他プロセスが開いているファイルは置換せず `ModelInUseException` |
| 同時取得 | **実測** | 取得は一度に 1 つ（2 つ目は `ModelBusyException`） |
| 検証キャッシュ | **実測** | サイズ + 更新時刻をキーにプロセス内で検証結果をキャッシュ。同サイズで中身だけ変わったファイルは再検証で `Corrupt`。起動時に選択中のモデルを背景で検証してキャッシュを温める |
| 切替と既定の永続化 | **実測** | 既定は `small`。`config.json` の `recognition.model` を `base` に変えて実 exe で認識（`small` 約 14 s → `base` 約 4.7 s、この VM の CPU・モデルロード込み）。切替時、待機中の子は解放される |
| 初回起動の導線 | 実装・**目視未確認** | 起動時に選択中のモデルが未取得・破損なら設定ウィンドウを自動で開き、「ローカル認識」で取得（進捗バー・MiB 表示・取消つき）。実 exe（セッション 0）で `model_status small=NotDownloaded` を出して設定ウィンドウ生成後も落ちずに常駐することを確認。実クリックでの取得は未確認（取得処理本体は実 HF で確認済み） |

#### catalog の出典とモデルごとの照合範囲

| モデル | サイズ（bytes） | SHA-256（先頭 16 桁） | HF メタデータ（tree API + resolve ヘッダー） | **実ダウンロード + ハッシュ照合** | 実際にロードして認識 |
| --- | --- | --- | --- | --- | --- |
| `base` | 147,951,465 | `60ed5bc3dd14eea8` | 一致 | **実施（独立経路の `Get-FileHash` も一致）** | **実施**（日本語認識成功） |
| `small` | 487,601,967 | `1be3a9b2063867b9` | 一致 | **実施（同上）** | **実施**（日本語認識成功。M1 で手動配置した同名ファイルとも矛盾なし） |
| `medium` | 1,533,763,059 | `6c14d5adee5f86394` | 一致 | **未実施**（容量のため、依頼どおりメタデータ照合まで） | 未実施 |
| `large-v3-turbo` | 1,624,555,275 | `1fc70f774d38eb16` | 一致 | **未実施**（同上） | 未実施 |

`medium` / `large-v3-turbo` は、catalog 値が HF の公開値と一致することまでを実証した。実際に取得してハッシュが一致するか、ロードできるか、3 GB 級のハッシュ計算に要する時間（起動時の背景検証）は未計測。

## 2. 自動テストと実機確認の一覧

- `dotnet test tests\Whisparrot.Tests -c Release`: **235 件合格 / 失敗 0**（M1: 49、M2 新規: 186）。内訳の主なもの: 設定（スキーマ・検証・未来 schema・復旧・atomic・DPAPI・URL・ホットキー）、自動起動、NO_PROXY・プロキシ規則、NetworkContext の fixture 結合 20 件超、catalog・ModelManager の fixture 試験。
- 実 exe（ARM64、x64）の診断モード（追加）:
  - `--settings-probe <dir>`: 設定の往復・DPAPI・未来 schema・壊れた JSON・明示リセット。両 arch で PASS。
  - `--autostart-probe`: 実 HKCU Run キー。両 arch で PASS。
  - `--settings-window-probe <dir>`: 実 `SettingsWindow` を生成し、コントロールに値を入れて「保存」まで（不正 URL の拒否、ホットキー登録失敗で保存しない、秘密の暗号化、空欄の再保存で暗号文維持）。両 arch で PASS。自動起動はテスト用サブキーへ、ホットキー登録は代役。**見た目・操作感は確認できない**。
  - `--model-download <id> [--dir d]`: NetworkContext 経由の実取得。`base` / `small` で成功。
  - `--pipeline-file <wav> --use-settings`: `config.json` + catalog でモデルを解決して認識。`base` / `small`（ARM64）、`base`（x64 エミュレーション）で成功。改ざんした `small` は「壊れています（SHA-256 が一致しません）」で拒否。
- 実 exe の常駐起動（セッション 0）: 設定なし / モデルなし、モデルあり、未来 schema、壊れた JSON の 4 通りで落ちずに常駐し、後ろ 2 つでは `config.json` が不変・ホットキー未登録・`settings_error` を出す。待機時 working set は 242〜275 MiB（M1 の約 229 MiB から設定・ModelManager・設定ウィンドウ生成ぶん増えた可能性。切り分けはしていない）。
- exe サイズ（圧縮込み）: ARM64 73,904,547 bytes（70.5 MiB）、x64 77,850,366 bytes（74.2 MiB）。M1 から実質変わらず。

## 3. 設計・依頼からの逸脱と判断

| 点 | 内容と理由 |
| --- | --- |
| ホットキーの入力 UI | 設計 §6.2 の「捕捉欄」ではなく、**修飾キーのチェックボックス + キーの選択**にした。捕捉欄にすると、すでに登録中のグローバルホットキー（Win+J 等）を押した瞬間に録音が始まり、欄に届かないため。登録検査は保存時（新規登録に成功してから旧登録を解除）。捕捉欄の完成は T14 |
| 採用結果の永続化 | 依頼どおり、交渉の結果が設定のキーと違えば `hotkey` に保存する。`config.json` に「ユーザーが選んだキー」と「採用したキー」を分ける欄がない（設計は隠れた設定を許さない）ため、**一時的な登録失敗（例: 他アプリが Win+J を一時的に占有）で次善の Ctrl+Win+J が保存されると、以後はそれが先頭候補になる**。Win+J へ戻すには設定画面で選び直す。この粘着性が問題なら、スキーマを変えずに済む範囲では「保存済みキーが既定候補のいずれかなら毎回既定順で交渉する」案があるが、利用者が明示的に Ctrl+Alt+J を選んだ場合と区別できなくなる。**司令塔の判断を要する** |
| NO_PROXY の素のホスト名 | 設計 §9.1 の文言どおり、`example.com`（先頭ドットなし）は**完全一致のみ**、`.example.com` がサブドメインも含む。curl 等は素のホストでもサブドメインを含めるため、挙動が違う。直結へ誤って回すより、プロキシに回して失敗する側へ倒した |
| `https://` のプロキシ | 未検証のため環境変数・手動とも**拒否**（§9.1 の「検証済みの https://host:port」に該当しない）。要望があれば別途 fixture で検証する |
| `updates.*` の将来時刻 | 設計どおり厳密に拒否（許容幅なし）。時計が巻き戻った環境では `config.json` が「不正」になり、ホットキーが有効にならない。T15 で扱いを再確認するのがよい |
| 投入方式・整文の UI | 投入方式の「タイピング」と整文の「ChatGPT」は選べない表示（T14 / T13）。整文の「互換エンドポイント」は保存のみ（T11）で、画面に「保存のみ」と明記 |
| 部分取得の再開 | `.part` からの再開（Range）はしない。失敗・取消で `.part` を捨て、次回は最初から取得する。大きなモデル（約 1.5 GB）では通信断のコストが大きい。T14 以降の改善候補 |
| テスト専用の TLS 入口 | `NetworkContext` に `internal` のコンストラクター（自己署名 fixture 証明書の受け入れ）を置いた。本番コードから呼ぶ経路・設定項目はない |
| 診断モード | `--settings-probe` / `--autostart-probe` / `--settings-window-probe` / `--model-download` と `--pipeline-file --use-settings` を追加（M1 と同じ扱い。T17 で見直す）。`--settings-window-probe` は private フィールドをリフレクションで操作する診断で、コントロール名を変えると壊れる |
| `tests/Whisparrot.Integration/` | 作らず、fixture 結合試験は既存の `Whisparrot.Tests` 内（M1 と同じ方針。自動化の整理は T17） |

## 4. ビルド・実行手順（追加分）

```powershell
dotnet test tests\Whisparrot.Tests -c Release            # 235 件
build\publish.ps1 -Arch all                              # M1 と同じ
build\verify-catalog.ps1                                 # catalog を HF の公開メタデータと再照合（ダウンロードなし）
whisparrot.exe --model-download small --dir <dir>        # NetworkContext 経由の取得と SHA-256 照合
```

プロキシ越しの取得は、`HTTPS_PROXY` / `NO_PROXY` を設定して `--model-download` を実行すれば確認できる（`route=` 行に経路が出る）。

## 5. 未確認の項目と、対話デスクトップでの確認手順

次は非対話セッションでは確認できていない。結果は未取得。

1. **Win+J 採用結果の永続化**: 初回起動（`config.json` なし）で、標準出力の `hotkey_adopted=Win+J` / `hotkey_persisted=Win+J` を確認する。Win+J が登録でき、設定のキーと同じなので `config.json` は**作られない**のが正しい。Win+J を別アプリで占有してから起動すると Ctrl+Win+J が採用され、`config.json` の `hotkey` が書き換わる（§3 の粘着性の確認を兼ねる）。
2. **設定ウィンドウの目視**: トレイの左クリック / 「設定」で開く。各セクション（入力・ローカル認識・整文・通信・起動）が §6.2 の項目どおり並び、スクロール・保存/キャンセルが機能すること。
3. **ホットキー変更の即時反映**: 修飾キー・キーを変えて保存 → 再起動なしで新しいキーが効き、旧キーが解放される。他アプリが使用中のキーを指定すると、保存せずエラーが出る。
4. **モデル取得の実クリック**: モデル未配置で起動すると設定が自動で開く。`small` の「取得」で進捗が進み、取消で `models\` に `.part` が残らず、完了後に「取得済み（検証済み）」と表示され、すぐ録音できる。`base` へ切り替えて保存 → 次の録音から切り替わる。
5. **マイク選択**: 一覧に実デバイスが出て、選んだデバイスで録音される。デバイスを外した状態で録音すると別デバイスへ切り替えずエラーになる。
6. **自動起動**: 設定画面で ON → 再ログインで起動する（Run キーの書き込みは実測済み。ログオン時の起動そのものは未確認）。
7. **プロキシ**: 実プロキシ環境で、環境変数モード・手動（認証あり）・なしの 3 モードで `--model-download` と取得ボタンが期待どおりの経路になる。
8. **`medium` / `large-v3-turbo`**: 実ダウンロードとハッシュ照合、ロード、背景検証にかかる時間（約 1.5 GB のハッシュ）。
9. M1 の §5 の残り（キー配送、メモ帳への貼り付け、ピル、素の Windows、x64 実機 + Vulkan）は変わらず未確認。

## 6. 残課題

- §5 の対話確認。
- **司令塔の判断**: §3「採用結果の永続化」の粘着性、`updates.*` 将来時刻の厳密拒否、NO_PROXY の素のホスト名の扱い。
- T10: GPU 本実装と D-3、設定画面の「実際の実行方式」は `LastBackend` を表示する枠だけ用意済み（実機のログ判定は T10）。
- T11: 整文クライアント（設定・DPAPI・URL 規則・`NetworkContext(Cleanup)` は用意済み。`ProxyConfig.FromSettings` が復号失敗を明示エラーにする）。
- T14: ホットキー捕捉欄、タイピング方式、設定・ピルの見た目とアクセシビリティ、バックグラウンド取得（ウィンドウを閉じても取得を続ける）。現在は取得中にウィンドウを閉じると確認のうえ取消。
- T15: 更新確認が `NetworkContext(Update)` を使う。許可ホストの規則は最小の仮置き（`github.com` / `githubusercontent.com`）。
- 依頼で「今回は直さない」とされた既知の所見（follow-up としてここに残す）:
  - アプリ起動直後は不可視のホットキー窓がフォアグラウンドを握る。
  - 待機時 working set（約 229 MiB → 本フェーズ後 242〜275 MiB）。増加分の原因は未切り分け。
  - 認識ワーカー常駐時のモデル保持（small 約 0.8 GB）。モデル切替時には待機中の子を解放するようにしたが、待機が続く間の保持は M1 のまま。
