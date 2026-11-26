# SaruMon

Windows の通知領域で、選択したネットワークインタフェースの通信状態を表示する常駐アプリです。

[最新版をダウンロード](https://github.com/sarulab-ou/SaruMon/releases/latest)（Windows x64・.NET ランタイム同梱）

## 使い方

1. `SaruMon-win-x64.zip` を、書き込み可能なフォルダへ展開します。
2. `SaruMon.exe` を起動し、監視するインタフェースを選びます。選択は次回起動時にも引き継ぎます。
3. ウィンドウを閉じるか最小化すると、通知領域で監視を続けます。アイコンが見えない場合は、タスクバー右端の隠れているアイコンを確認してください。

通知領域のアイコンは、0.5 秒ごとの通信量に応じて変わります。

| 色 | 状態 |
| --- | --- |
| 緑 | 受信中 |
| 青 | 送信中 |
| 紫 | 送受信中 |
| 灰 | 接続中・通信なし |
| オレンジ | 未接続、または対象を利用できない状態 |

アイコンからウィンドウを開くと、受信・送信速度と通信量を確認できます。右クリックメニューから終了できます。起動中にもう一度 `SaruMon.exe` を開くと、既存のウィンドウを表示します。

Windows 起動時の自動起動は、アプリ内の設定で有効にできます。初期状態では無効です。

## 更新

**ヘルプ → 更新を確認** から、[GitHub Releases](https://github.com/sarulab-ou/SaruMon/releases) の最新版を確認できます。更新版があれば、確認後にダウンロードし、SHA-256 を照合して置き換え・再起動します。更新にはインターネット接続と、実行ファイルのあるフォルダへの書き込み権限が必要です。

更新対象は公開済みの正式版です。開発用の `dotnet run` からは、配布版と同じ自動置き換えは行いません。

## 監視する情報

Windows が提供するインタフェース単位の送受信バイト数を読み取ります。パケットの内容・接続先・プロセス別通信は収集しません。速度は各サンプル間のバイト数の差から計算するため、瞬間的な最大速度を示すものではありません。管理者権限は不要です。

## 開発

Windows と .NET 10 SDK を使用します。配布版はランタイムを含む Windows x64 用の単一 EXE です。

```powershell
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
dotnet run --project src/SaruMon/SaruMon.csproj
```

テストと配布ファイルの生成:

```powershell
./scripts/build.ps1
```

出力:

- `artifacts/publish/win-x64/SaruMon.exe`
- `artifacts/SaruMon-win-x64.zip`
- `artifacts/SaruMon-win-x64.zip.sha256`

テストのみ実行する場合:

```powershell
$env:PYTHONUTF8 = '1'
$env:PYTHONIOENCODING = 'utf-8'
$env:CI = 'true'
dotnet run --project tests/SaruMon.Tests/SaruMon.Tests.csproj --configuration Release
```

公開手順と確認項目は [docs/releasing.md](docs/releasing.md) を参照してください。
