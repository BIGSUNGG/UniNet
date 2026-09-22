using System.Collections;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;
using UnityEngine.TestTools;

namespace UniNet.Tests
{
    /// <summary>P4 NetworkTransform 컴포넌트 — 기반 컴포넌트 RPC·서버 시뮬레이션·위치 복제 검증 (PlayMode 루프백).</summary>
    public sealed class NetworkTransformPlayTests
    {
        private static readonly int Port = 30000 + (System.Environment.TickCount % 2000) * 8 + 8;   // 실행별 랜덤 포트 — 플레이 모드 종료 후 리스너 소켓이 에디터 프로세스에 잔존하는 환경 문제 회피

        [TearDown]
        public void StopListeners() => UniNetManager.HostStop();

        [UnityTest]
        public IEnumerator SubmitMove가_기반_컴포넌트_RPC로_서버_시뮬레이션을_구동한다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var template = new GameObject("NTMove");
            template.AddComponent<NetworkTransform>();
            var go = UniNetManager.NetworkInstantiate(template);
            var nt = go.GetComponent<NetworkTransform>();
            UnityEngine.Object.Destroy(template);
            nt.MovementRule = (ref float x, ref float y, ref float z, float ix, float iy, float iz, float dt) =>
                x += ix * 4f * dt;   // 게임 이동 규칙 주입 (DIP) — 델리게이트(비직렬화·비전파)라 스폰 후 클론에 주입

            // SubmitMove → 기반 컴포넌트 ServerRpc(루프백) → 서버 권위 입력 → MovementRule 스텝
            nt.SubmitMove(1f, 0f, 0f);
            float deadline = Time.realtimeSinceStartup + 5f;
            while (go.transform.position.x <= 0.3f && Time.realtimeSinceStartup < deadline)
                yield return null;

            Assert.Greater(go.transform.position.x, 0.3f, "기반 컴포넌트 RPC가 서버 시뮬레이션을 구동한다 (라우팅 검증)");
        }

        [UnityTest]
        public IEnumerator SetNetworkPosition이_권위_좌표를_즉시_반영한다()
        {
            var hostTask = UniNetManager.HostAsync(Port);
            while (!hostTask.IsCompleted) yield return null;
            Assert.IsFalse(hostTask.IsFaulted, hostTask.Exception?.ToString());

            var template = new GameObject("NTPosition");
            template.AddComponent<NetworkTransform>();
            var go = UniNetManager.NetworkInstantiate(template);
            var nt = go.GetComponent<NetworkTransform>();
            UnityEngine.Object.Destroy(template);

            nt.SetNetworkPosition(new Vector3(5f, 1f, 6f));
            yield return null;   // 서버 틱 1프레임 — 권위 좌표 유지 확인

            Assert.AreEqual(5f, go.transform.position.x, 0.01f, "SetNetworkPosition — 권위 좌표 반영");
            Assert.AreEqual(6f, go.transform.position.z, 0.01f);
        }
    }
}
