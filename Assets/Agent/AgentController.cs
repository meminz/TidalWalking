using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class AgentController : MonoBehaviour
{
    [Header("References")]
    public TerrainGraphManager terrainGraphManager;
    public WaterController waterController;
    public new Collider collider;

    [Header("Movement Settings")]
    public float flatSpeed = 1f;
    public float uphillSpeed = 0.5f;
    public float safetyMargin = 0.1f; // Height above predicted water

    [Header("Pathfinding")]
    public float replanInterval = 1f;
    public float predictionTime = 8f;

    [Header("Debug")]
    public bool visualizePath = true;

    private TerrainGraph terrainGraph;
    private List<Node> currentPath;
    private int currentPathIndex = 0;
    private float timeSinceLastCheck = 0f;
    private HeuristicFunction pathfindingHeuristic;

    private Vector3 currentTarget;
    private bool isMoving = false;
    private bool reachedGoal = false;
    private float safeHeight;

    private FSM fsm;

    void Start()
    {
        if (terrainGraphManager == null)
            terrainGraphManager = FindFirstObjectByType<TerrainGraphManager>();
        if (waterController == null)
            waterController = FindFirstObjectByType<WaterController>();
        if (collider == null)
            collider = FindFirstObjectByType<Collider>();

        terrainGraph = terrainGraphManager.GetGraph();

        if (terrainGraph == null)
        {
            Debug.LogError("TerrainGraph not found!");
            return;
        }

        // Position agent at start node
        Node startNode = terrainGraph.GetStartNode();
        Vector3 startPos = terrainGraph.GetNodePosition(startNode);
        startPos.y += collider.bounds.size.y / 2;
        transform.position = startPos;

        safeHeight = waterController.maxWaterLevel + 0.1f;

        pathfindingHeuristic = (Node start, Node goal) =>
        {
            Vector3 fromPos = terrainGraph.GetNodePosition(start);
            Vector3 toPos = terrainGraph.GetNodePosition(goal);

            float distance = Vector3.Distance(fromPos, toPos);
            float heightBonus = toPos.y * 0.9f; // Prefer higher ground

            // float safeBonus = 0f;
            // if (toPos.y < safeHeight)
            //     safeBonus = (toPos.y - safeHeight) * 3f;

            return distance - heightBonus;
        };


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
        // FSMTransition cannotReachGoal = new(PathToGoalIsUnsafe);
        FSMTransition cannotReachGoal = new(IsTideThreatening);
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

        fsm.Update();

        // Periodic transition checks
        timeSinceLastCheck += Time.deltaTime;
        if (timeSinceLastCheck >= replanInterval)
        {
            timeSinceLastCheck = 0f;
        }

    }

    // ========== FSM ACTIONS ==========

    void TraverseUpdate()
    {
        // Debug.Log($"TraverseUpdate: isMoving={isMoving}");
        if (isMoving)
            MoveTowardsTarget();
    }

    void PlanPathToGoal()
    {
        Debug.Log("FSM: Planning path to goal");
        if (currentPath != null && currentPath[currentPath.Count - 1] == terrainGraph.GetGoalNode())
        {
            Debug.Log("path to goal already set when exiting HighGround state");
            SetNextTarget();
            isMoving = true;
            return;
        }

        Node currentNode = GetCurrentNode();
        if (currentNode == null)
        {
            Debug.Log("FSM: cannot get current node!");
            return;
        }

        Node goalNode = terrainGraph.GetGoalNode();

        // List<Node> path = CalculateTideAwarePath(currentNode, goalNode);
        List<Node> path = FindPath(currentNode, goalNode);

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
        
        if (terrainGraph.GetNodePosition(currentNode).y > safeHeight)
        {
            Debug.Log($"Already on high ground");
            isMoving = false;
            return;
        }

        // Find safe high ground
        Node highGroundNode = FindSafeHighGround();

        if (highGroundNode == null)
        {
            Debug.LogError("No safe high ground found!");
            isMoving = false;
            return;
        }

        // Calculate path to high ground
        // List<Node> path = CalculateTideAwarePath(currentNode, highGroundNode);
        List<Node> path = FindPath(currentNode, highGroundNode);

        if (path == null || path.Count == 0)
        {
            Debug.LogError($"Cannot reach high ground!");
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
    bool IsTideThreatening()
    {
        if (!waterController.IsRising() || currentPath == null || currentPath.Count == 0)
            return false;

        float currentTime = Time.time;
        float travelTime = 0f;

        int nodesToCheck = Mathf.Min(5, currentPath.Count - currentPathIndex);

        // Check upcoming nodes with actual arrival time prediction
        for (int i = currentPathIndex; i < Mathf.Min(currentPathIndex + nodesToCheck, currentPath.Count); ++i)
        {
            Vector3 nodePos = terrainGraph.GetNodePosition(currentPath[i]);

            // Calculate when we'll arrive at this node
            if (i > currentPathIndex)
            {
                Vector3 prevPos = terrainGraph.GetNodePosition(currentPath[i - 1]);
                float distance = Vector3.Distance(prevPos, nodePos);
                float heightDiff = nodePos.y - prevPos.y;
                float speed = (heightDiff > 0) ? uphillSpeed : flatSpeed;
                travelTime += distance / speed;
            }

            // Predict water at arrival time
            float arrivalTime = currentTime + travelTime;
            float predictedWater = waterController.GetWaterLevelAtTime(arrivalTime);

            if (nodePos.y < predictedWater + safetyMargin)
            {
                Debug.Log($"Node {i} will be unsafe when we arrive (water: {predictedWater:F1}m, node: {nodePos.y:F1}m)");
                return true;
            }
        }

        return false;
    }

    bool CanSafelyReachGoal()
    {
        if (waterController.IsRising())
            return false;

        float currentWater = waterController.GetCurrentWaterLevel();
        if (transform.position.y < currentWater + safetyMargin)
            return false;

        Node currentNode = GetCurrentNode();
        Node goalNode = terrainGraph.GetGoalNode();

        if (currentNode == null || goalNode == null)
            return false;

        // Get a path to goal
        List<Node> testPath = FindPath(currentNode, goalNode);
        // List<Node> testPath = currentPath;

        if (testPath == null || testPath.Count == 0)
            return false;

        // Now check if this path is safe with arrival time prediction
        float currentTime = Time.time;
        float travelTime = 0f;
        int nodesToCheck = Mathf.Min(5, currentPath.Count - currentPathIndex);

        for (int i = 0; i < Mathf.Min(nodesToCheck, testPath.Count); ++i)
        {
            Vector3 nodePos = terrainGraph.GetNodePosition(testPath[i]);

            if (i > 0)
            {
                Vector3 prevPos = terrainGraph.GetNodePosition(testPath[i - 1]);
                float distance = Vector3.Distance(prevPos, nodePos);
                float heightDiff = nodePos.y - prevPos.y;
                float speed = (heightDiff > 0) ? uphillSpeed : flatSpeed;
                travelTime += distance / speed;
            }

            float arrivalTime = currentTime + travelTime;
            float predictedWater = waterController.GetWaterLevelAtTime(arrivalTime);

            if (nodePos.y < predictedWater + safetyMargin)
                // Path will become unsafe
                return false;
        }

        // Path is safe!
        currentPath = testPath;
        return true;
    }

    bool HasReachedGoal()
    {
        return reachedGoal;
    }

    // ========== PATHFINDING ==========
    // float pathfindingHeuristic(Node start, Node goal) 
    // {
    //     Vector3 fromPos = terrainGraph.GetNodePosition(start);
    //     Vector3 toPos = terrainGraph.GetNodePosition(goal);

    //     float distance = Vector3.Distance(fromPos, toPos);
    //     float heightBonus = toPos.y * 0.9f; // Prefer higher ground

    //     // float safeBonus = 0f;
    //     // if (toPos.y < safeHeight)
    //     //     safeBonus = (toPos.y - safeHeight) * 3f;

    //     return distance - heightBonus;
    // }

    List<Node> FindPath(Node start, Node goal)
    {
        Debug.Log("Calculating path.");
        Graph graph = terrainGraph.GetGraph();
        Edge[] pathEdges = AStarSolver.Solve(graph, start, goal, pathfindingHeuristic);

        if (pathEdges.Length == 0)
            return null;

        // Convert to node list
        List<Node> path = new() { start };
        foreach (Edge edge in pathEdges)
            path.Add(edge.to);

        return path;
    }

    Node FindSafeHighGround()
    {
        Vector3 currentPos = transform.position;
        Node currentNode = GetCurrentNode();

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
        if (currentPath == null || currentPath.Count == 0)
        {
            isMoving = false;
            Debug.LogWarning("SetNextTarget called with empty path.");
            return;
        }

        if (currentPathIndex >= currentPath.Count)
        {
            reachedGoal = currentPath[currentPath.Count - 1] == terrainGraph.GetGoalNode();
            isMoving = false;
            return;
        }

        currentTarget = terrainGraph.GetNodePosition(currentPath[currentPathIndex]);
    }

    void MoveTowardsTarget()
    {
        // Debug.Log($"Moving towards target {currentPathIndex}/{currentPath.Count}");

        Vector3 direction = currentTarget - transform.position;
        float heightDiff = currentTarget.y - transform.position.y;
        float speed = (heightDiff > 0.01f) ? uphillSpeed : flatSpeed;

        float step = speed * Time.deltaTime;
        transform.position = Vector3.MoveTowards(transform.position, currentTarget, step);

        // Terrain terrain = terrainGraphManager.GetComponent<Terrain>();
        // float terrainHeight = terrain.SampleHeight(transform.position);
        // float minHeight = terrain.transform.position.y + terrainHeight + collider.bounds.size.y /2 ;
        // if (transform.position.y < minHeight)
        // {
        //     // Vector3 pos = transform.position;
        //     // pos.y = minHeight;
        //     transform.position.y = minHeight;
        // }

        if (direction.sqrMagnitude > 0.01f) // Only rotate if moving
        {
            Quaternion targetRotation = Quaternion.LookRotation(direction);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * 5f);
        }

        if (Vector3.Distance(transform.position, currentTarget) < 0.1f)
        {
            ++currentPathIndex;
            SetNextTarget();
        }
    }

    // debug
    void OnDrawGizmos()
    {
        if (!visualizePath || currentPath==null || terrainGraph==null)
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
