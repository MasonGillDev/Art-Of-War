using System;
using System.Collections.Generic;

namespace EmpireGame
{
    public class Tile
    {
        private static readonly Random Random = new Random();

        public int Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }

        public List<Person> Population { get; set; } = new List<Person>();

        public Terrains TerrainType { get; set; }
        public Resource Resource { get; set; }

        public Kingdom? Owner { get; set; }
        public Structure? Structure { get; set; }

        public int Age { get; set; } = 0;
        public int age
        {
            get { return Age; }
            set 
            {
                Age = value;
                // Notify listeners that the tile has been updated.
                OnTileUpdated?.Invoke();
            }
        }

        // This event is fired when the tile’s data (like Age) changes.
        public event Action OnTileUpdated;

        public int MovementCost { get; set; }
        public float? ResourceGathering { get; set; } = 0.0f;

        public Tile(int x, int y, Terrains terrainType, int movementCost)
        {
            X = x;
            Y = y;
            TerrainType = terrainType;
            MovementCost = movementCost;

            DetermineResource(TerrainType);
        }



        private void DetermineResource(Terrains terrain)
        {
            switch (terrain)
            {
                case Terrains.Grass:
                    // 50% chance Livestock, 50% Wool
                    Resource = (Random.Next(2) == 0)
                        ? Resource.Livestock
                        : Resource.Wool;
                    ResourceGathering = 0.1f;
                    break;

                case Terrains.Cliff:
                    // 25% Iron, 75% Stone
                    Resource = (Random.Next(4) == 0)
                        ? Resource.Iron
                        : Resource.Stone;

                    break;

                case Terrains.River:
                case Terrains.Lake:
                    Resource = Resource.Water;
                    ResourceGathering = 0.1f;
                    break;

                case Terrains.Woods:
                    Resource = Resource.Wood;
                    ResourceGathering = 0.1f;
                    break;

                case Terrains.Desert:
                    Resource = Resource.None;
                    break;

                case Terrains.Farm:
                    Resource = Resource.Crops;
                    ResourceGathering = 0.1f;
                    break;

                default:
                    Resource = Resource.None;
                    break;
            }
        }
    }

    public enum Terrains
    {
        Grass,
        Cliff,
        River,
        Lake,
        Woods,
        Desert,
        Farm,
        Enemy
    }

    public enum Resource
    {
        Wood,
        Iron,
        Wool,
        Crops,
        Livestock,
        Stone,
        Water,
        None
    }
}
