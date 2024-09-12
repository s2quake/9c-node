using Bencodex.Types;
using Libplanet.Action;
using Libplanet.Action.Loader;

namespace NineChronicles.Node;

public class ActionLoader : IActionLoader
{
    private readonly IActionLoader _actionLoader = new Nekoyume.Action.Loader.NCActionLoader();

    public ActionLoader()
    {
    }

    public IAction LoadAction(long index, IValue value) =>
        _actionLoader.LoadAction(index, value);
}
