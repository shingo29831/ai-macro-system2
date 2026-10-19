# 1. 初期アーキテクチャ設計およびMVVM・DIフレームワークの選定

## ステータス
承認済み (Accepted)

## コンテキスト
新規マクロデスクトップアプリの構築にあたり、Windows 11 x64環境で高信頼性・保守性・安全性を担保するビルド基盤とアーキテクチャ構造を定義する必要がある。
操作記録からWorkflow IRを構築し、検証・承認・監査を経て安全に実行するため、UIと業務ロジック、外部インフラ（ブラウザ・Excel・DB）の関心事を分離しなければならない。

## 決定事項
1. **プロジェクト分割と依存方向**:
   - `AI.MacroSystem.Domain` (`net10.0`): 外部非依存。エンティティ・値オブジェクト・列挙型のみ。
   - `AI.MacroSystem.Application` (`net10.0`): ユースケース・IR抽象契約。Domainのみ参照。
   - `AI.MacroSystem.Infrastructure` (`net10.0`): 具象実装（SQLite, Playwright, Excel COM）。Application, Domainを参照。
   - `AI.MacroSystem.App` (`net10.0-windows`): WPFデスクトップUI。Application, Infrastructureを参照。
   - `AI.MacroSystem.UnitTests` (`net10.0`) / `AI.MacroSystem.IntegrationTests` (`net10.0-windows`): テスト基盤。

2. **DIフレームワーク**:
   - .NET標準の `Microsoft.Extensions.Hosting` および `Microsoft.Extensions.DependencyInjection` を採用。
   - アプリケーション起動時に汎用ホストを構成し、ライフサイクル管理および依存注入を一元化する。

3. **MVVMフレームワーク**:
   - 公式推奨かつ軽量な `CommunityToolkit.Mvvm` を採用。
   - ソースジェネレーターを活用し、コードビハインドへのロジック漏れを防止しながら冗長なボイラープレートを最小化する。

4. **依存関係とビルド設定の一元管理**:
   - `Directory.Packages.props` による Central Package Management (CPM) を適用。
   - `Directory.Build.props` で Nullable・ImplicitUsings・TreatWarningsAsErrors を全プロジェクトに一律強制。

## 理由
- 標準的かつ最小限の依存関係により、過剰な抽象化フレームワークへのロックインを防ぐ。
- DomainからWPFやCOMへの不要な参照を排除することで、実行エンジンや検証ルールの完全な単体テスト容易性を確保する。

## 代替案
- **Prism / ReactiveUI**: 機能豊富だが、学習コストが高く過度な依存を持ち込むため今回は採用を見送り。
- **Autofac / Castle Windsor**: サードパーティDIコンテナは現行の.NET標準DIに対して優位性が薄く、標準DIで十分要件を満たせるため不採用。

## 影響
- UI層（`App`）とロジック層が明確に分離され、ViewModel経由でのみ状態変更とコマンド実行が制御される。
- 各レイヤーの独立性が高まり、将来的なCLI実行やヘッドレス実行エンジンの追加が容易になる。
