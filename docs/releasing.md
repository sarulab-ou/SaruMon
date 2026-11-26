# リリース

アプリの更新元は `sarulab-ou/SaruMon` の最新の正式版 GitHub Release です。プレリリースは通常の更新対象にしません。公開リポジトリのリリースを想定しており、アプリは GitHub の認証情報を保存・要求しません。

## 配布形式

- タグは `vMAJOR.MINOR.PATCH`（例: `v0.1.0`）。プロジェクトの `Version` と一致させます。
- `SaruMon-win-x64.zip` の直下に、自己完結型の単一ファイル `SaruMon.exe` を入れます。
- `SaruMon-win-x64.zip.sha256` には ZIP の SHA-256 とファイル名を記載します。
- `scripts/build.ps1` がテスト・発行・ZIP 作成・ハッシュ生成を一括で行います。ビルド成果物は Git 管理対象外です。

ZIP とハッシュは同じ GitHub Release に配置します。ハッシュ照合はダウンロード破損を検出するためのもので、コード署名の代わりではありません。現在のビルドスクリプトにコード署名処理は含まれていません。

## 公開前の確認

1. プロジェクトの `Version` を更新し、`./scripts/build.ps1` を実行します。
2. ZIP を作業フォルダへ展開し、`SaruMon.exe` が単体で起動することを確認します。
3. インタフェースの選択・送受信表示・切断と再接続・ウィンドウを閉じた後の常駐を確認します。
4. もう一度 EXE を開き、既存ウィンドウが前面に出ることを確認します。
5. 更新の確認、設定の保持、終了が正常に動作することを確認します。更新版がある場合は、旧版から実際の更新と再起動を確認します。

公開リリースがなくても、ビルド後に `./scripts/test-updater.ps1` でローカル更新処理を検証できます。`artifacts/updater-smoke/` の検証用コピーだけを使い、終了待ち・EXE 置換・再起動・二重起動・更新対象が変更された場合の拒否を確認します。エラーケースでは検証用スクリプトのエラーダイアログだけをファイル出力に置き換えます。配布元 EXE は変更しません。これは GitHub 経由のダウンロードを含む更新全体の試験とは別です。

## GitHub Actions

通常の push / pull request では `Build` が Windows 上でテストと配布ファイルの生成を行います。

公開するコミットを確定して対応する `v*` タグを push すると、`Release` がタグとプロジェクトの版番号を検証し、Windows で作成した成果物を GitHub Release にアップロードします。タグの push は正式版の公開操作です。ワークフローを追加しただけではリリースは公開されません。

Windows で Git / GitHub CLI を使う場合は WSL Ubuntu から実行してください。リリースワークフローの GitHub CLI は Ubuntu runner 上で実行します。

## 参考

- [.NET の単一ファイル配布](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/)
- [GitHub Actions setup-dotnet](https://github.com/actions/setup-dotnet)
- [GitHub CLI release create](https://cli.github.com/manual/gh_release_create)
