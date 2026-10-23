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
	/// How much further out than half the width the left edge sits at the start of the road and at its end (looking
	/// along it), blended smoothly between the two; negative pulls the edge in. A lane appearing on one side over a
	/// taper is an extra of one lane at that end only.
	/// </summary>
	[Range(-500.0f, 500.0f)] public float LeftExtraStart { get; set { field = value.Clamp(-10000.0f, 10000.0f); } } = 0.0f;
	[Range(-500.0f, 500.0f)] public float LeftExtraEnd { get; set { field = value.Clamp(-10000.0f, 10000.0f); } } = 0.0f;
	[Range(-500.0f, 500.0f)] public float RightExtraStart { get; set { field = value.Clamp(-10000.0f, 10000.0f); } } = 0.0f;
	[Range(-500.0f, 500.0f)] public float RightExtraEnd { get; set { field = value.Clamp(-10000.0f, 10000.0f); } } = 0.0f;

	/// <summary>
	/// A ramp leaves or joins the road it meets at a junction from that road's side rather than its middle: where
	/// this road meets a junction, its centreline is shifted so that its outer edge lines up with the widest edge of
	/// the roads running through there on that side, and it leaves parallel to them. A highway exit is a ramp off a
	/// road with a lane's extra width at that end; an entry is one onto a road with the extra at its start.
	/// </summary>
	public bool Ramp { get; set; } = false;

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

	/// <summary>
	/// Whether Conform Terrain To Roads shapes the ground to this road. Off for a road whose surroundings are built
	/// by hand — one on top of a dam, along a quay, through a model of its own — where the ground is left alone.
	/// </summary>
	public bool ConformTerrain { get; set; } = true;

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
