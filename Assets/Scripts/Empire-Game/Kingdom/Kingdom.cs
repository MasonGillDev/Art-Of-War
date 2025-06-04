using System;
using System.Collections.Generic;

namespace EmpireGame
{
    public class Kingdom
    {

        public List<Tile> VisableTiles { get; set; } = new List<Tile>();

        public int WoodStock { get; set; } = 50;
        public int IronStock { get; set; } = 50;
        public int WoolStock { get; set; } = 50;
        public int CropsStock { get; set; } = 50;
        public int LivestockStock { get; set; } = 50;
        public int StoneStock { get; set; } = 50;
        public int WaterStock { get; set; } = 50;
        public List<Person> Population { get; set; } = new List<Person>();

        public List<Tile> ControlledTiles { get; set; } = new List<Tile>();

        public Tile OriginTile { get; set; }


        public void InitPopulation(int PopCount)
        {
            for (int i = 0; i < PopCount; i++)
            {
                Person person = new Person();
                person.PositionX = OriginTile.X;
                person.PositionY = OriginTile.Y;
                person.age = UnityEngine.Random.Range(18, 50);
                Population.Add(person);
            }
        }

    }
}