using System.Collections.Generic;
using UniNet.Core;
using UniNet.Unity;
using UnityEngine;

namespace UniNet.Tests
{
    // ── GEN-002·GEN-004 fixtures — Fixtures 어셈블리 규칙 준수 (NetworkBehaviour 픽스처는 EditMode 어셈블리에 두지
    //    않는다 — Gen003EnumFixtures.cs 머리글 참조). GEN-002: 중첩 NB — 생성 partial이 중첩 선언에 병합되려면
    //    컨테이너도 partial이어야 한다 (비-partial 컨테이너는 생성 `partial class`와 충돌 — CS0260).
    //    GEN-004: Vector/Quaternion·1차원 컬렉션 RPC 인자. ──

    /// <summary>GEN-002 — 중첩 NetworkBehaviour의 컨테이너 (non-MonoBehaviour partial 클래스).</summary>
    public sealed partial class Gen002NestedHost
    {
        /// <summary>중첩 NB — RPC(Vector3 인자) + [Replicated] 필드 혼합. 유령 최상위 타입이 아니라 이 선언에 병합된다.</summary>
        public sealed partial class NestedRpcSlave : NetworkBehaviour
        {
            public int PingCalls;
            public Vector3 LastPos = Vector3.zero;
            public int Hp;

            [ServerRpc]
            internal partial void RpcPing(Vector3 pos);

            private void RpcPing_Implementation(Vector3 pos)
            {
                PingCalls++;
                LastPos = pos;
            }
        }
    }

    /// <summary>GEN-004 — Vector/Quaternion·1차원 컬렉션 RPC 인자 픽스처 (와이어 계약 + 왕복 검증용).</summary>
    public sealed partial class Gen004VectorRpcObject : NetworkBehaviour
    {
        public int VecCalls;
        public int CollCalls;
        public Vector3 LastV;
        public Quaternion LastQ = Quaternion.identity;
        public int[] LastIds = System.Array.Empty<int>();
        public List<Vector3> LastPoints = new List<Vector3>();
        public string[] LastTags = System.Array.Empty<string>();

        [ServerRpc]
        internal partial void RpcVec(Vector3 v, Quaternion q);

        private void RpcVec_Implementation(Vector3 v, Quaternion q)
        {
            VecCalls++;
            LastV = v;
            LastQ = q;
        }

        /// <summary>string[] 태그 — 참조형 요소의 null 플래그 경로(배열 내 null 문자열)를 함께 검증한다.</summary>
        [ServerRpc]
        internal partial void RpcColl(int[] ids, List<Vector3> points, string[] tags);

        private void RpcColl_Implementation(int[] ids, List<Vector3> points, string[] tags)
        {
            CollCalls++;
            LastIds = ids;
            LastPoints = points;
            LastTags = tags;
        }

        /// <summary>기존 스칼라 경로(int/string/float) — GEN-004 리팩터링 전후 와이어 불변 스냅샷용.</summary>
        [ServerRpc]
        internal partial void RpcScalar(int a, string b, float c);

        private void RpcScalar_Implementation(int a, string b, float c) { }
    }
}
