using System.Reactive.Subjects;
using NineChronicles.RPC.Shared.Exceptions;

namespace NineChronicles.Node;

public class ExceptionRenderer
{
    private readonly Subject<(RPCException, string)> _exceptionRenderSubject = new();

    public void RenderException(RPCException code, string msg)
    {
        _exceptionRenderSubject.OnNext((code, msg));
    }

    public IObservable<(RPCException Code, string Message)> EveryException()
        => _exceptionRenderSubject;
}
