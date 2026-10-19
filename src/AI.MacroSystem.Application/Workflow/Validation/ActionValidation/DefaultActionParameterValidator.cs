// @role: 標準組み込みアクション（Delay, Log, WebNavigate, Excel等）の引数契約・値域バリデータ
namespace AI.MacroSystem.Application.Workflow.Validation.ActionValidation;

using System.Text.RegularExpressions;
using AI.MacroSystem.Domain.Workflow.Actions;

public sealed class DefaultActionParameterValidator : IActionParameterValidator
{
    private static readonly Regex VariableNameRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex CellAddressRegex = new(@"^[A-Za-z]+[1-9][0-9]*(:[A-Za-z]+[1-9][0-9]*)?$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedLogLevels = new(StringComparer.OrdinalIgnoreCase)
    {
        "Debug", "Info", "Warn", "Warning", "Error"
    };

    public bool CanValidate(string actionType)
    {
        return actionType is "Delay" or "Log" or "WebNavigate" or "ExcelReadCell" or "ExcelWriteCell";
    }

    public IEnumerable<WorkflowValidationError> Validate(string actionType, ActionParameters parameters, string nodeId, string path)
    {
        switch (parameters)
        {
            case DelayParameters delay:
                if (delay.Milliseconds < 0)
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.InvalidActionParameter,
                        nodeId,
                        $"{path}.parameters.milliseconds",
                        $"Delay milliseconds must be non-negative, but was {delay.Milliseconds}.");
                }
                else if (delay.Milliseconds > 3_600_000)
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.InvalidActionParameter,
                        nodeId,
                        $"{path}.parameters.milliseconds",
                        $"Delay milliseconds cannot exceed 3,600,000 (1 hour), but was {delay.Milliseconds}.");
                }
                break;

            case LogParameters log:
                if (string.IsNullOrWhiteSpace(log.Message))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.MissingRequiredParameter,
                        nodeId,
                        $"{path}.parameters.message",
                        "Log message cannot be null or empty.");
                }
                if (!AllowedLogLevels.Contains(log.Level))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.InvalidActionParameter,
                        nodeId,
                        $"{path}.parameters.level",
                        $"Log level '{log.Level}' is not recognized. Allowed levels: {string.Join(", ", AllowedLogLevels)}.");
                }
                break;

            case WebNavigateParameters nav:
                if (string.IsNullOrWhiteSpace(nav.Url))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.MissingRequiredParameter,
                        nodeId,
                        $"{path}.parameters.url",
                        "WebNavigate url cannot be empty.");
                }
                else if (!Uri.TryCreate(nav.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.InvalidActionParameter,
                        nodeId,
                        $"{path}.parameters.url",
                        $"WebNavigate url '{nav.Url}' must be a valid absolute HTTP or HTTPS URL.");
                }
                break;

            case ExcelReadCellParameters excelRead:
                if (string.IsNullOrWhiteSpace(excelRead.WorkbookPath))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.MissingRequiredParameter,
                        nodeId,
                        $"{path}.parameters.workbookPath",
                        "WorkbookPath cannot be empty.");
                }
                if (string.IsNullOrWhiteSpace(excelRead.SheetName))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.MissingRequiredParameter,
                        nodeId,
                        $"{path}.parameters.sheetName",
                        "SheetName cannot be empty.");
                }
                if (string.IsNullOrWhiteSpace(excelRead.CellAddress) || !CellAddressRegex.IsMatch(excelRead.CellAddress))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.InvalidActionParameter,
                        nodeId,
                        $"{path}.parameters.cellAddress",
                        $"CellAddress '{excelRead.CellAddress}' is not a valid Excel cell notation (e.g. 'A1').");
                }
                if (string.IsNullOrWhiteSpace(excelRead.OutputVariable) || !VariableNameRegex.IsMatch(excelRead.OutputVariable))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.InvalidActionParameter,
                        nodeId,
                        $"{path}.parameters.outputVariable",
                        $"OutputVariable '{excelRead.OutputVariable}' must be a valid identifier name.");
                }
                break;

            case ExcelWriteCellParameters excelWrite:
                if (string.IsNullOrWhiteSpace(excelWrite.WorkbookPath))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.MissingRequiredParameter,
                        nodeId,
                        $"{path}.parameters.workbookPath",
                        "WorkbookPath cannot be empty.");
                }
                if (string.IsNullOrWhiteSpace(excelWrite.SheetName))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.MissingRequiredParameter,
                        nodeId,
                        $"{path}.parameters.sheetName",
                        "SheetName cannot be empty.");
                }
                if (string.IsNullOrWhiteSpace(excelWrite.CellAddress) || !CellAddressRegex.IsMatch(excelWrite.CellAddress))
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.InvalidActionParameter,
                        nodeId,
                        $"{path}.parameters.cellAddress",
                        $"CellAddress '{excelWrite.CellAddress}' is not a valid Excel cell notation (e.g. 'A1').");
                }
                if (excelWrite.Value is null)
                {
                    yield return new WorkflowValidationError(
                        ValidationErrorCode.MissingRequiredParameter,
                        nodeId,
                        $"{path}.parameters.value",
                        "ExcelWriteCell value expression cannot be null.");
                }
                break;
        }
    }
}
