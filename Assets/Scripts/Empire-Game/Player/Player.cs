using UnityEngine;
using EmpireGame;

public class PlayerManager : MonoBehaviour
{
    public static PlayerManager Instance { get; private set; }

    // The kingdom that belongs to the player
    public Kingdom PlayerKingdom { get; private set; }

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Call this once your kingdoms are generated.
    /// </summary>
    public void SetPlayerKingdom(Kingdom kingdom)
    {
        PlayerKingdom = kingdom;
        Debug.Log("Player kingdom assigned: " + kingdom.GetHashCode());
    }
}
