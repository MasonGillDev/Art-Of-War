using UnityEngine;
using System.Collections.Generic;
using EmpireGame;

public class PeopleManager : MonoBehaviour
{
    public static PeopleManager Instance { get; private set; }
    private List<Person> people = new List<Person>();
    
    private void Awake()
    {
        if (Instance == null)
            Instance = this;
        else
            Destroy(gameObject);
    }
    
    // Single registration point - no need for individual MonoBehaviours
    public void RegisterPerson(Person person)
    {
        if (!people.Contains(person))
            people.Add(person);
    }
    
    public void UnregisterPerson(Person person)
    {
        people.Remove(person);
    }
    
    private void OnEnable()
    {
        if (GameClock.Instance != null)
            GameClock.Instance.OnMinuteTick += UpdatePeople;
    }
    
    private void OnDisable()
    {
        if (GameClock.Instance != null)
            GameClock.Instance.OnMinuteTick -= UpdatePeople;
    }
    
    private void UpdatePeople(int currentMinute)
    {
        // This runs once per tick for all people
        for (int i = people.Count - 1; i >= 0; i--)
        {
            people[i].UpdateForMinute(currentMinute);
        }
    }
}
