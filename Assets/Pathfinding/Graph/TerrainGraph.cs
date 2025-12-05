using System;
using System.Collections.Generic;
using UnityEngine;

public class TerrainGraph
{
    private float tileSize;
    private Node[,] matrix;
    private Dictionary<Node, Vector3> nodePositions; // world position (x, y=height, z)
    private Dictionary<Node, Vector2Int> nodeIndices; // grid indices (i, j)
    private Graph graph;

    private Terrain terrain;
    private TerrainData terrainData;
    private Vector3 terrainSize;
    private float safeHeight;

    // 8-directional movement: N, NE, E, SE, S, SW, W, NW
    private readonly Vector2Int[] directions = new Vector2Int[]
    {
        Vector2Int.up,                          // N  (0, 1)
        Vector2Int.right,                       // E  (1, 0)
        Vector2Int.down,                        // S  (0, -1)
        Vector2Int.left,                        // W  (-1, 0)
        Vector2Int.up + Vector2Int.right,       // NE (1, 1)
        Vector2Int.down + Vector2Int.right,     // SE (1, -1)
        Vector2Int.down + Vector2Int.left,      // SW (-1, -1)
        Vector2Int.up + Vector2Int.left         // NW (-1, 1)
    };

    public TerrainGraph(Terrain terrain, float tileSize, float safeHeight)
    {
        this.terrain = terrain;
        this.terrainData = terrain.terrainData;
        this.terrainSize = terrainData.size;
        this.tileSize = tileSize;
        this.safeHeight = safeHeight;

        // Calculate grid dimensions
        int gridWidth = (int)Math.Floor(terrainSize.x / tileSize);
        int gridLength = (int)Math.Floor(terrainSize.z / tileSize);

        matrix = new Node[gridWidth, gridLength];
        nodePositions = new Dictionary<Node, Vector3>();
        nodeIndices = new Dictionary<Node, Vector2Int>();
        graph = new Graph();

        BuildGraph();
    }

    void BuildGraph()
    {
        int gridWidth = matrix.GetLength(0);
        int gridLength = matrix.GetLength(1);

        // First pass: create all nodes
        for (int i = 0; i < gridWidth; ++i)
        {
            for (int j = 0; j < gridLength; ++j)
            {
                // Calculate world position (center of tile)
                float worldX = (i + 0.5f) * tileSize;
                float worldZ = (j + 0.5f) * tileSize;

                // Get height from terrain at this position
                float height = GetTerrainHeightAtPosition(worldX, worldZ);

                // Create node
                Node n = new Node($"Node_{i}_{j}");
                matrix[i, j] = n;

                // Store position and indices
                Vector3 worldPos = terrain.transform.position + new Vector3(worldX, height, worldZ);
                nodePositions[n] = worldPos;
                nodeIndices[n] = new Vector2Int(i, j);

                // Add to graph
                graph.AddNode(n);
            }
        }

        // Second pass: create edges between neighbors
        for (int i = 0; i < gridWidth; ++i)
        {
            for (int j = 0; j < gridLength; ++j)
            {
                Node currentNode = matrix[i, j];
                Vector3 currentPos = nodePositions[currentNode];

                // Check all 8 directions
                foreach (Vector2Int dir in directions)
                {
                    int ni = i + dir.x;
                    int nj = j + dir.y;

                    // Check bounds
                    if (ni >= 0 && ni < gridWidth && nj >= 0 && nj < gridLength)
                    {
                        Node neighborNode = matrix[ni, nj];
                        Vector3 neighborPos = nodePositions[neighborNode];

                        // Calculate edge weight based on distance and height difference
                        float weight = CalculateEdgeWeight(currentPos, neighborPos);

                        // Create edge
                        Edge edge = new Edge(currentNode, neighborNode, weight);
                        graph.AddEdge(edge);
                    }
                }
            }
        }
    }

    float GetTerrainHeightAtPosition(float worldX, float worldZ)
    {
        // Convert world position to terrain-local position (0-1 range)
        float normalizedX = worldX / terrainSize.x;
        float normalizedZ = worldZ / terrainSize.z;

        // Clamp to valid range
        normalizedX = Mathf.Clamp01(normalizedX);
        normalizedZ = Mathf.Clamp01(normalizedZ);

        // Get interpolated height from terrain
        float height = terrainData.GetInterpolatedHeight(normalizedX, normalizedZ);

        return height;
    }

    float CalculateEdgeWeight(Vector3 from, Vector3 to)
    {
        float cost = Vector3.Distance(from, to);
        float heightDiff = to.y - from.y;

        if (heightDiff > 0.1f)
            cost *= 2f;

        // float safeHeight = 7.1f; // Around max water level

        if (to.y < safeHeight)
        {
            float dangerPenalty = (safeHeight - to.y) * 6f;
            cost += dangerPenalty;
        }

        return cost;
    }

    // remove edges to/from nodes that are underwater
    public void UpdateWalkableNodes(float currentWaterLevel)
    {
        // rebuild edges based on water level
        // mark nodes as unwalkable by removing their outgoing edges

        int gridWidth = matrix.GetLength(0);
        int gridLength = matrix.GetLength(1);

        graph = new Graph();

        for (int i = 0; i < gridWidth; ++i)
        {
            for (int j = 0; j < gridLength; ++j)
            {
                graph.AddNode(matrix[i, j]);
            }
        }

        // skip underwater nodes
        for (int i = 0; i < gridWidth; ++i)
        {
            for (int j = 0; j < gridLength; ++j)
            {
                Node currentNode = matrix[i, j];
                Vector3 currentPos = nodePositions[currentNode];

                // skip if current node is underwater
                if (currentPos.y < currentWaterLevel)
                    continue;

                foreach (Vector2Int dir in directions)
                {
                    int ni = i + dir.x;
                    int nj = j + dir.y;

                    if (ni >= 0 && ni < gridWidth && nj >= 0 && nj < gridLength)
                    {
                        Node neighborNode = matrix[ni, nj];
                        Vector3 neighborPos = nodePositions[neighborNode];

                        // skip if neighbor is underwater
                        if (neighborPos.y < currentWaterLevel)
                            continue;

                        float weight = CalculateEdgeWeight(currentPos, neighborPos);

                        Edge edge = new Edge(currentNode, neighborNode, weight);
                        graph.AddEdge(edge);
                    }
                }
            }
        }
    }

    // quantization (world position to node)
    public Node GetNodeAtPosition(float worldX, float worldZ)
    {
        // Convert to local terrain coordinates
        Vector3 terrainPos = terrain.transform.position;
        float localX = worldX - terrainPos.x;
        float localZ = worldZ - terrainPos.z;

        int i = (int)Math.Floor(localX / tileSize);
        int j = (int)Math.Floor(localZ / tileSize);

        // Check bounds
        if (i < 0 || i >= matrix.GetLength(0) || j < 0 || j >= matrix.GetLength(1))
            return null;

        return matrix[i, j];
    }

    // localization (node to world position)
    public Vector3 GetNodePosition(Node n)
    {
        if (!nodePositions.ContainsKey(n))
            return Vector3.zero;

        return nodePositions[n];
    }

    // get the graph for pathfinding
    public Graph GetGraph()
    {
        return graph;
    }

    public void SetGraph(Graph newGraph)
    {
        graph=newGraph;
    }

    // get start and goal nodes (corners of terrain)
    public Node GetStartNode()
    {
        return matrix[0, 0];
    }

    public Node GetGoalNode()
    {
        return matrix[matrix.GetLength(0) - 1, matrix.GetLength(1) - 1];
    }

    // get all nodes
    public Node[,] GetMatrix()
    {
        return matrix;
    }

    public Vector2Int[] GetDirections()
    {
        return directions;
    }


}
