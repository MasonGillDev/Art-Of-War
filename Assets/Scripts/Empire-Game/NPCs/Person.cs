using System;
using System.Collections.Generic;
using NUnit.Framework.Constraints;
using UnityEngine;


namespace EmpireGame
{
    public  class Person
    {
        public int PositionX { get; set; }
        public int PositionY { get; set; }
        public int age { get; set; }
        public Structure? JobSite { get; set; }
        public House? Home { get; set; }
        public bool HasSlept { get; set; } = true;
        public JobType CurrrentJob { get; set; } = JobType.None;


        public Person()
        {
            PeopleManager.Instance?.RegisterPerson(this);
        }

        public virtual void UpdateForMinute(int currentMinute)
        {
            age++;
            if (age >= 75)
            {
                PeopleManager.Instance?.UnregisterPerson(this);
                // Additional death logic
            }
        }
        
         public void Remove()
        {
            PeopleManager.Instance?.UnregisterPerson(this);
        }

    }
}