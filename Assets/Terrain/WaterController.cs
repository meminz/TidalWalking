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
        // start at minimum level
        currentWaterLevel = minWaterLevel;
        UpdateWaterPosition();
    }

    void Update()
    {
        // update water level
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
        // calculate cycle duration
        float range = maxWaterLevel - minWaterLevel;
        float cycleDuration = range / waterSpeed * 2f; // Up + down

        // normalize time within cycle
        float timeInCycle = futureTime % cycleDuration;
        float halfCycle = cycleDuration / 2f;

        if (timeInCycle < halfCycle)
        {
            // rising phase
            return minWaterLevel + (waterSpeed * timeInCycle);
        }
        else
        {
            // falling phase
            float fallingTime = timeInCycle - halfCycle;
            return maxWaterLevel - (waterSpeed * fallingTime);
        }
    }
}
