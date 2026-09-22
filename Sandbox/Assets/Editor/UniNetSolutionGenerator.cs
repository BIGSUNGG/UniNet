using System.Reflection;
using Unity.CodeEditor;
using UnityEngine;

namespace UniNet.Editor
{
    /// <summary>
    /// Entry point to regenerate sln/csproj — lets batchmode refresh the solution too.
    /// Usage: Unity.exe -batchmode -quit -projectPath &lt;Sandbox&gt; -executeMethod UniNet.Editor.UniNetSolutionGenerator.Generate
    /// Calls SyncAll on the registered script editor (Visual Studio or Rider package — package types are internal, so via reflection; editor-only).
    /// The sln/csproj are gitignored (for local browsing — ADR-0005).
    /// </summary>
    public static class UniNetSolutionGenerator
    {
        public static void Generate()
        {
            var editor = CodeEditor.CurrentEditor;
            if (editor == null)
            {
                Debug.LogError("[UniNetSolutionGen] 등록된 스크립트 에디터가 없습니다 — Preferences > External Tools에서 Visual Studio 또는 Rider 선택 필요");
                return;
            }
            var syncAll = editor.GetType().GetMethod("SyncAll",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (syncAll != null)
            {
                syncAll.Invoke(editor, null);
                Debug.Log($"[UniNetSolutionGen] 솔루션 생성 완료 (Sandbox/*.sln, 생성기={editor.GetType().Name})");
            }
            else
            {
                Debug.LogError("[UniNetSolutionGen] 현재 스크립트 에디터(" + editor.GetType().Name
                    + ")가 sln 생성을 지원하지 않습니다 — Preferences > External Tools에서 Visual Studio 또는 Rider 선택 필요");
            }
        }
    }
}
