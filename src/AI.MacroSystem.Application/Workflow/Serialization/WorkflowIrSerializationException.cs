namespace AI.MacroSystem.Application.Workflow.Serialization;

public sealed class WorkflowIrSerializationException : Exception
{
    public WorkflowIrSerializationException(string message) : base(message)
    {
    }

    public WorkflowIrSerializationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
