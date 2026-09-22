namespace UniNet.Core.Hosting
{
    /// <summary>
    /// Contract for providing the transform (position/rotation) carried in spawn messages — implemented by
    /// <see cref="NetworkBehaviour"/>. Core does not reference UnityEngine, so this interface passes the
    /// values across the layer boundary (same pattern as IUniNetSystemChannel).
    /// </summary>
    public interface IUniNetSpawnTransform
    {
        /// <summary>Writes the current transform in spawn wire format (seven single-precision values).</summary>
        void GetSpawnTransform(out float px, out float py, out float pz, out float qx, out float qy, out float qz, out float qw);
    }
}
