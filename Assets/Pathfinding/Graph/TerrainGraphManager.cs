using UnityEngine;

[RequireComponent(typeof(Terrain))]
public class TerrainGraphManager : MonoBehaviour
{
    [Header("Graph Settings")]
    public float tileSize = 1f;

    private TerrainGraph terrainGraph;
    private Terrain terrain;

    void Awake()
    {
        terrain = GetComponent<Terrain>();
    }

    public void BuildGraph()
    {
        Debug.Log("Building terrain graph...");
        terrainGraph = new TerrainGraph(terrain, tileSize);
        Debug.Log($"Graph built with {terrainGraph.GetMatrix().Length} nodes");
    }

    public TerrainGraph GetGraph()
    {
        return terrainGraph;
    }
}