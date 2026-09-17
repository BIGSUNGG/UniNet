using UniNet.Core.Hosting;
using UnityEngine;

namespace Arena
{
    /// <summary>
    /// 탑다운 카메라 — 클라이언트는 내 플레이어(IsOwner)를 추적하고, 서버는 아레나 전체를 고정 시점으로 본다.
    /// 에셋 없이 직교 투영 탑다운 구성.
    /// </summary>
    public sealed class ArenaCameraRig : MonoBehaviour
    {
        private ArenaPlayer _mine;
        private float _nextScan;

        private void Start()
        {
            var cam = GetComponent<Camera>();
            if (cam != null)
            {
                cam.orthographic = true;
                cam.orthographicSize = 22f;
            }
        }

        private void LateUpdate()
        {
            bool isServerOnly = UniNetEnvironment.Server != null && UniNetEnvironment.Client == null;
            if (isServerOnly)
            {
                // 서버 — 아레나 전체 고정 시점
                transform.position = new Vector3(0f, 30f, 0f);
                transform.rotation = Quaternion.Euler(90f, 0f, 0f);
                return;
            }

            if (Time.unscaledTime >= _nextScan)
            {
                _nextScan = Time.unscaledTime + 0.5f;
                _mine = null;
                foreach (var player in FindObjectsByType<ArenaPlayer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (player.IsOwner)
                    {
                        _mine = player;
                        break;
                    }
                }
            }

            var target = _mine != null
                ? new Vector3(_mine.transform.position.x, 30f, _mine.transform.position.z)
                : new Vector3(0f, 30f, 0f);   // 관전 — 중앙
            transform.position = Vector3.Lerp(transform.position, target, 8f * Time.deltaTime);
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
    }
}
