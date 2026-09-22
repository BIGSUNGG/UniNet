using System;

namespace UniNet.Core
{
    /// <summary>클라이언트 → 서버 RPC. 서버에서만 실행된다 (서버 권위).
    /// 기본으로 오브젝트 소유자의 호출만 허용되며, 비소유 발신은 서버 디스패치에서 거부된다 (ADR-0016).</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class ServerRpcAttribute : Attribute
    {
        /// <summary>true(기본) — 발신자가 오브젝트 소유자일 때만 실행한다 (비소유 발신 거부 + 경고 로그).
        /// false — 모든 클라 발신을 허용한다 (예: 전 클라가 상태를 보고하는 RPC — 서버가 권위 검증).</summary>
        public bool RequireOwnership { get; set; } = true;

        /// <summary>true — 서버 디스패치 직전에 검증 훅 &lt;RPC&gt;_Validate(같은 매개변수, Task&lt;bool&gt; 반환)를 실행하고,
        /// false 반환 시 RPC 구현은 실행되지 않는다 (옵트인 — [ServerRpc(Validate = true)]).
        /// 자동 감지는 없다 — 플래그 없이 _Validate 메서드만 두면 실행되지 않는다 (제너레이터가 경고).</summary>
        public bool Validate { get; set; } = false;
    }
}
