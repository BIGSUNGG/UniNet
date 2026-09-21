using System.Collections.Generic;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// 네트워크 오브젝트 기반 클래스 — RPC·리플리케이션의 대상이 되는 MonoBehaviour.
    /// 네트워크 엔티티는 GameObject 단위다 (netId 1개 — 씬 오브젝트는 씬 경로 해시, 동적 스폰은 서버 할당).
    /// 오브젝트에 여러 NetworkBehaviour가 있으면 각자 SubId(슬롯)를 받아 독립적으로 RPC·리플리케이션된다 (ADR-0010).
    /// 슬롯 순서는 GetComponents 순서 — 런타임 컴포넌트 증감은 금지(양단 슬롯 불변식).
    /// </summary>
    public abstract partial class NetworkBehaviour : MonoBehaviour, IUniNetSpawnTransform, IUniNetReplicationPolicy
    {
        private ulong _netId;
        private bool _netIdComputed;
        private byte _subId;
        private bool _registeredServer;
        private bool _registeredClient;

        /// <summary>네트워크 오브젝트 ID — 오브젝트(게임오브젝트) 단위. 씬 오브젝트는 씬 이름+계층 경로(형제 인덱스 포함)의 FNV-1a 64bit, 동적 스폰은 서버 할당값.</summary>
        public ulong NetId
        {
            get
            {
                if (!_netIdComputed)
                {
                    _netId = Fnv1a.Hash64(BuildHierarchyPath());
                    _netIdComputed = true;
                }
                return _netId;
            }
        }

        /// <summary>오브젝트 내 이 컴포넌트의 서브슬롯 — GetComponents 순서 (등록 시 주입).</summary>
        public byte SubId => _subId;

        /// <summary>서버 권위로 동작 중인가 (서버/호스트에서 true).</summary>
        public bool IsServer => UniNetEnvironment.Server != null && _registeredServer;

        /// <summary>클라이언트로 접속 중인가 (클라/호스트에서 true).</summary>
        public bool IsClient => UniNetEnvironment.Client != null && _registeredClient;

        /// <summary>이 오브젝트를 로컬 플레이어의 연결이 소유하는가 (입력·권위 판단용 — 오브젝트 단위).</summary>
        public bool IsOwner
        {
            get
            {
                var client = UniNetEnvironment.Client;
                return client != null && client.GetOwner(NetId) == client.LocalConnId && client.LocalConnId != 0;
            }
        }

        // ── P3 리플리케이션 정책 (가시성·우선순위·휴면·주기) ──

        /// <summary>리플리케이션 우선순위 (UE NetPriority 상응, 기본 1). 대역폭 예산이 부족하면 높은 값부터 전송된다.</summary>
        public float NetworkPriority { get; set; } = 1f;

        /// <summary>오브젝트별 전송 주기(Hz, UE NetUpdateFrequency 상응). 0 = 매 틱 무제한 (기본).</summary>
        public float NetworkUpdateFrequencyHz { get; set; }

        /// <summary>가시성 컬 거리(월드 단위 반경, UE NetCullDistance 상응). 0 = 미적용 (기본). 서버 SetViewerPosition 으로 뷰어 위치 제공 필요.</summary>
        public float NetworkCullDistance { get; set; }

        /// <summary>휴면 — true 동안 서버가 이 오브젝트의 델타 비교·전송을 중단한다 (UE NetDormancy 단순화). 깨울 때 FlushNetworkDormancy.</summary>
        public bool NetworkDormant { get; set; }

        /// <summary>휴면을 해제한다 — 이후 틱에서 휴면 중 누적된 변경분이 전송된다 (UE FlushNetDormancy 상응).</summary>
        public void FlushNetworkDormancy() => NetworkDormant = false;

        /// <summary>연결별 사용자 정의 관련성 (UE IsNetRelevantFor 상응) — 기본 항상 관련. 거리 컬과 AND로 적용된다.</summary>
        public virtual bool IsNetworkRelevant(long viewerConnId) => true;

        float IUniNetReplicationPolicy.NetworkPriority => NetworkPriority;
        float IUniNetReplicationPolicy.NetworkUpdateFrequencyHz => NetworkUpdateFrequencyHz;
        float IUniNetReplicationPolicy.NetworkCullDistance => NetworkCullDistance;
        bool IUniNetReplicationPolicy.IsNetworkDormant => NetworkDormant;
        bool IUniNetReplicationPolicy.IsNetworkRelevant(long viewerConnId) => IsNetworkRelevant(viewerConnId);

        // ── P4 래그 컴펜세이션 훅 (위치 히스토리·리와인드) ──

        /// <summary>서버가 이 오브젝트의 위치 히스토리를 기록한다 (래그 컴펜세이션 리와인드 대상 — 기본 false, 서버 전용 동작).</summary>
        public bool NetworkRewindHistory { get; set; }

        private PositionHistory _rewindHistory;

        internal PositionHistory RewindHistory => _rewindHistory ??= new PositionHistory(128, 1.0 / 60.0);   // 60Hz 샘플링 — 128 샘플 ≈ 2.1초 창 (프레임레이트 무관)

        /// <summary>테스트 관찰자 — 현재까지 기록된 위치 샘플 수.</summary>
        internal int RewindSampleCount => _rewindHistory?.SampleCount ?? 0;

        /// <summary>드라이버 서버 틱에서 호출 — 서버 도메인 시각으로 위치 샘플 기록 (NetworkRewindHistory가 true일 때만).</summary>
        internal void RecordRewindSample(double serverTime)
        {
            var p = transform.position;
            RewindHistory.Record(serverTime, p.x, p.y, p.z);
        }

        /// <summary>
        /// 서버 전용 — 과거 serverTime 시점(UniNetTime 도메인)의 위치를 질의한다 (래그 컴펜세이션 리와인드).
        /// 히스토리 미기록 오브젝트는 현재 transform 위치를 반환한다(리와인드 비대상 — 호출자 계약).
        /// </summary>
        public bool GetHistoryPosition(double serverTime, out float x, out float y, out float z)
        {
            if (IsServer && _rewindHistory != null && _rewindHistory.Sample(serverTime, out x, out y, out z))
                return true;
            var p = transform.position;
            x = p.x;
            y = p.y;
            z = p.z;
            return true;
        }

        /// <summary>동적 스폰 — 서버가 할당한 netId를 주입한다 (경로 해시 계산을 선제한다).</summary>
        internal void AssignNetId(ulong netId)
        {
            _netId = netId;
            _netIdComputed = true;
        }

        /// <summary>등록 시 오브젝트 내 슬롯을 주입한다.</summary>
        internal void AssignSubId(byte subId) => _subId = subId;

        /// <summary>서버 등록 완료 표시 (IsServer 활성화).</summary>
        internal void MarkServerRegistered() => _registeredServer = true;

        /// <summary>클라 등록 완료 표시 (IsClient 활성화).</summary>
        internal void MarkClientRegistered() => _registeredClient = true;

        /// <summary>스폰 와이어 포맷으로 현재 변환을 출력한다 (서버가 스폰 메시지에 실음).</summary>
        void IUniNetSpawnTransform.GetSpawnTransform(
            out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw)
        {
            var p = transform.position;
            var r = transform.rotation;
            px = p.x; py = p.y; pz = p.z;
            qx = r.x; qy = r.y; qz = r.z; qw = r.w;
        }

        /// <summary>씬 로드 시 전체 등록 (UniNetManager 시작 시 1회 호출) — 오브젝트당 1회, 컴포넌트 전체를 슬롯으로.</summary>
        internal static void RegisterAllToServer()
        {
            var server = UniNetEnvironment.Server;
            if (server == null) return;

            foreach (var comps in CollectSceneObjects())
            {
                if (comps.Length > byte.MaxValue)
                {
                    Debug.LogError($"[UniNet] 오브젝트당 NetworkBehaviour는 최대 255개다 (SubId byte 상한) — 등록 건너뜀: {comps[0].gameObject.name}");
                    continue;
                }
                server.RegisterSceneObject(comps[0].NetId, comps);
                for (byte i = 0; i < comps.Length; i++)
                {
                    comps[i].AssignSubId(i);
                    comps[i]._registeredServer = true;
                }
            }
        }

        /// <summary>클라 측 전체 등록 — 서버와 같은 구성(슬롯 순서)으로.</summary>
        internal static void RegisterAllToClient()
        {
            var client = UniNetEnvironment.Client;
            if (client == null) return;

            foreach (var comps in CollectSceneObjects())
            {
                if (comps.Length > byte.MaxValue)
                {
                    Debug.LogError($"[UniNet] 오브젝트당 NetworkBehaviour는 최대 255개다 (SubId byte 상한) — 등록 건너뜀: {comps[0].gameObject.name}");
                    continue;
                }
                client.Register(comps[0].NetId, comps);
                for (byte i = 0; i < comps.Length; i++)
                {
                    comps[i].AssignSubId(i);
                    comps[i]._registeredClient = true;
                    UniNetTypeRegistry.Find(comps[i].GetType())?.InitClientSnapshot(comps[i]);
                }
            }
        }

        /// <summary>씬의 NetworkBehaviour를 게임오브젝트 단위로 묶는다 (GetComponents 순서 = 슬롯).</summary>
        private static List<NetworkBehaviour[]> CollectSceneObjects()
        {
            var result = new List<NetworkBehaviour[]>();
            var seen = new HashSet<GameObject>();
            foreach (var nb in FindObjectsByType<NetworkBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (seen.Add(nb.gameObject))
                    result.Add(nb.gameObject.GetComponents<NetworkBehaviour>());
            return result;
        }

        private string BuildHierarchyPath()
        {
            var parts = new List<string>();
            var current = transform;
            while (current != null)
            {
                parts.Add(current.name + ":" + current.GetSiblingIndex());
                current = current.parent;
            }
            parts.Reverse();
            return gameObject.scene.name + "/" + string.Join("/", parts);
        }
    }
}
