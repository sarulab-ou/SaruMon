# 開発環境とビルド

SaruMon は C# / .NET 10 / Windows Forms で実装しています。配布版は .NET ランタイムを含む Windows x64 用の単一 EXE です。

## 必要なもの

- Windows x64
- .NET 10 SDK
- PowerShell 7 以降
- インストーラーを作成する場合は WSL Ubuntu 24.04 と NSIS 3

Windows 上の Git / GitHub CLI 操作には WSL Ubuntu を使います。.NET のビルドは Windows 上で行います。インストーラー用の NSIS は、スクリプトで Ubuntu 24.04 の配布元から固定版を取得します。システムへのインストールは行いません。

## 起動・テスト

リポジトリのルートで実行します。

```powershell
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
dotnet run --project src/SaruMon/SaruMon.csproj
```

テストのみ実行する場合:

```powershell
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
dotnet run --project tests/SaruMon.Tests/SaruMon.Tests.csproj --configuration Release
```

テストは追加のテストフレームワークに依存しないコンソールアプリです。失敗した場合は終了コードが 0 以外になり、成功時は件数を出力します。

## 配布版を作成

```powershell
./scripts/build.ps1
```

テスト後、自己完結型の単一 EXE と更新用 ZIP を生成します。

インストーラーも作成する場合は、最初にビルド用ツールを取得してから実行します。取得する NSIS は Ubuntu の `3.09-4ubuntu1` で、リポジトリ内の `.tools/nsis-root/` に展開します。

```powershell
./scripts/install-installer-tools.ps1
./scripts/build-installer.ps1
```

`build-installer.ps1` は通常、`build.ps1` によるテストと発行も実行します。配布用 EXE をビルド済みの場合は `-SkipBuild` で省略できます。取得済み NSIS の場所を指定する場合は `-NsisRoot <Windows のパス>`、WSL ディストリビューションを指定する場合は `-WslDistribution <名前>` を使用します。コンパイルは `-WX` を指定し、警告も失敗として扱います。

| 出力 | 用途 |
| --- | --- |
| `artifacts/publish/win-x64/SaruMon.exe` | 配布用の単一 EXE |
| `artifacts/SaruMon-win-x64.zip` | ポータブル版・アプリ内更新用 |
| `artifacts/SaruMon-win-x64.zip.sha256` | アプリ内更新で照合するハッシュ |
| `artifacts/SaruMon-0.1.3-Setup-x64.exe` | v0.1.3 インストーラー |
| `artifacts/SaruMon-0.1.3-SHA256SUMS.txt` | v0.1.3 インストーラーの SHA-256 |

ビルド成果物は Git に追加しません。公開手順は [releasing.md](releasing.md) を参照してください。

アプリアイコンの描画を変更した場合は、Windows 上で `./scripts/generate-icon.ps1` を実行し、`assets/SaruMon.ico` を再生成します。通常のビルドでは再生成は不要です。

## インストーラーの自動起動設定

「Windows ログイン時に起動」コンポーネントは既定で選択され、現在のユーザーのログイン時にインストール先の `SaruMon.exe --tray` を実行するよう登録します。対話形式ではチェックを外せます。サイレントインストールで登録を無効にする場合は `/NoStartup` を指定します。

```powershell
Start-Process -FilePath '.\artifacts\SaruMon-0.1.3-Setup-x64.exe' -ArgumentList '/S', '/NoStartup' -WindowStyle Hidden -Wait
```

ZIP 版はアプリ内で自動起動を有効にします。インストーラーの自動起動登録は、Windows の通知領域でアイコンを常時表示する設定を変更しません。表示位置は起動後に利用者が調整します。

## 通信アイコン

通知領域では、受信を緑の下向き矢印、送信を赤の上向き矢印で表します。各方向の直近の速度から発光の強さを独立に選び、通信がない方向は暗い灰色、未接続はオレンジの × にします。累積バイト数を発光の判定に使いません。通信量の差分計算とアイコン描画を変更する場合は、片方向・双方向・待機・未接続の各表示を確認します。

## TCP 接続先と逆引き

`ConnectionsView` は、選択インタフェースのローカル IP に対応する現在の TCP 接続を表示します。IPv4 / IPv6 の宛先 IP・ポート・状態・ローカル側エンドポイントを保持し、タブ表示中は 2 秒ごとに更新します。接続ごとのバイト数や通信内容は取得しません。

逆引きは既定で無効です。`ReverseDnsResolver` はタイムアウト 2 秒、最大 256 件のメモリー内キャッシュを使用し、成功結果は 5 分、失敗結果は 1 分保持します。各ビューは最大 4 件ずつ並列に問い合わせます。逆引き無効化、対象インタフェース変更、タブ非表示時は処理をキャンセルし、IP とポートの表示を優先します。名前が取得できない場合は `—` とし、PTR 名を閲覧サイト名として扱いません。

## UDP 送信先とログ保存

`UdpConnectionsView` と `UdpMonitorClient` は、利用者が開始した場合だけ管理者権限の補助プロセスを起動し、Windows の ETW イベントから UDP 送信を観測します。UI プロセスは昇格しません。初期状態は停止で、停止操作・アプリ終了時に補助プロセスを終了します。タブ非表示中も観測を続けますが、表示更新と新しい逆引きは止めます。

ビューは 2 秒ごとに送信先 IP・ポート、任意の逆引き名、初回検出時刻、累積送信バイト数、送信数、最終検出時刻、ローカル側エンドポイントを更新します。初回検出は観測中にその宛先への送信を初めて見つけた時刻で、ソケットの作成時刻ではありません。TCP のバイト数と UDP の受信はこの集計の対象外です。

`UdpFlowTracker` は観測開始後のフローを最大 2,048 件保持し、期限切れや容量超過による既存行の削除を行いません。上限に達した後も既存フローの集計を続け、新規フローの未記録イベント数を `DroppedEvents` で報告します。各行は累積送信バイト数 `Bytes` と送信数 `Packets` を持ちます。ETW 側のイベント欠落と容量上限は UI と CSV の記録範囲へ反映します。

停止後は集計を保持し、新規開始・選択インタフェース変更でクリアします。対象変更では補助プロセスのフィルターも更新し、利用できる IP がない場合はデータを表示しません。フォームの終了処理はビューの `ShutdownAsync` を待ち、補助プロセスの終了を確認してから閉じます。

「ログを保存」はクリック時のスナップショットを固定し、`ConnectionLogExporter` で UTF-8 BOM 付き CSV を書き出します。保存時に追加の DNS 問い合わせは行わず、逆引きが有効な場合だけキャッシュ済みの名前を含めます。時刻はローカルオフセットを含む ISO 8601、CSV の引用符・改行・区切り文字と表計算ソフトの式として解釈される文字列を処理します。書き出し完了後に保存先を置き換えます。記録の自動保存は行いません。

CSV の列は次の 12 項目です。プロトコル欄は `UDP`、総送信量は `UdpFlowInfo.Bytes`、送信イベント数は `UdpFlowInfo.Packets` です。

```text
インタフェース,プロトコル,ローカルIP,ローカルポート,宛先IP,宛先ポート,逆引きホスト名,初回検出時刻,最終送信時刻,総送信量（バイト）,送信イベント数,記録範囲
```

ETW の UDP イベントには IPv6 のスコープ ID が含まれないため、UDP のアドレス照合はスコープを除外します。異なるインタフェースで同じ IPv6 アドレスを使用する場合は区別できません。IPv4-mapped IPv6 は IPv4 に正規化します。

## 構成

| 場所 | 内容 |
| --- | --- |
| `src/SaruMon.Core/` | インタフェースの列挙・統計取得、差分と速度の計算、TCP 接続と UDP フローの処理 |
| `src/SaruMon/` | Windows Forms、通知領域、設定、二重起動制御、接続先ビューと逆引き |
| `src/SaruMon/Udp/` | UDP 観測用の ETW・昇格補助プロセス・プロセス間通信 |
| `src/SaruMon/Updates/` | GitHub Releases の確認、ダウンロード、検証と置き換え |
| `tests/SaruMon.Tests/` | 差分計算、リリース検証、更新 ZIP、プロセス間通知のテスト |
| `scripts/` | 配布物の生成と統合テスト |
| `installer/SaruMon.nsi` | NSIS インストーラー・アンインストーラー |

設定ファイルは `%LOCALAPPDATA%\SaruMon\settings.json`、更新処理のログは `%LOCALAPPDATA%\SaruMon\update.log` です。開発用起動では、配布 EXE の自動置き換えは行いません。

配布物に含める依存ライブラリーの情報は [third-party-notices.md](third-party-notices.md) を参照してください。
