using System;
using UniNet.Unity;
using UnityEngine;

namespace Usage
{
    /// <summary>
    /// 연결 수명주기 사용법 — 목적에 맞는 호출 하나만 사용한다.
    /// 서버 단독: ServerAsync / 클라 단독: ClientAsync / 한 프로세스 개발용: HostAsync
    /// </summary>
    public sealed class UsageBootstrap : MonoBehaviour
    {
        [SerializeField] private int _port = 7777;

        private async void Start()
        {
            try
            {
                await UniNetManager.HostAsync(_port);
                // await UniNetManager.ServerAsync(_port);
                // await UniNetManager.ClientAsync("127.0.0.1", _port);
            }
            catch (NotImplementedException)
            {
                // ponytail: 스텁 단계 임시 장치 — P1(NetworkManager 구현)에서 이 catch 블록 제거
                Debug.LogWarning("UniNet API는 아직 스텁 상태입니다 (P1에서 구현 예정).");
            }
        }
    }
}
