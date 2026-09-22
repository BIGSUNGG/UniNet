using UnityEngine;

namespace Arena
{
    /// <summary>
    /// Local FX — not network objects. MulticastRpc implementations run on everyone (server included), so each
    /// instance pops shape FX on its own screen. Spawned primitives remove themselves.
    /// </summary>
    internal static class ArenaFx
    {
        /// <summary>InitialOnly color seed → display color (shared by players and bullets).</summary>
        public static Color SeedToColor(int seed)
            => Color.HSVToRGB((seed % 1000) / 1000f, 0.75f, 0.95f);

        /// <summary>Fire and hit flash — a sphere expands and fades out.</summary>
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

        /// <summary>Death explosion — a large sphere expands quickly.</summary>
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

        /// <summary>P4 hitscan tracer — a thin box along the shot line appears briefly and disappears.</summary>
        public static void Tracer(Vector3 from, Vector3 to, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Object.Destroy(go.GetComponent<Collider>());
            go.name = "FxTracer";
            var dir = to - from;
            go.transform.position = from + dir * 0.5f;
            go.transform.localScale = new Vector3(0.07f, 0.07f, Mathf.Max(0.1f, dir.magnitude));
            if (dir.sqrMagnitude > 0.0001f)
                go.transform.rotation = Quaternion.LookRotation(dir.normalized, Vector3.up);
            var fx = go.AddComponent<TracerFade>();
            fx.Lifetime = 0.12f;
            fx.Color = new Color(color.r, color.g, color.b, 0.85f);
        }

        /// <summary>Tracer fade — keeps the stretched scale and only lowers alpha (unlike FxParticle's uniform scale).</summary>
        private sealed class TracerFade : MonoBehaviour
        {
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
                Color.a = 1f - t;
                _material.color = Color;
            }
        }

        /// <summary>One-shot FX particle that expands, fades, and removes itself.</summary>
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
                Color.a = 1f - t;   // own material instance — never shared with other FX
                _material.color = Color;
            }
        }
    }
}
