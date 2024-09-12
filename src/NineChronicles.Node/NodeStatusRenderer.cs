using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace NineChronicles.Node;

public class NodeStatusRenderer
{
    private readonly Subject<bool> _preloadStatusSubject = new();

    public void PreloadStatus(bool isPreloadStarted = false)
    {
        _preloadStatusSubject.OnNext(isPreloadStarted);
    }

    public IObservable<bool> EveryChangedStatus() => _preloadStatusSubject.AsObservable();
}
