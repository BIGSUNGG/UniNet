using UniNet.Core;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// 배선 확인용 플레이스홀더 — UniNet.Unity → UniNet.Core 참조와 MonoBehaviour
    /// 컴파일을 검증한다. P1 착수 시 NetworkManager 등 실제 바인딩으로 대체한다.
    /// </summary>
    public sealed class UniNetBootstrap : MonoBehaviour
    {
        /// <summary>코어 어셈블리 참조가 걸려 있는지 확인하는 값.</summary>
        public string CoreVersion => UniNetInfo.Version;
    }
}
