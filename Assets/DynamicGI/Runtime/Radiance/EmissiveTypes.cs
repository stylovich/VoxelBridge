using System.Runtime.InteropServices;
using UnityEngine;

namespace DynamicGI.Radiance
{
    /// <summary>Matches EmissiveContributorData in RadianceInject.compute.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EmissiveContributorGpuData
    {
        public const int Stride = sizeof(float) * 12;

        public Vector4 CenterAndRange;
        public Vector4 Radiance;
        public Vector4 BoundsExtentsAndMaxCascade;
    }
}
