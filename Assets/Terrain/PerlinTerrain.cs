using UnityEngine;

[RequireComponent(typeof(Terrain))]
public class PerlinTerrain : MonoBehaviour
{
	[Header("Terrain Settings")]
	public Vector3 terrainSize = new(50, 10, 100); // width, height, length

	[Header("Perlin Noise Settings")]
	public float perlinScale = 10f;
	public int octaves = 5;
	public float persistence = 1f;

	[Header("Corner Constraints")]
	public float minCornerHeight = 8.5f;
	public float safeZoneRadius = 8f;

	[Header("Debug")]
	public int randomSeed = 0;
	public bool makeItFlat = false;

	private Terrain terrain;
	private TerrainData terrainData;
	private float[,] heightMap;

	void Start()
	{
		GenerateTerrain();
	}

	public void GenerateTerrain()
	{
		// Initialize random seed
		if (randomSeed == 0) randomSeed = (int)System.DateTime.Now.Ticks;
		Random.InitState(randomSeed);

		// Get terrain components
		terrain = GetComponent<Terrain>();
		terrainData = terrain.terrainData;

		// Set terrain size to match our requirements
		terrainData.size = terrainSize;

		if (makeItFlat)
		{
			int resolution = terrainData.heightmapResolution;
			terrainData.SetHeights(0, 0, new float[resolution, resolution]);
			return;
		}

		// Generate the heightmap
		GenerateHeightMap();

		// Apply corner constraints
		EnsureCornerHeights();

		// Apply to terrain
		terrainData.SetHeights(0, 0, heightMap);

		Debug.Log($"Terrain generated with seed {randomSeed}");
		Debug.Log($"Corner heights: " +
				  $"[0,0]={GetWorldHeight(0, 0):F2}m, " +
				  $"[0,max]={GetWorldHeight(0, heightMap.GetLength(1) - 1):F2}m, " +
				  $"[max,0]={GetWorldHeight(heightMap.GetLength(0) - 1, 0):F2}m, " +
				  $"[max,max]={GetWorldHeight(heightMap.GetLength(0) - 1, heightMap.GetLength(1) - 1):F2}m");

		TerrainGraphManager graphManager = GetComponent<TerrainGraphManager>();
		if (graphManager==null) Debug.Log("wtf");
		graphManager?.BuildGraph();
	}

	void GenerateHeightMap()
	{
		int resolution = terrainData.heightmapResolution;
		heightMap = new float[resolution, resolution];

		// Random offset for Perlin noise to avoid always using the same pattern
		float offsetX = Random.Range(0f, 10000f);
		float offsetZ = Random.Range(0f, 10000f);

		// Track min/max for normalization
		float minHeight = float.MaxValue;
		float maxHeight = float.MinValue;

		// Generate raw Perlin noise with multiple octaves
		for (int z = 0; z < resolution; ++z)
		{
			for (int x = 0; x < resolution; ++x)
			{
				// Convert heightmap coordinates to world coordinates (0-1 range)
				float worldX = (float)x / (resolution - 1);
				float worldZ = (float)z / (resolution - 1);

				// Generate multi-octave Perlin noise
				float height = GeneratePerlinOctaves(worldX, worldZ, offsetX, offsetZ);

				heightMap[z, x] = height;

				if (height < minHeight) minHeight = height;
				if (height > maxHeight) maxHeight = height;
			}
		}

		// Normalize to 0-1 range
		NormalizeHeightMap(minHeight, maxHeight);
	}

	float GeneratePerlinOctaves(float x, float z, float offsetX, float offsetZ)
	{
		float total = 0f;
		float amplitude = 1f;
		float frequency = 1f;
		float maxValue = 0f; // Used for normalizing result to 0-1

		for (int i = 0; i < octaves; ++i)
		{
			float sampleX = x * frequency * perlinScale + offsetX;
			float sampleZ = z * frequency * perlinScale + offsetZ;

			// Perlin noise returns value between 0 and 1, we shift it to -1 to 1
			float perlinValue = Mathf.PerlinNoise(sampleX, sampleZ) * 2f - 1f;

			total += perlinValue * amplitude;
			maxValue += amplitude;

			amplitude *= persistence;
		}

		// Normalize to 0-1 range
		return (total / maxValue + 1f) * 0.5f;
	}

	void NormalizeHeightMap(float min, float max)
	{
		int resolution = heightMap.GetLength(0);

		for (int z = 0; z < resolution; ++z)
		{
			for (int x = 0; x < resolution; ++x)
			{
				heightMap[z, x] = (heightMap[z, x] - min) / (max - min);
			}
		}
	}

	void EnsureCornerHeights()
	{
		int maxX = heightMap.GetLength(1) - 1;
		int maxZ = heightMap.GetLength(0) - 1;

		Vector2Int startCorner = new(0, 0);
		Vector2Int goalCorner = new(maxZ, maxX);

		EnsureCornerMinimumHeight(startCorner);
		EnsureCornerMinimumHeight(goalCorner);
	}

	void EnsureCornerMinimumHeight(Vector2Int cornerHeightmapPos)
	{
		int resolution = heightMap.GetLength(0);
		float minHeightNormalized = minCornerHeight / terrainSize.y;

		float worldToHeightmapX = (resolution - 1) / terrainSize.x;
		float worldToHeightmapZ = (resolution - 1) / terrainSize.z;

		int radiusHM_X = Mathf.CeilToInt(safeZoneRadius * worldToHeightmapX);
		int radiusHM_Z = Mathf.CeilToInt(safeZoneRadius * worldToHeightmapZ);
		int blendRadiusHM_X = Mathf.CeilToInt(safeZoneRadius * 2f * worldToHeightmapX);
		int blendRadiusHM_Z = Mathf.CeilToInt(safeZoneRadius * 2f * worldToHeightmapZ);

		for (int dz = -blendRadiusHM_Z; dz <= blendRadiusHM_Z; ++dz)
		{
			for (int dx = -blendRadiusHM_X; dx <= blendRadiusHM_X; ++dx)
			{
				int nz = cornerHeightmapPos.x + dz;
				int nx = cornerHeightmapPos.y + dx;

				if (nx < 0 || nx >= heightMap.GetLength(1) || nz < 0 || nz >= resolution)
					continue;

				float normalizedDistX = (float)Mathf.Abs(dx) / radiusHM_X;
				float normalizedDistZ = (float)Mathf.Abs(dz) / radiusHM_Z;
				float normalizedDist = Mathf.Sqrt(normalizedDistX * normalizedDistX + normalizedDistZ * normalizedDistZ);

				if (normalizedDist <= 1f)
				{
					// Core safe zone - ensure minimum height, preserve hills
					heightMap[nz, nx] = Mathf.Max(heightMap[nz, nx], minHeightNormalized);
				}
				else if (normalizedDist <= 2f)
				{
					// Blend zone - smoothly transition
					float blendFactor = 1f - ((normalizedDist - 1f) / 1f);
					blendFactor = Mathf.Clamp01(blendFactor);
					blendFactor = blendFactor * blendFactor * (3f - 2f * blendFactor);

					float currentHeight = heightMap[nz, nx];
					float targetHeight = Mathf.Max(currentHeight, minHeightNormalized);
					heightMap[nz, nx] = Mathf.Lerp(currentHeight, targetHeight, blendFactor);
				}
			}
		}
	}


	// Utility: Get actual world height at heightmap coordinates
	float GetWorldHeight(int z, int x)
	{
		return heightMap[z, x] * terrainSize.y;
	}

	// Public method to regenerate terrain (useful for testing)
	[ContextMenu("Regenerate Terrain")]
	public void RegenerateTerrain()
	{
		randomSeed = 0; // Force new seed
		GenerateTerrain();
	}
}
