using UniNet.Core.Hosting;
using UnityEngine;

namespace Arena
{
    /// <summary>
    /// Top-down camera — clients follow their own player (IsOwner); a dedicated server views the whole arena from a fixed angle.
    /// Orthographic top-down setup with no assets.
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
                // server — fixed view of the whole arena
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
                : new Vector3(0f, 30f, 0f);   // spectating — center of the arena
            transform.position = Vector3.Lerp(transform.position, target, 8f * Time.deltaTime);
            transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        }
    }
}
