using UnityEngine;
using UnityEngine.UI;
using EmpireGame;

public class ResourceDisplay : MonoBehaviour
{
    [Header("Resource Text Elements")]
    public Text woodText;
    public Text ironText;
    public Text woolText;
    public Text cropsText;
    public Text livestockText;
    public Text stoneText;
    public Text waterText;
    
    private Kingdom playerKingdom;
    
    public void SetKingdom(Kingdom kingdom)
    {
        playerKingdom = kingdom;
    }
    
    void Update()
    {
        if (playerKingdom != null)
        {
            UpdateResourceDisplay();
        }
    }
    
    void UpdateResourceDisplay()
    {
        if (woodText != null) woodText.text = $"Wood: {playerKingdom.WoodStock}";
        if (ironText != null) ironText.text = $"Iron: {playerKingdom.IronStock}";
        if (woolText != null) woolText.text = $"Wool: {playerKingdom.WoolStock}";
        if (cropsText != null) cropsText.text = $"Crops: {playerKingdom.CropsStock}";
        if (livestockText != null) livestockText.text = $"Livestock: {playerKingdom.LivestockStock}";
        if (stoneText != null) stoneText.text = $"Stone: {playerKingdom.StoneStock}";
        if (waterText != null) waterText.text = $"Water: {playerKingdom.WaterStock}";
    }
}