using System.Numerics;
using Realism.Minecraft;

namespace Realism.Meshing;

/// <summary>
/// Finds the height of the smooth terrain surface at a point, built exactly like the mesher builds it
/// (terrain occupancy at block centres, three [1 2 1] blur passes, iso 0.5), but only for a small block around it.
/// Used to keep the live camera on the smooth ground instead of Minecraft's block steps.
/// </summary>
public static class TerrainProbe
{
    const int R = 5;          // horizontal reach: 3 blur passes need 3 blocks, plus interpolation
    const int N = 2 * R + 1;
    const int HalfH = 6;
    const int NH = 2 * HalfH + 1;

    /// <summary>Smooth ground height (world Y) at (x, z) near <paramref name="nearY"/>, or null if there is none close by.</summary>
    public static float? SurfaceY(World world, float x, float z, float nearY)
    {
        int bx = (int)MathF.Floor(x) - R, by = (int)MathF.Floor(nearY) - HalfH, bz = (int)MathF.Floor(z) - R;
        var d = new float[N * NH * N];
        int Idx(int i, int j, int k) => (j * N + k) * N + i;
        for (int k = 0; k < N; k++)
        for (int i = 0; i < N; i++)
        {
            var c = world.GetChunk((bx + i) >> 4, (bz + k) >> 4);
            if (c == null) return null;
            for (int j = 0; j < NH; j++)
                d[Idx(i, j, k)] = Mat.IsTerrain(c.Get((bx + i) & 15, by + j, (bz + k) & 15)) ? 1 : 0;
        }

        var tmp = new float[d.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            for (int axis = 0; axis < 3; axis++)
            {
                for (int j = 0; j < NH; j++)
                for (int k = 0; k < N; k++)
                for (int i = 0; i < N; i++)
                {
                    float At(int di, int dj, int dk) => d[Idx(Math.Clamp(i + di, 0, N - 1), Math.Clamp(j + dj, 0, NH - 1), Math.Clamp(k + dk, 0, N - 1))];
                    tmp[Idx(i, j, k)] = axis switch
                    {
                        0 => 0.25f * At(-1, 0, 0) + 0.5f * At(0, 0, 0) + 0.25f * At(1, 0, 0),
                        1 => 0.25f * At(0, -1, 0) + 0.5f * At(0, 0, 0) + 0.25f * At(0, 1, 0),
                        _ => 0.25f * At(0, 0, -1) + 0.5f * At(0, 0, 0) + 0.25f * At(0, 0, 1),
                    };
                }
                (d, tmp) = (tmp, d);
            }
        }

        // Samples sit at block centres, so the local coordinate of world point x is x - (bx + 0.5).
        float lx = x - bx - 0.5f, lz = z - bz - 0.5f;
        float Sample(float ly)
        {
            int i0 = (int)MathF.Floor(lx), j0 = (int)MathF.Floor(ly), k0 = (int)MathF.Floor(lz);
            float fx = lx - i0, fy = ly - j0, fz = lz - k0;
            float r = 0;
            for (int n = 0; n < 8; n++)
            {
                int i = Math.Clamp(i0 + (n & 1), 0, N - 1), j = Math.Clamp(j0 + ((n >> 1) & 1), 0, NH - 1), k = Math.Clamp(k0 + ((n >> 2) & 1), 0, N - 1);
                float w = ((n & 1) != 0 ? fx : 1 - fx) * (((n >> 1) & 1) != 0 ? fy : 1 - fy) * (((n >> 2) & 1) != 0 ? fz : 1 - fz);
                r += w * d[Idx(i, j, k)];
            }
            return r;
        }

        // Walk down from above the reference height to the first crossing into solid.
        float start = nearY - by - 0.5f + 2.5f;
        float prevY = start, prev = Sample(prevY);
        if (prev >= 0.5f) return null; // buried: no open surface here
        for (float ly = start - 0.1f; ly > nearY - by - 0.5f - 3.5f; ly -= 0.1f)
        {
            float v = Sample(ly);
            if (v >= 0.5f)
                return by + 0.5f + ly + (prevY - ly) * (v - 0.5f) / Math.Max(v - prev, 1e-5f);
            prev = v;
            prevY = ly;
        }
        return null;
    }
}
