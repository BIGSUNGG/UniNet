using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// FR-02 fixture — two independently changing unconditional replicated fields on ONE sub-object, so a loss in one
    /// field can be observed while the other keeps producing deltas (unreliable-stream convergence regression).
    /// Field indexes (delta mask bits): 0=FieldA, 1=FieldB.
    /// </summary>
    public sealed partial class UnreliableDuo : NetworkBehaviour
    {
        [Replicated]
        public int FieldA;

        [Replicated]
        public int FieldB;
    }
}
