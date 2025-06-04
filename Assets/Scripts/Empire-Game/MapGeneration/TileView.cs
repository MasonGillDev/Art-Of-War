using UnityEngine;
using EmpireGame;

public class TileView : MonoBehaviour
{
    public Tile tileData; // The underlying data model for this tile.

    
    private SpriteRenderer spriteRenderer;

    void Awake()
    {
        spriteRenderer = GetComponent<SpriteRenderer>();
    }

    void OnEnable()
    {
        if (tileData != null)
            tileData.OnTileUpdated += UpdateVisuals;
    }

    void OnDisable()
    {
        if (tileData != null)
            tileData.OnTileUpdated -= UpdateVisuals;
    }

    // This method updates the view based on the current tile data.
    public void UpdateVisuals()
    {
        string path = $"Sprites/Landscape/{tileData.TerrainType}"; 
        if(tileData.TerrainType == Terrains.River || tileData.TerrainType == Terrains.Lake)
        {
            path = "Sprites/Landscape/Water";
        }
        Sprite s = Resources.Load<Sprite>(path);
        if(s != null)
        {
            spriteRenderer.sprite = s;
        }
        else{
            Debug.LogWarning($"Sprite not found at {path}");
        }

       

        // You could also update the sprite itself or other visual properties.
        Debug.Log($"Tile at ({tileData.X}, {tileData.Y}) updated. Age: {tileData.Age}");
    }

    // This method handles the click event (part of the view).
    void OnMouseDown()
    {
        Debug.Log("Tile clicked: " + tileData.Population.Count);
        // You could notify a central manager here or update the model.
        // For example: GameManager.Instance.HandleTileClick(tileData);
    }

    // This method handles time-based visual updates.
    // It could be called by subscribing to the GameClock event.
    
}
