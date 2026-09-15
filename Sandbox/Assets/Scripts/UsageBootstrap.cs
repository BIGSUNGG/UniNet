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
            catch (Exception e)
            {
                Debug.LogException(e);
            }
        }
    }
}
