using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// A road between two nodes: a cubic Bezier whose handles hang off the nodes, plus the road type it is built as.
/// Every segment carries its own profile and width, which is what lets a road change type from one node to the
/// next.
/// </summary>
public sealed class RoadSegment
{
	public int Id { get; set; }

	public int StartNodeId { get; set; }

	public int EndNodeId { get; set; }

	/// <summary>
	/// Bezier handle leaving the start node into the segment (P1 - P0).
	/// </summary>
	public Vector3 StartTangent { get; set; }

	/// <summary>
	/// Bezier handle leaving the end node into the segment (P2 - P3).
	/// </summary>
	public Vector3 EndTangent { get; set; }

	[Range(50.0f, 2000.0f)] public float Width { get; set; } = 500.0f;

	/// <summary>
	/// The road type: surface material and borders. Null falls back to the network's default profile.
	/// </summary>
	public RoadProfile Profile { get; set; }

	/// <summary>
	/// Set, this road runs on a bridge: a deck is built under it in this style, with sides and supports. Null is a
	/// road on the ground.
	/// </summary>
	public BridgeStyle Bridge { get; set; }

	/// <summary>
	/// What stands along this road and before its junctions (lamp posts, trees, hydrants...). Null is a bare road.
	/// </summary>
	public RoadPropSet Props { get; set; }

	/// <summary>
	/// How many lanes the road is divided into, side by side across its width.
	/// </summary>
	[Range(MinLanes, MaxLanes)] public int LaneCount { get; set { field = value.Clamp(MinLanes, MaxLanes); } } = 2;

	/// <summary>
	/// How many of the lanes run from the start node to the end node. With right-hand traffic those are the
	/// lanes on the right, looking along the road; the rest run the other way. Equal to the lane count for a
	/// one-way road, zero for a one-way road running end to start.
	/// </summary>
	[Range(0, MaxLanes)] public int ForwardLanes { get; set { field = value.Clamp(0, MaxLanes); } } = 1;

	/// <summary>
	/// Whether this road gets a pedestrian crossing where it meets a junction at each end (when its road type has
	/// crossings at all).
	/// </summary>
	public bool CrossingAtStart { get; set; } = true;

	public bool CrossingAtEnd { get; set; } = true;

	public const int MinLanes = 1;
	public const int MaxLanes = 6;

	public bool CrossingAt(int _NodeId)
	{
		return StartNodeId == _NodeId ? CrossingAtStart : CrossingAtEnd;
	}

	/// <summary>
	/// Lanes running end to start, counted from the left edge looking along the road.
	/// </summary>
	public int BackwardLanes => LaneCount - ForwardLanes.Clamp(0, LaneCount);

	public bool IsOneWay => ForwardLanes <= 0 || ForwardLanes >= LaneCount;

	/// <summary>
	/// Whether the lane at an index (0 = leftmost, looking along the road) runs start to end.
	/// </summary>
	public bool IsLaneForward(int _Lane)
	{
		return _Lane >= BackwardLanes;
	}

	/// <summary>
	/// The even split for a lane count: two-way with the odd lane going forward, a single lane one-way.
	/// </summary>
	public static int DefaultForwardLanes(int _LaneCount)
	{
		return (_LaneCount + 1) / 2;
	}



	public bool Touches(int _NodeId)
	{
		return StartNodeId == _NodeId || EndNodeId == _NodeId;
	}



	public int OtherNodeId(int _NodeId)
	{
		return StartNodeId == _NodeId ? EndNodeId : StartNodeId;
	}
}
