# AI Macro System (ai-macro-system-2)

操作記録・マルチソースログ抽出・ローカル優先AI・構造化 Workflow IR (v0.7) に基づく、高信頼性・安全第一のデスクトップマクロ自動化システムです。

---

## 1. 主な機能とアーキテクチャ

- **高精度操作記録**: Windows 低レベルフック (`SetWindowsHookEx`) および UIAutomation による入力・クリック・対象ウィンドウの自動取得
- **マルチログ相関抽出**: 時間窓相関・信頼度スコアリング・ノイズ除去によるアプリ横断転記の自動推論
- **安全な 8 段階静的検証**: JSON Schema、型・引数範囲、データ参照スコープ、制御フロー、事前/事後条件、許可スコープ、副作用・リスク評価
- **不変バージョン & 承認フロー**: 正式版の不変管理、ハッシュ照合による計画承認、高リスク操作の個別承認強制
- **堅牢な実行アダプター**: Chrome (Web)・Excel (COM/CSV)・独自アプリ対応、Unknown 状態の自動再試行防止
- **クラッシュリカバリー**: 異常終了後の起動時に外部状態を照合し、勝手な自動再試行を排した安全な手動復旧

---

## 2. 動作要件・前提環境

| 項目 | 要件 | 備考 |
| :--- | :--- | :--- |
| **OS** | Windows 10 (1809 以降) / Windows 11 (64-bit) | Win32 API / WPF / UIAutomation を使用するため Windows 必須 |
| **SDK / Runtime** | .NET 8.0 SDK 以降 (Windows デスクトップランタイム) | WPF アプリケーションのビルド・実行に対応 |
| **対象ブラウザ** | Google Chrome (または Chromium 系ブラウザ) | Web 自動化・記録対象 |
| **対象表計算** | Microsoft Excel (デスクトップ版) | COM 連携 (未インストール環境向けに CSV フォールバック機能あり) |
| **IDE / CLI** | Visual Studio 2022 (v17.8+) または VS Code / .NET CLI | Visual Studio の場合は「.NET デスクトップ開発」ワークロードが必要 |

---

## 3. 環境構築手順

### 3.1. リポジトリのクローン
```bash
git clone <リポジトリURL>
cd ai-macro-system-2
```

### 3.2. .NET SDK の確認
```bash
dotnet --version
```
※ 出力されるバージョンが `8.0.xxx` 以上であることを確認してください。

### 3.3. 依存パッケージの復元
```bash
dotnet restore AiMacroSystem.sln
```

### 3.4. ビルド
```bash
# Debug ビルド
dotnet build AiMacroSystem.sln

# Release ビルド
dotnet build AiMacroSystem.sln -c Release
```

---

## 4. テストの実行

仕様書第 18 章に基づく全受け入れ条件（型・スコープ検証、AI フォールバック、副作用制御、クラッシュリカバリー等）を検証します。

```bash
dotnet test tests/AiMacroSystem.Tests/AiMacroSystem.Tests.csproj
```

特定のテストカテゴリのみ実行する場合:
```bash
dotnet test tests/AiMacroSystem.Tests/AiMacroSystem.Tests.csproj --filter "FullyQualifiedName~SpecificationAcceptanceTests"
```

---

## 5. アプリケーションの起動方法

### 5.1. CLI から起動
```bash
# Debug 実行
dotnet run --project src/AiMacroSystem/AiMacroSystem.csproj

# Release 実行
dotnet run --project src/AiMacroSystem/AiMacroSystem.csproj -c Release
```

### 5.2. Visual Studio から起動
1. `AiMacroSystem.sln` を Visual Studio 2022 で開く。
2. ソリューションエクスプローラーで `AiMacroSystem` を右クリックし、「**スタートアップ プロジェクトに設定**」を選択。
3. `F5`（デバッグ実行）または `Ctrl + F5`（デバッグなし実行）を押下。

> **Note (管理者権限について)**:
> 操作対象アプリケーション（ブラウザや業務ソフト）が管理者権限で動作している場合、Windows のセキュリティ制約 (UIPI) により低レベルイベントの監視や操作注入が制限されることがあります。その場合は、本アプリケーションも管理者権限（「管理者として実行」）で起動してください。

---

## 6. 基本操作フロー

1. **操作記録**:
   - 「操作記録・AI変換」タブで「かんたん記録開始」をクリック。
   - 画面上部に小型フローティングオーバーレイが表示され、メイン画面が最小化されます。
   - ブラウザでの検索や Excel への入力操作を行い、オーバーレイの「停止してAI変換」を押下。
2. **ビジュアルブロック編集**:
   - 記録から合成された操作ステップがビジュアルブロックとして展開されます。
   - ステップの並び替え、追加・削除、パラメータ変更を行い、下書き保存（ドラフト）または正式版として確定。
3. **静的検証・承認**:
   - 「検証結果・レビュー」タブで、8段階静的検証（許可オリジン、ファイルパス、型・スコープ）の合格を確認。
   - 承認者名を入力し、計画を承認。
4. **マクロ実行**:
   - 「マクロ実行」をクリックすると、実行対象ウィンドウの選択ダイアログ（サムネイル付き）が表示されます。
   - 操作先ウィンドウを指定して実行を開始。実行中はモニター最上部に進行オーバーレイが表示され、一時停止・キャンセルが可能です。
5. **異常終了時のリカバリー**:
   - 万が一実行中にクラッシュや強制終了が発生した場合、次回起動時に未終端実行を自動検知します。
   - 「復旧判断」タブにて外部状態の照合結果を確認し、安全に破棄または新規再実行を選択できます。

---

## 7. プロジェクト構成

```text
ai-macro-system-2/
├── src/
│   └── AiMacroSystem/
│       ├── Domain/             # Workflow IR モデル、状態機械、契約・計画定義、Enum
│       ├── Application/        # 実行エンジン、マルチログ抽出、AI オーケストレーター、静的検証、復旧
│       ├── Infrastructure/     # 実環境ドライバ (Chrome/Excel/Win32/UIA)、SQLite 永続化
│       ├── Presentation/       # WPF 画面 (MainWindow, オーバーレイ, ウィンドウ選択ダイアログ)
│       ├── Schemas/            # Workflow IR v0.7 JSON Schema
│       ├── App.cs              # DI 構成・エントリポイント
│       └── AiMacroSystem.csproj
├── tests/
│   └── AiMacroSystem.Tests/    # 仕様書第18章の受け入れテスト群
├── .gitignore
├── README.md
└── AiMacroSystem.sln
```
