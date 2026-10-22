using System;
using System.Reflection;
using NUnit.Framework;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// TRS-001 — NetworkTransform의 옵트인 회전·스케일 동기화. NetworkTransformRemoteRenderTests 패턴(가짜 클럭 +
    /// 리플렉션 + 직접 notify)을 따른다: 서버 샘플링(T1 전반·T5), 리모트 Slerp 보간(T1), 각변위 스냅(T2),
    /// 스케일 즉시 적용(T3), Awake 스폰 시드(T4), 오너 적용·서버 무시(T6), 후발 합류(T7).
    /// 네트워크 스택 없이 생성 적용 경로와 동일한 "필드 쓰기 + notify" 시퀀스를 재현한다 — 회전 쿼터니언은
    /// 컴포넌트별 notify 4회(한 델타), 스케일은 3회로 호출한다 (선언 순 와이어 적용 순서와 동일).
    /// </summary>
    public sealed class NetworkTransformRotationScaleTests
    {
        private static readonly BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly Func<double> OriginalClock = UniNetTime.LocalClock;

        private double _clock;
        private NetworkServer _prevServer;

        [SetUp]
        public void SetUp()
        {
            _clock = 1000.0;   // arbitrary non-zero start — far from the monotonicity guard's initial state
            UniNetTime.Reset();
            UniNetTime.LocalClock = () => _clock;
        }

        [TearDown]
        public void TearDown()
        {
            UniNetTime.LocalClock = OriginalClock;
            UniNetTime.Reset();
            UniNetEnvironment.SetClient(null);
            UniNetEnvironment.SetServer(_prevServer);   // restore the ambient server (null when none was set)
            _prevServer = null;
            foreach (var nt in UnityEngine.Object.FindObjectsByType<NetworkTransform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(nt.gameObject);
        }

        private NetworkTransform NewRemote()
        {
            var go = new GameObject("nt-rs-remote");
            var nt = go.AddComponent<NetworkTransform>();
            InvokeAwake(nt);
            var client = new NetworkClient();
            client.SetLocalConnId(1);
            client.ApplyOwner(nt.NetId, 2);   // owner = conn 2 → this instance is a remote viewer
            UniNetEnvironment.SetClient(client);
            typeof(NetworkBehaviour).GetMethod("MarkClientRegistered", Flags).Invoke(nt, null);   // IsClient gate
            return nt;
        }

        private NetworkTransform NewOwner()
        {
            var go = new GameObject("nt-rs-owner");
            var nt = go.AddComponent<NetworkTransform>();
            InvokeAwake(nt);
            var client = new NetworkClient();
            client.SetLocalConnId(1);
            client.ApplyOwner(nt.NetId, 1);   // owner = local conn → IsOwner = true
            UniNetEnvironment.SetClient(client);
            typeof(NetworkBehaviour).GetMethod("MarkClientRegistered", Flags).Invoke(nt, null);
            return nt;
        }

        /// <summary>Authority instance — real server entry + server registration flag (the watch polls Server.GetOwner).</summary>
        private NetworkTransform NewAuthority()
        {
            var go = new GameObject("nt-rs-authority");
            var nt = go.AddComponent<NetworkTransform>();
            InvokeAwake(nt);
            var server = new NetworkServer();
            server.RegisterSceneObject(nt.NetId, new object[] { nt });
            typeof(NetworkBehaviour).GetMethod("MarkServerRegistered", Flags).Invoke(nt, null);
            _prevServer = UniNetEnvironment.Server;
            UniNetEnvironment.SetServer(server);
            return nt;
        }

        /// <summary>Applies one received rotation delta (as the generated apply would: per-component set + notify ×4) and advances the clock.</summary>
        private void SampleRot(NetworkTransform nt, Quaternion q, double gapSeconds)
        {
            Set(nt, "_rx", q.x);
            Set(nt, "_ry", q.y);
            Set(nt, "_rz", q.z);
            Set(nt, "_rw", q.w);
            Notify(nt, "OnNetRotComponentChanged");
            Notify(nt, "OnNetRotComponentChanged");
            Notify(nt, "OnNetRotComponentChanged");
            Notify(nt, "OnNetRotComponentChanged");
            _clock += gapSeconds;
        }

        /// <summary>Applies one received scale delta (per-component set + notify ×3) — no clock advance, no Update: immediate apply must not need either.</summary>
        private static void ApplyScale(NetworkTransform nt, float x, float y, float z)
        {
            Set(nt, "_sx", x);
            Set(nt, "_sy", y);
            Set(nt, "_sz", z);
            Notify(nt, "OnNetScaleComponentChanged");
            Notify(nt, "OnNetScaleComponentChanged");
            Notify(nt, "OnNetScaleComponentChanged");
        }

        private static void Notify(NetworkTransform nt, string method)
            => typeof(NetworkTransform).GetMethod(method, Flags).Invoke(nt, new object[] { 0f });

        private static void Set(NetworkTransform nt, string field, float value)
            => typeof(NetworkTransform).GetField(field, Flags).SetValue(nt, value);

        private static float Get(NetworkTransform nt, string field)
            => (float)typeof(NetworkTransform).GetField(field, Flags).GetValue(nt);

        private static void InvokeUpdate(NetworkTransform nt)
            => typeof(NetworkTransform).GetMethod("Update", Flags).Invoke(nt, null);

        private static float Angle(NetworkTransform nt, Quaternion expected)
            => Quaternion.Angle(nt.transform.rotation, expected);

        /// <summary>EditMode에서는 Awake가 자동 실행되지 않으므로(비-ExecuteInEditMode) 시드 검증 전 명시 호출 — 기존 리플렉션 명시 호출 관행과 동일.</summary>
        private static void InvokeAwake(NetworkTransform nt)
            => typeof(NetworkTransform).GetMethod("Awake", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(nt, null);

        [Test]
        public void T1_서버_샘플은_리모트에서_Slerp_보간된다()
        {
            // server side — SyncRotation on: the game rotates the transform, Update samples it into the replicated fields
            var server = NewAuthority();
            server.SyncRotation = true;
            var target = Quaternion.Euler(0f, 90f, 0f);
            server.transform.rotation = target;
            InvokeUpdate(server);
            Assert.AreEqual(target.x, Get(server, "_rx"), 1e-4f, "서버 샘플 — transform.rotation → 복제 필드 (x)");
            Assert.AreEqual(target.y, Get(server, "_ry"), 1e-4f, "서버 샘플 (y)");
            Assert.AreEqual(target.z, Get(server, "_rz"), 1e-4f, "서버 샘플 (z)");
            Assert.AreEqual(target.w, Get(server, "_rw"), 1e-4f, "서버 샘플 (w)");

            // remote side — a steady sweep 0→90° (3°/샘플 @ 60Hz = 180°/s): the renderer must follow BEHIND the stream (Slerp), not hold the latest
            var nt = NewRemote();
            nt.InterpolationDelay = 0.12f;
            for (int i = 1; i <= 30; i++)
            {
                SampleRot(nt, Quaternion.Euler(0f, i * 3f, 0f), 1.0 / 60.0);
                InvokeUpdate(nt);   // tick per sample — each pending delta is consumed and buffered per-tick (the real flow)
            }

            var latest = Quaternion.Euler(0f, 90f, 0f);
            Assert.Greater(Angle(nt, latest), 1f, "정상 회전 — 렌더는 스트림 뒤를 따라간다 (최신 회전에 즉시 도달하지 않는다 = 보간)");
            Assert.Greater(Angle(nt, Quaternion.identity), 1f, "시작 회전에서 스윕이 진행했다");
        }

        [Test]
        public void T2_회전_텔레포트는_보간없이_스냅한다()
        {
            var nt = NewRemote();
            nt.InterpolationDelay = 0.12f;

            // steady warm-up stream — the turn-rate EMA arms the discontinuity judgment
            for (int i = 1; i <= 10; i++)
            {
                SampleRot(nt, Quaternion.Euler(0f, i * 3f, 0f), 1.0 / 60.0);
                InvokeUpdate(nt);   // tick per sample — per-tick buffering (the real flow)
            }
            InvokeUpdate(nt);
            var latest = Quaternion.Euler(0f, 30f, 0f);
            Assert.Greater(Angle(nt, latest), 0.5f, "프리켄디트 — 직전까지 보간 중 (최신 회전 홀드 아님)");

            // angular jump of 120° — beyond max(SnapThreshold, 3×EMA): buffer cleared, the jump renders immediately
            var jumped = Quaternion.Euler(0f, 150f, 0f);
            SampleRot(nt, jumped, 1.0 / 60.0);
            InvokeUpdate(nt);
            Assert.Less(Angle(nt, jumped), 0.01f, "각변위 스냅 — 불연속은 버퍼를 클리어하고 즉시 렌더 (회전 미끄러짐 없음)");

            // the stream resumes and interpolates from the snapped pose
            for (int i = 1; i <= 5; i++)
            {
                SampleRot(nt, Quaternion.Euler(0f, 150f + i * 3f, 0f), 1.0 / 60.0);
                InvokeUpdate(nt);   // tick per sample — per-tick buffering (file-wide consistency, r33 ISSUE-3)
            }
            Assert.Greater(Angle(nt, Quaternion.Euler(0f, 165f, 0f)), 0.5f, "스냅 이후 스트림 재개 — 새 자세 기준으로 다시 보간");
        }

        [Test]
        public void T3_스케일은_보간없이_즉시_적용된다()
        {
            var nt = NewRemote();

            ApplyScale(nt, 2f, 1f, 3f);   // notify only — no Update, no clock advance
            Assert.AreEqual(new Vector3(2f, 1f, 3f), nt.transform.localScale, "풀 벡터 델타 — notify 시점 즉시 적용 (Update 불필요)");

            ApplyScale(nt, 2f, 4f, 3f);   // single-component delta — unchanged components keep their current values
            Assert.AreEqual(new Vector3(2f, 4f, 3f), nt.transform.localScale, "단일 성분 델타 — 미변경 성분 보존");
        }

        [Test]
        public void T4_Awake는_스폰_변환으로_회전과_스케일을_시드한다()
        {
            var go = new GameObject("nt-rs-seed");
            go.transform.rotation = Quaternion.Euler(30f, 60f, 90f);   // non-identity spawn pose
            go.transform.localScale = new Vector3(2f, 3f, 4f);
            var nt = go.AddComponent<NetworkTransform>();
            InvokeAwake(nt);   // Awake seeds the replicated fields

            var q = go.transform.rotation;
            Assert.AreEqual(q.x, Get(nt, "_rx"), 1e-5f, "스폰 회전 시드 (x) — (0,0,0,0) 쿼터니언 방지");
            Assert.AreEqual(q.y, Get(nt, "_ry"), 1e-5f, "스폰 회전 시드 (y)");
            Assert.AreEqual(q.z, Get(nt, "_rz"), 1e-5f, "스폰 회전 시드 (z)");
            Assert.AreEqual(q.w, Get(nt, "_rw"), 1e-5f, "스폰 회전 시드 (w)");
            float magnitude = Mathf.Sqrt(Get(nt, "_rx") * Get(nt, "_rx") + Get(nt, "_ry") * Get(nt, "_ry")
                                         + Get(nt, "_rz") * Get(nt, "_rz") + Get(nt, "_rw") * Get(nt, "_rw"));
            Assert.AreEqual(1f, magnitude, 1e-3f, "시드 쿼터니언은 단위 크기 — 와이어 유효성");
            Assert.AreEqual(2f, Get(nt, "_sx"), 1e-5f, "스폰 스케일 시드 (x)");
            Assert.AreEqual(3f, Get(nt, "_sy"), 1e-5f, "스폰 스케일 시드 (y)");
            Assert.AreEqual(4f, Get(nt, "_sz"), 1e-5f, "스폰 스케일 시드 (z)");
        }

        [Test]
        public void T5_옵트인이_꺼진_서버는_회전과_스케일을_샘플링하지_않는다()
        {
            var server = NewAuthority();   // SyncRotation/SyncScale default false — 기존 게임 기본 동작 불변
            var seedW = Get(server, "_rw");

            server.transform.rotation = Quaternion.Euler(0f, 45f, 0f);
            server.transform.localScale = new Vector3(3f, 3f, 3f);
            InvokeUpdate(server);

            Assert.AreEqual(0f, Get(server, "_rx"), 1e-6f, "옵트인 오프 — 회전 필드 불변 (델타 전송 없음)");
            Assert.AreEqual(0f, Get(server, "_ry"), 1e-6f, "옵트인 오프 — y 성분 불변 (샘플링됐다면 0.383로 변했을 값)");
            Assert.AreEqual(seedW, Get(server, "_rw"), 1e-6f, "옵트인 오프 — w 성분 불변");
            Assert.AreEqual(1f, Get(server, "_sx"), 1e-6f, "옵트인 오프 — 스케일 필드 불변");
            Assert.Less(Quaternion.Angle(server.transform.rotation, Quaternion.Euler(0f, 45f, 0f)), 0.01f,
                "게임 로직 회전은 그대로 — 서버가 transform을 되돌려 쓰지 않는다 (간섭 없음)");
        }

        [Test]
        public void T6_오너는_회전을_적용하고_서버는_무시한다()
        {
            // owner — rotation is not predicted: the server-driven value applies directly at Update
            var owner = NewOwner();
            var target = Quaternion.Euler(0f, 120f, 0f);
            SampleRot(owner, target, 1.0 / 60.0);
            InvokeUpdate(owner);
            Assert.Less(Angle(owner, target), 0.01f, "오너 — 회전 노티파이 적용 (포지션과 달리 IsOwner 가드 없음)");

            // server — its transform is the sampling source: the echoed delta must not drive it
            var server = NewAuthority();
            var before = server.transform.rotation;
            SampleRot(server, target, 1.0 / 60.0);
            InvokeUpdate(server);
            Assert.Less(Quaternion.Angle(server.transform.rotation, before), 0.001f, "서버 — 회전 노티파이 무시 (transform 무간섭, 권위 유지)");
        }

        [Test]
        public void T7_후발_합류는_현시점_회전으로_시작하고_스트림에_수렴한다()
        {
            var nt = NewRemote();
            nt.InterpolationDelay = 0.12f;

            // the server has been spinning — at join time the full-state spawn/catch-up carries the CURRENT pose (200°)
            var joinPose = Quaternion.Euler(0f, 200f, 0f);
            SampleRot(nt, joinPose, 1.0 / 60.0);
            InvokeUpdate(nt);
            Assert.Less(Angle(nt, joinPose), 0.01f, "후발 합류 — 현시점 회전 즉시 렌더 (단일 샘플 홀드, 스폰 시점 회전 아님)");

            // the ongoing stream continues from there and converges — no backward regression toward the join pose
            for (int i = 1; i <= 10; i++)
            {
                SampleRot(nt, Quaternion.Euler(0f, 200f + i * 3f, 0f), 1.0 / 60.0);
                InvokeUpdate(nt);   // tick per sample — per-tick buffering (r33 ISSUE-3 consistency)
            }
            Assert.Less(Angle(nt, Quaternion.Euler(0f, 230f, 0f)), 30f, "스트림 수렴 — 최신 방향을 향한다 (역방향 회귀 없음)");
            Assert.Greater(Angle(nt, joinPose), 1f, "합류 시점 자세에서 진행했다 (동결 아님)");
        }
        // ── r33 보강 — SetNetworkRotation 계약 + SyncScale 서버 샘플링 ──

        [Test]
        public void SetNetworkRotation은_서버에서_회전을_즉시_기록하고_복제_필드에_반영한다()
        {
            var server = NewAuthority();
            server.SyncRotation = true;

            var pose = Quaternion.Euler(15f, 45f, 0f);
            var posBefore = server.transform.position;   // r34 FINDING-1 — rotation-only contract pin
            Assert.DoesNotThrow(() => server.SetNetworkRotation(pose));
            Assert.AreEqual(pose, server.transform.rotation, "즉시 transform 반영");

            InvokeUpdate(server);   // 서버 샘플 → 복제 필드
            Assert.AreEqual(pose.x, Get(server, "_rx"), 1e-4f, "복제 필드 (x)");
            Assert.AreEqual(pose.z, Get(server, "_rz"), 1e-4f, "복제 필드 (z)");
            Assert.AreEqual(pose.w, Get(server, "_rw"), 1e-4f, "복제 필드 (w)");
            Assert.AreEqual(posBefore, server.transform.position, "SetNetworkRotation은 위치를 건드리지 않는다");

            // SetNetworkPosition과 무관하게 회전만 설정됨을 재확인 — 위치는 건드리지 않는다
            Assert.DoesNotThrow(() => server.SetNetworkRotation(Quaternion.Euler(0f, 90f, 0f)));
            InvokeUpdate(server);
            Assert.AreEqual(Mathf.Sin(45f * Mathf.Deg2Rad), Get(server, "_ry"), 0.001f, "재호출 — 회전 갱신 (90° yaw의 쿼터니언 y 성분)");
        }

        [Test]
        public void SyncScale이_켜진_서버는_localScale을_샘플링한다()
        {
            var server = NewAuthority();
            server.SyncScale = true;
            server.transform.localScale = new UnityEngine.Vector3(1.5f, 2.5f, 3.5f);

            InvokeUpdate(server);   // 서버 샘플 → 복제 필드

            Assert.AreEqual(1.5f, Get(server, "_sx"), 1e-4f, "스케일 샘플 (x)");
            Assert.AreEqual(2.5f, Get(server, "_sy"), 1e-4f, "스케일 샘플 (y)");
            Assert.AreEqual(3.5f, Get(server, "_sz"), 1e-4f, "스케일 샘플 (z)");
        }
    }
}
