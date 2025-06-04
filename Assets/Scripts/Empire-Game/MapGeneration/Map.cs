// EmpireGame/MapGeneration/Map.cs
using System;
using System.Collections.Generic;

namespace EmpireGame
{
    public class Map
    {
        public Tile[,] Tiles { get; private set; }
        private static readonly Random Random = new Random();

        public int Width { get; } = 100;  // Increased for better visualization
        public int Height { get; } = 100; // Increased for better visualization

        private float SCALE = 0.25f;  // Controls the "zoom level" of the noise
        private int OCTAVES = 4;     // Number of noise layers
        private float PERSISTENCE = 0.7f; // How much each octave contributes
        private  float LACUNARITY = 1.8f;  // How much detail is added in each octave

        // Elevation and moisture thresholds for biome determination
        private const float WATER_LEVEL = 0.25f;
        private const float MOUNTAIN_LEVEL = 0.50f;
        private const float HILL_LEVEL = 0.55f;
        private const float DESERT_MOISTURE = 0.25f;
        private const float GRASS_MOISTURE = 0.45f;

        // Cache the generated noise values
        private float[,] elevationNoise;
        private float[,] moistureNoise;

        public Map(int width, int height, float noiseScale, int octaves, float persistence, float lacunarity)
        {
            Width = width;
        Height = height;
        SCALE = noiseScale;
        OCTAVES = octaves;
        PERSISTENCE = persistence;
        LACUNARITY = lacunarity;

            Tiles = new Tile[Width, Height];

            // Generate noise maps first
            GenerateNoiseMaps();

            // Then create the terrain based on the noise
            GenerateMap();
        }

        private void GenerateNoiseMaps()
        {
            elevationNoise = new float[Width, Height];
            moistureNoise = new float[Width, Height];

            // Create PerlinNoiseGenerator
            PerlinNoiseGenerator elevationGenerator = new PerlinNoiseGenerator(92345);
            PerlinNoiseGenerator moistureGenerator = new PerlinNoiseGenerator(94321);

            // Generate elevation noise
            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y < Height; y++)
                {
                    // Generate perlin noise for elevation
                    elevationNoise[x, y] = elevationGenerator.OctavePerlin(x * SCALE, y * SCALE, OCTAVES, PERSISTENCE, LACUNARITY);

                    // Generate different perlin noise for moisture
                    moistureNoise[x, y] = moistureGenerator.OctavePerlin(x * SCALE, y * SCALE, OCTAVES, PERSISTENCE, LACUNARITY);
                }
            }



            

            // Apply island gradient (more water near edges)
            ApplyIslandGradient();
            SmoothElevation();
            SmoothElevation();
            SmoothElevation();
        }


private void SmoothElevation()
{
    float[,] temp = new float[Width, Height];
    for (int x = 0; x < Width; x++)
    for (int y = 0; y < Height; y++)
    {
        float sum = 0f;
        int count = 0;
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        {
            int nx = x + dx, ny = y + dy;
            if (nx >= 0 && nx < Width && ny >= 0 && ny < Height)
            {
                sum += elevationNoise[nx, ny];
                count++;
            }
        }
        temp[x, y] = sum / count;
    }
    elevationNoise = temp;
}


        private void ApplyIslandGradient()
        {
            // Create an island effect by lowering elevation near map edges
            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y < Height; y++)
                {
                    // Calculate distance from center (0.0 to 1.0)
                    float distX = 2.0f * Math.Abs(x - Width / 2.0f) / Width;
                    float distY = 2.0f * Math.Abs(y - Height / 2.0f) / Height;

                    // Use the maximum distance as our gradient factor (creates a square falloff)
                    float distFromCenter = Math.Max(distX, distY);

                    // Smooth the gradient with a power function
                    float falloff = (float)Math.Pow(distFromCenter, 2.0f);

                    // Reduce elevation based on distance from center
                    elevationNoise[x, y] = Math.Max(0.0f, elevationNoise[x, y] - falloff * 0.5f);
                }
            }
        }

        private void GenerateMap()
        {
            // Keep track of river sources for later
            List<(int X, int Y)> riverSources = new List<(int X, int Y)>();

            // First pass: Create terrain based on elevation and moisture noise
            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y < Height; y++)
                {
                    float elevation = elevationNoise[x, y];
                    float moisture = moistureNoise[x, y];

                    // Determine terrain type based on elevation and moisture
                    Terrains terrain = DetermineTerrain(elevation, moisture);

                    // Calculate movement cost based on terrain
                    int movementCost = GetMovementCostForTerrain(terrain);

                    // Create the tile
                    Tiles[x, y] = new Tile(x, y, terrain, movementCost);

                    // If it's a mountain with high elevation, it might be a river source
                    if (terrain == Terrains.Cliff && elevation > MOUNTAIN_LEVEL + 0.1f && Random.NextDouble() < 0.15f)
                    {
                        riverSources.Add((x, y));
                    }
                }
            }

            // Second pass: Generate rivers from mountains to water
            GenerateRivers(riverSources);
        }

        private void GenerateRivers(List<(int X, int Y)> riverSources)
        {
            foreach (var source in riverSources)
            {
                GenerateRiver(source.X, source.Y);
            }
        }

        private void GenerateRiver(int startX, int startY)
        {
            int x = startX;
            int y = startY;

            // Maximum river steps to prevent infinite loops
            int maxSteps = Width + Height;
            int steps = 0;

            // Follow elevation downhill
            while (steps < maxSteps)
            {
                // Find the lowest neighbor
                var (nextX, nextY) = FindLowestNeighbor(x, y);

                // If we're at a minimum or reached water, stop
                if ((nextX == x && nextY == y) || Tiles[x, y].TerrainType == Terrains.River ||
                    Tiles[x, y].TerrainType == Terrains.Lake)
                    break;

                // Update current position
                x = nextX;
                y = nextY;

                // Make this tile a river
                if (elevationNoise[x, y] < WATER_LEVEL)
                {
                    // If the tile is already low elevation, make it a lake instead
                    Tiles[x, y].TerrainType = Terrains.Lake;
                }
                else
                {
                    Tiles[x, y].TerrainType = Terrains.River;
                }

                // Recalculate movement cost
                Tiles[x, y].MovementCost = GetMovementCostForTerrain(Tiles[x, y].TerrainType);

                // Update resource
                Tiles[x, y].Resource = Resource.Water;

                steps++;
            }
        }

        private (int X, int Y) FindLowestNeighbor(int x, int y)
        {
            float lowestElevation = elevationNoise[x, y];
            int lowestX = x;
            int lowestY = y;

            // Check all 8 neighbors
            for (int nx = -1; nx <= 1; nx++)
            {
                for (int ny = -1; ny <= 1; ny++)
                {
                    // Skip center tile
                    if (nx == 0 && ny == 0) continue;

                    // Check if neighbor is within bounds
                    int newX = x + nx;
                    int newY = y + ny;
                    if (newX >= 0 && newX < Width && newY >= 0 && newY < Height)
                    {
                        // If this neighbor has lower elevation, it becomes our new lowest
                        if (elevationNoise[newX, newY] < lowestElevation)
                        {
                            lowestElevation = elevationNoise[newX, newY];
                            lowestX = newX;
                            lowestY = newY;
                        }
                    }
                }
            }

            return (lowestX, lowestY);
        }

        private Terrains DetermineTerrain(float elevation, float moisture)
        {
            // Water (ocean and lakes)
            if (elevation < WATER_LEVEL)
            {
                return Terrains.Lake;
            }

            // High elevation = mountains
            if (elevation > MOUNTAIN_LEVEL)
            {
                return Terrains.Cliff;
            }

            // Hills
            if (elevation > HILL_LEVEL)
            {
                return Terrains.Cliff; // Using Cliff for hills too for simplicity
            }

            // Determine biomes based on moisture for flat terrain
            if (moisture < DESERT_MOISTURE)
            {
                return Terrains.Desert;
            }
            else if (moisture < GRASS_MOISTURE)
            {
                return Terrains.Grass;
            }
            else
            {
                return Terrains.Woods;
            }
        }

        private int GetMovementCostForTerrain(Terrains terrain)
        {
            return terrain switch
            {
                Terrains.Grass => 1,
                Terrains.Cliff => 3,
                Terrains.River => 2,
                Terrains.Lake => 2, // Water is hard to traverse
                Terrains.Woods => 2,
                Terrains.Desert => 2,
                Terrains.Farm => 1,
                _ => 1,
            };
        }
    }
}