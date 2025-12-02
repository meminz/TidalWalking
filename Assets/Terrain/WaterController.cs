using UnityEngine;

public class WaterController : MonoBehaviour
{
    [Header("Water Level Settings")]
    public float minWaterLevel = 0.5f;
    public float maxWaterLevel = 7f;
    public float waterSpeed = 0.5f;

    private float currentWaterLevel;
    private bool isRising = true;

    void Start()
    {
        // Start at minimum level
        currentWaterLevel = minWaterLevel;
        UpdateWaterPosition();
    }

    void Update()
    {
        // Update water level
        if (isRising)
        {
            currentWaterLevel += waterSpeed * Time.deltaTime;
            if (currentWaterLevel >= maxWaterLevel)
            {
                currentWaterLevel = maxWaterLevel;
                isRising = false;
            }
        }
        else
        {
            currentWaterLevel -= waterSpeed * Time.deltaTime;
            if (currentWaterLevel <= minWaterLevel)
            {
                currentWaterLevel = minWaterLevel;
                isRising = true;
            }
        }

        UpdateWaterPosition();
    }

    void UpdateWaterPosition()
    {
        Vector3 pos = transform.position;
        pos.y = currentWaterLevel;
        transform.position = pos;
    }

    public float GetCurrentWaterLevel()
    {
        return currentWaterLevel;
    }

    public bool IsRising()
    {
        return isRising;
    }


    public float GetWaterLevelAtTime(float futureTime)
    {
        // Calculate cycle duration
        float range = maxWaterLevel - minWaterLevel;
        float cycleDuration = range / waterSpeed * 2f; // Up + down

        // Normalize time within cycle
        float timeInCycle = futureTime % cycleDuration;
        float halfCycle = cycleDuration / 2f;

        if (timeInCycle < halfCycle)
        {
            // Rising phase
            return minWaterLevel + (waterSpeed * timeInCycle);
        }
        else
        {
            // Falling phase
            float fallingTime = timeInCycle - halfCycle;
            return maxWaterLevel - (waterSpeed * fallingTime);
        }
    }
}
