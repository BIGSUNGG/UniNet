using System;
using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// P4 이동 예측 컴포넌트 — 트랜스폼 위치의 서버 권위 복제 + 소유 클라 예측·조정 + 리모트 인터폴레이션 (ADR-0013).
    /// 같은 오브젝트의 다른 NetworkBehaviour와 독립 슬롯(SubId)으로 동작한다 (ADR-0010 멀티컴포넌트).
    /// 이동 규칙은 <see cref="MovementRule"/>(게임 주입), 입력은 <see cref="SubmitMove"/>, 시뮬레이션 게이트는
    /// <see cref="SimulationEnabled"/>로 제공한다 — 서버(권위)와 소유 클라(예측)가 같은 규칙을 공유해야 하며,
    /// 규칙이 어긋나면 예측 오차가 상수가 된다.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class NetworkTransform : NetworkBehaviour
    {
        /// <summary>이동 규칙 — 서버(권위)와 예측 클라가 동일 적용한다. null이면 시뮬레이션 정지 (위치 복제만). 게임이 주입한다 (DIP).</summary>
        public delegate void MovementStep(ref float x, ref float y, ref float z, float inX, float inY, float inZ, float dt);

        /// <summary>게임 이동 규칙 — null이면 위치 복제만 동작한다 (서버 주입).</summary>
        public MovementStep? MovementRule;

        /// <summary>시뮬레이션 게이트 — false면 서버·예측 모두 이동하지 않는다 (사망·이동 불가 상태). 게임이 제어한다.</summary>
        public bool SimulationEnabled = true;

        /// <summary>리모트 렌더 지연 (초) — 서버 시간보다 이만큼 뒤 시점으로 보간 렌더한다 (틱 스케줄링 적용 시점 제어).</summary>
        public float InterpolationDelay = 0.12f;

        /// <summary>예측 조정 — 오차가 snapThreshold²를 넘으면 서버 값 스냅, 아니면 잔여 오차의 softRate를 흡수한다.</summary>
        public float SnapThreshold = 0.5f;

        /// <summary>예측 소프트 조정 비율 — 서버 상태 수신 시 잔여 오차를 이 비율만큼 즉시 흡수한다.</summary>
        public float SoftRate = 0.15f;

        /// <summary>소유 클라 이동 예측 (기본 true) — false면 소유 클라도 권위 좌표를 렌더한다.</summary>
        public bool PredictOwner = true;

        [Replicated(Notify = nameof(OnNetXChanged))] private float _px;
        [Replicated(Notify = nameof(OnNetYChanged))] private float _py;
        [Replicated(Notify = nameof(OnNetZChanged))] private float _pz;

        private float _inX;   // 권위 입력 (서버 — SubmitMove RPC로 갱신)
        private float _inY;
        private float _inZ;
        private float _predX;         // 예측 좌표 (소유 클라)
        private float _predY;
        private float _predZ;
        private bool _predInit;       // 최초 서버 상태 수신 전 예측 억제
        private readonly SnapshotBuffer<float> _bufX = new(32, LerpF);
        private readonly SnapshotBuffer<float> _bufY = new(32, LerpF);
        private readonly SnapshotBuffer<float> _bufZ = new(32, LerpF);

        /// <summary>테스트 관찰자 — 리모트 버퍼에 적재된 샘플 수 (X축 기준).</summary>
        internal int BufferedSampleCount => _bufX.SampleCount;

        private static float LerpF(float a, float b, double t) => a + (b - a) * (float)t;

        private void Awake()
        {
            var p = transform.position;
            _px = p.x;
            _py = p.y;
            _pz = p.z;   // 스폰 트랜스폽을 초기 복제값으로 — Spawn 전 배치가 스폰 상태에 실린다
        }

        /// <summary>
        /// 로컬 입력 제출 — 소유 클라에서 예측에 즉시 반영되고 서버로 전송된다(서버·호스트는 권위 입력 직접 적용).
        /// 값은 [-1,1]로 클램프된다 (신뢰 경계 — 서버 Validate와 이중 방어).
        /// </summary>
        public void SubmitMove(float inX, float inY, float inZ = 0f)
        {
            inX = Mathf.Clamp(inX, -1f, 1f);
            inY = Mathf.Clamp(inY, -1f, 1f);
            inZ = Mathf.Clamp(inZ, -1f, 1f);
            _inX = inX;   // 소유 클라 — 예측 입력 즉시 반영 (서버 응답 전에도 로컬 이동이 입력을 따라간다)
            _inY = inY;
            _inZ = inZ;
            if (IsServer)
            {
                return;   // 서버·호스트 — 권위 입력 직접 적용 (RPC 불필요)
            }
            RpcSubmitMove(inX, inY, inZ);
        }

        /// <summary>서버 전용 — 네트워크 위치를 설정한다. 다음 틱에 전 클라에 복제된다 (트랜스폽도 즉시 반영).</summary>
        public void SetNetworkPosition(Vector3 position)
        {
            _px = position.x;
            _py = position.y;
            _pz = position.z;
            transform.position = position;
        }

        private void Update()
        {
            if (IsServer)
            {
                if (SimulationEnabled && MovementRule != null)
                    MovementRule(ref _px, ref _py, ref _pz, _inX, _inY, _inZ, Time.deltaTime);
                transform.position = new Vector3(_px, _py, _pz);   // 권위 좌표 반영
            }
            else if (IsOwner && PredictOwner)
            {
                if (!_predInit || !SimulationEnabled || MovementRule == null)
                {
                    transform.position = new Vector3(_px, _py, _pz);   // 최초 상태 수신 전·게이트 꺼짐 — 권위 좌표 렌더
                    return;
                }
                MovementRule(ref _predX, ref _predY, ref _predZ, _inX, _inY, _inZ, Time.deltaTime);
                transform.position = new Vector3(_predX, _predY, _predZ);
            }
            else if (IsOwner)
            {
                transform.position = new Vector3(_px, _py, _pz);   // 예측 비활성 소유 클라 — 권위 좌표 렌더
            }
            else if (IsClient)
            {
                // 리모트 — 인터폴레이션 버퍼로 과거 시점 렌더 (적용 시점 제어). 버퍼 비어 있으면 현 위치 유지
                double renderTime = UniNetTime.Now - InterpolationDelay;
                if (_bufX.TrySample(renderTime, out var x) && _bufY.TrySample(renderTime, out var y) && _bufZ.TrySample(renderTime, out var z))
                    transform.position = new Vector3(x, y, z);
            }
        }

        /// <summary>네트워크 X 수신 — 서버는 무시(권위 원본 보존), 소유 클라는 예측 조정, 리모트는 버퍼 적재. 첫 수신 시 3축 기준점을 함께 확정한다.</summary>
        private void OnNetXChanged(float prev)
        {
            if (IsServer) return;
            if (IsOwner)
            {
                if (!_predInit)
                {
                    _predInit = true;   // 최초 상태 — 예측 기준점 3축 스냅
                    _predX = _px;
                    _predY = _py;
                    _predZ = _pz;
                    return;
                }
                _predX = ReconcileAxis(_predX, _px, SnapThreshold, SoftRate);
                return;
            }
            _bufX.Add(UniNetTime.Now, _px);
        }

        /// <summary>네트워크 Y 수신 — 위와 동일.</summary>
        private void OnNetYChanged(float prev)
        {
            if (IsServer) return;
            if (IsOwner)
            {
                if (!_predInit) { _predY = _py; return; }
                _predY = ReconcileAxis(_predY, _py, SnapThreshold, SoftRate);
                return;
            }
            _bufY.Add(UniNetTime.Now, _py);
        }

        /// <summary>네트워크 Z 수신 — 위와 동일.</summary>
        private void OnNetZChanged(float prev)
        {
            if (IsServer) return;
            if (IsOwner)
            {
                if (!_predInit)
                {
                    _predInit = true;   // Z가 마지막 축일 수 있다 — 여기서 기준점 3축 완결
                    _predX = _px;
                    _predY = _py;
                    _predZ = _pz;
                    return;
                }
                _predZ = ReconcileAxis(_predZ, _pz, SnapThreshold, SoftRate);
                return;
            }
            _bufZ.Add(UniNetTime.Now, _pz);
        }

        /// <summary>
        /// 예측 조정 (축 단위) — 오차가 snapThreshold를 넘으면 서버 값 스냅(하드 조정), 아니면 잔여 오차의
        /// softRate를 즉시 흡수한다(소프트 조정) — UE ClientAdjustPosition 단순형.
        /// ponytail: 입력 시퀀스 ack·미적용 입력 재적용 큐는 UE급 구현 — 예측 오차가 눈에 띄면 도입.
        /// </summary>
        internal static float ReconcileAxis(float predicted, float server, float snapThreshold, float softRate)
        {
            float error = server - predicted;
            if (error * error > snapThreshold * snapThreshold)
                return server;
            return predicted + error * softRate;
        }

        // ---- 입력 전송 ServerRpc (컴포넌트 소유 — 기반 클래스 RPC 스캔·파생 인스턴스 라우팅 검증 대상) ----

        /// <summary>클라 → 서버 이동 입력 (부동 소수 벡터, [-1,1]). 서버가 권위 입력으로 유지한다.</summary>
        [ServerRpc]
        private partial void RpcSubmitMove(float inX, float inY, float inZ);

        private Task<bool> RpcSubmitMove_Validate(float inX, float inY, float inZ)
            => Task.FromResult(!float.IsNaN(inX) && !float.IsInfinity(inX)
                               && !float.IsNaN(inY) && !float.IsInfinity(inY)
                               && !float.IsNaN(inZ) && !float.IsInfinity(inZ)
                               && Math.Abs(inX) <= 1.01f && Math.Abs(inY) <= 1.01f && Math.Abs(inZ) <= 1.01f);

        private void RpcSubmitMove_Implementation(float inX, float inY, float inZ)
        {
            _inX = inX;
            _inY = inY;
            _inZ = inZ;
        }
    }
}
