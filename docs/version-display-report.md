# バージョン表示 実装レポート

ブランチ: `feat/version-display`（origin/main 562e757 から分岐）。マージ・タグ・リリースは行っていない。

## 変更点
- `Whisparrot.Core/VersionText.Format`: AssemblyInformationalVersion（空なら AssemblyVersion の 3 桁）を `v<版>` に整形する。`+` 以降のメタデータは落とす（例: `0.2.0+562e757…` → `v0.2.0`）。どちらも無ければ `v0.0.0`。
- `AppInfo.DisplayVersion`: exe の AssemblyInformationalVersion を読んで上記ヘルパーに渡す。既存の `AppInfo.Version`（native 展開先の階層用）は変更なし。
- 設定画面: 「起動・更新」グループの既存のバージョン行を `whisparrot v0.2.0 / <アーキ>` 表記に変更。
- トレイ: ツールチップを `whisparrot v0.2.0（<ホットキー>）` のようにバージョン込みへ。メニュー構成・設定の保存は変更なし。
- `build/publish.ps1`: `-Version` を追加。指定時は `-p:Version` と `-p:InformationalVersion` に渡す。
- `Directory.Build.props` の既定 Version を 0.1.0 → 0.2.0。

## テスト（Windows ARM64 実測）
- 全 484 件合格（失敗 0・スキップ 0）。うち追加は `VersionTextTests`（整形・フォールバック）。
- `--settings-window-probe`: `settings_window_probe=PASS`（全項目 PASS）。

## publish（`-Arch all -Version 0.2.0`, cpu）
| RID | サイズ |
|---|---|
| win-arm64 | 73,942,719 bytes (70.5 MiB) |
| win-x64 | 77,890,145 bytes (74.3 MiB) |

## exe のバージョン情報（FileVersionInfo 実測）
| RID | FileVersion | ProductVersion |
|---|---|---|
| win-arm64 | 0.2.0.0 | 0.2.0+10524fad2788675f6e5cd0c13448a5966d7f4721 |
| win-x64 | 0.2.0.0 | 0.2.0+10524fad2788675f6e5cd0c13448a5966d7f4721 |

ProductVersion の `+` 以降は SDK が付与するコミット情報で、表示時に落とされ `v0.2.0` になる。

## 未確認
- 実際のトレイのツールチップと設定画面の表示文字列を目視では確認していない（probe は表示文字列を検証しない。整形ロジックは単体テストで確認）。
