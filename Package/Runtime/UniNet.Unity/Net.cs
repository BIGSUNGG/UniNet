using System;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// 호출 사이트 편의 진입점 — 파일 상단에 <c>using static UniNet.Unity.Net;</c>를 두면
    /// <see cref="NetworkInstantiate(GameObject)"/>·<see cref="NetworkDestroy(GameObject)"/>를
    /// 일반 Instantiate/Destroy처럼 한정자 없이 쓸 수 있다. 실제 구현은 <see cref="UniNetManager"/>에 있다 (전달만).
    /// </summary>
    public static class Net
    {
        /// <summary>원본(프리팹·템플릿)을 복제·등록·전파한다 — 반환값이 등록된 인스턴스다 (UniNetManager 전달).</summary>
        public static GameObject NetworkInstantiate(GameObject original)
            => UniNetManager.NetworkInstantiate(original);

        /// <summary>위치·회전을 지정해 복제·등록·전파한다 (UniNetManager 전달).</summary>
        public static GameObject NetworkInstantiate(GameObject original, Vector3 position, Quaternion rotation)
            => UniNetManager.NetworkInstantiate(original, position, rotation);

        /// <summary>구성 콜백으로 비직렬화 초기 상태(private [Replicated] 등)를 세팅해 복제·등록·전파한다 — 콜백은 복제 직후·전파 직전에 실행된다 (UniNetManager 전달).</summary>
        public static GameObject NetworkInstantiate(GameObject original, Action<GameObject> configure)
            => UniNetManager.NetworkInstantiate(original, configure);

        /// <summary>서버 네트워크 파괴 — 전 클라에 파괴를 전파하고 로컬도 파괴한다 (UniNetManager 전달).</summary>
        public static void NetworkDestroy(GameObject instance)
            => UniNetManager.NetworkDestroy(instance);
    }
}
