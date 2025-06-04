// EmpireGame/MapGeneration/PerlinNoiseGenerator.cs
using System;

namespace EmpireGame
{
    public class PerlinNoiseGenerator
    {
        private readonly int[] permutation;

        public PerlinNoiseGenerator(int seed)
        {
            Random random = new Random(seed);
            permutation = new int[512];

            // Initialize the permutation array with values 0-255
            int[] p = new int[256];
            for (int i = 0; i < 256; i++)
            {
                p[i] = i;
            }

            // Shuffle the array using the seed
            for (int i = 255; i > 0; i--)
            {
                int j = random.Next(0, i + 1);
                int temp = p[i];
                p[i] = p[j];
                p[j] = temp;
            }

            // Duplicate the permutation array to avoid overflow issues
            for (int i = 0; i < 256; i++)
            {
                permutation[i] = p[i];
                permutation[i + 256] = p[i];
            }
        }

        // Generate Perlin noise at specified coordinates
        public float Perlin(float x, float y, float z = 0)
        {
            // Find unit cube that contains the point
            int xi = (int)Math.Floor(x) & 255;
            int yi = (int)Math.Floor(y) & 255;
            int zi = (int)Math.Floor(z) & 255;

            // Find relative x, y, z of point in cube
            float xf = x - (int)Math.Floor(x);
            float yf = y - (int)Math.Floor(y);
            float zf = z - (int)Math.Floor(z);

            // Compute fade curves for each of x, y, z
            float u = Fade(xf);
            float v = Fade(yf);
            float w = Fade(zf);

            // Hash coordinates of the 8 cube corners
            int a = permutation[xi] + yi;
            int aa = permutation[a] + zi;
            int ab = permutation[a + 1] + zi;
            int b = permutation[xi + 1] + yi;
            int ba = permutation[b] + zi;
            int bb = permutation[b + 1] + zi;

            // Calculate noise contributions from each of the 8 corners
            float x1 = Lerp(Grad(permutation[aa], xf, yf, zf),
                           Grad(permutation[ba], xf - 1, yf, zf),
                           u);
            float x2 = Lerp(Grad(permutation[ab], xf, yf - 1, zf),
                           Grad(permutation[bb], xf - 1, yf - 1, zf),
                           u);
            float y1 = Lerp(x1, x2, v);

            float x3 = Lerp(Grad(permutation[aa + 1], xf, yf, zf - 1),
                           Grad(permutation[ba + 1], xf - 1, yf, zf - 1),
                           u);
            float x4 = Lerp(Grad(permutation[ab + 1], xf, yf - 1, zf - 1),
                           Grad(permutation[bb + 1], xf - 1, yf - 1, zf - 1),
                           u);
            float y2 = Lerp(x3, x4, v);

            // Range of Perlin noise output is -1 to 1
            float result = Lerp(y1, y2, w);

            // Convert to 0 to 1 range
            return (result + 1) * 0.5f;
        }

        // Generate Fractal Brownian Motion noise (multiple octaves of Perlin noise)
        public float OctavePerlin(float x, float y, int octaves, float persistence, float lacunarity, float z = 0)
        {
            float total = 0;
            float frequency = 1;
            float amplitude = 1;
            float maxValue = 0;  // Used for normalizing result to 0.0 - 1.0

            for (int i = 0; i < octaves; i++)
            {
                total += Perlin(x * frequency, y * frequency, z * frequency) * amplitude;

                maxValue += amplitude;

                amplitude *= persistence;
                frequency *= lacunarity;
            }

            return total / maxValue;
        }

        // Fade function as defined by Ken Perlin. This eases coordinate values
        // so that they will ease towards integral values. This ends up smoothing
        // the final output.
        private static float Fade(float t)
        {
            // 6t^5 - 15t^4 + 10t^3 (Improved smoother step)
            return t * t * t * (t * (t * 6 - 15) + 10);
        }

        // Linear interpolation
        private static float Lerp(float a, float b, float t)
        {
            return a + t * (b - a);
        }

        // Calculate the dot product of the distance and gradient vectors
        private static float Grad(int hash, float x, float y, float z)
        {
            // Convert low 4 bits of hash code into 12 gradient directions
            int h = hash & 15;
            float u = h < 8 ? x : y;
            float v = h < 4 ? y : (h == 12 || h == 14 ? x : z);
            return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
        }
    }
}