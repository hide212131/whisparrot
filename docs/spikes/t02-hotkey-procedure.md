# T02 ホットキー probe: 対話セッションでの確認手順書

- 対象: `spike/t02-hotkey`（`hotkey-probe`）
- 目的: Win+J / Ctrl+Win+J / Ctrl+Alt+J が **対話デスクトップで** 登録でき、かつ **キーが実際に配送される** ことを確認する（設計 §5 / D-4）。
- 状態: **キー配送は未確認**。SSH セッションで得られた結果（`s01-report.md` §T02）は、キー配送についても Win+J の可否についても何も保証しない。

## 0. 前提

- 実行は、検証機のデスクトップにログインした **対話セッション**（`explorer.exe` が動いているセッション）で行う。SSH 経由の実行は不可（SSH はセッション 0 の非対話ウィンドウステーションで、`RegisterHotKey` は `ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION` (1459) で失敗する）。
- ビルド（リポジトリのルートから）:

```powershell
dotnet publish spike/t02-hotkey -c Release -r win-arm64 --self-contained -p:PublishSingleFile=true -o out\hk-arm64
dotnet publish spike/t02-hotkey -c Release -r win-x64   --self-contained -p:PublishSingleFile=true -o out\hk-x64
```

  ARM64 VM では `win-arm64` を使う。Iris Xe の x64 実機では `win-x64` を使う。
- `out\` 以下は commit しない。

## 1. 環境の記録（両機で最初に 1 回）

| 項目 | 記録方法 | 記録欄 |
| --- | --- | --- |
| Windows のエディションとビルド | `winver` またはプローブ出力の `os=` | |
| キーボード配列 / 物理キーボードの種類 | 設定 → 時刻と言語 | |
| IME の状態（既定 ON/OFF、入力方式） | タスクバーの言語表示 | |
| VM の場合: ホスト側ソフトとキー転送設定 | VM 管理ソフトの設定（Win キーをゲストへ送る設定の有無） | |
| Windows のヒント通知（Win+J で出る「ヒント」）が有効か | 設定 → システム → 通知 →「Windows を使用する際のヒントや提案を表示」 | |

## 2. 登録可否（`register`）

対話セッションのターミナルで実行する。

```powershell
.\out\hk-arm64\hotkey-probe.exe register
```

出力の `session_id` が 0 でなく、`interactive=True`、`window_station=WinSta0` であることを確認してから、次の 3 行を記録する。

| 組合せ | `register[...]` | `win32_error` |
| --- | --- | --- |
| Win+J | | |
| Ctrl+Win+J | | |
| Ctrl+Alt+J | | |

- `FAILED win32_error=1409`（`ERROR_HOTKEY_ALREADY_REGISTERED`）は、他のプロセス（または OS）がすでに保有している。保有者の特定を試みる（既知の常駐アプリを終了して再実行し、結果が変わるかを見る）。
- 常駐アプリの競合「あり」「なし」を分けて記録する（設計 §5 手順 2）。

## 3. キー配送（`listen`）— **ここが未確認の核心**

```powershell
.\out\hk-arm64\hotkey-probe.exe listen 120
```

120 秒の間に、メモ帳を前景にして次を行う。プローブの標準出力（`WM_HOTKEY ...` 行）と、OS 側の挙動の **両方** を観察する。

| # | 操作 | 期待 | 観察（WM_HOTKEY 行の有無 / OS 側の副作用） |
| --- | --- | --- | --- |
| 1 | Win+J を 1 回押して離す | `combo=Win+J` が 1 行。Windows のヒント通知へのフォーカス移動やスタートメニューの出現が **起きない** | |
| 2 | Ctrl+Win+J を 1 回 | `combo=Ctrl+Win+J` が 1 行。OS 側で別の動作が起きない | |
| 3 | Ctrl+Alt+J を 1 回 | `combo=Ctrl+Alt+J` が 1 行 | |
| 4 | Win+J を 3 秒押しっぱなし | `MOD_NOREPEAT` により 1 行だけ | |
| 5 | 押下中・解除後にメモ帳のキャレット位置とフォーカスを見る | フォーカスが移動していない | |
| 6 | Windows のヒント通知を表示させた状態で Win+J | OS の通知フォーカスが同時に動かない（動いたら不合格。「イベントが届いた」だけで「奪えた」としない） | |
| 7 | IME ON と OFF の両方で 1〜3 | どちらも同じ結果 | |

追加の状況（設計 §5 手順 4）: ログイン直後、スリープ／サスペンド復帰後。

- Win キーを含む組合せの場合、**Win キーを離したときにスタートメニューが開くか** も観察する（`WM_HOTKEY` が来ていても、OS 側で Start が開けば入力の邪魔になる）。
- VM では、ホストがキーを横取りしていないか（ゲストにキーダウンが届いているか）を、メモ帳に別のキー（例: `J` 単独）を打って確認し、ホストによる横取りと Windows 側の予約を区別する。

## 4. 解除と再登録

- `listen` 終了時に `unregister[...]=OK` が 3 行出ることを確認する。
- 続けてもう一度 `register` を実行し、再登録できることを確認する。
- 常駐アプリを 2 つ起動して（`listen 120` を 2 つ）、後発が `1409` になることを確認する（競合時の挙動の確認）。

## 5. 判定

設計 §5 に従う。Win+J が 2 機のどちらかで不安定なら Ctrl+Win+J、次に Ctrl+Alt+J で同じ表を埋める。両機で通り、一般的な編集操作と競合しにくい候補を既定にする。候補が全滅した場合は本人と決める（低レベルキーフックで OS キーを潰す実装は追加しない）。

このプローブでは次の項目は確認できない。製品の最小経路（T04 以降）ができてから、設計 §5 手順 3 の通りに行う。

- メモ帳・ブラウザ・チャット欄で各 20 回トグルしたときの開始／停止の一致
- 設定画面で別キーに変更して戻せること
