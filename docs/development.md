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
| `artifacts/SaruMon-0.1.6-Setup-x64.exe` | v0.1.6 インストーラー |
| `artifacts/SaruMon-0.1.6-SHA256SUMS.txt` | v0.1.6 インストーラーの SHA-256 |

ビルド成果物は Git に追加しません。公開手順は [releasing.md](releasing.md) を参照してください。

アプリアイコンの描画を変更した場合は、Windows 上で `./scripts/generate-icon.ps1` を実行し、`assets/SaruMon.ico` を再生成します。通常のビルドでは再生成は不要です。

## インストーラーの自動起動設定

「Windows ログイン時に起動」コンポーネントは既定で選択され、現在のユーザーのログイン時にインストール先の `SaruMon.exe --tray` を実行するよう登録します。対話形式ではチェックを外せます。サイレントインストールで登録を無効にする場合は `/NoStartup` を指定します。

```powershell
Start-Process -FilePath '.\artifacts\SaruMon-0.1.6-Setup-x64.exe' -ArgumentList '/S', '/NoStartup' -WindowStyle Hidden -Wait
```

ZIP 版はアプリ内で自動起動を有効にします。インストーラーの自動起動登録は、Windows の通知領域でアイコンを常時表示する設定を変更しません。表示位置は起動後に利用者が調整します。

## 通信アイコン

通知領域では白地を使い、受信を緑の下向き矢印、送信を赤の上向き矢印で表します。矢印は太く、色と濃さを固定します。0.5 秒ごとの更新で、直前の計測区間に表示対象の通信があった方向だけ表示します。両方向とも対象の通信がない場合は白地だけ、未接続はオレンジの × にします。

`AppSettings.UnicastOnlyIndicator` は既定で `false` です。ウィンドウ下部のチェックボックスと通知領域の右クリックメニューで同期して切り替えます。オフでは送受信バイト数の差分、オンではユニキャスト送受信パケット数の差分が正かどうかを方向ごとに判定します。設定の切り替えは表示対象だけを変え、速度・合計・グラフの値や集計開始点は変えません。ユニキャストのカウンターを取得できない場合は判定できないことを表示し、全体の通信量で代用しません。

`TrafficNetwork.ReadCounters` は、同じ `GetIPStatistics()` の結果からバイト数と `UnicastPacketsReceived` / `UnicastPacketsSent` を取得します。ポーリングは既存の 500 ミリ秒タイマーを共有し、ユニキャスト表示のための ETW、補助プロセス、DNS 問い合わせ、ログ生成は追加しません。パケット数から方向の活動だけを判定するため、パケットの内容は取得しません。TCP・UDP や LAN・インターネットの区別はしません。

[.NET 10 の Windows 実装](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Net.NetworkInformation/src/System/Net/NetworkInformation/SystemIPInterfaceStatistics.cs)は、統計オブジェクトの作成時に `GetIfEntry2` を一度呼び、バイト数とユニキャストパケット数の各プロパティで同じ取得済み構造体のフィールドを読み取ります。そのため、同じオブジェクトからこれらの値を読む際に OS への追加照会は発生しません。

変更時は、バイト数だけ増えてユニキャストパケット数が増えない場合、片方向・双方向、待機・未接続を確認します。初回、カウンターのリセット、取得できないカウンターの復帰、インタフェース切り替えで過去の累積値による矢印が出ないこと、通常のバイト集計が保持されることも確認します。設定を切り替えた際の表示と再起動時の設定保持を確認し、負荷を測定していない場合は CPU 使用率などの数値を推定で記載しないでください。

## TCP 接続先と逆引き

`ConnectionsView` の「現在の接続」は、選択インタフェースのローカル IP に対応する OS の TCP 接続一覧です。IPv4 / IPv6 の宛先 IP・ポート・状態・ローカル側エンドポイントを保持し、この小タブの表示中は 2 秒ごとに更新します。通常権限で取得し、接続ごとのバイト数や通信内容は含めません。

「相手ごとの通信量」は `TcpPeerView` と `TrafficMonitorClient(MonitoringMode.Tcp)` を使います。利用者が開始した時だけ補助プロセスを昇格し、TCP の送信・受信・接続イベントを観測します。`TcpPeerTracker` は選択ローカル IP の通信を相手 IP ごとにまとめ、異なるポート・接続も同じ相手の `SentBytes` / `ReceivedBytes` に加算します。`RecordConnection` は Connect / Accept イベントごとに `ConnectionCount` を増やします。監視開始前からの接続については回数を推測せず、接続イベント未観測なら 0 です。ループバックで両端が選択ローカル IP に含まれる場合は、両端のイベントを加算します。`FirstSeen` / `LastSeen` は、監視中にその相手の接続イベントまたは通信を最初・最後に観測した時刻です。最大 2,048 相手を期限切れなしで保持し、上限後は既存相手の合計を維持しながら新規相手の未記録イベントを数えます。

TCP の「ログを保存」はクリック時点のスナップショットを固定し、`TcpLogExporter` で次の 7 列を UTF-8 BOM 付き CSV に保存します。相手名は逆引きが有効でキャッシュ済みの場合だけ含めます。CSV はポート・ローカル側・メタデータ列を追加しません。開始時刻と最終時刻はローカルオフセット付き ISO 8601 です。部分集計の場合は画面、保存ダイアログの題名、既定のファイル名、保存結果に「一部未記録」と表示します。

```text
相手IP,相手名,上り合計（バイト）,下り合計（バイト）,接続回数,開始時刻,最終時刻
```

逆引きは既定で無効です。`ReverseDnsResolver` はタイムアウト 2 秒、最大 256 件のメモリー内キャッシュを使用し、成功結果は 5 分、失敗結果は 1 分保持します。各ビューは最大 4 件ずつ並列に問い合わせます。逆引き無効化、対象インタフェース変更、タブ非表示時は処理をキャンセルし、IP とポートの表示を優先します。名前が取得できない場合は `—` とし、PTR 名を閲覧サイト名として扱いません。

## UDP 送信先とログ保存

`UdpConnectionsView` と `TrafficMonitorClient(MonitoringMode.Udp)` は、利用者が開始した場合だけ管理者権限の補助プロセスを起動し、Windows の ETW イベントから UDP 送信を観測します。TCP と UDP は独立したクライアント・補助プロセス・セッションを使い、片方の開始停止がもう片方に影響しません。UI プロセスは昇格しません。初期状態は停止で、停止操作・アプリ終了時に補助プロセスを終了します。タブ非表示中も観測を続けますが、表示更新と新しい逆引きは止めます。

ビューは 2 秒ごとに送信先 IP・ポート、任意の逆引き名、開始時刻、累積送信バイト数、送信数、最終時刻、ローカル側エンドポイントを更新します。開始時刻と最終時刻は監視中にその宛先への送信を最初・最後に観測した時刻で、ソケットの作成・終了時刻ではありません。UDP に接続回数は追加しません。TCP のバイト数と UDP の受信はこの集計の対象外です。

`UdpFlowTracker` は観測開始後のフローを最大 2,048 件保持し、期限切れや容量超過による既存行の削除を行いません。上限に達した後も既存フローの集計を続け、新規フローの未記録イベント数を `DroppedEvents` で報告します。各行は累積送信バイト数 `Bytes` と送信数 `Packets` を持ちます。ETW 側のイベント欠落と容量上限は UI と CSV の記録範囲へ反映します。

停止後は集計を保持し、新規開始・選択インタフェース変更でクリアします。対象変更では補助プロセスのフィルターも更新し、利用できる IP がない場合はデータを表示しません。フォームの終了処理はビューの `ShutdownAsync` を待ち、補助プロセスの終了を確認してから閉じます。

「保存」はクリック時のスナップショットを固定し、`ConnectionLogExporter` で UTF-8 BOM 付き CSV を書き出します。保存時に追加の DNS 問い合わせは行わず、逆引きが有効な場合だけキャッシュ済みの名前を含めます。時刻はローカルオフセットを含む ISO 8601、CSV の引用符・改行・区切り文字と表計算ソフトの式として解釈される文字列を処理します。書き出し完了後に保存先を置き換えます。記録の自動保存は行いません。

CSV の列は次の 12 項目です。プロトコル欄は `UDP`、総送信量は `UdpFlowInfo.Bytes`、送信イベント数は `UdpFlowInfo.Packets` です。

```text
インタフェース,プロトコル,ローカルIP,ローカルポート,宛先IP,宛先ポート,逆引きホスト名,開始時刻,最終時刻,総送信量（バイト）,送信イベント数,記録範囲
```

ETW のネットワークイベントには IPv6 のスコープ ID が含まれないため、TCP / UDP の通信量の照合はスコープを除外します。異なるインタフェースで同じ IPv6 アドレスを使用する場合は区別できません。IPv4-mapped IPv6 は IPv4 に正規化します。「現在の接続」の OS テーブルではスコープを維持します。

## 構成

| 場所 | 内容 |
| --- | --- |
| `src/SaruMon.Core/` | インタフェースの列挙・統計取得、差分と速度の計算、TCP 接続・相手別通信量と UDP フローの処理 |
| `src/SaruMon/` | Windows Forms、通知領域、設定、二重起動制御、接続先ビューと逆引き |
| `src/SaruMon/Monitoring/` | TCP / UDP 観測用の ETW・昇格補助プロセス・プロセス間通信 |
| `src/SaruMon/Updates/` | GitHub Releases の確認、ダウンロード、検証と置き換え |
| `tests/SaruMon.Tests/` | 差分計算、リリース検証、更新 ZIP、プロセス間通知のテスト |
| `scripts/` | 配布物の生成と統合テスト |
| `installer/SaruMon.nsi` | NSIS インストーラー・アンインストーラー |

設定ファイルは `%LOCALAPPDATA%\SaruMon\settings.json`、更新処理のログは `%LOCALAPPDATA%\SaruMon\update.log` です。開発用起動では、配布 EXE の自動置き換えは行いません。

配布物に含める依存ライブラリーの情報は [third-party-notices.md](third-party-notices.md) を参照してください。
