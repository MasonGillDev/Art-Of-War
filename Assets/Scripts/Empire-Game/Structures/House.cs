
using System.Collections.Generic;
using System;
namespace EmpireGame
{
    public class House : Structure
    {
        public Map GameMap { get; private set; }

        public int Capacity { get; set; }
        public List<Person> Inhabitants { get; set; } = new List<Person>();

        public int OccupencySearchRadius { get; set; }
        public int Level { get; set; }

        public House(Map gameMap, Tile myTile)
        {
            GameMap = gameMap;
            this.MyTile = myTile;
            this.MaxHealth = 50;
            this.Health = 50;
            this.BuildTime = 3;
            myTile.Structure = this;
            OccupyHouse();
        }

        public void OccupyHouse()
        {
            int centerX = MyTile.X;
            int centerY = MyTile.Y;
            int radius = OccupencySearchRadius;

            for (int dx = -radius; dx <= radius; dx++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    // Optional: Enforce a circular search area.
                    if (Math.Sqrt(dx * dx + dy * dy) > radius)
                        continue;

                    int tileX = centerX + dx;
                    int tileY = centerY + dy;

                    if (tileX < 0 || tileX >= GameMap.Width || tileY < 0 || tileY >= GameMap.Height)
                        continue;

                    Tile tile = GameMap.Tiles[tileX, tileY];

                    foreach (Person person in tile.Population)
                    {
                        if (person.Home == null && person.CurrrentJob == JobType.None)
                        {
                            person.Home = this;
                            Inhabitants.Add(person);

                            if (Inhabitants.Count >= Capacity)
                                return;
                        }
                    }
                }
            }
        }

    }
}