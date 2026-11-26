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
| `artifacts/SaruMon-0.1.1-Setup-x64.exe` | v0.1.1 インストーラー |
| `artifacts/SaruMon-0.1.1-SHA256SUMS.txt` | v0.1.1 インストーラーの SHA-256 |

ビルド成果物は Git に追加しません。公開手順は [releasing.md](releasing.md) を参照してください。

アプリアイコンの描画を変更した場合は、Windows 上で `./scripts/generate-icon.ps1` を実行し、`assets/SaruMon.ico` を再生成します。通常のビルドでは再生成は不要です。

## 構成

| 場所 | 内容 |
| --- | --- |
| `src/SaruMon.Core/` | インタフェースの列挙・統計取得、差分と速度の計算 |
| `src/SaruMon/` | Windows Forms、通知領域、設定、二重起動制御 |
| `src/SaruMon/Updates/` | GitHub Releases の確認、ダウンロード、検証と置き換え |
| `tests/SaruMon.Tests/` | 差分計算、リリース検証、更新 ZIP、プロセス間通知のテスト |
| `scripts/` | 配布物の生成と統合テスト |
| `installer/SaruMon.nsi` | NSIS インストーラー・アンインストーラー |

設定ファイルは `%LOCALAPPDATA%\SaruMon\settings.json`、更新処理のログは `%LOCALAPPDATA%\SaruMon\update.log` です。開発用起動では、配布 EXE の自動置き換えは行いません。
