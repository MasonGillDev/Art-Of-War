using UnityEngine;
using System;
using System.Collections;

public class GameClock : MonoBehaviour
{
    public static GameClock Instance { get; private set; }
    
    // The tick interval in real time (seconds) for each in-game minute.
    public float tickInterval = 1f;
    
    // The current in-game minute count.
     [Header("Game Time:")]
    public int currentMinute { get; private set; } = 0;
    
    // Event that fires every tick; subscribers receive the new minute as a parameter.
    public event Action<int> OnMinuteTick;

    private void Awake()
    {
        // Implement a singleton pattern for easy global access.
        if (Instance == null)
            Instance = this;
        else
            Destroy(gameObject);
    }
    
    private IEnumerator Start()
    {
        while (true)
        {
            yield return new WaitForSeconds(tickInterval);
            currentMinute++;
            OnMinuteTick?.Invoke(currentMinute);
            Debug.Log("Minute Tick: " + currentMinute);
        }
    }
}
