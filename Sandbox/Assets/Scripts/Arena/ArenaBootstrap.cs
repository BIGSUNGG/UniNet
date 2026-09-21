using System;
using System.Collections.Generic;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEngine;

namespace Arena
{
    /// <summary>
    /// 아레나 부트스트랩 — 씬에 1개 존재하며 역할에 따라 UniNet을 시작한다.
    /// 서버(권위)는 접속 수와 플레이어 수를 일치시킨다: 접속이 늘면 스폰, 줄면 잉여 플레이어를 파괴한다.
    /// 소유권은 라이브러리의 라운드로빈 최소 정책이 배정하므로, 게임은 "누가 무엇을 소유하는가"를 가정하지
    /// 않고 클라이언트는 IsOwner로 내 아바타를 찾는다 (이름·색은 아바타 고유값).
    /// </summary>
    public sealed class ArenaBootstrap : MonoBehaviour
    {
        [SerializeField] private ArenaRole _role = ArenaRole.Auto;
        [SerializeField] private int _port = ArenaConfig.Port;

        /// <summary>서버 인스턴스 (클라 역할이면 null) — 플레이어 수 관리에 사용.</summary>
        private NetworkServer _server;
        private float _nextManageUtc;
        private int _spawnCounter;
        private ArenaRole _resolvedRole;
        private bool _started;   // 리슨/접속이 실제 시작됐는가 (Stop은 시작된 것만 정리)

        private async void Start()
        {
            var role = ArenaRoleResolver.Resolve(_role);
            _resolvedRole = role;
            ArenaHud.SetRole(role);
            Debug.Log($"[Arena] 부트스트랩 시작 role={role} port={_port}");

            try
            {
                switch (role)
                {
                    case ArenaRole.Server:
                        await UniNetManager.ServerAsync(_port);
                        break;
                    case ArenaRole.Client:
                        await UniNetManager.ClientAsync("127.0.0.1", _port);
                        break;
                    case ArenaRole.Host:
                        await UniNetManager.HostAsync(_port);
                        break;
                }
                _started = true;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                enabled = false;
                return;
            }

            _server = UniNetEnvironment.Server;
            if (_server != null)
            {
                // P3-⑤ 채널 예산 — 플레이어 유형 틱당 전송량을 제한하는 유형별 대역폭 관리 예시 (P4 전환으로 총알이 제거되어 대상 변경)
                _server.SetReplicationChannelBudget(typeof(ArenaPlayer), ArenaConfig.PlayerChannelBudgetPerTickBytes);
            }
        }

        /// <summary>Play 모드 종료 — 리스너·연결을 정리해 소켓을 언바인딩한다 (미정지 시 동일 포트 재시작이 바인딩 실패한다).</summary>
        private void OnApplicationQuit()
        {
            if (!_started) return;            switch (_resolvedRole)
            {
                case ArenaRole.Server:
                    UniNetManager.ServerStop();
                    break;
                case ArenaRole.Client:
                    UniNetManager.ClientStop();
                    break;
                case ArenaRole.Host:
                    UniNetManager.HostStop();
                    break;
            }
            Debug.Log($"[Arena] 부트스트랩 정지 role={_resolvedRole}");
        }

        private void Update()
        {
            if (_server == null) return;
            if (UniNetEnvironment.Server != _server) return;   // 다른 런타임(테스트 등)이 환경을 점유하면 관리에서 물러난다
            if (Time.unscaledTime < _nextManageUtc) return;
            _nextManageUtc = Time.unscaledTime + 0.5f;
            ManagePlayers();
        }

        /// <summary>서버 — 접속 수와 동적 플레이어 수를 일치시키고, 연결별 뷰어 위치를 소유 플레이어 좌표로 유지한다 (P3-①).</summary>
        private void ManagePlayers()
        {
            int connections = 0;
            foreach (var conn in _server.SnapshotConnections())
                if (!conn.Disconnected)
                    connections++;

            var players = FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            // P3-① 뷰어 위치 — 컬 거리 판정의 기준점. 각 연결의 소유 플레이어 좌표를 서버에 제공한다
            foreach (var player in players)
            {
                var ownerConnId = _server.GetEntry(player.NetId)?.OwnerConnId ?? 0;
                if (ownerConnId != 0)
                    _server.SetViewerPosition(ownerConnId, player.transform.position.x, 0f, player.transform.position.z);
            }

            int diff = connections - players.Length;

            for (int i = 0; i < diff; i++)
                SpawnPlayer();

            if (diff < 0)
                DestroySurplus(players, -diff);
        }

        private void SpawnPlayer()
        {
            var (x, z) = ArenaConfig.SpawnPoints[_spawnCounter % ArenaConfig.SpawnPoints.Length];
            var go = new GameObject("ArenaPlayer");
            go.transform.position = new Vector3(x, 0.5f, z);

            var player = go.AddComponent<ArenaPlayer>();
            player.InitServerState(
                ArenaConfig.DisplayNames[_spawnCounter % ArenaConfig.DisplayNames.Length],
                _spawnCounter * 173 + 57);   // 이름별 고유 색 시드 (InitialOnly — 스폰 전 설정)
            _spawnCounter++;

            // Instantiate/생성 → Spawn 한 줄: netId 할당·소유권 배정·전 클라 스폰 전파
            UniNetManager.Spawn(go);
            Debug.Log($"[Arena] 플레이어 스폰 name={player.DisplayName} netId={player.NetId}");
        }

        private static void DestroySurplus(ArenaPlayer[] players, int count)
        {
            // 최근 스폰(netId 큰 순)부터 잉여분 제거 — 남은 플레이어는 재배정된 소유자가 이어받는다
            System.Array.Sort(players, (a, b) => b.NetId.CompareTo(a.NetId));
            var destroyed = new HashSet<ulong>();
            for (int i = 0; i < count && i < players.Length; i++)
            {
                if (!destroyed.Add(players[i].NetId)) continue;
                Debug.Log($"[Arena] 잉여 플레이어 파괴 name={players[i].DisplayName} netId={players[i].NetId}");
                UniNetManager.NetworkDestroy(players[i].gameObject);
            }
        }
    }
}
