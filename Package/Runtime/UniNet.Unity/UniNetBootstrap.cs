using UniNet.Core;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// Wiring placeholder — verifies the UniNet.Unity → UniNet.Core reference and that the
    /// MonoBehaviour compiles. Intended to be replaced by the real bindings (NetworkManager etc.) once P1 work starts.
    /// </summary>
    public sealed class UniNetBootstrap : MonoBehaviour
    {
        /// <summary>Value confirming that the core assembly reference resolves.</summary>
        public string CoreVersion => UniNetInfo.Version;
    }
}
