using UnityEngine;

namespace Arena
{
    /// <summary>
    /// 로컬 FX — 네트워크 오브젝트가 아니다. MulticastRpc 구현이 전원(서버 포함)에서 호출해
    /// 각 인스턴스가 자기 화면에 도형 FX를 띄운다. 생성 프리미티브는 스스로 사라진다.
    /// </summary>
    internal static class ArenaFx
    {
        /// <summary>InitialOnly 색 시드 → 표시 색 (플레이어·총알 공용).</summary>
        public static Color SeedToColor(int seed)
            => Color.HSVToRGB((seed % 1000) / 1000f, 0.75f, 0.95f);

        /// <summary>발사·피격 섬광 — 구가 팽창하며 사라진다.</summary>
        public static void Flash(Vector3 position, Color color, float size)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.position = position;
            go.transform.localScale = Vector3.one * size;
            go.name = "FxFlash";
            var fx = go.AddComponent<FxParticle>();
            fx.TargetScale = size * 2.2f;
            fx.Lifetime = 0.18f;
            fx.Color = new Color(color.r, color.g, color.b, 0.9f);
        }

        /// <summary>사망 폭발 — 큰 구가 빠르게 팽창한다.</summary>
        public static void Explosion(Vector3 position)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.Destroy(go.GetComponent<Collider>());
            go.transform.position = position;
            go.name = "FxExplosion";
            var fx = go.AddComponent<FxParticle>();
            fx.TargetScale = 3.5f;
            fx.Lifetime = 0.45f;
            fx.Color = new Color(1f, 0.45f, 0.1f, 0.85f);
        }

        /// <summary>스스로 팽창·페이드 후 제거되는 1회용 FX 파티클.</summary>
        private sealed class FxParticle : MonoBehaviour
        {
            public float TargetScale;
            public float Lifetime;
            public Color Color;

            private float _age;
            private Renderer _renderer;
            private Material _material;

            private void Start()
            {
                _renderer = GetComponent<Renderer>();
                _material = new Material(_renderer.sharedMaterial);
                _renderer.sharedMaterial = _material;
                _material.color = Color;
                transform.localScale = Vector3.one * 0.2f;
            }

            private void Update()
            {
                _age += Time.deltaTime;
                float t = _age / Lifetime;
                if (t >= 1f)
                {
                    Destroy(gameObject);
                    return;
                }
                transform.localScale = Vector3.one * Mathf.Lerp(0.2f, TargetScale, t);
                Color.a = 1f - t;   // 자체 재질 인스턴스 — 다른 FX와 공유되지 않는다
                _material.color = Color;
            }
        }
    }
}
