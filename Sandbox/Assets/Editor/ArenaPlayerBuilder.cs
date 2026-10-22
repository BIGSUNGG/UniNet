using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UniNet.Arena.Editor
{
    /// <summary>
    /// Player-build entry points for the Arena demo — the ARENA-001 verification layer. Run from a terminal or CI:
    ///   Unity.exe -batchmode -projectPath &lt;Sandbox&gt; -executeMethod UniNet.Arena.Editor.ArenaPlayerBuilder.BuildServer
    ///   Unity.exe -batchmode -projectPath &lt;Sandbox&gt; -executeMethod UniNet.Arena.Editor.ArenaPlayerBuilder.BuildClient
    ///
    /// Single scene (Arena), Windows 64-bit, output under Builds/ relative to the project folder. The server build
    /// uses the dedicated-server subtarget — per Unity 6 guidance the Server subtarget itself yields the headless
    /// server player (the legacy EditorUserBuildSettings.enableHeadlessMode toggle is obsolete: "use
    /// standaloneBuildSubtarget instead"). Launched players pick their network role via --arena-role=server|client
    /// (ArenaRoleResolver) and verify the demo with --arena-smoke (ArenaBuildSmoke).
    /// </summary>
    public static class ArenaPlayerBuilder
    {
        private const string ScenePath = "Assets/Scenes/Arena.unity";
        private const string ServerOutput = "Builds/arena-server/ArenaServer.exe";
        private const string ClientOutput = "Builds/arena-client/ArenaClient.exe";

        /// <summary>Dedicated Windows server build → Builds/arena-server/ArenaServer.exe (headless by subtarget).</summary>
        public static void BuildServer()
        {
            Build(ServerOutput, StandaloneBuildSubtarget.Server);
        }

        /// <summary>Windows client build → Builds/arena-client/ArenaClient.exe (default player subtarget).</summary>
        public static void BuildClient()
        {
            Build(ClientOutput, StandaloneBuildSubtarget.Player);
        }

        private static void Build(string output, StandaloneBuildSubtarget subtarget)
        {
            // fail fast with a clear message — BuildPlayer's own error for a missing scene is far less obvious
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath) == null)
            {
                Debug.LogError($"[Arena] 빌드 씬 미발견: {ScenePath} — Arena 씬 경로를 확인하세요");
                return;
            }

            EditorUserBuildSettings.standaloneBuildSubtarget = subtarget;   // Player | Server — the Server value is what makes the build headless

            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },      // single-scene demo — nothing else ships
                locationPathName = output,         // relative paths resolve against the project folder
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None,
            });

            if (report.summary.result != BuildResult.Succeeded)
                Debug.LogError($"[Arena] 플레이어 빌드 실패: {output} — result={report.summary.result} errors={report.summary.totalErrors}");
            else
                Debug.Log($"[Arena] 플레이어 빌드 완료: {report.summary.outputPath} ({report.summary.totalSize / (1024 * 1024)}MB, subtarget={subtarget})");
        }
    }
}
