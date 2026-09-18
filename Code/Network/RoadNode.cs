using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// A point of the road graph: a road end, a pass-through point where a road bends, or a junction once three or more
/// segments meet there. Positions are in network-local space.
/// </summary>
public sealed class RoadNode
{
	public int Id { get; set; }

	public Vector3 Position { get; set; }

	/// <summary>
	/// While set, the network keeps the tangent handles of every segment at this node in sync with its neighbours:
	/// smooth through a pass-through node, straight at a road end and into a junction. Cleared once the handles are
	/// placed by hand.
	/// </summary>
	public bool AutoTangents { get; set; } = true;

	/// <summary>
	/// Radius of the rounded corners where two roads meet at this node. 0 gives sharp corners.
	/// </summary>
	[Range(0.0f, 1000.0f)] public float CornerRadius { get; set; } = 150.0f;
}
