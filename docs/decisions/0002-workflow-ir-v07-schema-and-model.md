# 2. Workflow IR v0.7のJSON Schema仕様およびC#モデル設計

## ステータス
承認済み (Accepted)

## コンテキスト
マクロシステムの記録・生成・検証・実行を安全かつ監査可能に行うため、外部AIやUIに依存しない厳格な中間表現（Workflow IR）を策定する必要がある。
任意コード実行や無限ループ、未定義パラメータによる脆弱性を防ぐため、厳格な型付け、スキーマ検証、未知プロパティの排除が必須となる。

## 決定事項
1. **スキーマ仕様と配置**:
   - JSON Schema Draft 2020-12に準拠し、`schemas/v0.7/workflow-ir.schema.json` を正本として配置。
   - `AI.MacroSystem.Application` に埋め込みリソースとしても同梱し、環境非依存で検証を実行可能にする。
2. **バージョニング規則**:
   - セマンティックバージョニングを採用し、初期契約を `0.7` とする。
   - IRルートの `schemaVersion` は定数 `"0.7"` に固定し、不一致時はシリアライズ・デシリアライズの両段階で拒否する。
   - 正式ワークフローの `version` は不変（イミュータブル）な整数値とする。
3. **構造化ノードとID管理**:
   - `Sequence`, `If`, `ForEach`, `While`, `Action` の5種類のノードを定義。
   - 各ノードは一意な `nodeId` を保持する。
   - `While` ノードは無限ループを未然に防ぐため、ループ制限 `maxIterations`（正の整数）を必須とする。
4. **型付きアクションパラメータと未型付けJsonElementの排除**:
   - `IActionTypeRegistry` を通じて登録されたアクション型（初期: `Delay`, `Log`, `WebNavigate`, `ExcelReadCell`, `ExcelWriteCell`）のみ許可する。
   - 未知のアクション型および未知の引数プロパティは即座に拒否する。
   - `JsonElement` の生インスタンスを実行時に引き回さず、専用の具象型またはC#プリミティブ型にデシリアライズする。
5. **安全な条件式ツリー**:
   - C#コード、JavaScript、PowerShell等の任意スクリプト実行形式を排除。
   - `BinaryConditionExpression`, `UnaryConditionExpression`, `LogicalConditionExpression`, `LiteralExpression`, `VariableExpression` による安全なAST（式ツリー）のみ許容する。
6. **未知フィールドの拒否**:
   - JSON Schemaで `additionalProperties: false` を全オブジェクトに適用。
   - `System.Text.Json` で `UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow` を強制。

## 理由
- AIが生成した未検証・悪意あるコードや予期しないプロパティの混入を静的段階で遮断するため。
- 条件式をAST化することで、実行エンジンの評価ロジックを安全に制限し、副作用の事前静的検証を可能にするため。

## 代替案
- **文字列ベースの条件式（例: `item.count > 0` の式パーサー）**: パース失敗時の安全性やスクリプトインジェクションのリスクがあるため不採用。
- **動的パラメータ（`Dictionary<string, object>` / `JsonElement`）**: 型安全性が損なわれ、実行時例外の原因となるため不採用。

## 影響
- アクションを追加する際は、専用のパラメータクラスを定義し `IActionTypeRegistry` およびJSON Schemaへ登録する必要がある。
- 実行エンジンは安全性が保証されたIRのみを受け取る前提で構築できる。
