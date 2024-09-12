namespace NineChronicles.Node;

public enum NodeExceptionType
{
    NoAnyPeer = 0x01,

    DemandTooHigh = 0x02,

    TipNotChange = 0x03,

    MessageNotReceived = 0x04,

    ActionTimeout = 0x05,
}
