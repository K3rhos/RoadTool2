using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// The road network: a graph of nodes joined by segments, which is the single source of truth for every road in
/// it. All the meshes under this GameObject are derived from the graph and regenerated on demand, one chunk at a
/// time (see RoadNetwork.Rebuild.cs).
/// </summary>
[Icon("add_road")]
public sealed partial class RoadNetwork : Component, Component.ExecuteInEditor
{
	public const string ChunkTag = "road_chunk";

	/// <summary>
	/// Undo/redo and scene loads assign whole new lists here instead of mutating the existing ones, so the setters
	/// are the one place that reliably sees "the graph changed" and asks for a reconcile pass.
	/// </summary>
	[Property, Hide]
	public List<RoadNode> Nodes
	{
		get;
		set
		{
			field = value ?? new List<RoadNode>();
			MarkDirty();
		}
	} = new();

	[Property, Hide]
	public List<RoadSegment> Segments
	{
		get;
		set
		{
			field = value ?? new List<RoadSegment>();
			MarkDirty();
		}
	} = new();

	/// <summary>
	/// Shared counter for node and segment ids. Serialized so ids stay unique across undo and reloads.
	/// </summary>
	[Property, Hide] public int NextId { get; set; } = 1;

	/// <summary>
	/// The road type used by roads that have none of their own.
	/// </summary>
	[Property(Title = "Default Profile"), Group("Road")] public RoadProfile DefaultProfile { get; set { field = value; MarkDirty(); } }

	/// <summary>
	/// How far above the node positions the road surface sits. Nodes are placed on the ground, so without this the
	/// surface would z-fight with flat terrain.
	/// </summary>
	[Property(Title = "Surface Offset"), Group("Road"), Range(0.0f, 50.0f)] public float SurfaceOffset { get; set { field = value; MarkDirty(); } } = 1.0f;



	/// <summary>
	/// The road type a segment is built as: its own, else the network's default, else plain asphalt.
	/// </summary>
	public RoadProfile ProfileOf(RoadSegment _Segment)
	{
		return _Segment?.Profile ?? DefaultProfile ?? RoadProfile.Fallback;
	}

	[Property(Title = "Sample Spacing"), Group("Generation"), Range(10.0f, 200.0f)] public float SampleSpacing { get; set { field = value.Clamp(5.0f, 1000.0f); MarkDirty(); } } = 80.0f;
	[Property(Title = "Chunk Length"), Group("Generation"), Range(200.0f, 10000.0f)] public float ChunkLength { get; set { field = value.Clamp(100.0f, 100000.0f); MarkDirty(); } } = 3000.0f;

	/// <summary>
	/// How many chunks may be (re)built per editor frame. Keeps the editor responsive while a long road catches up.
	/// </summary>
	[Property(Title = "Chunks Per Frame"), Group("Generation"), Range(1, 32)] public int RebuildBudget { get; set; } = 4;

	/// <summary>
	/// Draws every border piece's rows over the scene: arm chunks green, junction corner halves red and cyan,
	/// block corner patches yellow, caps magenta. The editor also writes the numbers behind every piece to
	/// Libraries/RoadTool2/debug/borders.txt whenever a piece is rebuilt.
	/// </summary>
	[Property(Title = "Debug Borders"), Group("Debug")] public bool DebugBorders { get; set { field = value; MarkDirty(); } }

	/// <summary>
	/// Bumped each time a border piece is built with its debug record, so the editor knows when to write it out.
	/// </summary>
	public int DebugRevision { get; private set; }



	public RoadNode FindNode(int _Id)
	{
		return Nodes.FirstOrDefault(node => node.Id == _Id);
	}



	public RoadSegment FindSegment(int _Id)
	{
		return Segments.FirstOrDefault(segment => segment.Id == _Id);
	}



	public RoadSegment FindSegmentBetween(int _NodeA, int _NodeB)
	{
		if (_NodeA == _NodeB)
			return null;

		return Segments.FirstOrDefault(segment => segment.Touches(_NodeA) && segment.Touches(_NodeB));
	}



	public IEnumerable<RoadSegment> GetSegmentsAt(int _NodeId)
	{
		return Segments.Where(segment => segment.Touches(_NodeId));
	}



	public int GetDegree(int _NodeId)
	{
		return Segments.Count(segment => segment.Touches(_NodeId));
	}



	public RoadNode FindNearestNode(Vector3 _LocalPosition, float _MaxDistance)
	{
		RoadNode nearest = null;
		float nearestDistance = _MaxDistance;

		foreach (var node in Nodes)
		{
			float distance = Vector3.DistanceBetween(node.Position, _LocalPosition);

			if (distance > nearestDistance)
				continue;

			nearest = node;
			nearestDistance = distance;
		}

		return nearest;
	}



	public RoadNode AddNode(Vector3 _LocalPosition)
	{
		var node = new RoadNode { Id = NextId++, Position = _LocalPosition };

		Nodes.Add(node);
		MarkDirty();

		return node;
	}



	/// <summary>
	/// Joins two nodes with a road. Returns null when the join makes no sense (same node, unknown node, or a road
	/// already runs between them).
	/// </summary>
	public RoadSegment AddSegment(int _StartNodeId, int _EndNodeId, float _Width, RoadProfile _Profile = null)
	{
		if (_StartNodeId == _EndNodeId)
			return null;

		if (FindNode(_StartNodeId) is null || FindNode(_EndNodeId) is null)
			return null;

		if (FindSegmentBetween(_StartNodeId, _EndNodeId) is not null)
			return null;

		var start = FindNode(_StartNodeId);
		var end = FindNode(_EndNodeId);

		// Straight handles to begin with: that is what a node with hand-placed tangents keeps (the refresh below
		// leaves those nodes alone), and what a junction arm wants anyway.
		var segment = new RoadSegment
		{
			Id = NextId++,
			StartNodeId = _StartNodeId,
			EndNodeId = _EndNodeId,
			Width = _Width,
			Profile = _Profile,
			StartTangent = StraightHandle(start.Position, end.Position),
			EndTangent = StraightHandle(end.Position, start.Position)
		};

		Segments.Add(segment);

		RefreshTangentsAround(_StartNodeId);
		RefreshTangentsAround(_EndNodeId);
		MarkDirty();

		return segment;
	}



	/// <summary>
	/// Inserts a node on a road without changing the road's shape: the segment keeps its id and becomes the first
	/// half, a new segment covers the rest. A split landing within <paramref name="_MinEndDistance"/> of an end
	/// returns that end node instead, so no sliver segments get made.
	/// </summary>
	public RoadNode SplitSegment(int _SegmentId, float _T, float _MinEndDistance = 25.0f)
	{
		var segment = FindSegment(_SegmentId);

		if (segment is null || !TryGetCurve(segment, out var curve))
			return null;

		var table = GetCurveTable(segment);
		float distance = _T * table.Length;

		if (distance <= _MinEndDistance)
			return FindNode(segment.StartNodeId);

		if (table.Length - distance <= _MinEndDistance)
			return FindNode(segment.EndNodeId);

		curve.Split(_T, out var first, out var second);

		// The split point's handles come straight from the subdivision, which is the only way the two halves keep
		// tracing the old road, so they are hand-placed from the node's point of view.
		var node = new RoadNode { Id = NextId++, Position = first.P3, AutoTangents = false };
		Nodes.Add(node);

		int endNodeId = segment.EndNodeId;

		segment.EndNodeId = node.Id;
		segment.StartTangent = first.P1 - first.P0;
		segment.EndTangent = first.P2 - first.P3;

		Segments.Add(new RoadSegment
		{
			Id = NextId++,
			StartNodeId = node.Id,
			EndNodeId = endNodeId,
			Width = segment.Width,
			Profile = segment.Profile,
			StartTangent = second.P1 - second.P0,
			EndTangent = second.P2 - second.P3
		});

		MarkDirty();

		return node;
	}



	/// <summary>
	/// Switches a node between automatic and hand-placed tangents. Going manual straightens every handle at the
	/// node, which is the "sharp corner" look, unless <paramref name="_Straighten"/> is off, which just freezes the
	/// handles as they are (so a road added next keeps its straight handle there without kinking the roads already
	/// through the node); going automatic hands the smoothing back to the network.
	/// </summary>
	public void SetAutoTangents(int _NodeId, bool _Auto, bool _Straighten = true)
	{
		var node = FindNode(_NodeId);

		if (node is null)
			return;

		node.AutoTangents = _Auto;

		if (!_Auto && _Straighten)
		{
			foreach (var segment in GetSegmentsAt(_NodeId))
			{
				var other = FindNode(segment.OtherNodeId(_NodeId));

				if (other is not null)
					SetHandleInternal(segment, _NodeId, StraightHandle(node.Position, other.Position));
			}
		}

		RefreshTangentsAround(_NodeId);
		MarkDirty();
	}



	/// <summary>
	/// The Bezier handle a segment has at one of its nodes, pointing from the node into the segment.
	/// </summary>
	public static Vector3 GetHandle(RoadSegment _Segment, int _NodeId)
	{
		return _Segment.StartNodeId == _NodeId ? _Segment.StartTangent : _Segment.EndTangent;
	}



	/// <summary>
	/// Places one handle by hand. With <paramref name="_Mirror"/>, a pass-through node keeps its other handle
	/// opposite (own length kept) so the road stays smooth through it.
	/// </summary>
	public void SetHandle(int _SegmentId, int _NodeId, Vector3 _Handle, bool _Mirror)
	{
		var segment = FindSegment(_SegmentId);
		var node = FindNode(_NodeId);

		if (segment is null || node is null || !segment.Touches(_NodeId))
			return;

		node.AutoTangents = false;
		SetHandleInternal(segment, _NodeId, _Handle);

		if (_Mirror && !_Handle.IsNearZeroLength)
		{
			var attached = GetSegmentsAt(_NodeId).ToList();

			if (attached.Count == 2)
			{
				var other = attached[0] == segment ? attached[1] : attached[0];
				float otherLength = GetHandle(other, _NodeId).Length;

				SetHandleInternal(other, _NodeId, -_Handle.Normal * otherLength);
			}
		}

		MarkDirty();
	}



	private static void SetHandleInternal(RoadSegment _Segment, int _NodeId, Vector3 _Handle)
	{
		if (_Segment.StartNodeId == _NodeId)
			_Segment.StartTangent = _Handle;
		else
			_Segment.EndTangent = _Handle;
	}



	public void MoveNode(int _NodeId, Vector3 _LocalPosition)
	{
		var node = FindNode(_NodeId);

		if (node is null)
			return;

		// A drag that is holding still would otherwise rebuild the chunks every frame over float noise.
		if (Vector3.DistanceBetween(node.Position, _LocalPosition) < 0.01f)
			return;

		node.Position = _LocalPosition;

		RefreshTangentsAround(_NodeId);
		MarkDirty();
	}



	/// <summary>
	/// Removes a road. A node left with nothing attached goes with it, the others get their smoothing refreshed.
	/// </summary>
	public bool RemoveSegment(int _SegmentId)
	{
		var segment = FindSegment(_SegmentId);

		if (segment is null)
			return false;

		Segments.Remove(segment);

		foreach (int nodeId in new[] { segment.StartNodeId, segment.EndNodeId })
		{
			if (GetDegree(nodeId) == 0)
				Nodes.RemoveAll(node => node.Id == nodeId);
			else
				RefreshTangentsAround(nodeId);
		}

		MarkDirty();

		return true;
	}



	public bool RemoveNode(int _NodeId)
	{
		if (FindNode(_NodeId) is null)
			return false;

		foreach (var segment in GetSegmentsAt(_NodeId).ToList())
			RemoveSegment(segment.Id);

		// RemoveSegment already dropped it once it had no roads left; this covers a node that never had any.
		Nodes.RemoveAll(node => node.Id == _NodeId);
		MarkDirty();

		return true;
	}



	/// <summary>
	/// Takes a pass-through node out and joins its two roads into one, so the road carries on without it. The
	/// merged road keeps the handles the two roads had at their far ends, stretched to the full length (which
	/// undoes a split exactly on a straight road and closely on a curved one), and the longer road's width and
	/// type. Returns the merged road, or null when the node is not a plain pass-through (a junction, a road end,
	/// a loop, or two roads whose far ends are already joined), in which case nothing changes.
	/// </summary>
	public RoadSegment DissolveNode(int _NodeId)
	{
		var node = FindNode(_NodeId);

		if (node is null)
			return null;

		var attached = GetSegmentsAt(_NodeId).ToList();

		if (attached.Count != 2)
			return null;

		var first = attached[0];
		var second = attached[1];
		int startId = first.OtherNodeId(_NodeId);
		int endId = second.OtherNodeId(_NodeId);

		if (startId == endId || FindSegmentBetween(startId, endId) is not null)
			return null;

		float firstLength = GetCurveTable(first)?.Length ?? 0.0f;
		float secondLength = GetCurveTable(second)?.Length ?? 0.0f;
		float total = firstLength + secondLength;
		var main = secondLength > firstLength ? second : first;

		var merged = new RoadSegment
		{
			Id = NextId++,
			StartNodeId = startId,
			EndNodeId = endId,
			Width = main.Width,
			Profile = main.Profile,
			StartTangent = GetHandle(first, startId) * (firstLength > 0.0f ? total / firstLength : 1.0f),
			EndTangent = GetHandle(second, endId) * (secondLength > 0.0f ? total / secondLength : 1.0f)
		};

		Segments.Remove(first);
		Segments.Remove(second);
		Nodes.Remove(node);
		Segments.Add(merged);

		RefreshTangentsAround(startId);
		RefreshTangentsAround(endId);
		MarkDirty();

		return merged;
	}



	public bool TryGetCurve(RoadSegment _Segment, out RoadCurve _Curve)
	{
		var start = FindNode(_Segment.StartNodeId);
		var end = FindNode(_Segment.EndNodeId);

		if (start is null || end is null)
		{
			_Curve = default;
			return false;
		}

		_Curve = new RoadCurve(start.Position, start.Position + _Segment.StartTangent, end.Position + _Segment.EndTangent, end.Position);
		return true;
	}



	private readonly Dictionary<int, RoadCurveTable> m_CurveTables = new();

	/// <summary>
	/// The arc-length table of a segment, rebuilt only when its curve actually changed. Picking asks for every
	/// segment's table every frame, so this has to be cheap on the no-change path.
	/// </summary>
	public RoadCurveTable GetCurveTable(RoadSegment _Segment)
	{
		if (!TryGetCurve(_Segment, out var curve))
			return null;

		if (m_CurveTables.TryGetValue(_Segment.Id, out var table) && table.Curve.Equals(curve))
			return table;

		table = RoadCurveTable.Create(curve);
		m_CurveTables[_Segment.Id] = table;

		return table;
	}



	private void PruneCurveTables()
	{
		var live = new HashSet<int>(Segments.Select(segment => segment.Id));
		var stale = m_CurveTables.Keys.Where(id => !live.Contains(id)).ToList();

		foreach (int id in stale)
			m_CurveTables.Remove(id);
	}
}
