using System.Collections.Generic;
using UniNet.Core.Hosting;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UniNet.Unity.Editor
{
    /// <summary>
    /// Stamps every scene NetworkBehaviour with a serialized stable netId at scene save time (ADR-0023).
    /// The ID is the FNV-1a 64 hash of the GameObject's GlobalObjectId (scene GUID + local file id),
    /// so it survives renames, sibling reorders, added/removed objects, DontDestroyOnLoad moves and
    /// differing build versions — neither end re-derives identity from runtime hierarchy at play time.
    /// Objects whose scene has never been saved with this postprocessor keep 0 and fall back to the
    /// hierarchy-path hash at runtime (previous behavior).
    /// </summary>
    [InitializeOnLoad]   // re-subscribes sceneSaving after every domain reload — the static ctor alone runs only on first use, leaving the hook dead after play-mode reloads
    public sealed class SceneNetIdPostprocessor : AssetPostprocessor
    {
        static SceneNetIdPostprocessor()
        {
            EditorSceneManager.sceneSaving += OnSceneSaving;
        }

        private static void OnSceneSaving(Scene scene, string path)
        {
            StampScene(scene);
        }

        /// <summary>Assigns _sceneNetId to every NetworkBehaviour GameObject in the scene (idempotent — only writes changed values).</summary>
        internal static void StampScene(Scene scene)
        {
            var stamped = new HashSet<GameObject>();
            var ids = new HashSet<ulong>();
            foreach (var nb in UnityEngine.Object.FindObjectsByType<NetworkBehaviour>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (nb.gameObject.scene != scene || !stamped.Add(nb.gameObject))
                    continue;

                var goId = GlobalObjectId.GetGlobalObjectIdSlow(nb.gameObject);
                if (goId.identifierType != 2 || goId.assetGUID.Empty())
                {
                    Debug.LogWarning($"[UniNet] 씬 저장 전 오브젝트의 GlobalObjectId를 만들 수 없다 — 경로 해시 폴백 유지 (0): {nb.gameObject.name}", nb.gameObject);
                    continue;
                }

                ulong id = Fnv1a.Hash64(goId.ToString());
                if (!ids.Add(id))
                {
                    Debug.LogError($"[UniNet] 씬 안에서 안정 netId 중복 — 등록 시 조용히 덮어써진다. Unity 예외 상황이니 리포트: {nb.gameObject.name} id={id}", nb.gameObject);
                    continue;
                }

                foreach (var comp in nb.gameObject.GetComponents<NetworkBehaviour>())
                {
                    var so = new SerializedObject(comp);
                    var prop = so.FindProperty("_sceneNetId");
                    if (prop != null && prop.ulongValue != id)
                    {
                        prop.ulongValue = id;
                        so.ApplyModifiedPropertiesWithoutUndo();
                    }
                }
            }
        }
    }
}
