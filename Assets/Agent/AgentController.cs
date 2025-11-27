using System.Collections.Generic;
using UnityEngine;

public class AgentController : MonoBehaviour
{
    [Header("References")]
    public TerrainGraphManager terrainGraphManager;
    public WaterController waterController;

    [Header("Movement Settings")]
    public float flatSpeed = 1f;
    public float uphillSpeed = 0.5f;
    public float safetyMargin = 0.5f; // Height above predicted water

    [Header("Pathfinding")]
    public float replanInterval = 1f; // Check for replanning every second
    public float predictionTime = 10f; // Predict water this many seconds ahead

    [Header("Debug")]
    public bool visualizePath = true;

    private TerrainGraph terrainGraph;
    private List<Node> currentPath;
    private int currentPathIndex = 0;
    private float timeSinceLastCheck = 0f;

    private Vector3 currentTarget;
    private bool isMoving = false;
    private bool reachedGoal = false;

    private FSM fsm;

    void Start()
    {
        if (terrainGraphManager == null)
            terrainGraphManager = FindFirstObjectByType<TerrainGraphManager>();
        if (waterController == null)
            waterController = FindFirstObjectByType<WaterController>();

        terrainGraph = terrainGraphManager.GetGraph();

        if (terrainGraph == null)
        {
            Debug.LogError("TerrainGraph not found!");
            return;
        }

        // Position agent at start node
        Node startNode = terrainGraph.GetStartNode();
        Vector3 startPos = terrainGraph.GetNodePosition(startNode);
        transform.position = startPos;

        // FSM Setup
        FSMState traverse = new();
        FSMState seekHighGround = new();
        FSMState goalReached = new();

        traverse.enterActions.Add(PlanPathToGoal);
        traverse.stayActions.Add(TraverseUpdate);

        seekHighGround.enterActions.Add(PlanPathToHighGround);
        seekHighGround.stayActions.Add(TraverseUpdate);

        goalReached.enterActions.Add(OnGoalReached);

        // Transitions
        FSMTransition cannotReachGoal = new(PathToGoalIsUnsafe);
        traverse.AddTransition(cannotReachGoal, seekHighGround);

        FSMTransition canResumeToGoal = new(CanSafelyReachGoal);
        seekHighGround.AddTransition(canResumeToGoal, traverse);

        FSMTransition reachedGoalFromTraverse = new(HasReachedGoal);
        traverse.AddTransition(reachedGoalFromTraverse, goalReached);

        FSMTransition reachedGoalFromHighGround = new(HasReachedGoal);
        seekHighGround.AddTransition(reachedGoalFromHighGround, goalReached);

        fsm = new FSM(traverse);
    }

    void Update()
    {
        if (terrainGraph == null) return;

        // Check if drowned
        if (transform.position.y < waterController.GetCurrentWaterLevel())
        {
            Debug.LogError("Agent drowned!");
            enabled = false;
            return;
        }

        // Update FSM
        fsm.Update();

        // Periodic transition checks
        timeSinceLastCheck += Time.deltaTime;
        if (timeSinceLastCheck >= replanInterval)
        {
            timeSinceLastCheck = 0f;
            // FSM will check transitions automatically
        }
    }

    // ========== FSM ACTIONS ==========

    void TraverseUpdate()
    {
        if (isMoving)
            MoveTowardsTarget();
    }

    void PlanPathToGoal()
    {
        Debug.Log("FSM: Planning path to goal");

        Node currentNode = GetCurrentNode();
        if (currentNode == null) return;

        Node goalNode = terrainGraph.GetGoalNode();

        // Calculate path considering future water levels
        List<Node> path = CalculateTideAwarePath(currentNode, goalNode);

        if (path == null || path.Count == 0)
        {
            Debug.LogWarning("PlanPathToGoal found no path (will transition to HighGround)");
            isMoving = false;
            return;
        }

        currentPath = path;
        currentPathIndex = 0;
        SetNextTarget();
        isMoving = true;

        Debug.Log($"Path to goal: {currentPath.Count} nodes");
    }

    void PlanPathToHighGround()
    {
        Debug.Log("FSM: Seeking high ground");

        Node currentNode = GetCurrentNode();
        if (currentNode == null) return;

        // Find safe high ground
        Node highGroundNode = FindSafeHighGround();

        if (highGroundNode == null)
        {
            Debug.LogError("No safe high ground found!");
            isMoving = false;
            return;
        }

        // Calculate path to high ground
        List<Node> path = CalculateTideAwarePath(currentNode, highGroundNode);

        if (path == null || path.Count == 0)
        {
            Debug.LogError("Cannot reach high ground!");
            isMoving = false;
            return;
        }

        currentPath = path;
        currentPathIndex = 0;
        SetNextTarget();
        isMoving = true;

        Vector3 highGroundPos = terrainGraph.GetNodePosition(highGroundNode);
        Debug.Log($"Path to high ground at height {highGroundPos.y:F1}m: {currentPath.Count} nodes");
    }

    void OnGoalReached()
    {
        isMoving = false;
        reachedGoal = true;
        Debug.Log("SUCCESS: Goal reached!");
    }

    // ========== FSM TRANSITIONS ==========

    bool PathToGoalIsUnsafe()
    {
        // Can we reach the goal with current water predictions?
        Node currentNode = GetCurrentNode();
        if (currentNode == null) return true;

        Node goalNode = terrainGraph.GetGoalNode();
        List<Node> testPath = CalculateTideAwarePath(currentNode, goalNode);

        return (testPath == null || testPath.Count == 0);
    }

    bool CanSafelyReachGoal()
    {
        // Only transition back if water is falling AND we can reach goal
        if (waterController.IsRising())
            return false;

        Node currentNode = GetCurrentNode();
        if (currentNode == null) return false;

        Node goalNode = terrainGraph.GetGoalNode();
        List<Node> testPath = CalculateTideAwarePath(currentNode, goalNode);

        return (testPath != null && testPath.Count > 0);
    }

    bool HasReachedGoal()
    {
        return reachedGoal;
    }

    // ========== PATHFINDING ==========


    // Heuristic for A*
    static float EuclideanHeuristic(Node from, Node to)
    {
        if (from.sceneObject == null || to.sceneObject == null)
        {
            // Nodes don't have sceneObjects, we can't use their positions
            // We'll need to pass positions differently
            return 0f;
        }
        return (from.sceneObject.transform.position - to.sceneObject.transform.position).magnitude;
    }

    List<Node> CalculateTideAwarePath(Node start, Node goal)
    {
        // Mark nodes as unwalkable if they'll be underwater in the near future
        float currentTime = Time.time;
        float checkTime = currentTime + predictionTime;

        // Get predicted water level
        float predictedWater = waterController.GetWaterLevelAtTime(checkTime);

        // Update graph walkability WITHOUT rebuilding structure
        UpdateGraphWalkability(predictedWater);


        // Run A*
        Graph graph = terrainGraph.GetGraph();
        Edge[] pathEdges = AStarSolver.Solve(graph, start, goal, EuclideanHeuristic);

        if (pathEdges.Length == 0)
            return null;

        // Convert to node list
        List<Node> path = new List<Node> { start };
        foreach (Edge edge in pathEdges)
            path.Add(edge.to);

        return path;
    }

    void UpdateGraphWalkability(float waterLevel)
    {
        // Instead of rebuilding graph, just remove edges to/from underwater nodes
        Node[,] matrix = terrainGraph.GetMatrix();
        int gridWidth = matrix.GetLength(0);
        int gridLength = matrix.GetLength(1);

        Graph newGraph = new Graph();

        // Add all nodes
        for (int i = 0; i < gridWidth; ++i)
            for (int j = 0; j < gridLength; ++j)
                newGraph.AddNode(matrix[i, j]);

        // Add edges only between safe nodes
        for (int i = 0; i < gridWidth; ++i)
        {
            for (int j = 0; j < gridLength; ++j)
            {
                Node currentNode = matrix[i, j];
                Vector3 currentPos = terrainGraph.GetNodePosition(currentNode);

                // Skip if underwater
                if (currentPos.y < waterLevel + safetyMargin)
                    continue;

                // Check all 8 neighbors
                Vector2Int[] directions = new Vector2Int[]
                {
                    Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left,
                    Vector2Int.up + Vector2Int.right, Vector2Int.down + Vector2Int.right,
                    Vector2Int.down + Vector2Int.left, Vector2Int.up + Vector2Int.left
                };

                foreach (Vector2Int dir in directions)
                {
                    int ni = i + dir.x;
                    int nj = j + dir.y;

                    if (ni >= 0 && ni < gridWidth && nj >= 0 && nj < gridLength)
                    {
                        Node neighborNode = matrix[ni, nj];
                        Vector3 neighborPos = terrainGraph.GetNodePosition(neighborNode);

                        // Skip if neighbor underwater
                        if (neighborPos.y < waterLevel + safetyMargin)
                            continue;

                        // Calculate edge weight
                        float horizontalDist = Vector2.Distance(
                            new Vector2(currentPos.x, currentPos.z),
                            new Vector2(neighborPos.x, neighborPos.z)
                        );

                        float weight = horizontalDist;
                        if (neighborPos.y > currentPos.y)
                            weight *= 2f; // Uphill penalty

                        newGraph.AddEdge(new Edge(currentNode, neighborNode, weight));
                    }
                }
            }
        }

        terrainGraph.SetGraph(newGraph);
    }

    Node FindSafeHighGround()
    {
        // Find node that will be safe even at maximum water level
        float maxWater = waterController.maxWaterLevel;
        float safeHeight = maxWater + safetyMargin + 1f; // Extra buffer

        Node currentNode = GetCurrentNode();
        Vector3 currentPos = transform.position;

        Node bestNode = null;
        float bestDist = float.MaxValue;

        Node[,] matrix = terrainGraph.GetMatrix();
        int gridWidth = matrix.GetLength(0);
        int gridLength = matrix.GetLength(1);

        for (int i = 0; i < gridWidth; ++i)
        {
            for (int j = 0; j < gridLength; ++j)
            {
                Node node = matrix[i, j];
                Vector3 pos = terrainGraph.GetNodePosition(node);

                // Must be high enough
                if (pos.y < safeHeight)
                    continue;

                // Find closest
                float dist = Vector2.Distance(
                    new Vector2(currentPos.x, currentPos.z),
                    new Vector2(pos.x, pos.z)
                );

                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestNode = node;
                }
            }
        }

        return bestNode;
    }

    Node GetCurrentNode()
    {
        Node node = terrainGraph.GetNodeAtPosition(transform.position.x, transform.position.z);
        if (node == null)
        {
            Debug.LogError("Agent not on valid node!");
        }
        return node;
    }

    // ========== MOVEMENT ==========

    void SetNextTarget()
    {
        if (currentPathIndex >= currentPath.Count)
        {
            reachedGoal = (currentPath[currentPath.Count - 1] == terrainGraph.GetGoalNode());
            return;
        }

        currentTarget = terrainGraph.GetNodePosition(currentPath[currentPathIndex]);
    }

    void MoveTowardsTarget()
    {
        float heightDiff = currentTarget.y - transform.position.y;
        float speed = (heightDiff > 0.01f) ? uphillSpeed : flatSpeed;

        float step = speed * Time.deltaTime;
        transform.position = Vector3.MoveTowards(transform.position, currentTarget, step);

        if (Vector3.Distance(transform.position, currentTarget) < 0.1f)
        {
            ++currentPathIndex;
            SetNextTarget();
        }
    }

    // ========== DEBUG ==========

    void OnDrawGizmos()
    {
        if (!visualizePath || currentPath == null || terrainGraph == null)
            return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < currentPath.Count - 1; ++i)
        {
            Vector3 from = terrainGraph.GetNodePosition(currentPath[i]);
            Vector3 to = terrainGraph.GetNodePosition(currentPath[i + 1]);
            Gizmos.DrawLine(from, to);
        }

        Gizmos.color = Color.magenta;
        Gizmos.DrawSphere(currentTarget, 0.5f);
    }
}