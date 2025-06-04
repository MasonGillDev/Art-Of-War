using UnityEngine;
using System.Collections.Generic;
using EmpireGame;

public class MovementManager : MonoBehaviour
{
    private List<Person> people = new List<Person>();
    private List<Tile> tiles = new List<Tile>();

    private Tile CurrentTile {get;set;}

    private int TileTimeRequired {get;set;}

    private int TimeAtTile {get;set;} = 0;
    private int TileIndex {get;set;} = 0;



    public static MovementManager Instance { get; private set; }

    private void Awake()
    {
        // Optional: If you want only one instance at a time
        if (Instance == null)
            Instance = this;
        else
            Destroy(gameObject);
    }

    private void OnDisable()
    {
        if (GameClock.Instance != null)
            GameClock.Instance.OnMinuteTick -= UpdateJourney;
    }

    private void OnEnable()
    {
        GameClock.Instance.OnMinuteTick += UpdateJourney;
    }

    /// <summary>
    /// Initializes the MovementManager with a list of people to move.
    /// </summary>
    /// <param name="newPeople">The people that will be managed by this MovementManager.</param>
    public void Initialize(List<Person> newPeople, List<Tile> tilePath)
    {
        people = new List<Person>(newPeople);
        tiles = tilePath;
        Debug.Log("MovementManager initialized with " + people.Count + " people.");

        CurrentTile = tiles[TileIndex];
        TileTimeRequired = CurrentTile.MovementCost;

    }



    private void UpdateJourney(int currentMinute)
{
    TimeAtTile++;

    if (TimeAtTile >= TileTimeRequired)
    {
        TileIndex++;

        if (TileIndex >= tiles.Count)
        {
            Debug.Log("Journey completed. Destroying MovementManager.");
            Destroy(gameObject); // This destroys the MovementManager GameObject.
            return;
        }

        CurrentTile = tiles[TileIndex];

        foreach (var person in people)
        {
            person.PositionX = CurrentTile.X;
            person.PositionY = CurrentTile.Y;
            CurrentTile.Population.Add(person);
        }

        TimeAtTile = 0;
        TileTimeRequired = CurrentTile.MovementCost;
    }
}





    
}
