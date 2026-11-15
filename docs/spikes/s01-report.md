# スパイク S-01〜S-03 / T02 報告

- 対象タスク: 設計書 T01（S-01 / S-02 / S-03）と T02（ホットキー probe）
- ブランチ: `spike/s01`（main へはマージしない）
- 実施日: 2026-11-15
- コード: `spike/s01-csharp/`（C# WPF + Whisper.net）、`spike/s01-rust/`（whisper-rs、**未コンパイル**）、`spike/t02-hotkey/`（RegisterHotKey probe）
- 関連文書: [T02 対話セッションでの確認手順書](t02-hotkey-procedure.md)

## 0. 結論の要約

| 項目 | 結果 |
| --- | --- |
| D-2（C# 推奨） | **実測は C# 推奨を支持する。覆す根拠は出ていない。** ただし確定は x64 実機（Iris Xe）の Vulkan 実測を待つ（§7）。Rust 側は ARM64 VM でビルドが通らず、実測比較はできなかった |
| 単一 exe（両アーキ） | win-arm64 / win-x64 とも単一ファイル publish でき、ARM64 は **ネイティブ ARM64 で CPU 認識が成功** した |
| 設計の前提と違った点 | (1) `IncludeNativeLibrariesForSelfExtract` だけでは Whisper.net が native DLL を見つけられず、`IncludeAllContentForSelfExtract` が必要。(2) GPU ランタイム DLL が欠落・破損すると、同一プロセス内の CPU フォールバック中に **プロセスごと異常終了** する（子プロセス隔離が必須であることの実証）。(3) `LoadedLibrary = Vulkan` でも GPU が無ければ CPU で計算される。(4) Whisper.net の native は VC++ 再頒布パッケージに依存する |
| T02 | SSH（セッション0）では 3 組合せすべて `ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION (1459)` で登録失敗。これは Win+J の可否を示さない。**キー配送は未確認** |
| 未実測 | GPU（Vulkan / CUDA）の実動作と速度、x64 実機、Rust の実測、VC++ 再頒布パッケージが無い素の Windows での起動（§8） |

## 1. 環境

| 項目 | 内容 |
| --- | --- |
| 検証機 | Windows 11 Home（ビルド 10.0.26300）、ARM64 VM、2 vCPU、RAM 8 GB、仮想 GPU（Vulkan / CUDA の実 GPU なし） |
| 実行セッション | すべて SSH 経由（セッション 0、非対話ウィンドウステーション）。対話デスクトップでの実行はしていない |
| 開発ツール | .NET SDK 10.0.404（ARM64）、Rust stable（MSVC）、VS Build Tools、LLVM（ARM64 版） |
| この VM の状態 | .NET SDK・Rust・VC++ 再頒布パッケージ（14.50、ARM64 / x64）が **入っている**。素の Windows 11 ではない |
| 作業・編集機 | macOS |
| 主な依存 | Whisper.net 1.9.1（`Whisper.net.Runtime` / `.Runtime.Vulkan` / `.Runtime.Cuda.Windows`）、whisper.cpp は Whisper.net 同梱版 |
| モデル | `ggml-small.bin`（Hugging Face `ggerganov/whisper.cpp`、487,601,967 bytes、SHA-256 `1be3a9b2…987b`） |
| 音声 | `samples/jfk.wav`（whisper.cpp リポジトリ、352,078 bytes、約 11.0 秒、16 kHz mono PCM16、英語） |

認識の言語は音声に合わせて英語（`--lang en`）で実行した。日本語音声での精度は D-3 の範囲で本スパイクの対象外。

スレッド数は設計 §4.1 の式（`max(1, min(8, 論理プロセッサ数 - 1))`）をそのまま使ったため、2 vCPU の本機では **1 スレッド** で動いている。以降の認識秒数はこの条件の値であり、設計の合格目安（Iris Xe の x64 実機）とは比較できない。

## 2. S-01: 単一 exe と両アーキ

### 2.1 構成

`spike/s01-csharp/Spike.S01.csproj`: `net10.0-windows`、WPF + WinForms（`NotifyIcon` 用）、`PublishSingleFile`、`SelfContained`、`PublishTrimmed=false`。

- 既定の起動はトレイ常駐 + ピル placeholder（280×52 DIP、角丸 26、`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`、画面下中央から 24 DIP 上）。
- `--recognize <model> <wav> [--order cpu|vulkan|cuda|cuda12|auto] [--lang ..] [--repeat N] [--log]`: Whisper.net で認識し、`RuntimeOptions.LoadedLibrary` などを出力する。
- `--recognize-isolated ...`: 子プロセス認識 + CPU 再試行の最小再現（§3）。
- `--bench-startup`: プロセス開始からピル表示呼び出しまでの時間と、5 秒後の working set を出して終了する。
- `-p:Backend=cpu|vulkan|cuda|cuda12|all` で Runtime パッケージを切り替え、`-p:Compress=true` で単一ファイル圧縮、`-p:Prune=false` で後述の除外を無効化する。

### 2.2 必要だった設定（設計 §2.2 への差分）

設計の `IncludeNativeLibrariesForSelfExtract=true` **だけでは動かなかった**。`runtimes/<rid>/` の native DLL が見つからず、`FileNotFoundException: Native Library not found in default paths` で認識に失敗した（arm64 / x64 とも）。`IncludeAllContentForSelfExtract=true` を併用すると、展開先が `AppContext.BaseDirectory` になり、Whisper.net が `runtimes/<rid>/` を見つけて認識できた。

この設定の副作用（実測）:

- マネージドアセンブリも含めて **約 460 ファイル、157〜170 MiB** を `%TEMP%\.net\<アプリ名>\<ハッシュ>\` へ展開する（§2.4）。初回起動が遅く、ディスクとウイルス対策ソフトの負荷が増える。
- `AppContext.BaseDirectory` が exe のフォルダではなく展開先を指す。設計 §2.2 の「設定パスの基点は `Environment.ProcessPath`」は **必須** である（`config.json` / `models/` を `BaseDirectory` 基準にすると見つからない）。
- 代替案（未実装）: マネージドは単一ファイルに残し、Whisper の native だけ exe リソースとして埋め込んで、設計どおり `%LOCALAPPDATA%\<アプリ名>\runtime\...` へハッシュ確認付きで展開する。T05 で比較する価値がある。

### 2.3 exe サイズ（実測、バイト数）

「除外あり」は §4 の Prune（Windows 以外と対象外アーキの native を除く）を有効にした既定構成。

| 構成 | 除外なし | 除外あり（既定） | 除外あり + 単一ファイル圧縮 |
| --- | --- | --- | --- |
| win-arm64 / CPU | 193,073,844（184.1 MiB） | **189,440,212（180.7 MiB）** | **72,201,393（68.9 MiB）** |
| win-x64 / CPU | 179,217,207（170.9 MiB） | **175,649,103（167.5 MiB）** | **76,424,613（72.9 MiB）** |
| win-x64 / CPU + Vulkan | 300,422,645（286.5 MiB） | 235,631,268（224.7 MiB） | 未計測 |
| win-x64 / CPU + CUDA 13 | 335,791,232（320.2 MiB） | 332,223,128（316.8 MiB） | 未計測 |
| win-x64 / CPU + Vulkan + CUDA 13 | 456,996,670（435.8 MiB） | 392,205,293（374.0 MiB） | 249,757,595（238.2 MiB） |

- 「除外なし」の 4 構成は `--log` 追加前のコードでビルドした値で、除外あり側とはコードが 4 KiB 程度異なる。
- exe の大半は .NET 10 ランタイム + WPF + WinForms（自己完結、トリム無し）で、フォルダ形式で publish した場合の主要ファイルは `System.Private.CoreLib.dll` 16 MB、`PresentationFramework.dll` 15.8 MB、`System.Windows.Forms.dll` 13.7 MB など。Whisper の CPU runtime は 1 アーキあたり約 2 MB で、サイズへの影響は小さい。
- 圧縮（`EnableCompressionInSingleFile`）で CPU 版は約 6 割縮む。起動時間への影響は測定誤差の範囲だった（§2.4）。
- モデル（small は 487,601,967 bytes）は exe に含まれない。

### 2.4 起動時間・待機時 working set・展開後総量

起動時間は `--bench-startup` の「プロセス開始から `Application.Startup` 内でピル表示を呼ぶまで」。コールドは `%TEMP%\.net\` の展開先を削除した直後の 1 回目、ウォームは 2〜4 回目。working set は起動 5 秒後の値。いずれもセッション 0 での実行で、画面に描画されたことの確認ではない（§8）。

| 構成（除外あり） | 起動 コールド | 起動 ウォーム | 待機 working set | 展開ファイル数 / 展開後総量 |
| --- | --- | --- | --- | --- |
| win-arm64 / CPU（ネイティブ） | 約 1.5 秒 | 約 0.29〜0.39 秒 | 約 82 MiB | 461 / 178,072,069 bytes（169.8 MiB） |
| win-arm64 / CPU + 圧縮 | 約 1.6 秒 | 約 0.30〜0.41 秒 | 約 82 MiB | 同上 |
| win-x64 / CPU（x64 エミュレーション） | 約 2.3 秒 | 約 0.58〜1.5 秒 | 約 109〜122 MiB | 462 / 164,787,991 bytes（157.2 MiB） |
| win-x64 / CPU + 圧縮 | 約 2.3 秒 | 約 0.58〜2.1 秒 | 約 109〜122 MiB | 同上 |
| win-x64 / CPU + Vulkan | 約 2.5 秒 | （未反復） | 約 123 MiB | 467 / 224,773,399 bytes（214.4 MiB） |
| win-x64 / CPU + CUDA 13 | 約 3.0 秒 | （未反復） | 約 118 MiB | 467 / 321,361,687 bytes（306.5 MiB） |
| win-x64 / CPU + Vulkan + CUDA 13 | 約 3.6 秒 | 約 0.58〜1.5 秒 | 約 122 MiB | 472 / 381,347,095 bytes（363.7 MiB） |

- x64 の数値は ARM64 上のエミュレーションで取ったもので、x64 実機の値ではない。ウォームのばらつきが大きいのはそのため。
- 待機 working set は、ウィンドウを作って待機しているだけの値。認識中は §2.5 のとおり約 800〜850 MiB に上がる（small）。

### 2.5 認識（small、jfk.wav 約 11.0 秒、1 スレッド）

| 構成 | 実行方式（`LoadedLibrary`） | モデルロード | 認識時間 | RTF | ピーク working set |
| --- | --- | --- | --- | --- | --- |
| win-arm64 ネイティブ | Cpu | 1.0〜1.7 秒 | 13.2 / 13.9 / 13.6 秒（3 回）、別実行 12.7 秒 | 約 1.2 | 約 800 MiB |
| win-x64（エミュレーション） | Cpu | 約 2.6 秒 | 25.2 / 24.5 秒 | 約 2.2 | 約 840 MiB |

認識結果（全構成で一致）: `And so my fellow Americans, ask not what your country can do for you, ask what you can do for your country.`

Whisper.net の x64 runtime は AVX / AVX2 / FMA / F16C を要求するが、この VM のエミュレーション上でも動作した。

### 2.6 ネイティブ依存一覧（`dumpbin /dependents`）

| ファイル | 依存する DLL（api-ms-win-crt-\* と KERNEL32 を除く） |
| --- | --- |
| `whisper.dll`（arm64） | `ggml-whisper.dll`、`ggml-base-whisper.dll`、**`MSVCP140.dll`、`VCRUNTIME140.dll`** |
| `ggml-whisper.dll` / `ggml-base-whisper.dll`（arm64） | `ggml-cpu-whisper.dll`（ggml-whisper のみ）、`MSVCP140.dll`、`VCRUNTIME140.dll` |
| `ggml-cpu-whisper.dll`（arm64） | `ggml-base-whisper.dll`、`ADVAPI32.dll`、`MSVCP140.dll`、`VCRUNTIME140.dll` |
| `whisper.dll` 等（x64） | 上記に加え **`VCRUNTIME140_1.dll`**、`ggml-base` / `ggml-cpu` は **`VCOMP140.DLL`**（OpenMP） |
| `ggml-vulkan-whisper.dll`（x64） | `ggml-base-whisper.dll`、**`vulkan-1.dll`**（ドライバー提供）、`MSVCP140.dll`、`VCRUNTIME140(_1).dll` |
| `ggml-cuda-whisper.dll`（x64、CUDA 13） | `ggml-base-whisper.dll`、**`cublas64_13.dll`**（パッケージに含まれない）、`MSVCP140.dll`、`VCRUNTIME140(_1).dll` |
| 単一ファイル exe（apphost） | `KERNEL32`、`ADVAPI32`、`ole32`、`OLEAUT32`、`USER32`、`SHELL32`、`VERSION`、`api-ms-win-core-winrt`、CRT の api-ms-win-crt-\* |
| WPF native（`PresentationNative` / `wpfgfx` / `PenImc`） | `USER32`、`GDI32`、`ole32`、`WindowsCodecs`、`WINMM`、`D3DCOMPILER_47` など（OS 標準。`D3DCompiler_47_cor3.dll` は .NET 側で同梱） |

**VC++ 再頒布パッケージ（MSVCP140 / VCRUNTIME140 / VCRUNTIME140_1 / VCOMP140）が必要**である。Whisper.net の README も前提条件に挙げている。.NET の自己完結配布は `vcruntime140_cor3.dll` を含むが、`MSVCP140.dll` と `VCOMP140.DLL` は含まない。この VM は再頒布パッケージが入っているため、**無い環境での起動は未検証**である（§8）。対策の候補（未実施）は、必要な 4 DLL をアプリローカルで同梱すること。

### 2.7 Rust 比較（whisper-rs）

**ブロッカーにより実測できなかった。** `whisper-rs 0.16.0`（`whisper-rs-sys 0.15.0`）で最小コンソール認識（`spike/s01-rust`）を書いたが、**未コンパイル**（ビルドが `whisper-rs-sys` のビルドスクリプトで止まり、このクレート自体のコンパイルまで進んでいない。whisper-rs 0.16 の API との整合も未検証）。

| 試行 | 結果 |
| --- | --- |
| aarch64-pc-windows-msvc、CMake の Visual Studio ジェネレーター（MSVC） | bindgen は通ったが、ggml-cpu の CMake が `MSVC is not supported for ARM, use clang` で停止（約 1 分） |
| 同、Ninja + clang（whisper.cpp 同梱の `arm64-windows-llvm.cmake` を指定） | `cmake` クレートが付与する MSVC 流のフラグ `-nologo` を clang が `unknown argument` として拒否して停止（約 7 秒） |
| x86_64-pc-windows-msvc（x64 ホストのツールチェーン） | ビルドスクリプトが x64 プロセスで動くため、この VM の ARM64 版 libclang を読めず `Unable to find libclang` で停止 |

x64 版 LLVM を入れる、または `CFLAGS` を上書きして clang 向けに整える、といった回避は未試行である。いずれも、Rust 案が「whisper.cpp のビルドと ABI を自前管理する」という設計 §2.1 の指摘を裏づける材料になるが、Rust の exe サイズや認識速度の実測はない。文書上の比較は設計 §2.1 の表のままで、実測による上書きはしていない。**「Rust の exe が大幅に小さい」ことも「小さくない」ことも本スパイクでは示せていない。**

## 3. S-02: ARM64 の CPU 認識と GPU 失敗時の復帰

### 3.1 実測結果

| 確認 | 結果 |
| --- | --- |
| Windows ARM64 VM のネイティブ ARM64 CPU 認識 | **成功**（§2.5）。プロセスアーキテクチャは `Arm64`。x64 エミュレーション動作とは区別して記録した |
| ARM64 ビルドで `--order vulkan` / `auto` を指定 | 例外なく CPU にフォールバックして認識成功（ARM64 には既製の Vulkan runtime が無く、そもそも読み込み候補にならない） |
| x64 + Vulkan runtime、Vulkan loader はあるが GPU デバイスが無い | `LoadedLibrary = Vulkan` と報告されるが、whisper.cpp のログは `whisper_backend_init_gpu: no GPU found`、認識時間は CPU と同じ（約 24 秒）。**クラッシュなし** |
| x64 + CUDA runtime、NVIDIA 環境なし | Whisper.net が CUDA 利用可否を事前に判定して候補から外し、CPU で認識成功 |
| **GPU runtime の DLL を削除 / 破損させ、同一プロセス内で `--order vulkan`（CPU は次候補）** | Vulkan ロード失敗後、次候補（CPU）の初期化で `GGML_ASSERT(prev != ggml_uncaught_exception) failed` となり **プロセスが異常終了**（終了コード -1073740791 = 0xC0000409）。破損・削除のどちらも同じ |
| **同じ状況を `--recognize-isolated`（子プロセスで GPU 試行 → 失敗なら CPU の子で一度だけ再試行）で実行** | 1 回目の子が異常終了しても **親は生存**し、2 回目の子（CPU）で認識成功、親の終了コードは 0 |

### 3.2 コード構成の観点での評価

- **GPU 初期化に失敗してもアプリが落ちない構成は、認識を別プロセスに隔離すれば成立する**。設計 §3.2（同じ exe の `--recognizer-worker` + 親が保持する PCM を CPU 子へ一度だけ渡す）の意図どおり切り離せることを、`IsolatedRecognize.cs` で最小再現した。
- 逆に、**Whisper.net の `RuntimeLibraryOrder` による同一プロセス内のフォールバックだけに頼る構成は危険**である。DLL がロードできない状況で、次候補の初期化中にプロセスが落ちる。設計 §4.1 の「ライブラリの選択順だけに CPU 復帰を任せず、子プロセスの初期化と終了コードも判定する」は、実測で裏づけられた。
- **`RuntimeOptions.LoadedLibrary` は「実際に GPU で計算した」ことの証明にならない。** 設定画面の「実際の実行方式」は、whisper.cpp のログ（`no GPU found` / デバイス名）等で照合する必要がある（設計 §4.1 の「DLL がロードできたことと実際の GPU 演算が成功したことを区別する」どおり）。Whisper.net の `LogProvider.AddLogger` でこれらのログを取得できる。
- 今回、ワーカー内の「GPU が無いのに Vulkan と表示される」ケースは、ワーカーが `LoadedLibrary` だけを報告する実装だと誤表示する。ログ判定も報告に含める設計にすること。

### 3.3 未確認

- Iris Xe の実 Vulkan 認識、Vulkan loader 不在（`vulkan-1.dll` が無い）の挙動、NVIDIA 実機の CUDA→Vulkan→CPU は **未実測**（この VM では実施不可）。
- ARM64 VM の GPU は「Vulkan / CUDA 実デバイスなし」と記録した（仮想 GPU のみ）。

## 4. S-03: Runtime パッケージ別の配布サイズ増分

### 4.1 NuGet パッケージ実物（Whisper.net 1.9.1、nupkg の中身）

| パッケージ | nupkg サイズ | Windows 向けの内容（展開後 bytes） | 対象 |
| --- | --- | --- | --- |
| `Whisper.net.Runtime`（CPU） | 18,482,556 | win-arm64: `whisper` 469,504、`ggml-whisper` 80,896、`ggml-base` 626,176、`ggml-cpu` 728,064（計約 1.9 MB）。win-x64 約 2.0 MB、win-x86 約 1.7 MB。他に Linux / Android / iOS / WASM | Windows x86 / x64 / **ARM64**、他 OS |
| `Whisper.net.Runtime.NoAvx` | 2,531,679 | win-x64 / win-x86 の AVX 非対応 CPU 向け（約 2.5 MB） | x86 / x64 |
| `Whisper.net.Runtime.Vulkan` | 36,867,136 | win-x64: `ggml-vulkan-whisper.dll` 58,013,696 + CPU 一式の複製（計約 59.9 MB）。**Linux x64 の `libggml-vulkan-whisper.so` 58,670,792 も同梱** | Windows **x64 のみ**（Linux x64）。ARM64 の Vulkan は無い |
| `Whisper.net.Runtime.Cuda.Windows`（CUDA 13、`Whisper.net.Runtime.Cuda` 経由） | 142,586,522 | win-x64: `ggml-cuda-whisper.dll` 154,601,984 + CPU 一式 | Windows x64。**CUDA Toolkit 13.0.1 以上が前提**（`cublas64_13.dll` は非同梱） |
| `Whisper.net.Runtime.Cuda12.Windows`（CUDA 12） | 250,058,371 | win-x64: `ggml-cuda-whisper.dll` **563,957,248（537.8 MiB）** + CPU 一式 | Windows x64。CUDA Toolkit 12.4.1 以上が前提 |
| `Whisper.net.AllRuntimes` | 19,366 | メタパッケージ（上記すべて + CoreML / Metal / OpenVino 等に依存） | 設計どおり、無条件には入れない |

ライセンスは、確認した Whisper.net の Runtime 系 4 パッケージの nuspec がいずれも MIT 表記（Whisper.net 本体は LICENSE ファイル）。whisper.cpp 本体と NVIDIA の再配布条件（CUDA の `cublas` 等）は **今回は確認していない**。

### 4.2 単一 exe への増分（実測、§2.3 の除外あり構成、win-x64 CPU との差）

| 追加する runtime | 増分（bytes） | 増分（MiB） | 圧縮した場合 |
| --- | --- | --- | --- |
| Vulkan | 59,982,165 | 57.2 | 未計測 |
| CUDA 13 | 156,574,025 | 149.3 | 未計測 |
| Vulkan + CUDA 13 | 216,556,190 | 206.5 | 173,332,982（165.3 MiB、CUDA の DLL は圧縮が効きにくい） |
| （参考）CUDA 12 | 未ビルド | nupkg の DLL 実寸から約 537.8 MiB 増と見込む | — |

- **除外しないと Vulkan の増分が 121,205,438 bytes（実測の約 2 倍）** になる。Runtime パッケージの targets が、ビルド対象に関係なく Linux 用の `.so` や他アーキの DLL まで出力へ入れるため。本スパイクの `PruneNativeRuntimes` ターゲットで除外できた（認識の再確認済み）。
- CUDA を仕様から黙って落とさない（設計 S-03）ために記録する: **CUDA は (1) 増分が大きく、(2) CUDA Toolkit（`cublas64_13.dll` など）が利用者の PC に必要で、パッケージに同梱されない**。「置くだけで使える」配布とは相性が悪い。`cublas` を同梱する場合はサイズとライセンスの追加確認が要る。CUDA 12 版はさらに DLL が約 3.6 倍大きい。
- 圧縮は exe のディスクサイズを下げるが、実行時は展開後 160〜380 MiB が必要（§2.4）。

### 4.3 ロード経路

- Whisper.net は `runtimes/<backend>/win-x64/` を探し、無ければ `runtimes/win-x64/`（CPU）を使う。`RuntimeOptions.RuntimeLibraryOrder` でバックエンドの試行順を指定できる（本スパイクで確認）。
- 依存 DLL（`ggml-base` → `ggml-cpu` → `ggml-vulkan` → `ggml` → `whisper`）を順にロードする。1 つでも欠けると §3.1 のとおり同一プロセスでは安全に戻れない。

## 5. T02: ホットキー probe

### 5.1 実装

`spike/t02-hotkey/`（`hotkey-probe`）。メッセージ専用ウィンドウ（`HWND_MESSAGE`）に対し `RegisterHotKey(hwnd, id, mod | MOD_NOREPEAT, 0x4A)` を Win+J / Ctrl+Win+J / Ctrl+Alt+J で実行し、戻り値と `GetLastError`、セッション ID、ウィンドウステーション名を出力する。`listen <秒>` で `WM_HOTKEY` の到着を記録する。win-arm64（ネイティブ）と win-x64（エミュレーション）で実行した。

### 5.2 SSH セッション（セッション 0）での登録結果

| 組合せ | 登録 | Win32 エラー |
| --- | --- | --- |
| Win+J | **失敗** | 1459（`ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION`、「この操作には、対話型のウィンドウ ステーションが必要です」） |
| Ctrl+Win+J | **失敗** | 1459 |
| Ctrl+Alt+J | **失敗** | 1459 |

実行時の環境表示: `session_id=0`、`window_station=Service-0x0-…`、`interactive=False`（arm64 / x64 とも同じ）。

**この結果は、Win+J が奪えるか・使えるかについて何も示さない。** 3 組合せとも、キー自体ではなく「SSH のセッションが対話ウィンドウステーションを持たない」ことが原因で失敗している。登録成功時もキー配送は別問題であるため、本スパイクの結果から既定キーの可否を断定しない。

### 5.3 キー配送

**未確認。** 対話セッション（この VM には `explorer.exe` が動作する対話セッションが存在することだけは確認した）での確認手順書を [t02-hotkey-procedure.md](t02-hotkey-procedure.md) に残した。対話セッションへの起動（タスクスケジューラの対話タスク等）は、指示の範囲（SSH のみ）を超えるため行っていない。x64 実機（Iris Xe）側も未実施。

D-4（既定ホットキー）は未確定のまま。設計 §5 の順（Win+J → Ctrl+Win+J → Ctrl+Alt+J）で、手順書の表を埋めてから判断する。

## 6. 再現手順（概要）

```text
# Windows 側（リポジトリのルートから。.NET SDK 10 が必要）
dotnet publish spike/s01-csharp -c Release -r win-arm64 -p:Backend=cpu -o out\arm64-cpu
dotnet publish spike/s01-csharp -c Release -r win-x64   -p:Backend=all -o out\x64-all
out\arm64-cpu\spike-s01.exe --recognize <ggml-small.bin> <jfk.wav> --lang en --order cpu --repeat 3
out\x64-all\spike-s01.exe   --recognize <ggml-small.bin> <jfk.wav> --lang en --order vulkan --log
out\x64-all\spike-s01.exe   --bench-startup
```

`out\` 以下、モデル、音声は commit しない。

## 7. D-2（技術選定）への結論

**実測は設計の C# 推奨を支持し、覆す根拠は出ていない。** ただし、次の条件つきで確定する。

支持する根拠（実測）:

- C# で win-arm64 / win-x64 の単一 exe が作れ、ARM64 は **ネイティブで CPU 認識に成功**した。
- トレイ + ピルの WPF 骨格、スレッド・状態管理、後続の更新・DPAPI 等は C# の標準ライブラリで足りる見通し（最小の骨格（トレイ・ピル・認識・隔離デモ）は C# 約 300 行）。
- GPU 失敗時の隔離（子プロセス + CPU 再試行）は、設計 §3.2 の構成で成立することを最小コードで確認した。
- Rust 側は ARM64 VM でビルドが通らず（§2.7）、whisper.cpp のビルド管理負担という設計上の懸念が実際に現れた。

条件・要対応（C# のまま進める場合）:

1. **隔離は必須**: 同一プロセス内フォールバックはプロセスを落とす（§3.1）。T05 の認識ワーカーの設計どおりに子プロセス化する。
2. **native 展開方式の再決定**: `IncludeAllContentForSelfExtract` は動くが 460 ファイル / 160〜380 MiB を `%TEMP%` へ展開する。設計 §2.2 の自前展開（`%LOCALAPPDATA%` 配下、ハッシュ確認付き）に切り替えるか T05 で比較する。設定・モデルの基点は `Environment.ProcessPath` に固定する。
3. **VC++ 再頒布パッケージ依存**: 素の Windows 11 での起動は未検証。アプリローカル同梱（4 DLL）または README での前提記載が必要。
4. **サイズ**: 自己完結 + WPF + WinForms の CPU 版は 168〜181 MiB（無圧縮）、圧縮で約 69〜73 MiB。サイズ最優先なら WPF/WinForms の代替や Rust が有利になり得るが、**Rust の実測はなく、比較はできていない**。仕様にサイズの数値目標が無いため、許容範囲の判断は本人に確認したい（§9）。
5. **GPU の配布**: Vulkan は x64 のみ（+57 MiB）。ARM64 は CPU のみ。CUDA は +149 MiB かつ CUDA Toolkit が利用者側に必要（U-01 の判断材料）。
6. **確定の条件**: S-02 の終了条件（Iris Xe での Vulkan 実認識、loader 不在、NVIDIA の CUDA）は未達のため、D-2 の最終確定は x64 実機での確認後とする。

## 8. 未実測・未確認の項目

| 項目 | 理由 / 状態 |
| --- | --- |
| Iris Xe 実機の Vulkan 認識と速度、D-3 のベンチ | この VM に GPU が無く、x64 実機を使えない |
| NVIDIA 実機の CUDA、CUDA→Vulkan→CPU の経路 | 同上 |
| Vulkan loader 不在（`vulkan-1.dll` 無し）の挙動 | この VM の System32 に loader があり再現できない |
| x64 実機での exe サイズ以外の値（起動、速度、working set） | x64 の値は ARM64 上のエミュレーション。実機の値ではない |
| Rust（whisper-rs）の exe サイズ・起動・認識 | ビルドが通らなかった（§2.7）。コードは未コンパイル |
| VC++ 再頒布パッケージ / .NET SDK が無い素の Windows 11 での起動 | この VM には入っている |
| トレイアイコン・ピルの実際の描画、非フォーカス、DPI | SSH セッション 0 での実行。ウィンドウ生成と起動時間のみ計測 |
| ホットキーのキー配送、Win+J の OS 側との競合、既定キーの確定 | 対話セッションが必要（§5.3、手順書あり） |
| NAudio（録音）の ARM64 動作 | 本スパイクの範囲外（録音は T04） |
| 日本語音声の認識精度 | 範囲外（D-3） |
| CUDA の再配布ライセンス（`cublas` 等） | 未確認 |
| 圧縮時の Vulkan / CUDA 単体の exe サイズ | 未ビルド |

## 9. 未決・司令塔へ確認

1. **サイズの許容範囲**: 仕様に exe サイズの数値目標が無い。自己完結の C# は無圧縮で約 170〜180 MiB（圧縮で約 70 MiB）、GPU 同梱でさらに増える。許容を本人に確認したい（Rust 実測が無いため、決定の材料としては「C# の実測のみ」）。
2. **VC++ 再頒布パッケージ**: アプリローカル同梱を許容するか、README で前提とするか。ポータブル（置くだけで使える）の要件に直結する。
3. **CUDA の扱い**: CUDA Toolkit を利用者側の前提とする運用で仕様の「CUDA 対応」を満たすとみなすか。それとも `cublas` を同梱する（サイズ +、ライセンス確認）か。
4. **T02 の対話セッション確認**: 実行方法（本人が対話デスクトップで手順書を実施、またはタスクスケジューラの対話タスクを使う許可）を決めたい。
5. **Rust 比較を再試行するか**: x64 版 LLVM の導入など環境整備が要る。D-2 が C# 推奨で進む前提なら優先度は低いと考える。
6. **Rust の実測が無いことの扱い**: 設計 S-01 の終了条件は「C# と Rust の同一 whisper.cpp 系列の 10 秒認識」だが、Rust は未達。この項目を T01 の完了判定から外すか。
