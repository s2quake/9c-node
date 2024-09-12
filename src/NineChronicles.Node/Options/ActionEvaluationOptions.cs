using Libplanet.Node.Options;
using Microsoft.Extensions.Options;

namespace NineChronicles.Node.Options;

[Options(Position)]
public sealed class ActionEvaluationOptions : OptionsBase<ActionEvaluationOptions>
{
    public const string Position = "ActionEvaluation";
}
