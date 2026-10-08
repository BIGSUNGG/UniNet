using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace UniNet.Tests
{
    /// <summary>
    /// Batch entry point that bakes the late-load test scene (ADR-0025) — run once via
    /// `-executeMethod UniNet.Tests.LateSceneBaker.Bake`. Creates a minimal scene holding one
    /// VerifyPlayer, saves it (SceneNetIdPostprocessor stamps its netId on save) and appends it to
    /// the Build Settings so PlayMode tests can load it by name. Re-runnable — an existing scene is left untouched.
    /// </summary>
    public static class LateSceneBaker
    {
        private const string ScenePath = "Assets/Scenes/LateScene.unity";

        public static void Bake()
        {
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var go = new GameObject("LatePlayer");
                go.AddComponent<VerifyPlayer>();   // test-fixture NetworkBehaviour — the object SceneLoadRegistrationTests registers late
                EditorSceneManager.SaveScene(scene, ScenePath);
            }

            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);   // LoadScene-by-name requires inclusion
            if (!scenes.Exists(s => s.path == ScenePath))
            {
                scenes.Add(new EditorBuildSettingsScene(ScenePath, true));
                EditorBuildSettings.scenes = scenes.ToArray();
            }

            AssetDatabase.SaveAssets();
            Debug.Log("[UniNet-Test] 늦은 로드 테스트 씬 베이크 완료: " + ScenePath);
        }
    }
}
