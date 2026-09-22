using System;
using System.Threading.Tasks;
using UniNet.Core;
using UniNet.Core.Hosting;
using UnityEngine;

namespace UniNet.Unity
{
    /// <summary>
    /// P4 movement prediction component — server-authoritative replication of the transform position,
    /// with prediction and reconciliation on the owning client and interpolation on remotes (See ADR-0013).
    /// Runs as an independent slot (SubId) alongside other NetworkBehaviours on the same object (See ADR-0010 multi-component).
    /// Provide the movement rule via <see cref="MovementRule"/> (injected by your game), input via <see cref="SubmitMove"/>,
    /// and the simulation gate via <see cref="SimulationEnabled"/> — the server (authority) and the owning client (prediction)
    /// must share the same rule, otherwise prediction error becomes a constant offset.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed partial class NetworkTransform : NetworkBehaviour
    {
        /// <summary>Movement step — applied identically by the server (authority) and the predicting client. When null, simulation stops (position replication only). Injected by your game (DIP).</summary>
        public delegate void MovementStep(ref float x, ref float y, ref float z, float inX, float inY, float inZ, float dt);

        /// <summary>Your game's movement rule — when null, only position replication runs (assign on the server, and on predicting clients).</summary>
        public MovementStep? MovementRule;

        /// <summary>Simulation gate — when false, neither the server nor the predictor moves (death, movement disabled). Controlled by your game.</summary>
        public bool SimulationEnabled = true;

        /// <summary>Remote render delay (seconds) — remotes interpolate this far behind server time (smooths over tick gaps).</summary>
        public float InterpolationDelay = 0.12f;

        /// <summary>Prediction reconciliation — snaps to the server value when the error exceeds SnapThreshold², otherwise absorbs SoftRate of the remaining error.</summary>
        public float SnapThreshold = 0.5f;

        /// <summary>Soft correction rate — the fraction of remaining prediction error absorbed immediately on each server state update.</summary>
        public float SoftRate = 0.15f;

        /// <summary>Owning-client movement prediction (default true) — when false, the owner also renders the authoritative position.</summary>
        public bool PredictOwner = true;

        [Replicated(Notify = nameof(OnNetXChanged))] private float _px;
        [Replicated(Notify = nameof(OnNetYChanged))] private float _py;
        [Replicated(Notify = nameof(OnNetZChanged))] private float _pz;

        private float _inX;   // authoritative input (server — updated via the SubmitMove RPC)
        private float _inY;
        private float _inZ;
        private float _predX;         // predicted position (owning client)
        private float _predY;
        private float _predZ;
        private bool _predInit;       // suppress prediction until the first server state arrives
        private readonly SnapshotBuffer<float> _bufX = new(32, LerpF);
        private readonly SnapshotBuffer<float> _bufY = new(32, LerpF);
        private readonly SnapshotBuffer<float> _bufZ = new(32, LerpF);

        /// <summary>Test observer — number of samples buffered for remote interpolation (X axis).</summary>
        internal int BufferedSampleCount => _bufX.SampleCount;

        private static float LerpF(float a, float b, double t) => a + (b - a) * (float)t;

        private void Awake()
        {
            var p = transform.position;
            _px = p.x;
            _py = p.y;
            _pz = p.z;   // seed the initial replicated state from the spawn transform — placement before spawn becomes the spawn state
        }

        /// <summary>
        /// Submits local input — applied to prediction immediately on the owning client and sent to the server
        /// (on server/host it becomes the authoritative input directly). Values are clamped to [-1, 1]
        /// (trust boundary — defense in depth alongside the server-side Validate).
        /// </summary>
        public void SubmitMove(float inX, float inY, float inZ = 0f)
        {
            inX = Mathf.Clamp(inX, -1f, 1f);
            inY = Mathf.Clamp(inY, -1f, 1f);
            inZ = Mathf.Clamp(inZ, -1f, 1f);
            _inX = inX;   // owning client — feed prediction immediately (local movement follows input before any server reply)
            _inY = inY;
            _inZ = inZ;
            if (IsServer)
            {
                return;   // server/host — already the authoritative input, no RPC needed
            }
            RpcSubmitMove(inX, inY, inZ);
        }

        /// <summary>Server-only — sets the networked position; it replicates to all clients on the next tick (the transform updates immediately too).</summary>
        public void SetNetworkPosition(Vector3 position)
        {
            _px = position.x;
            _py = position.y;
            _pz = position.z;
            transform.position = position;
        }

        private void Update()
        {
            if (IsServer)
            {
                if (SimulationEnabled && MovementRule != null)
                    MovementRule(ref _px, ref _py, ref _pz, _inX, _inY, _inZ, Time.deltaTime);
                transform.position = new Vector3(_px, _py, _pz);   // apply the authoritative position
            }
            else if (IsOwner && PredictOwner)
            {
                if (!_predInit || !SimulationEnabled || MovementRule == null)
                {
                    transform.position = new Vector3(_px, _py, _pz);   // no server state yet or gate off — render the authoritative position
                    return;
                }
                MovementRule(ref _predX, ref _predY, ref _predZ, _inX, _inY, _inZ, Time.deltaTime);
                transform.position = new Vector3(_predX, _predY, _predZ);
            }
            else if (IsOwner)
            {
                transform.position = new Vector3(_px, _py, _pz);   // owner without prediction — render the authoritative position
            }
            else if (IsClient)
            {
                // Remote — render a past moment from the interpolation buffer (delay controls when updates are applied). Keep the current position while the buffer is empty
                double renderTime = UniNetTime.Now - InterpolationDelay;
                if (_bufX.TrySample(renderTime, out var x) && _bufY.TrySample(renderTime, out var y) && _bufZ.TrySample(renderTime, out var z))
                    transform.position = new Vector3(x, y, z);
            }
        }

        /// <summary>Networked X received — ignored on the server (authority keeps its own), reconciled on the owning client, buffered on remotes. The first reception anchors the 3-axis prediction baseline.</summary>
        private void OnNetXChanged(float prev)
        {
            if (IsServer) return;
            if (IsOwner)
            {
                if (!_predInit)
                {
                    _predInit = true;   // first state — snap all 3 axes to set the prediction baseline
                    _predX = _px;
                    _predY = _py;
                    _predZ = _pz;
                    return;
                }
                _predX = ReconcileAxis(_predX, _px, SnapThreshold, SoftRate);
                return;
            }
            _bufX.Add(UniNetTime.Now, _px);
        }

        /// <summary>Networked Y received — same handling as the X axis.</summary>
        private void OnNetYChanged(float prev)
        {
            if (IsServer) return;
            if (IsOwner)
            {
                if (!_predInit) { _predY = _py; return; }
                _predY = ReconcileAxis(_predY, _py, SnapThreshold, SoftRate);
                return;
            }
            _bufY.Add(UniNetTime.Now, _py);
        }

        /// <summary>Networked Z received — same handling as the X axis.</summary>
        private void OnNetZChanged(float prev)
        {
            if (IsServer) return;
            if (IsOwner)
            {
                if (!_predInit)
                {
                    _predInit = true;   // Z may be the last axis to arrive — complete the 3-axis baseline here
                    _predX = _px;
                    _predY = _py;
                    _predZ = _pz;
                    return;
                }
                _predZ = ReconcileAxis(_predZ, _pz, SnapThreshold, SoftRate);
                return;
            }
            _bufZ.Add(UniNetTime.Now, _pz);
        }

        /// <summary>
        /// Reconciles one prediction axis — snaps to the server value (hard correction) when the error exceeds
        /// snapThreshold, otherwise absorbs softRate of the remaining error immediately (soft correction) —
        /// a simplified UE ClientAdjustPosition.
        /// ponytail: no input-sequence ack / pending-input replay queue (UE-grade) — add one if visible prediction error appears.
        /// </summary>
        internal static float ReconcileAxis(float predicted, float server, float snapThreshold, float softRate)
        {
            float error = server - predicted;
            if (error * error > snapThreshold * snapThreshold)
                return server;
            return predicted + error * softRate;
        }

        // ---- Input ServerRpc (owned by this component — exercises base-class RPC scanning and derived-instance routing) ----

        /// <summary>Client-to-server movement input (float vector, [-1, 1]). The server keeps it as the authoritative input.</summary>
        [ServerRpc(Validate = true)]
        private partial void RpcSubmitMove(float inX, float inY, float inZ);

        private Task<bool> RpcSubmitMove_Validate(float inX, float inY, float inZ)
            => Task.FromResult(!float.IsNaN(inX) && !float.IsInfinity(inX)
                               && !float.IsNaN(inY) && !float.IsInfinity(inY)
                               && !float.IsNaN(inZ) && !float.IsInfinity(inZ)
                               && Math.Abs(inX) <= 1.01f && Math.Abs(inY) <= 1.01f && Math.Abs(inZ) <= 1.01f);

        private void RpcSubmitMove_Implementation(float inX, float inY, float inZ)
        {
            _inX = inX;
            _inY = inY;
            _inZ = inZ;
        }
    }
}
