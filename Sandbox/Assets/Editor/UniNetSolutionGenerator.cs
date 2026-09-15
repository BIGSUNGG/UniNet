using System.Reflection;
using Unity.CodeEditor;
using UnityEngine;

namespace UniNet.Editor
{
    /// <summary>
    /// sln/csproj 재생성 진입점 — 배치모드에서도 솔루션을 갱신할 수 있게 한다.
    /// 사용: Unity.exe -batchmode -quit -projectPath &lt;Sandbox&gt; -executeMethod UniNet.Editor.UniNetSolutionGenerator.Generate
    /// 등록된 스크립트 에디터(Visual Studio/Rider 패키지 모두)의 SyncAll을 호출한다 (패키지 타입이 internal이라 리플렉션 경유 — 에디터 전용).
    /// sln/csproj는 gitignore 대상(로컬 브라우징용 — ADR-0005).
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
