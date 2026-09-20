using Sandbox;

namespace RedSnail.RoadTool2;

public enum RoadChunkKind
{
	/// <summary>
	/// The road surface itself.
	/// </summary>
	Surface,

	/// <summary>
	/// What runs along the road's sides: sidewalks, verges.
	/// </summary>
	Border,

	/// <summary>
	/// The painted lines on the surface: lane lines, centre line, edge lines.
	/// </summary>
	Marking,

	/// <summary>
	/// The bridge under a road: deck, sides, supports.
	/// </summary>
	Bridge
}

/// <summary>
/// Marks a generated mesh child of a <see cref="RoadNetwork"/> and remembers what it covers: a stretch of a segment,
/// or the junction at a node, and whether it is the surface or the border of it. The network uses it to replace
/// exactly the pieces that went stale and leave the rest alone.
/// </summary>
[Icon("view_week")]
public sealed class RoadChunk : Component
{
	/// <summary>
	/// The segment this chunk is a stretch of, or -1 for a junction piece.
	/// </summary>
	[Property, ReadOnly] public int SegmentId { get; set; } = -1;

	/// <summary>
	/// The node this chunk is the junction of, or -1 for a road stretch.
	/// </summary>
	[Property, ReadOnly] public int NodeId { get; set; } = -1;

	[Property, ReadOnly] public RoadChunkKind Kind { get; set; } = RoadChunkKind.Surface;

	[Property, ReadOnly] public int ChunkIndex { get; set; }

	[Property, ReadOnly] public int ChunkCount { get; set; }

	/// <summary>
	/// A locked chunk is never regenerated, so its mesh can be edited by hand. It keeps its geometry even if the road
	/// under it changes; unlock it to have it rebuilt.
	/// </summary>
	[Property]
	public bool Locked
	{
		get;
		set
		{
			field = value;

			// Unlocking is a request to catch up with the graph.
			GameObject?.Parent?.Components.Get<RoadNetwork>()?.MarkDirty();
		}
	}

	/// <summary>
	/// Hash of everything this chunk was generated from. When it no longer matches what the graph says it should be
	/// the chunk is stale.
	/// </summary>
	[Property, Hide] public long Signature { get; set; }

	/// <summary>
	/// The rows a border piece was built from, when the network's debug overlay is on. Not saved.
	/// </summary>
	public BorderDebug Debug { get; set; }

	public bool IsJunction => NodeId >= 0;



	protected override void DrawGizmos()
	{
		if (Debug is null)
			return;

		var network = GameObject?.Parent?.Components.Get<RoadNetwork>();

		if (network is null || !network.DebugBorders)
			return;

		Gizmo.Draw.IgnoreDepth = true;
		Gizmo.Draw.LineThickness = 2.0f;

		foreach (var line in Debug.Lines)
		{
			Gizmo.Draw.Color = line.Color;
			Gizmo.Draw.Line(line.From, line.To);
		}

		foreach (var marker in Debug.Markers)
		{
			Gizmo.Draw.Color = marker.Color;
			Gizmo.Draw.SolidSphere(marker.Position, 2.0f);
		}
	}
}
