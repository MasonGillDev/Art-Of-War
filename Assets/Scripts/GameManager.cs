using EmpireGame;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

public class GameManager : MonoBehaviour
{
   

    [Header("Map Settings")]
    public int mapWidth = 100;
    public int mapHeight = 100;

    public float noiseScale = 0.13f;
    public int octaves = 6;
    public float persistence = 0.7f;
    public float lacunarity = 2.0f;
    private Map map;
    public Sprite tileSprite; 
    private Kingdom[] kingdoms = new Kingdom[4];
    
    [Header("UI")]
    public ResourceDisplay resourceDisplay;

    void Start()
    {
    
     InitializeMap();

     GenerateKingdoms();

     RenderMap();
     
     // Set up resource display for the first kingdom (player's kingdom)
     if (resourceDisplay != null && kingdoms[0] != null)
     {
         resourceDisplay.SetKingdom(kingdoms[0]);
     }
    }

     void InitializeMap()
    {
        
        map = new Map(mapWidth, mapHeight, noiseScale, octaves, persistence, lacunarity);        

    }

    void RenderMap()
    {
        
        for (int x = 0; x < map.Width; x++)
        {
            for (int y = 0; y < map.Height; y++)
            {
                Tile tile = map.Tiles[x , y];
                
               
                CreateTileVisual(tile);
            }
        }
    }

    void CreateTileVisual(Tile tile)
{
    GameObject tileGO = new GameObject("Tile_" + tile.X + "_" + tile.Y);
    tileGO.transform.position = new Vector3(tile.X, tile.Y, 0);

    SpriteRenderer sr = tileGO.AddComponent<SpriteRenderer>();

    TileView view = tileGO.AddComponent<TileView>();

    // if (tileSprite != null)
    //     {
    //         sr.sprite = tileSprite;
    //     }
    //     else
    //     {
    //         Debug.LogWarning("tileSprite is not assigned in GameManager!");
    //     }
    

    
    tileGO.transform.localScale = new Vector3(7, 7, 1);


    BoxCollider2D collider = tileGO.AddComponent<BoxCollider2D>();
    collider.size = new Vector2(1, 1);
    view.tileData = tile;
    view.UpdateVisuals();
    
    Debug.Log($"Created tile at ({tile.X}, {tile.Y}) with terrain {tile.TerrainType}");
}


  void GenerateKingdoms()
{
    // Create 4 kingdoms
    for (int i = 0; i < 4; i++)
    {
        kingdoms[i] = new Kingdom();
    }
    
    // Create 4 kingdom center points with more randomization
    Vector2[] kingdomCenters = new Vector2[4]
    {
        new Vector2(mapWidth * Random.Range(0.15f, 0.35f), mapHeight * Random.Range(0.15f, 0.35f)),  // Bottom-left
        new Vector2(mapWidth * Random.Range(0.65f, 0.85f), mapHeight * Random.Range(0.15f, 0.35f)),  // Bottom-right
        new Vector2(mapWidth * Random.Range(0.15f, 0.35f), mapHeight * Random.Range(0.65f, 0.85f)),  // Top-left
        new Vector2(mapWidth * Random.Range(0.65f, 0.85f), mapHeight * Random.Range(0.65f, 0.85f))   // Top-right
    };

        // Add the origin tile to the kingdoms
        for (int i = 0; i < 4; i++)
        {
            int originX = Mathf.RoundToInt(kingdomCenters[i].x);
            int originY = Mathf.RoundToInt(kingdomCenters[i].y);

            // Make sure coordinates are within bounds
            originX = Mathf.Clamp(originX, 0, mapWidth - 1);
            originY = Mathf.Clamp(originY, 0, mapHeight - 1);

            kingdoms[i].OriginTile = map.Tiles[originX, originY];
            kingdoms[i].InitPopulation(20);
    }
    
    // Create a secondary influence point for each kingdom for more complex shapes
        Vector2[] secondaryCenters = new Vector2[4];
        for (int i = 0; i < 4; i++)
        {
            // Place a secondary center within a certain range of the main center
            float angle = Random.Range(0, Mathf.PI * 2);
            float distance = Random.Range(mapWidth * 0.1f, mapWidth * 0.25f);
            secondaryCenters[i] = kingdomCenters[i] + new Vector2(
                Mathf.Cos(angle) * distance,
                Mathf.Sin(angle) * distance
            );

            // Keep it within map bounds
            secondaryCenters[i].x = Mathf.Clamp(secondaryCenters[i].x, 0, mapWidth);
            secondaryCenters[i].y = Mathf.Clamp(secondaryCenters[i].y, 0, mapHeight);
    }
    
    // Different noise scales for more variety
    float[] noiseScales = new float[] { 0.03f, 0.05f, 0.04f, 0.06f }; 
    
    // Different influence weights for each kingdom
    float[] kingdomStrengths = new float[] { 1.0f, 1.2f, 0.9f, 1.1f };
    
    // Different terrain type preferences (lower values = more likely to claim)
    float[,] terrainPreference = new float[4, System.Enum.GetValues(typeof(Terrains)).Length];
    
    // Randomize terrain preferences for each kingdom
    for (int k = 0; k < 4; k++) {
        for (int t = 0; t < terrainPreference.GetLength(1); t++) {
            terrainPreference[k, t] = Random.Range(0.7f, 1.3f);
        }
        // Each kingdom has one terrain they particularly prefer
        terrainPreference[k, Random.Range(0, terrainPreference.GetLength(1))] = 0.5f;
    }
    
    // Assign each tile to a kingdom
    for (int x = 0; x < map.Width; x++)
    {
        for (int y = 0; y < map.Height; y++)
        {
            Tile tile = map.Tiles[x, y];
            
            // Skip water tiles - they don't belong to any kingdom
            if (tile.TerrainType == Terrains.Lake || tile.TerrainType == Terrains.River)
                continue;
                
            // Calculate influence from each kingdom
            float[] influences = new float[4];
            
            for (int i = 0; i < 4; i++)
            {
                // Primary center influence
                float primaryDist = Vector2.Distance(new Vector2(x, y), kingdomCenters[i]);
                
                // Secondary center influence (adds more complex shapes)
                float secondaryDist = Vector2.Distance(new Vector2(x, y), secondaryCenters[i]);
                float combinedDist = Mathf.Min(primaryDist, secondaryDist * 1.2f); // Secondary slightly weaker
                
                // Add perlin noise with kingdom-specific scale
                float noise1 = Mathf.PerlinNoise(x * noiseScales[i], y * noiseScales[i]) * 15;
                float noise2 = Mathf.PerlinNoise(x * noiseScales[i] * 2, y * noiseScales[i] * 2) * 7;
                
                // Natural barriers: mountains and hills are harder to claim
                float terrainFactor = 1.0f;
                if (tile.TerrainType == Terrains.Cliff) {
                    terrainFactor = 1.5f; // Harder to claim mountains
                }
                
                // Kingdom's preference for this terrain type
                float terrainPreferenceFactor = terrainPreference[i, (int)tile.TerrainType];
                
                // Calculate final influence (smaller = stronger claim)
                influences[i] = (combinedDist + noise1 + noise2) * terrainFactor * terrainPreferenceFactor / kingdomStrengths[i];
            }
            
            // Find kingdom with strongest influence (smallest value)
            int strongestKingdom = 0;
            float strongestInfluence = influences[0];
            
            for (int i = 1; i < 4; i++)
            {
                if (influences[i] < strongestInfluence)
                {
                    strongestInfluence = influences[i];
                    strongestKingdom = i;
                }
            }
            
            // Contested territory - if two kingdoms have very similar influence
            bool isContested = false;
            for (int i = 0; i < 4; i++)
            {
                if (i != strongestKingdom && Mathf.Abs(influences[i] - strongestInfluence) < 2.0f)
                {
                    isContested = true;
                    break;
                }
            }
            
            
            tile.Owner = kingdoms[strongestKingdom];
            kingdoms[strongestKingdom].ControlledTiles.Add(tile);
            
            // if (strongestKingdom != 1)
                // {
                //     tile.TerrainType = Terrains.Enemy;
                // }

                // You could mark contested territories differently if you want
                if (isContested)
                {

                }
        }
    }
    
    
    
    Debug.Log("Kingdoms generated successfully with diverse borders");
}



}