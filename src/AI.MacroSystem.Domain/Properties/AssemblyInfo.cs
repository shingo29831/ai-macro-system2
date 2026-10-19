// @role: アセンブリ属性および内部型アクセス許可の設定
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("AI.MacroSystem.Application")]
[assembly: InternalsVisibleTo("AI.MacroSystem.UnitTests")]
[assembly: InternalsVisibleTo("AI.MacroSystem.IntegrationTests")]
