# ADR 0005: 実行前IR Validator・データフロー検証およびループ安全契約

## ステータス
承認済 (Accepted)

## コンテキスト
構文スキーマ（Draft 2020-12）による検証だけでは、未知アクション引数の妥当性、NodeIdの重複、データフロー（未定義変数の参照や分岐不確定参照）、および無限ループリスクを完全に排除できない。
実行エンジンに渡されるIRは、これら全ての検証をパスした「不変かつ型安全な保証」を持つ必要があり、検証に合格しないIRから実行可能インスタンスが生成されることを防がなければならない。

## 決定事項

### 1. 不変 ValidatedWorkflowDefinition による実行契約
- 検証通過の証明型として `ValidatedWorkflowDefinition` を定義。
- コンストラクタは `internal` に制限し、`WorkflowValidator` のみが生成可能とする。
- 外部UI、AIプロバイダー、未検証ローダーからの直接インスタンス化を物理的に禁止する。

### 2. データフロー（Def-Use）および分岐スコープ規則
- **確実定義規則 (Definite Assignment)**: 変数は先行するアクション（例: `ExcelReadCell` の `OutputVariable`）によって確実に定義されていなければ後続で参照できない。前方参照および未定義参照は `UNDEFINED_VARIABLE` として即時拒絶する。
- **分岐不確定の拒絶規則 (Branch Uncertainty)**: `If` 分岐において、`Then` と `Else` の両方で同一変数が定義されない限り、分岐外への変数伝播は認めず、参照を試みた場合は `BRANCH_VARIABLE_UNCERTAIN` として拒絶する。
- **ループスコープ規則**: `ForEach` の `itemVariable` はループ本体ブロック内でのみ可視とし、外側での参照は `UNDEFINED_VARIABLE` とする。

### 3. 反復ループ上限の中央設定 (CentralLoopSettings)
- `WhileNode`:
  - `maxIterations` 必須（1以上5,000以下）。未指定または0以下・超過は `LOOP_LIMIT_INVALID` / `LOOP_LIMIT_EXCEEDED` で拒絶。
- `ForEachNode`:
  - `maxIterations` 省略時は `DefaultForEachMaxIterations` (1,000回) を安全既定値として適用。明示指定時は上限5,000件で強制。
- 決定理由: 自動化マクロにおける無限ポーリングや巨大コレクションによるOOM・ハングを防止するため。

### 4. 機械可読エラーコード体系
`WorkflowValidationError` にて以下を標準化:
- `SYNTAX_ERROR`: JSON構文エラー
- `SCHEMA_VIOLATION`: JSON Schema評価エラー
- `UNSUPPORTED_SCHEMA_VERSION`: スキーマバージョン不一致
- `DUPLICATE_NODE_ID`: NodeId一意性違反
- `UNKNOWN_ACTION_TYPE`: 未登録アクション種別
- `INVALID_ACTION_PARAMETER`: パラメータ型・値域違反（負のDelay、不正URL等）
- `MISSING_REQUIRED_PARAMETER`: 必須引数欠落
- `UNDEFINED_VARIABLE`: 未定義変数参照
- `BRANCH_VARIABLE_UNCERTAIN`: 分岐不確定変数参照
- `LOOP_LIMIT_EXCEEDED` / `LOOP_LIMIT_INVALID`: ループ上限超過・不正
