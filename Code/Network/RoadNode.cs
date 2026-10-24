using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// How the network keeps a node's tangent handles in line with its neighbours.
/// </summary>
public enum AutoTangentMode
{
	/// <summary>
	/// The handles stay exactly as they were placed.
	/// </summary>
	Disabled,

	/// <summary>
	/// The handles keep the direction and length they were given in plan, and only their pitch follows the heights:
	/// a sharp corner stays sharp while its roads rise and fall with the nodes. At a corner each road leaves at its
	/// own grade; two handles leaving opposite ways share one, so the road carries on through smoothly.
	/// </summary>
	[Title("Pitch Only")]
	PitchOnly,

	/// <summary>
	/// The handles are worked out entirely: smooth through a pass-through node, straight at a road end and into a
	/// junction.
	/// </summary>
	Full
}

/// <summary>
/// A point of the road graph: a road end, a pass-through point where a road bends, or a junction once three or more
/// segments meet there. Positions are in network-local space.
/// </summary>
public sealed class RoadNode
{
	public int Id { get; set; }

	public Vector3 Position { get; set; }

	/// <summary>
	/// How the network keeps the tangent handles of every segment at this node in line with its neighbours. Placing a
	/// handle by hand takes a node off Full.
	/// </summary>
	[Title("Auto Tangents")]
	public AutoTangentMode TangentMode
	{
		get => m_TangentMode ?? (AutoTangents ? AutoTangentMode.Full : AutoTangentMode.Disabled);
		set
		{
			m_TangentMode = value;
			AutoTangents = value == AutoTangentMode.Full;
		}
	}

	/// <summary>
	/// The mode as set. Null on a node saved before tangents had modes, and on one still in memory from then (a hot
	/// reload gives a new field its default, not a value worked out from the old one); those go by
	/// <see cref="AutoTangents"/> until they are given a mode.
	/// </summary>
	private AutoTangentMode? m_TangentMode;

	/// <summary>
	/// What <see cref="TangentMode"/> was before it had modes: on (Full) or off. Kept in step with the mode, and only
	/// read for a node that has no mode of its own yet.
	/// </summary>
	[Hide] public bool AutoTangents { get; set; } = true;

	/// <summary>
	/// Radius of the rounded corners where two roads meet at this node. 0 gives sharp corners.
	/// </summary>
	[Range(0.0f, 1000.0f)] public float CornerRadius { get; set; } = 150.0f;
}
