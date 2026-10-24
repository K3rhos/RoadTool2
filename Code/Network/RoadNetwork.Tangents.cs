using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace RedSnail.RoadTool2;

public sealed partial class RoadNetwork
{
	/// <summary>
	/// Recomputes the automatic tangent handles at a node and at each of its neighbours. A neighbour's smoothing
	/// direction runs through this node, and so does the grade of its pitch-only handles, so both have to follow
	/// whenever this node moves or gains a road.
	/// </summary>
	public void RefreshTangentsAround(int _NodeId)
	{
		RefreshTangentsAt(_NodeId);

		foreach (var segment in GetSegmentsAt(_NodeId).ToList())
			RefreshTangentsAt(segment.OtherNodeId(_NodeId));
	}



	private void RefreshTangentsAt(int _NodeId)
	{
		var node = FindNode(_NodeId);

		if (node is null || node.TangentMode == AutoTangentMode.Disabled)
			return;

		var segments = GetSegmentsAt(_NodeId).ToList();
		bool full = node.TangentMode == AutoTangentMode.Full;

		// Pitch only leaves the handles' directions as they were placed, so they say whether the road turns a corner
		// here: if it does, each road leaves at its own grade instead of one carried on through the node.
		bool corner = !full && IsCorner(_NodeId, segments);

		foreach (var segment in segments)
		{
			var other = FindNode(segment.OtherNodeId(_NodeId));

			if (other is null)
				continue;

			var neighbours = new List<Vector3>();

			foreach (var otherSegment in segments)
			{
				if (otherSegment == segment || corner)
					continue;

				// A ramp peels off the side of the road; the road flows on through the node as if it were not there.
				if (otherSegment.Ramp && !segment.Ramp)
					continue;

				var neighbour = FindNode(otherSegment.OtherNodeId(_NodeId));

				if (neighbour is not null)
					neighbours.Add(neighbour.Position);
			}

			Vector3 handle = full
				? ComputeAutoHandle(node.Position, other.Position, neighbours)
				: FitPitch(GetHandle(segment, _NodeId), node.Position, other.Position, neighbours);

			if (segment.StartNodeId == _NodeId)
				segment.StartTangent = handle;
			else
				segment.EndTangent = handle;
		}
	}



	/// <summary>
	/// Whether a node's two handles turn the road there rather than carry it straight on: they leave anything but
	/// opposite ways in plan. A road end or a junction is neither.
	/// </summary>
	private static bool IsCorner(int _NodeId, IReadOnlyList<RoadSegment> _Segments)
	{
		if (_Segments.Count != 2)
			return false;

		Vector3 first = GetHandle(_Segments[0], _NodeId).WithZ(0.0f);
		Vector3 second = GetHandle(_Segments[1], _NodeId).WithZ(0.0f);

		// A handle pulled in onto its node leaves in no particular direction: each road goes its own way.
		if (first.IsNearZeroLength || second.IsNearZeroLength)
			return true;

		float angle = MathF.Acos(Math.Clamp(Vector3.Dot(first.Normal, second.Normal), -1.0f, 1.0f)).RadianToDegree();

		return angle < 175.0f;
	}



	/// <summary>
	/// A handle given the grade an automatic one would leave the node at (see <see cref="AutoGrade"/>), keeping the
	/// direction and length it has in plan. A handle with no reach in plan has no grade to follow and stays as it is.
	/// </summary>
	private static Vector3 FitPitch(Vector3 _Handle, Vector3 _NodePosition, Vector3 _TargetPosition, IReadOnlyList<Vector3> _OtherNeighbours)
	{
		Vector3 flat = _Handle.WithZ(0.0f);

		if (flat.IsNearZeroLength)
			return _Handle;

		float horizontal = flat.Length;

		return flat + Vector3.Up * (AutoGrade(_NodePosition, _TargetPosition, _OtherNeighbours, flat / horizontal) * horizontal);
	}



	/// <summary>
	/// How steeply an automatic handle leaves a node, as rise per unit of horizontal travel in a direction (a flat unit
	/// vector): at a pass-through, the grade running from the road's other neighbour to its far node; at a junction,
	/// the grade of the road running through it; at a road end, a corner or a hairpin, the road's own grade to its far
	/// node, whichever way the handle points. The grades <see cref="ComputeAutoHandle"/> gives its handles.
	/// </summary>
	private static float AutoGrade(Vector3 _NodePosition, Vector3 _TargetPosition, IReadOnlyList<Vector3> _OtherNeighbours, Vector3 _Along)
	{
		Vector3 chord = _TargetPosition - _NodePosition;

		if (_OtherNeighbours.Count == 1)
		{
			Vector3 through = _TargetPosition - _OtherNeighbours[0];
			Vector3 run = through.WithZ(0.0f);

			if (!run.IsNearZeroLength && Vector3.Dot(through, chord) > 0.0f)
				return through.z / run.Length * Vector3.Dot(_Along, run.Normal);
		}
		else if (_OtherNeighbours.Count >= 2 && TryThroughGrade(_NodePosition, _OtherNeighbours, out Vector3 throughDirection, out float grade))
		{
			return grade * Vector3.Dot(_Along, throughDirection);
		}

		float horizontal = chord.WithZ(0.0f).Length;

		return horizontal > 0.001f ? chord.z / horizontal : 0.0f;
	}



	/// <summary>
	/// The curve a road from <paramref name="_FromNodeId"/> to <paramref name="_ToLocalPosition"/> would get, with
	/// <paramref name="_ToNodeId"/> naming the existing node at that position when the road joins one, and
	/// <paramref name="_Sharp"/> when the new node is going to be a corner. Lets a tool preview exactly what
	/// <see cref="AddSegment"/> will build.
	/// </summary>
	public RoadCurve PreviewCurve(int _FromNodeId, Vector3 _ToLocalPosition, int _ToNodeId = -1, bool _Sharp = false)
	{
		var from = FindNode(_FromNodeId);

		if (from is null)
			return new RoadCurve(_ToLocalPosition, _ToLocalPosition, _ToLocalPosition, _ToLocalPosition);

		var to = _ToNodeId >= 0 ? FindNode(_ToNodeId) : null;

		Vector3 startHandle = from.TangentMode == AutoTangentMode.Full && !_Sharp
			? ComputeAutoHandle(from.Position, _ToLocalPosition, GetNeighbourPositions(_FromNodeId))
			: StraightHandle(from.Position, _ToLocalPosition);

		Vector3 endHandle = to is not null && to.TangentMode == AutoTangentMode.Full && !_Sharp
			? ComputeAutoHandle(_ToLocalPosition, from.Position, GetNeighbourPositions(_ToNodeId))
			: StraightHandle(_ToLocalPosition, from.Position);

		return new RoadCurve(from.Position, from.Position + startHandle, _ToLocalPosition + endHandle, _ToLocalPosition);
	}



	private List<Vector3> GetNeighbourPositions(int _NodeId)
	{
		return GetSegmentsAt(_NodeId)
			.Select(segment => FindNode(segment.OtherNodeId(_NodeId)))
			.Where(node => node is not null)
			.Select(node => node.Position)
			.ToList();
	}



	/// <summary>
	/// The Bezier handle leaving a node toward one of its roads. A pass-through node (exactly one other neighbour)
	/// gets a direction running from that neighbour through the node, so the road flows through without a kink.
	/// Road ends point straight at the far node. A junction arm points at its far node in plan but leaves at the
	/// grade of the road running through the junction (the two other neighbours most nearly in line), so a ramp
	/// off a level highway starts level and climbs on its way, instead of setting off uphill from the junction and
	/// meeting it at a different height. The handle spans a third of the chord, which reproduces a straight line
	/// exactly when the nodes are collinear.
	/// </summary>
	private static Vector3 ComputeAutoHandle(Vector3 _NodePosition, Vector3 _TargetPosition, IReadOnlyList<Vector3> _OtherNeighbours)
	{
		Vector3 chord = _TargetPosition - _NodePosition;
		float length = chord.Length;

		if (length < 0.001f)
			return Vector3.Zero;

		Vector3 direction = chord / length;

		if (_OtherNeighbours.Count == 1)
		{
			Vector3 through = _TargetPosition - _OtherNeighbours[0];

			// A hairpin (both roads leaving on the same side) would fold the curve back on itself; keep it straight.
			if (!through.IsNearZeroLength && Vector3.Dot(through, chord) > 0.0f)
				direction = through.Normal;
		}
		else if (_OtherNeighbours.Count >= 2 && TryThroughGrade(_NodePosition, _OtherNeighbours, out Vector3 throughDirection, out float grade))
		{
			Vector3 flat = chord.WithZ(0.0f);

			if (!flat.IsNearZeroLength)
			{
				float horizontal = flat.Length;
				float climb = grade * Vector3.Dot(flat / horizontal, throughDirection);

				return (flat / horizontal + Vector3.Up * climb) * (horizontal / 3.0f);
			}
		}

		return direction * (length / 3.0f);
	}



	/// <summary>
	/// The road running through a junction, as the two neighbours most nearly opposite each other across the node
	/// (within 60 degrees of straight): its horizontal direction and its rise per unit of horizontal distance.
	/// </summary>
	private static bool TryThroughGrade(Vector3 _NodePosition, IReadOnlyList<Vector3> _Neighbours, out Vector3 _Direction, out float _Grade)
	{
		_Direction = Vector3.Zero;
		_Grade = 0.0f;

		float straightest = 0.5f;
		int bestA = -1;
		int bestB = -1;

		for (int i = 0; i < _Neighbours.Count; i++)
		{
			Vector3 a = (_Neighbours[i] - _NodePosition).WithZ(0.0f);

			if (a.IsNearZeroLength)
				continue;

			for (int j = i + 1; j < _Neighbours.Count; j++)
			{
				Vector3 b = (_Neighbours[j] - _NodePosition).WithZ(0.0f);

				if (b.IsNearZeroLength)
					continue;

				float straightness = -Vector3.Dot(a.Normal, b.Normal);

				if (straightness > straightest)
				{
					straightest = straightness;
					bestA = i;
					bestB = j;
				}
			}
		}

		if (bestA < 0)
			return false;

		Vector3 across = _Neighbours[bestB] - _Neighbours[bestA];
		Vector3 flat = across.WithZ(0.0f);

		if (flat.IsNearZeroLength)
			return false;

		_Direction = flat.Normal;
		_Grade = across.z / flat.Length;

		return true;
	}



	/// <summary>
	/// A handle pointing straight at the far node: a straight road when both ends have one.
	/// </summary>
	private static Vector3 StraightHandle(Vector3 _NodePosition, Vector3 _TargetPosition)
	{
		return (_TargetPosition - _NodePosition) / 3.0f;
	}
}
