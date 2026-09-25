using System.Collections.Generic;
using UniNet.Core;
using UniNet.Unity;

namespace UniNet.Tests
{
    /// <summary>
    /// FastArray test fixture (see ADR-0020) — List and array collection fields covering all element kinds:
    /// primitives, an [Message] element, and a custom-serializer element.
    /// </summary>
    public sealed partial class FastArrayTank : NetworkBehaviour
    {
        [Replicated(Notify = nameof(OnScores))]
        public List<int> Scores = new();

        [Replicated]
        public string[] Names = new string[0];

        [Replicated(Serializer = typeof(PackedFlagsSerializer))]
        public uint[] Flags = new uint[0];

        /// <summary>RepNotify call count for Scores (delta batches received).</summary>
        public int ScoreNotifyCalls;

        /// <summary>Previous state delivered to the last Scores RepNotify call (shallow copy).</summary>
        public List<int> LastPrevScores;

        private void OnScores(List<int> prev)
        {
            ScoreNotifyCalls++;
            LastPrevScores = prev;
        }
    }
}
