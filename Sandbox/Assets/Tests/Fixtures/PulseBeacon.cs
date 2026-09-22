using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>Second fixture type for channel-budget tests — one unconditional field (delta = 4-byte mask + 4-byte int = 8 bytes).</summary>
    public sealed partial class PulseBeacon : NetworkBehaviour
    {
        [Replicated] public int Charge;
    }
}
