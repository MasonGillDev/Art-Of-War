
using System.Collections.Generic;
using System;

namespace EmpireGame
{
    public class Farm : Structure
    {
        public Map GameMap { get; private set; }
        public int MaxEmployment { get; set; } = 3;
        public int NumEmployed { get; set; } = 0;
        public List<Person> Workers { get; set; } = new List<Person>();
        public int Level { get; set; }
        public float CropBoost { get; set; } = 0.1f;

        public int CropBoostRadious { get; set; } = 1;
        public List<Tile> TilesControlled { get; set; } = new List<Tile>();

        public float CropProduction { get; set; }


        public Farm(Map gameMap, Tile myTile)
        {
            GameMap = gameMap;
            this.MyTile = myTile;
            this.MaxHealth = 50;
            this.Health = 50;
            this.BuildTime = 3;
            myTile.Structure = this;
            // Adds tiles to TilesControlled and changed those tiles resource and terrain.
            BoostCropYield();

            CropProduction = CropBoost * TilesControlled.Count * NumEmployed;

        }

        public void BoostCropYield()
        {
            int centerX = MyTile.X;
            int centerY = MyTile.Y;

            for (int dx = -CropBoostRadious; dx <= CropBoostRadious; dx++)
            {
                for (int dy = -CropBoostRadious; dy <= CropBoostRadious; dy++)
                {



                    if (Math.Sqrt(dx * dx + dy * dy) > CropBoostRadious)
                        continue;

                    int newX = centerX + dx;
                    int newY = centerY + dy;

                    if (newX >= 0 && newX < GameMap.Width && newY >= 0 && newY < GameMap.Height)
                    {
                        if (GameMap.Tiles[newX, newY].TerrainType == Terrains.Grass && GameMap.Tiles[newX, newY].Structure == null)
                        {
                            

                            GameMap.Tiles[newX, newY].Resource = Resource.Crops;
                            GameMap.Tiles[newX, newY].TerrainType = Terrains.Farm;
                            //Now the farm itself will handle the resource gathering rahter than each individual tile.
                            GameMap.Tiles[newX, newY].ResourceGathering = 0;

                            TilesControlled.Add(GameMap.Tiles[newX, newY]);
                        }
                        else
                        {
                            
                        }
                    }
                }
            }
        }

        public void LevelUp()
        {
            this.Level += 1;
            this.MaxEmployment += 2;
            this.CropBoost += 0.2f;
            this.CropBoostRadious += 1;
            BoostCropYield();
        }

        public void AddWorker(Person worker)
        {
            if (NumEmployed == MaxEmployment)
            {
                return;
            }
            NumEmployed += 1;
            Workers.Add(worker);
        }


        public void RemoveWorker(Person worker)
        {
            if (NumEmployed == 0)
            {
                return;
            }
            Workers.Remove(worker);
        }
    }
}