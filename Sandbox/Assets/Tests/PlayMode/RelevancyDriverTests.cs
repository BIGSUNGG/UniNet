using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>드라이버 레벨 가시성 방어 — 일반 Destroy 직후 프레임의 틱이 파괴된 컴포넌트에 접근하지 않는다 (리뷰 라운드 1 ①).</summary>
    public sealed class RelevancyDriverTests
    {
        private const int Port = 7834;   // 타임아웃된 실행 시도의 잔존 리스너와 충돌 피하기 위한 전용 포트

        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();

        [UnityTest]
        public IEnumerator 일반_Destroy_직후_프레임의_틱은_예외_없이_돈다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var go = new GameObject("RelDestroyed");
            var player = go.AddComponent<SpawnablePlayer>();
            player.NetworkCullDistance = 10f;   // 컬 판정 경로 활성 — 파괴된 transform.position 접근이 문제였던 경로
            UniNetManager.Spawn(go);

            var server = UniNetEnvironment.Server;
            server.SetViewerPosition(1L, 0f, 0f, 0f);   // 첫 연결(호스트 클라) 뷰어 위치 — 거리 계산에 transform 접근

            LogAssert.ignoreFailingMessages = false;
            Object.Destroy(go);   // 일반 Destroy — 드라이버 스윕 경로 (명시 지원)
            yield return null;    // 스윕 + 틱 1프레임 — 파괴 엔트리 접근 없이 통과해야 한다
            yield return null;

            Assert.IsTrue(player == null, "오브젝트가 파괴됐다");
            Assert.IsNull(server.Get(player.NetId), "스윕 후 등록이 제거된다");
        }
    }
}
