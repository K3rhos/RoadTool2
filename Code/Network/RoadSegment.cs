using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// A road between two nodes: a cubic Bezier whose handles hang off the nodes, plus the profile the road is built
/// with. Every segment owns its own profile, which is what lets a road change type from one node to the next.
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



	public bool Touches(int _NodeId)
	{
		return StartNodeId == _NodeId || EndNodeId == _NodeId;
	}



	public int OtherNodeId(int _NodeId)
	{
		return StartNodeId == _NodeId ? EndNodeId : StartNodeId;
	}
}
