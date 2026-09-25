using System;
using UniNet.Core.Hosting;
using UniNet.Unity;
using UnityEditor;
using UnityEngine;

namespace UniNet.Usage
{
    /// <summary>
    /// Editor menu demo for the ADR-0020 serialization usage examples — runs a host-mode loopback
    /// (server + client in one process, real RUDP) and exercises QuantizedWeapon / InventoryRig,
    /// printing every synced value to the Console. Read SerializationUsage.cs for the declarations.
    /// </summary>
    internal static class SerializationUsageDemo
    {
        private static QuantizedWeapon _weapon;
        private static InventoryRig _rig;
        private static GameObject _weaponGo, _rigGo;
        private static float _startedAt;
        private static int _phase;
        private static int _aimNotifyBaseline, _packNotifyBaseline;

        [MenuItem("UniNet/Usage/직렬화 사용 예시 (호스트 루프백)")]
        public static async void Run()
        {
            Cleanup();
            _weaponGo = new GameObject("Usage_QuantizedWeapon");
            _rigGo = new GameObject("Usage_InventoryRig");
            _weapon = _weaponGo.AddComponent<QuantizedWeapon>();
            _rig = _rigGo.AddComponent<InventoryRig>();

            Debug.Log("[Usage] 호스트 시작 (메인=서버 + 클라, 루프백 RUDP) — 콘솔에 동기화 로그가 흐릅니다");
            try
            {
                RegisterAllGeneratedHubs();   // edit mode: RuntimeInitializeOnLoadMethod doesn't run — register every assembly's generated hub (idempotent; reflection avoids the CS0433 ambiguity when several script assemblies each emit a registration class)
                await UniNetManager.HostAsync(7833);
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                Cleanup();
                return;
            }

            _startedAt = Time.realtimeSinceStartup;
            _phase = 0;
            EditorApplication.update += Tick;
        }

        /// <summary>Finds and invokes every generated __UniNetRegistration.Register in the domain — each is idempotent for itself.</summary>
        private static void RegisterAllGeneratedHubs()
        {
            int registered = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType("UniNet.Generated.__UniNetRegistration")
                         ?? asm.GetType("UniNet.Generated.Hosting.__UniNetRegistration_Host");
                var m = t?.GetMethod("Register", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (m == null) continue;
                m.Invoke(null, null);
                registered++;
            }
            Debug.Log($"[Usage] 생성 허브 등록 완료 — {registered}개 어셈블리");
        }

        private static void Tick()
        {
            UniNetEnvironment.PumpMain();
            var server = UniNetEnvironment.Server;
            if (server == null) { Stop(); return; }
            float t = Time.realtimeSinceStartup - _startedAt;

            switch (_phase)
            {
                case 0 when t > 1f:   // registration settle
                    _aimNotifyBaseline = _weapon.AimMoveNotified;
                    _packNotifyBaseline = _rig.BackpackNotified;
                    _weapon.ServerAim(new Vector3(12.34f, -5.67f, 89.01f));   // quantized delta over the wire
                    _weapon.ServerCharge(0.75f);
                    _phase = 1;
                    break;
                case 1 when t > 2f:
                    _rig.ServerLoot(101);   // Insert op
                    _rig.ServerLoot(202);
                    _rig.ServerLoot(303);
                    _phase = 2;
                    break;
                case 2 when t > 3f:
                    _rig.ServerSwap(1, 999);   // Set op — only that element crosses the wire
                    _rig.ServerDrop(0);        // RemoveAt op
                    _phase = 3;
                    break;
                case 3 when t > 4f:
                    _weapon.ServerAim(new Vector3(12.349f, -5.67f, 89.01f));   // same quantization bucket — suppressed
                    _rig.ServerSetSlots(new uint[] { 0b1, 0b10, 0b11 });        // T[] + custom element serializer
                    _phase = 4;
                    break;
                case 4 when t > 5.5f:
                    Debug.Log($"[Usage] 결과 — aim={_weapon.AimPoint} (노티 {_weapon.AimMoveNotified - _aimNotifyBaseline}회, 같은 버킷 변경은 미전송), backpack=[{string.Join(",", _rig.Backpack)}] (노티 {_rig.BackpackNotified - _packNotifyBaseline}회), slots[{_rig.Slots.Length}]=[{string.Join(",", _rig.Slots)}]");
                    Debug.Log("[Usage] 완료 — 5초간 루프백 동기화를 관찰했습니다. 종료하려면 호스트를 멈추세요 (UniNet/Usage/정지)");
                    _phase = 5;
                    EditorApplication.update -= Tick;
                    break;
            }

            server.TickReplication();   // drive the send loop while the demo plays (edit mode)
        }

        [MenuItem("UniNet/Usage/정지")]
        public static void Stop()
        {
            EditorApplication.update -= Tick;
            UniNetManager.HostStop();
            Cleanup();
            Debug.Log("[Usage] 호스트 정지·정리 완료");
        }

        private static void Cleanup()
        {
            if (_weaponGo != null) UnityEngine.Object.DestroyImmediate(_weaponGo);
            if (_rigGo != null) UnityEngine.Object.DestroyImmediate(_rigGo);
            _weaponGo = _rigGo = null;
            _weapon = null;
            _rig = null;
        }
    }
}
