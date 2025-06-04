using UnityEngine;
using TMPro;

public class ClockDisplay : MonoBehaviour
{
    public TMP_Text minuteText;
    void Update()
    {
        if (GameClock.Instance != null)
            minuteText.text = $"Minute: {GameClock.Instance.currentMinute}";
    }
}
