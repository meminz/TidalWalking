using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class AgentController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float flatSpeed = 1f;
    public float uphillSpeed = 0.5f;
    public float safetyMargin = 0.1f; // Height above predicted water

    [Header("Pathfinding")]
    public float replanInterval = 1f;
    public int checkNodesAhead = 8;

    [Header("Debug")]
    public bool visualizePath = true;

    private TerrainGraphManager terrainGraphManager;
    private TerrainGraph terrainGraph;
    private WaterController waterController;
    private Collider _collider;

    private List<Node> currentPath;
    private int currentPathIndex = 0;
    private float timeSinceLastCheck = 0f;

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
        _collider = GetComponent<Collider>();

        terrainGraph = terrainGraphManager.GetGraph();

        if (terrainGraph == null)
        {
            Debug.LogError("TerrainGraph not found!");
            return;
        }

        // Position agent at start node
        Node startNode = terrainGraph.GetStartNode();
        Vector3 startPos = terrainGraph.GetNodePosition(startNode);
        startPos.y += _collider.bounds.size.y * 0.5f;
        transform.position = startPos;

        safeHeight = waterController.maxWaterLevel + 0.1f;
        // pathfindingHeuristic = Heuristic;

        // FSM Setup
        FSMState traverse = new();
        traverse.enterActions.Add(PlanPathToGoal);
        traverse.stayActions.Add(TraverseUpdate);

        FSMState seekHighGround = new();
        seekHighGround.enterActions.Add(PlanPathToHighGround);
        seekHighGround.stayActions.Add(TraverseUpdate);

        FSMState stayHigh = new FSMState();
        // stayHigh.enterActions.Add(StartWandering);
        stayHigh.stayActions.Add(WanderUpdate);

        FSMState goalReached = new();
        goalReached.enterActions.Add(OnGoalReached);

        // Transitions
        FSMTransition cannotReachGoal = new(IsTideThreatening);
        traverse.AddTransition(cannotReachGoal, seekHighGround);

        FSMTransition canResumeToGoal = new(CanSafelyReachGoal);
        seekHighGround.AddTransition(canResumeToGoal, traverse);

        FSMTransition reachedGoalFromTraverse = new(HasReachedGoal);
        traverse.AddTransition(reachedGoalFromTraverse, goalReached);

        FSMTransition reachedGoalFromHighGround = new(HasReachedGoal);
        seekHighGround.AddTransition(reachedGoalFromHighGround, goalReached);

        FSMTransition noProgressPossible = new FSMTransition(CannotMakeProgress);
        seekHighGround.AddTransition(noProgressPossible, stayHigh);

        FSMTransition canResumeFromWander = new FSMTransition(CanSafelyReachGoal);
        stayHigh.AddTransition(canResumeFromWander, traverse);

        FSMTransition reachedGoalFromWander = new FSMTransition(HasReachedGoal);
        stayHigh.AddTransition(reachedGoalFromWander, goalReached);


        fsm = new FSM(traverse);
    }




    private Vector3 wanderTarget;
    // private float wanderTimer = 0f;
    // private float wanderInterval = 3f; // Pick new target every 3 seconds

    // void StartWandering()
    // {
    //     Debug.Log("FSM: Starting to stayHigh on high ground");
    //     PickRandomWanderTarget();
    // }

    // void WanderUpdate()
    // {
    //     if (isMoving)
    //         MoveTowardsTarget();

    //     wanderTimer += Time.deltaTime;
    //     if (wanderTimer >= wanderInterval || Vector3.Distance(transform.position, currentTarget) < 0.2f)
    //     {
    //         wanderTimer = 0f;
    //         PickRandomWanderTarget();
    //     }
    // }

    void WanderUpdate()
    {
        if (isMoving)
            MoveTowardsTarget();

        // When path ends, pick next action
        if (!isMoving || (currentPath != null && currentPathIndex >= currentPath.Count))
        {
            // Try to make progress toward goal
            Node currentNode = GetCurrentNode();
            Node goalNode = terrainGraph.GetGoalNode();

            List<Node> path = FindPath(currentNode, goalNode);

            if (path != null && path.Count > 3)
            {
                // Truncate at first unsafe
                List<Node> safePortion = new List<Node>();
                foreach (Node node in path)
                {
                    Vector3 pos = terrainGraph.GetNodePosition(node);
                    if (pos.y > safeHeight)
                        safePortion.Add(node);
                    else
                        break;
                }

                if (safePortion.Count > 3) // Can make progress
                {
                    currentPath = safePortion;
                    currentPathIndex = 0;
                    SetNextTarget();
                    isMoving = true;
                    return;
                }
            }

            // Can't make progress - stayHigh locally
            PickRandomWanderTarget();
        }
    }


    void PickRandomWanderTarget()
    {
        Node currentNode = GetCurrentNode();
        if (currentNode == null) return;

        // Find safe adjacent nodes
        Edge[] edges = terrainGraph.GetGraph().getConnections(currentNode);
        List<Node> safeNeighbors = new();

        foreach (Edge edge in edges)
        {
            Vector3 neighborPos = terrainGraph.GetNodePosition(edge.to);
            if (neighborPos.y > safeHeight)
                safeNeighbors.Add(edge.to);
        }

        if (safeNeighbors.Count > 0)
        {
            Node randomNeighbor = safeNeighbors[UnityEngine.Random.Range(0, safeNeighbors.Count)];
            currentPath = new List<Node>{currentNode, randomNeighbor};
            currentPathIndex = 0;
            isMoving = true;
            // Debug.Log($"Wandering to {currentTarget}");
        }
    }

    bool CannotMakeProgress()
    {
        // Check if we've reached our high ground destination and can't progress
        if (currentPath == null || (currentPath != null && currentPathIndex >= currentPath.Count))
            // We've finished our path to high ground
            // Check if we can make ANY progress toward goal
            return !CanSafelyReachGoal();

        return false;
    }












    void Update()
    {
        if (terrainGraph == null) return;

        // Check if drowned
        if (transform.position.y - _collider.bounds.size.y * 0.5f < waterController.GetCurrentWaterLevel())
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
            currentPath = null;
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

        int nodesToCheck = Mathf.Min(checkNodesAhead, currentPath.Count - currentPathIndex);

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
        int nodesToCheck = Mathf.Min(checkNodesAhead, testPath.Count);

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
        currentPathIndex = 0;
        return true;
    }

    bool HasReachedGoal()
    {
        return reachedGoal;
    }

    // ========== PATHFINDING ==========
    float Heuristic(Node start, Node goal) 
    {
        Vector3 fromPos = terrainGraph.GetNodePosition(start);
        Vector3 toPos = terrainGraph.GetNodePosition(goal);

        float distance = Vector3.Distance(fromPos, toPos);
        float heightBonus = toPos.y * 0.9f; // Prefer higher ground

        // float safeBonus = 0f;
        // if (toPos.y < safeHeight)
        // {
        //     safeBonus = (toPos.y - safeHeight) * 4f;
        //     heightBonus /= 2;
        // }

        return distance - heightBonus;
    }

    List<Node> FindPath(Node start, Node goal)
    {
        Debug.Log("Calculating path.");
        Graph graph = terrainGraph.GetGraph();
        Edge[] pathEdges = AStarSolver.Solve(graph, start, goal, Heuristic);

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
        if (isMoving && currentPath != null && currentPathIndex < currentPath.Count)
            return currentPath[currentPathIndex];

        Node node = terrainGraph.GetNodeAtPosition(transform.position.x, transform.position.z);
        if (node == null)
            Debug.LogError("Agent not on valid node!");

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

        Vector3 adjTarget = new(currentTarget.x, transform.position.y, currentTarget.z);
        // Vector3 adjTarget = new(currentTarget.x, currentTarget.y + _collider.bounds.size.y * 0.5f, currentTarget.z);
        Vector3 direction = (adjTarget - transform.position).normalized;

        float heightDiff = adjTarget.y - transform.position.y;
        float speed = (heightDiff > 0.01f) ? uphillSpeed : flatSpeed;

        float step = speed * Time.deltaTime;
        transform.position = Vector3.MoveTowards(transform.position, adjTarget, step);
        // transform.position = Vector3.MoveTowards(transform.position, transform.position + direction, step);

        if (Vector3.Distance(
                    new Vector2(transform.position.x, transform.position.z),
                    new Vector2(currentTarget.x, currentTarget.z)
                    ) < 0.1f)
        {
            ++currentPathIndex;
            SetNextTarget();
            return;
        }

        if (direction.sqrMagnitude > 0.1f)
        {
            Quaternion targetRotation = Quaternion.LookRotation(direction);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * 5f);
        }

        Terrain terrain = terrainGraphManager.GetComponent<Terrain>();
        float targetY = terrain.SampleHeight(transform.position) + _collider.bounds.size.y * 0.5f;
        Vector3 pos = transform.position;
        pos.y = targetY;
        transform.position = pos;

    }

    Collider GetCollider()
    {
        return _collider;
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
