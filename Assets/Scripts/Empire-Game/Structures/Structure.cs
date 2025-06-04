using System;
using System.Collections.Generic;


namespace EmpireGame
{

    public abstract class Structure
    {

        public int Id { get; set; }
        public int Health { get; set; }
        public int MaxHealth { get; set; }

        public Tile MyTile { get; set; }

        public int BuildTime { get; set; }

        


        public virtual void TakeDamage(int amount)
        {
            Health -= amount;
        }

        public virtual void Repair(int amount)
        {
            Health += amount;
        }
    }
}