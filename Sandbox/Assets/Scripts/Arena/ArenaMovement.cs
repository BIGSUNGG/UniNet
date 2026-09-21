using UnityEngine;

namespace Arena
{
    /// <summary>
    /// P4 이동 규칙 단일 공급원 — 서버 권위 시뮬레이션과 클라 예측이 같은 Step을 공유한다
    /// (양쪽 규칙이 달라지면 예측 오차가 상수가 되므로 절대 복제하지 않는다).
    /// Reconcile은 서버 권위 좌표 수신 시 예측 좌표를 보정한다.
    /// </summary>
    internal static class ArenaMovement
    {
        /// <summary>이동 1스텝 — 속도·경계·기둥 충돌(축 분리) 규칙. 서버와 예측 클라가 동일 적용한다.</summary>
        public static (float X, float Z) Step(float x, float z, sbyte dx, sbyte dy, float dt)
        {
            float nx = Mathf.Clamp(x + dx * ArenaConfig.MoveSpeed * dt, -ArenaConfig.Half, ArenaConfig.Half);
            float nz = Mathf.Clamp(z + dy * ArenaConfig.MoveSpeed * dt, -ArenaConfig.Half, ArenaConfig.Half);
            if (!Blocked(nx, z)) x = nx;
            if (!Blocked(x, nz)) z = nz;
            return (x, z);
        }

        /// <summary>기둥 충돌 — 확장 AABB 점 포함 판정 (플레이어 반경 포함).</summary>
        public static bool Blocked(float x, float z)
        {
            foreach (var (px, pz, half) in ArenaConfig.Pillars)
            {
                float ex = half + ArenaConfig.PlayerRadius;
                if (x > px - ex && x < px + ex && z > pz - ex && z < pz + ex)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// P4 NetworkTransform.MovementRule 어댑터 — 컴포넌트 틱(서버 권위·예측)이 호출한다.
        /// Y축은 아레나 평면에서 사용하지 않는다. 부동 소수 입력을 8방향 sbyte 규칙으로 변환해 Step에 위임한다.
        /// </summary>
        public static void StepNet(ref float x, ref float y, ref float z, float inX, float inY, float inZ, float dt)
        {
            sbyte dx = (sbyte)Mathf.Clamp(Mathf.Round(inX), -1f, 1f);
            sbyte dy = (sbyte)Mathf.Clamp(Mathf.Round(inY), -1f, 1f);
            var (nx, nz) = Step(x, z, dx, dy, dt);
            x = nx;
            z = nz;   // y는 평면 높이 — 컴포넌트가 관리하지 않는다
        }

        /// <summary>
        /// P4 예측 조정 — 서버 권위 좌표 수신 시 예측 좌표를 보정한다 (축 단위).
        /// 오차가 snapThreshold를 넘으면 스냅(하드 조정), 아니면 잔여 오차의 softRate를 즉시 흡수한다(소프트 조정)
        /// — UE ClientAdjustPosition 단순형.
        /// ponytail: 입력 시퀀스 ack·미적용 입력 재적용 큐는 UE급 구현 — 예측 오차가 눈에 띄면 도입.
        /// </summary>
        public static float ReconcileAxis(float predicted, float server,
            float snapThreshold = 0.5f, float softRate = 0.15f)
        {
            float error = server - predicted;
            if (error * error > snapThreshold * snapThreshold)
                return server;
            return predicted + error * softRate;
        }
    }
}
