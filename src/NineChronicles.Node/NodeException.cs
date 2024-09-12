namespace NineChronicles.Node;

public class NodeException(NodeExceptionType code, string message)
{
    public int Code { get; } = (int)code;

    public string Message { get; } = message;
}
