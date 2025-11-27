using UnityEngine;

[RequireComponent(typeof(Terrain))]
public class TerrainGraphVisualizer : MonoBehaviour
{
    [Header("Graph Settings")]
    public float tileSize = 2f;

    [Header("Visualization")]
    public bool showNodes = true;
    public bool showEdges = false;

    private TerrainGraph terrainGraph;
    private Terrain terrain;

    void Start()
    {
        terrain = GetComponent<Terrain>();
        terrainGraph = new TerrainGraph(terrain, tileSize);
        Debug.Log($"Graph created with {terrainGraph.GetMatrix().Length} nodes");
    }

    public TerrainGraph GetTerrainGraph()
    {
        return terrainGraph;
    }

    void OnDrawGizmos()
    {
        if (terrainGraph == null || !showNodes)
            return;

        Node[,] matrix = terrainGraph.GetMatrix();
        if (matrix == null)
            return;

        int gridWidth = matrix.GetLength(0);
        int gridLength = matrix.GetLength(1);

        // draw nodes
        for (int i = 0; i < gridWidth; ++i)
        {
            for (int j = 0; j < gridLength; ++j)
            {
                Node node = matrix[i, j];
                Vector3 pos = terrainGraph.GetNodePosition(node);

                // color based on height
                float normalizedHeight = pos.y / terrain.terrainData.size.y;
                Gizmos.color = Color.Lerp(Color.red, Color.green, normalizedHeight);

                Gizmos.DrawSphere(pos, 0.3f);
            }
        }

        // draw edges
        if (showEdges)
        {
            Gizmos.color = new Color(1f, 1f, 0f, 0.3f);
            Graph graph = terrainGraph.GetGraph();
            Node[] nodes = graph.getNodes();

            foreach (Node node in nodes)
            {
                Vector3 fromPos = terrainGraph.GetNodePosition(node);
                Edge[] edges = graph.getConnections(node);

                foreach (Edge edge in edges)
                {
                    Vector3 toPos = terrainGraph.GetNodePosition(edge.to);
                    Gizmos.DrawLine(fromPos, toPos);
                }
            }
        }
    }
}