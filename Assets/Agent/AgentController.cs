using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class AgentController : MonoBehaviour
{
    [Header("Movement Settings")]
    public float flatSpeed = 1f;
    public float uphillSpeed = 0.5f;
    public float safetyMargin = 0.2f; // Height above predicted water

    [Header("Pathfinding")]
    public float checkInterval = 2f;
    public float wanderInterval = 2f;
    public int checkNodesAhead = 8;

    [Header("Debug")]
    public bool visualizePath = true;

    private TerrainGraphManager terrainGraphManager;
    private TerrainGraph terrainGraph;
    private WaterController waterController;
    private float halfHeight;

    private List<Node> currentPath;
    private int currentPathIndex = 0;
    private int furthestSafeNodeIndex = -1;

    private Vector3 currentTarget;
    private bool isMoving = false;
    private bool reachedGoal = false;
    private float safeHeight;

    private Vector3 wanderTarget;
    private float checkTimer = 0f;
    private float wanderTimer = 0f;

    private FSM fsm;

    void Start()
    {
        if (terrainGraphManager == null)
            terrainGraphManager = FindFirstObjectByType<TerrainGraphManager>();
        if (waterController == null)
            waterController = FindFirstObjectByType<WaterController>();
        Collider _collider = GetComponent<Collider>();
        halfHeight = _collider.bounds.size.y * 0.5f;

        terrainGraph = terrainGraphManager.GetGraph();

        if (terrainGraph == null)
        {
            Debug.LogError("TerrainGraph not found!");
            return;
        }

        // position agent at start node
        Node startNode = terrainGraph.GetStartNode();
        Vector3 startPos = terrainGraph.GetNodePosition(startNode);
        startPos.y += halfHeight;
        transform.position = startPos;

        safeHeight = waterController.maxWaterLevel + safetyMargin;

        // FSM Setup
        FSMState traverse = new();
        traverse.enterActions.Add(PlanPathToGoal);
        traverse.stayActions.Add(TraverseUpdate);

        FSMState seekHighGround = new();
        seekHighGround.enterActions.Add(PlanPathToHighGround);
        seekHighGround.stayActions.Add(TraverseUpdate);

        FSMState wander = new FSMState();
        wander.enterActions.Add(StartWandering);
        wander.stayActions.Add(WanderUpdate);

        FSMState goalReached = new();
        goalReached.enterActions.Add(OnGoalReached);

        // transitions
        FSMTransition cannotReachGoal = new(IsTideThreatening);
        traverse.AddTransition(cannotReachGoal, seekHighGround);

        FSMTransition reachedGoalFromTraverse = new(HasReachedGoal);
        traverse.AddTransition(reachedGoalFromTraverse, goalReached);

        FSMTransition canResumeToGoal = new(CanSafelyReachGoal);
        seekHighGround.AddTransition(canResumeToGoal, traverse);

        // shouldn't be necessary
        FSMTransition reachedGoalFromHighGround = new(HasReachedGoal);
        seekHighGround.AddTransition(reachedGoalFromHighGround, goalReached);

        FSMTransition noProgressPossible = new FSMTransition(CannotMakeProgress);
        seekHighGround.AddTransition(noProgressPossible, wander);

        FSMTransition canResumeFromWander = new FSMTransition(CanSafelyReachGoal);
        wander.AddTransition(canResumeFromWander, traverse);

        // shouldn't be necessary
        FSMTransition reachedGoalFromWander = new FSMTransition(HasReachedGoal);
        wander.AddTransition(reachedGoalFromWander, goalReached);


        fsm = new FSM(traverse);
    }



    void Update()
    {
        if (terrainGraph == null) return;

        // check if drowned
        if (transform.position.y - halfHeight < waterController.GetCurrentWaterLevel())
        {
            Debug.LogError("Agent drowned!");
            enabled = false;
            return;
        }

        fsm.Update();
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
        Debug.Log("Traverse to calculate new path");
        List<Node> path = FindPath(currentNode, goalNode);

        if (path == null || path.Count == 0)
        {
            Debug.LogWarning("PlanPathToGoal found no path (will transition to HighGround)");
            isMoving = false;
            return;
        }

        currentPath = path;
        if (NewPathRequiresGoingBack(path))
            currentPathIndex = 0;
        else
            currentPathIndex = FindNewPathClosestIndex(path);
        SetNextTarget();
        isMoving = true;

        Debug.Log($"Path to goal: {currentPath.Count} nodes");
    }

    void PlanPathToHighGround()
    {
        Debug.Log("FSM: Seeking high ground");
        checkTimer = 0f;

        Node currentNode = GetCurrentNode();
        if (currentNode == null)
        {
            Debug.LogError("Not a valid current node.");
            return;
        }

        if (currentPath != null && furthestSafeNodeIndex > currentPathIndex)
        {
            Debug.Log($"Truncating current path: current={currentPathIndex}, furthest safe={furthestSafeNodeIndex}");
            List<Node> safePath = currentPath.GetRange(currentPathIndex, furthestSafeNodeIndex - currentPathIndex + 1);

            currentPath = safePath;
            currentPathIndex = 0;
            furthestSafeNodeIndex = -1;
            SetNextTarget();
            isMoving = true;
            return;
        }
        
        if (terrainGraph.GetNodePosition(currentNode).y > safeHeight)
        {
            Debug.Log($"Already on high ground");
            currentPath = null;
            isMoving = false;
            return;
        }

        // find safe high ground
        Node highGroundNode = FindSafeHighGround();

        if (highGroundNode == null)
        {
            Debug.LogError("No safe high ground found!");
            isMoving = false;
            return;
        }

        // calculate path to high ground
        Debug.Log("High ground to calculate new path");
        List<Node> path = FindPath(currentNode, highGroundNode);

        if (path == null)
        {
            Debug.LogError($"Cannot reach high ground!");
            isMoving = false;
            return;
        }

        currentPath = path;
        if (NewPathRequiresGoingBack(path))
            currentPathIndex = 0;
        else
            currentPathIndex = FindNewPathClosestIndex(path);
        SetNextTarget();
        isMoving = true;

        Vector3 highGroundPos = terrainGraph.GetNodePosition(highGroundNode);
        Debug.Log($"Path to high ground at height {highGroundPos.y:F1}m: {currentPath.Count} nodes");
    }

    void StartWandering()
    {
        Debug.Log("FSM: Starting to wander on high ground");
        wanderTimer = 0f;
    }

    void WanderUpdate()
    {
        if (isMoving)
            MoveTowardsTarget();

        wanderTimer += Time.deltaTime;
        if (wanderTimer >= wanderInterval)
        {
            wanderTimer = 0f;
            // try to make progress toward goal
            Node currentNode = GetCurrentNode();
            Node goalNode = terrainGraph.GetGoalNode();

            Debug.Log("Wander to calculate new path");
            List<Node> path = FindPath(currentNode, goalNode);

            if (path != null && path.Count > 1)
            {
                // truncate at first unsafe
                List<Node> safePortion = new List<Node>();
                foreach (Node node in path)
                {
                    Vector3 pos = terrainGraph.GetNodePosition(node);
                    if (pos.y > safeHeight)
                        safePortion.Add(node);
                    else
                        break;
                }

                if (safePortion.Count > 1) // Can make progress
                {
                    currentPath = safePortion;
                    if (NewPathRequiresGoingBack(path))
                        currentPathIndex = 0;
                    else
                        currentPathIndex = FindNewPathClosestIndex(path);
                    SetNextTarget();
                    isMoving = true;
                    return;
                }
            }
        }

        // when path ends pick next action
        if (!isMoving || (currentPath != null && currentPathIndex >= currentPath.Count))
            PickRandomWanderTarget();
    }

    void PickRandomWanderTarget()
    {
        Node currentNode = GetCurrentNode();
        if (currentNode == null) return;

        // find safe adjacent nodes
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

    void OnGoalReached()
    {
        isMoving = false;
        reachedGoal = true;
        Debug.Log("SUCCESS: Goal reached!");
    }

    // ========== FSM TRANSITIONS ==========
    bool IsTideThreatening()
    {
        // if (!waterController.IsRising() || currentPath == null || currentPath.Count == 0)
        if (currentPath == null || currentPath.Count == 0)
            return false;

        float currentTime = Time.time;
        float travelTime = 0f;
        Terrain terrain = terrainGraphManager.GetComponent<Terrain>();

        furthestSafeNodeIndex = -1;
        int nodesToCheck = Mathf.Min(checkNodesAhead, currentPath.Count - currentPathIndex);

        // check upcoming nodes with actual arrival time prediction
        for (int i = currentPathIndex + 1; i < Mathf.Min(currentPathIndex + nodesToCheck, currentPath.Count); ++i)
        {
            Vector3 nodePos = terrainGraph.GetNodePosition(currentPath[i]);


            // calculate when we'll arrive at this node
            Vector3 prevPos = terrainGraph.GetNodePosition(currentPath[i - 1]);
            float distance = Vector3.Distance(prevPos, nodePos);
            float heightDiff = nodePos.y - prevPos.y;
            float speed = (heightDiff > 0.01f) ? uphillSpeed : flatSpeed;
            float edgeTravelTime = distance / speed;

            // sample points along the edge
            int samples = Mathf.Max(5, Mathf.CeilToInt(distance / 4f));
            
            float predictedWater;

            // check if sampled points in edge will become unsafe
            for (int s = 0; s < samples; s++)
            {
                float t = s / (float)samples;
                // we should get the actual terrain point
                Vector3 pointOnEdge = Vector3.Lerp(prevPos, nodePos, t);
                float terrainHeight = terrain.SampleHeight(pointOnEdge);
                float timeAtPoint = travelTime + (edgeTravelTime * t);
                float splitTime = currentTime + timeAtPoint;
                predictedWater = waterController.GetWaterLevelAtTime(splitTime);

                if (terrainHeight < predictedWater + 0.01f)
                {
                    Debug.Log($"Edge to node {i} will be unsafe at t={t:F2} (water: {predictedWater:F1}m, point: {terrainHeight:F1}m)");
                    return true;
                }
            }

            travelTime += edgeTravelTime;

            // predict water at arrival time
            float arrivalTime = currentTime + travelTime;
            predictedWater = waterController.GetWaterLevelAtTime(arrivalTime);

            if (nodePos.y < predictedWater + safetyMargin)
            {
                Debug.Log($"Node {i} will be unsafe when we arrive (water: {predictedWater:F1}m, node: {nodePos.y:F1}m)");
                return true;
            }

            if (nodePos.y > safeHeight)
                furthestSafeNodeIndex = i;
        }

        furthestSafeNodeIndex = -1; // all nodes are safe so we don't care
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


        checkTimer += Time.deltaTime;
        if (checkTimer < checkInterval)
            return false;

        checkTimer = 0f;
        Debug.Log("Testing if we can reach goal");
        List<Node> testPath = FindPath(currentNode, goalNode);

        if (testPath == null || testPath.Count == 0)
        {
            Debug.LogWarning("Test path is null or has 0 length");
            return false;
        }

        // check if this path is safe with arrival time prediction
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
                float speed = (heightDiff > 0.01f) ? uphillSpeed : flatSpeed;
                travelTime += distance / speed;
            }

            float arrivalTime = currentTime + travelTime;
            float predictedWater = waterController.GetWaterLevelAtTime(arrivalTime);

            if (nodePos.y < predictedWater + safetyMargin)
                // path will become unsafe
                return false;
        }

        // path is safe
        currentPath = testPath;
        if (NewPathRequiresGoingBack(testPath))
            currentPathIndex = 0;
        else
            currentPathIndex = FindNewPathClosestIndex(testPath);
        return true;
    }

    bool HasReachedGoal()
    {
        return reachedGoal;
    }

    bool CannotMakeProgress()
    {
        // reached high ground destination but can't progress
        if (currentPath == null || (currentPath != null && currentPathIndex >= currentPath.Count))
            // check if we can make progress toward goal
            return !CanSafelyReachGoal();

        return false;
    }

    // ========== PATHFINDING ==========
    float Heuristic(Node start, Node goal) 
    {
        Vector3 fromPos = terrainGraph.GetNodePosition(start);
        Vector3 toPos = terrainGraph.GetNodePosition(goal);

        float distance = Vector3.Distance(fromPos, toPos);
        float heightBonus = toPos.y * 0.9f; // Prefer higher ground

        return distance - heightBonus;
    }

    List<Node> FindPath(Node start, Node goal)
    {
        // Debug.Log("Calculating path.");
        Graph graph = terrainGraph.GetGraph();
        Edge[] pathEdges = AStarSolver.Solve(graph, start, goal, Heuristic);

        if (pathEdges.Length == 0)
            return null;

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

                // here we don't care about safety margin, just reach the min safe height
                if (pos.y <= waterController.maxWaterLevel + 0.01f)
                    continue;

                // find closest
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
            Debug.LogWarning("SetNextTarget called with empty path.");
            isMoving = false;
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

        // Vector3 adjTarget = new(currentTarget.x, transform.position.y, currentTarget.z);
        Vector3 adjTarget = new(currentTarget.x, currentTarget.y + halfHeight, currentTarget.z);
        // Vector3 adjTarget = currentTarget;
        Vector3 direction = (adjTarget - transform.position).normalized;

        float heightDiff = adjTarget.y - transform.position.y;
        float speed = (heightDiff > 0.01f) ? uphillSpeed : flatSpeed;

        if (direction.magnitude > 0.1f)
        {
            Terrain terrain = terrainGraphManager.GetComponent<Terrain>();
            Orientate(terrain, direction);
            StayAboveGround(terrain);

            // Vector3 surfaceNormal = Orientate(terrain, direction);
            // Vector3 nextPos = transform.position + direction * step;
            // float terrainHeight = terrain.SampleHeight(transform.position) + halfHeight;
            // nextPos = new Vector3(nextPos.x, terrainHeight, nextPos.z);
            // Vector3 aboveSurfacePos = nextPos + surfaceNormal * halfHeight;
            // transform.position = aboveSurfacePos;
        }


        float step = speed * Time.deltaTime;
        transform.position = Vector3.MoveTowards(transform.position, transform.position + direction, step);

        if (Vector3.Distance(
                    new Vector2(transform.position.x, transform.position.z),
                    new Vector2(currentTarget.x, currentTarget.z)
                    ) < 0.2f)
        {
            ++currentPathIndex;
            SetNextTarget();
        }


    }

    void StayAboveGround(Terrain terrain)
    {
        float targetY = terrain.SampleHeight(transform.position) + halfHeight;
        Vector3 pos = transform.position;
        pos.y = targetY;
        transform.position = pos;
    }

    Vector3 Orientate(Terrain terrain, Vector3 direction)
    {
        TerrainData terrainData = terrain.terrainData;

        // Convert world position to normalized terrain coordinates
        Vector3 terrainPosition = transform.position - terrain.transform.position;
        float normalizedX = Mathf.InverseLerp(0, terrainData.size.x, terrainPosition.x);
        float normalizedZ = Mathf.InverseLerp(0, terrainData.size.z, terrainPosition.z);

        // Ensure coordinates are within the valid range of [0, 1]
        normalizedX = Mathf.Clamp01(normalizedX);
        normalizedZ = Mathf.Clamp01(normalizedZ);

        // Get the interpolated normal
        Vector3 interpolatedNormal = terrainData.GetInterpolatedNormal(normalizedX, normalizedZ);
        Vector3 right = Vector3.Cross(direction, interpolatedNormal).normalized;
        Vector3 forward = Vector3.Cross(interpolatedNormal, right).normalized;

        Quaternion targetRot = Quaternion.LookRotation(forward, interpolatedNormal);

        transform.rotation = Quaternion.Slerp(
                transform.rotation,
                targetRot,
                Time.deltaTime * 4f
                );
        return interpolatedNormal;
    }

    int FindNewPathClosestIndex(List<Node> newPath)
    {
        float bestDist = float.MaxValue;
        int bestIndex = 0;

        Vector3 agentXZ = new Vector3(transform.position.x, 0, transform.position.z);

        for (int i = 0; i < Mathf.Min(4,newPath.Count); i++)
        {
            Vector3 nodePos = terrainGraph.GetNodePosition(newPath[i]);
            Vector3 nodeXZ = new Vector3(nodePos.x, 0, nodePos.z);

            float d = Vector3.Distance(agentXZ, nodeXZ);
            if (d < bestDist)
            {
                bestDist = d;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    bool NewPathRequiresGoingBack(List<Node> newPath)
    {
        if (currentPath == null || newPath == null || newPath.Count < 2)
            return false;

        // current forward direction along old path
        Vector3 oldForward = Vector3.zero;
        if (currentPathIndex < currentPath.Count - 1)
        {
            Vector3 a = terrainGraph.GetNodePosition(currentPath[currentPathIndex]);
            Vector3 b = terrainGraph.GetNodePosition(currentPath[currentPathIndex + 1]);
            oldForward = (b - a).normalized;
        }
        else
        {
            oldForward = transform.forward;
        }

        // new path’s direction
        Vector3 c = terrainGraph.GetNodePosition(newPath[0]);
        Vector3 d = terrainGraph.GetNodePosition(newPath[1]);
        Vector3 newForward = (d - c).normalized;

        // dot < 0 requires going back
        return Vector3.Dot(oldForward, newForward) < 0f;
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
