using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

public class oldAgentController : MonoBehaviour
{
    [Header("References")]
    public TerrainGraphManager terrainGraphManager;
    public WaterController waterController;

    [Header("Movement Settings")]
    public float flatSpeed = 1f; // meters per second (flat or downhill)
    public float uphillSpeed = 0.5f; // meters per second (uphill)
    public float replanInterval = 0.5f; // seconds between path recalculations

    [Header("Debug")]
    public bool visualizePath = true;

    private TerrainGraph terrainGraph;
    private List<Node> currentPath;
    private int currentPathIndex = 0;
    // private float timeSinceLastReplan = 0f;

    private Vector3 currentTarget;
    private bool isMoving = false;
    private bool reachedGoal = false;

    private FSM fsm;

    void Start()
    {
        // Auto-find references if not assigned
        if (terrainGraphManager == null)
        {
            terrainGraphManager = FindFirstObjectByType<TerrainGraphManager>();
        }
        if (waterController == null)
        {
            waterController = FindFirstObjectByType<WaterController>();
        }

        terrainGraph = terrainGraphManager.GetGraph();

        if (terrainGraph == null)
        {
            Debug.LogError("TerrainGraph not found! Make sure TerrainGraphVisualizer has built the graph.");
            return;
        }

        // Position agent at start node
        Node startNode = terrainGraph.GetStartNode();
        Vector3 startPos = terrainGraph.GetNodePosition(startNode);
        transform.position = startPos;
        
        // FSM
        FSMState traverse = new();
        FSMState highGround = new();
        FSMState reachedGoal = new();

        traverse.enterActions.Add(CalculatePath);
        traverse.stayActions.Add(TraverseUpdate);

        highGround.enterActions.Add(SeekHighGround);
        highGround.stayActions.Add(TraverseUpdate);

        reachedGoal.enterActions.Add(GoalReached);


        FSMTransition unsafeTransition = new(PathIsUnsafe);
        traverse.AddTransition(unsafeTransition, highGround);

        FSMTransition backToTraverse = new(CanResumeTraverse);
        highGround.AddTransition(backToTraverse, traverse);

        FSMTransition reachedGoalTransition = new(HasReachedGoal);
        traverse.AddTransition(reachedGoalTransition, reachedGoal);
        highGround.AddTransition(reachedGoalTransition, reachedGoal);

        fsm = new FSM(traverse);
   }

// FSM Actions
   public void TraverseUpdate()
    {
        if (isMoving)
            MoveTowardsTarget();
    }

    public void SeekHighGround()
    {
        float water = waterController.GetCurrentWaterLevel();
        SetPathToNearestHighGround(water);
    }

    public void GoalReached()
    {
        isMoving = false;
        Debug.Log("Goal reached.");
    }

// FSM Transitions
    public bool PathIsUnsafe()
    {
        float water = waterController.GetCurrentWaterLevel();

        if (currentPath == null || currentPath.Count == 0)
            return true;

        for (int i = currentPathIndex; i < currentPath.Count; i++)
        {
            Vector3 pos = terrainGraph.GetNodePosition(currentPath[i]);
            if (pos.y < water + 0.2f)   // small safety margin
                return true;
        }

        return false;
    }

    public bool CanResumeTraverse()
    {
        if (waterController.IsRising())
            return false;

        return CanReachGoalSafely();
    }

    public bool HasReachedGoal()
    {
        return reachedGoal;
    }



    void Update()
    {
        if (terrainGraph == null) return;

        fsm.Update();

        // Check if submerged
        if (transform.position.y < waterController.GetCurrentWaterLevel())
        {
            Debug.LogError("Agent drowned!");
            enabled = false;
        }
    }

    void CalculatePath()
    {
        // Update graph with current water level
        terrainGraph.UpdateWalkableNodes(waterController.GetCurrentWaterLevel());

        Node startNode = terrainGraph.GetStartNode();
        Node goalNode = terrainGraph.GetGoalNode();

        // Create heuristic function that captures terrainGraph
        HeuristicFunction heuristic = (Node from, Node to) =>
        {
            Vector3 fromPos = terrainGraph.GetNodePosition(from);
            Vector3 toPos = terrainGraph.GetNodePosition(to);
            return Vector3.Distance(fromPos, toPos);
        };

        // Run A*
        Graph graph = terrainGraph.GetGraph();
        Edge[] pathEdges = AStarSolver.Solve(graph, startNode, goalNode, heuristic);

        if (pathEdges.Length == 0)
        {
            Debug.LogError("No path found! Agent cannot reach goal.");
            return;
        }

        // Convert edges to list of nodes
        currentPath = new List<Node> { startNode };
        foreach (Edge edge in pathEdges)
        {
            currentPath.Add(edge.to);
        }

        currentPathIndex = 0;
        SetNextTarget();
        isMoving = true;

        Debug.Log($"Path calculated with {currentPath.Count} nodes");
    }

    // void CheckAndReplan()
    // {
    //     if (currentPath == null || currentPath.Count == 0)
    //         return;

    //     float currentWaterLevel = waterController.GetCurrentWaterLevel();

    //     // Check if any upcoming nodes in the path are now underwater
    //     for (int i = currentPathIndex; i < currentPath.Count; ++i)
    //     {
    //         Vector3 nodePos = terrainGraph.GetNodePosition(currentPath[i]);
    //         if (nodePos.y < currentWaterLevel)
    //         {
    //             Debug.Log("Path threatened by water! Replanning...");

    //             // Find closest safe node to current position
    //             Node currentNode = terrainGraph.GetNodeAtPosition(transform.position.x, transform.position.z);
    //             if (currentNode == null)
    //             {
    //                 Debug.LogError("Agent is not on a valid node!");
    //                 return;
    //             }

    //             // Replan from current position
    //             terrainGraph.UpdateWalkableNodes(currentWaterLevel);
    //             Node goalNode = terrainGraph.GetGoalNode();
    //             Graph graph = terrainGraph.GetGraph();

    //             // Create heuristic with closure
    //             HeuristicFunction heuristic = (Node from, Node to) =>
    //             {
    //                 Vector3 fromPos = terrainGraph.GetNodePosition(from);
    //                 Vector3 toPos = terrainGraph.GetNodePosition(to);
    //                 return Vector3.Distance(fromPos, toPos);
    //             };

    //             Edge[] newPathEdges = AStarSolver.Solve(graph, currentNode, goalNode, heuristic);

    //             if (newPathEdges.Length == 0)
    //             {
    //                 Debug.LogError("No alternative path found!");
    //                 return;
    //             }

    //             // Update path
    //             currentPath = new List<Node>();
    //             currentPath.Add(currentNode);
    //             foreach (Edge edge in newPathEdges)
    //             {
    //                 currentPath.Add(edge.to);
    //             }

    //             currentPathIndex = 0;
    //             SetNextTarget();

    //             Debug.Log($"New path calculated with {currentPath.Count} nodes");
    //             return;
    //         }
    //     }
    // }

    public void SetPathToNearestHighGround(float waterLevel)
    {
        float safetyHeight = waterLevel + 2.0f;  // 1 meter above water

        // Find the current node
        Node currentNode = terrainGraph.GetNodeAtPosition(transform.position.x, transform.position.z);
        if (currentNode == null)
        {
            Debug.LogError("SetPathToNearestHighGround: Agent is not on a valid node.");
            return;
        }

        // Find nearest node above safety height
        Node bestNode = null;
        float bestDist = float.MaxValue;

        foreach (Node n in terrainGraph.GetGraph().getNodes())
        {
            Vector3 pos = terrainGraph.GetNodePosition(n);

            // must be high enough
            if (pos.y < safetyHeight)
                continue;

            // compute horizontal distance from agent
            float d = (new Vector2(pos.x, pos.z) -
                       new Vector2(transform.position.x, transform.position.z)).sqrMagnitude;

            if (d < bestDist)
            {
                bestDist = d;
                bestNode = n;
            }
        }

        if (bestNode == null)
        {
            Debug.LogError("SetPathToNearestHighGround: No high ground is reachable.");
            return;
        }

        // A* path from current node to high ground
        // terrainGraph.UpdateWalkableNodes(waterLevel);

        Edge[] edges = AStarSolver.Solve(terrainGraph.GetGraph(), currentNode, bestNode, EuclideanHeuristic);

        if (edges == null || edges.Length == 0)
        {
            Debug.LogError("SetPathToNearestHighGround: A* found no path to high ground.");
            return;
        }

        // Convert edges to currentPath just like CalculatePath()
        currentPath = new List<Node>();
        currentPath.Add(currentNode);
        foreach (Edge e in edges)
            currentPath.Add(e.to);

        currentPathIndex = 0;
        SetNextTarget();
        isMoving = true;

        Debug.Log($"SeekHighGround: Found high ground at {terrainGraph.GetNodePosition(bestNode)}");
    }


    public bool CanReachGoalSafely()
    {
        float water = waterController.GetCurrentWaterLevel();
        terrainGraph.UpdateWalkableNodes(water);

        Node currentNode = terrainGraph.GetNodeAtPosition(transform.position.x, transform.position.z);
        Node goalNode = terrainGraph.GetGoalNode();

        HeuristicFunction h = (Node a, Node b) =>
            Vector3.Distance(terrainGraph.GetNodePosition(a), terrainGraph.GetNodePosition(b));

        Edge[] edges = AStarSolver.Solve(terrainGraph.GetGraph(), currentNode, goalNode, h);
        return edges.Length > 0;
    }


    void SetNextTarget()
    {
        if (currentPathIndex >= currentPath.Count)
        {
            reachedGoal = true;
            Debug.Log("Goal reached!");
            return;
        }

        Node targetNode = currentPath[currentPathIndex];
        currentTarget = terrainGraph.GetNodePosition(targetNode);
    }

    void MoveTowardsTarget()
    {
        Vector3 direction = (currentTarget - transform.position).normalized;

        // Determine speed based on vertical movement
        float heightDiff = currentTarget.y - transform.position.y;
        float speed = (heightDiff > 0.01f) ? uphillSpeed : flatSpeed;

        // Move towards target
        float step = speed * Time.deltaTime;
        transform.position = Vector3.MoveTowards(transform.position, currentTarget, step);

        // Check if reached target node
        if (Vector3.Distance(transform.position, currentTarget) < 0.1f)
        {
            ++currentPathIndex;
            SetNextTarget();
        }
    }

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

    void OnDrawGizmos()
    {
        if (!visualizePath || currentPath == null || terrainGraph == null)
            return;

        // Draw current path
        Gizmos.color = Color.cyan;
        for (int i = 0; i < currentPath.Count - 1; ++i)
        {
            Vector3 from = terrainGraph.GetNodePosition(currentPath[i]);
            Vector3 to = terrainGraph.GetNodePosition(currentPath[i + 1]);
            Gizmos.DrawLine(from, to);
        }

        // Draw current target
        Gizmos.color = Color.magenta;
        Gizmos.DrawSphere(currentTarget, 0.5f);
    }
}