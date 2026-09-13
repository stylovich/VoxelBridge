using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelScaleNormalization
    {
        // Express source-local geometry in metres, retaining the source's world rotation separately.
        internal static Vector3 SourceScale(Object source)
        {
            if (!(source is GameObject go)) return Vector3.one;
            Transform t = go.transform;
            Vector3 scale = t.lossyScale;
            if (!float.IsFinite(scale.x + scale.y + scale.z) ||
                scale.x <= .000001f || scale.y <= .000001f || scale.z <= .000001f)
                throw new InvalidOperationException("Normalize Scale requires positive, finite, non-zero world scale. Apply reflected transforms in the source model first.");
            Matrix4x4 actual = t.localToWorldMatrix;
            Matrix4x4 expected = Matrix4x4.TRS(t.position, t.rotation, scale);
            for (int column = 0; column < 3; column++)
            for (int row = 0; row < 3; row++)
                if (Mathf.Abs(actual[row, column] - expected[row, column]) > .0001f * scale[column])
                    throw new InvalidOperationException("Normalize Scale cannot preserve a sheared hierarchy. Apply the parent transforms or convert from an unscaled parent.");
            return scale;
        }

        internal static Transform UnscaledParent(Transform candidate)
        {
            for (var t = candidate; t != null; t = t.parent)
            {
                Matrix4x4 a = t.localToWorldMatrix;
                Matrix4x4 b = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
                bool unit = true;
                for (int c = 0; c < 3; c++)
                for (int r = 0; r < 3; r++) unit &= Mathf.Abs(a[r, c] - b[r, c]) < .00001f;
                if (unit) return t;
            }
            return null;
        }

        internal static void Place(Transform source, Transform destination, VoxelSourceAxes axes)
        {
            destination.SetPositionAndRotation(source.position, axes == VoxelSourceAxes.ZUp
                ? source.rotation * Quaternion.Euler(90, 0, 0) : source.rotation);
            destination.localScale = Vector3.one;
        }
    }
}
