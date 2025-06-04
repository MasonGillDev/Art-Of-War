using UnityEngine;

using EmpireGame;


public class TileClickHandler : MonoBehaviour
{
    public Tile tileData; 

    void OnMouseDown()
    {
        Debug.Log("Tile clicked: " + tileData.TerrainType);

    }
}
