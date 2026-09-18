using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Where roads meet at a node: how far back from the node each arm's mesh has to stop, and the polygon that fills
/// the space between the arms, with rounded corners. All of it is worked out on the arms' real (curved) edges, so
/// the outline lands exactly where the arm meshes end. Worked out from the graph on reconcile, never stored.
/// </summary>
internal sealed class RoadJunctionLayout
{
	public sealed class Arm
	{
		public RoadSegment Segment;
		public RoadCurveTable Table;

		/// <summary>
		/// The segment starts at this node (otherwise it ends here, and runs the other way).
		/// </summary>
		public bool AtStart;

		/// <summary>
		/// Horizontal unit direction leaving the node.
		/// </summary>
		public Vector2 Direction;

		public float Width;

		/// <summary>
		/// The trim the corners ask for; <see cref="Trim"/> is this unless a short road had to give some back.
		/// </summary>
		public float RawTrim;

		/// <summary>
		/// How far from the node, along the curve, the arm's own mesh starts.
		/// </summary>
		public float Trim;

		/// <summary>
		/// Samples of the arm near the node, from a straight extension behind the node (negative distances, where
		/// the outer corner of a sharp bend lives) out along the curve. Edges are offsets of these.
		/// </summary>
		public float[] Distances;
		public Vector3[] Centres;
		public Vector3[] Lefts;

		/// <summary>
		/// Index of the sample sitting on the node.
		/// </summary>
		public int NodeIndex;

		public float Length => Table.Length;

		public int SampleCount => Distances.Length;

		/// <summary>
		/// The curve distance of a point measured from this node along the arm.
		/// </summary>
		public float CurveDistance(float _FromNode)
		{
			return AtStart ? _FromNode : Table.Length - _FromNode;
		}

		/// <summary>
		/// A point on the polyline running parallel to the arm, <paramref name="_Offset"/> to the left of its centre
		/// (negative for the right). The road edges are the offsets at plus and minus half the width.
		/// </summary>
		public Vector3 EdgePoint(int _Index, float _Offset)
		{
			return Centres[_Index] + Lefts[_Index] * _Offset;
		}

		public Vector3[] OffsetPolyline(float _Offset)
		{
			var points = new Vector3[SampleCount];

			for (int i = 0; i < SampleCount; i++)
				points[i] = EdgePoint(i, _Offset);

			return points;
		}
	}

	/// <summary>
	/// Where arm A's left edge meets the right edge of arm B, the next arm counter-clockwise.
	/// </summary>
	private sealed class Corner
	{
		public int ArmA;
		public int ArmB;
		public Vector3 Point;
		public float ParamA;
		public float ParamB;
		public bool HasFillet;
		public Vector2 Centre;
		public float Radius;
		public Vector3 TangentA;
		public Vector3 TangentB;
		public float TangentParamA;
		public float TangentParamB;

		/// <summary>
		/// Unit direction from the sharp corner point towards the fillet centre, and the sine of half the angle
		/// between the two edges there: together they place where lines offset from the two edges meet.
		/// </summary>
		public Vector2 Bisector;
		public float HalfAngleSin;

		public float EndA => HasFillet ? TangentParamA : ParamA;

		public float EndB => HasFillet ? TangentParamB : ParamB;
	}

	/// <summary>
	/// How a border folds round a fillet whose centre lies on the border's side (the inside corner of a junction).
	/// Offsetting the arc towards its centre by more than the radius would fold the surface over itself; past the
	/// radius the outline is the square block corner instead: the point where lines offset from the two road
	/// edges by that much cross.
	/// </summary>
	public sealed class CornerFold
	{
		public Vector2 Apex;
		public Vector2 Bisector;
		public float HalfAngleSin;
		public float Radius;

		public Vector2 MitrePoint(float _Offset)
		{
			return Apex + Bisector * (_Offset / HalfAngleSin);
		}
	}

	private readonly struct Hit
	{
		public readonly Vector3 Point;
		public readonly float DistanceA;
		public readonly float DistanceB;
		public readonly Vector2 DirectionA;
		public readonly Vector2 DirectionB;

		public Hit(Vector3 _Point, float _DistanceA, float _DistanceB, Vector2 _DirectionA, Vector2 _DirectionB)
		{
			Point = _Point;
			DistanceA = _DistanceA;
			DistanceB = _DistanceB;
			DirectionA = _DirectionA;
			DirectionB = _DirectionB;
		}
	}

	/// <summary>
	/// How far out along each arm (in road widths) corners are looked for; two arms leaving at a very shallow
	/// angle meet further out than this and are left to overlap.
	/// </summary>
	private const float ForwardReach = 4.0f;

	/// <summary>
	/// How far behind the node (in road widths) the arms are extended straight, to catch the outside corner of a
	/// sharp bend.
	/// </summary>
	private const float BackwardReach = 3.0f;

	private const float SampleStep = 30.0f;
	private const float ArcStepDegrees = 15.0f;

	/// <summary>
	/// The stretch of outline between two neighbouring arms: from arm A's row along its left edge to the corner,
	/// round the corner, and out along arm B's right edge to B's row. Borders follow these; the half up to
	/// <see cref="MidIndex"/> belongs to arm A, the rest to arm B.
	/// </summary>
	public sealed class CornerPath
	{
		public Arm ArmA;
		public Arm ArmB;
		public List<Vector3> Points = new();
		public int MidIndex;

		/// <summary>
		/// Range of points on the fillet arc proper (between the two tangent points); -1 when the corner is sharp.
		/// </summary>
		public int ArcFirst = -1;
		public int ArcLast = -1;

		/// <summary>
		/// Set when the fillet centre lies on the border's side, so borders wider than the radius need the block
		/// corner treatment along the arc.
		/// </summary>
		public CornerFold Fold;

		/// <summary>
		/// Away-from-road directions at the two rows, exactly as the arm chunks compute them, so the border pieces
		/// meet without a crack.
		/// </summary>
		public Vector3 StartOutward;
		public Vector3 EndOutward;
	}

	public RoadNode Node { get; }

	public List<Arm> Arms { get; } = new();

	/// <summary>
	/// The filled area, in lifted local space, counter-clockwise seen from above. Empty until <see cref="BuildPolygon"/>.
	/// </summary>
	public List<Vector3> Polygon { get; } = new();

	public List<CornerPath> CornerPaths { get; } = new();

	public Vector3 Centre { get; private set; }

	private readonly List<Corner> m_Corners = new();
	private readonly float m_Lift;



	private RoadJunctionLayout(RoadNode _Node, float _Lift)
	{
		Node = _Node;
		m_Lift = _Lift;
	}



	/// <summary>
	/// The layout of the node's junction, or null when there is nothing to fill: fewer than two usable arms, or
	/// arms running straight through each other (a smooth pass-through node).
	/// </summary>
	public static RoadJunctionLayout Create(RoadNetwork _Network, RoadNode _Node, float _Lift)
	{
		var layout = new RoadJunctionLayout(_Node, _Lift);

		foreach (var segment in _Network.GetSegmentsAt(_Node.Id))
		{
			var table = _Network.GetCurveTable(segment);

			if (table is null)
				continue;

			bool atStart = segment.StartNodeId == _Node.Id;
			Vector3 tangent = atStart ? table.Curve.Tangent(0.0f) : -table.Curve.Tangent(1.0f);
			var direction = new Vector2(tangent.x, tangent.y);

			// A vertical arm has no side to meet anything on.
			if (direction.Length < 0.001f)
				continue;

			layout.Arms.Add(new Arm
			{
				Segment = segment,
				Table = table,
				AtStart = atStart,
				Direction = direction.Normal,
				Width = segment.Width
			});
		}

		if (layout.Arms.Count < 2)
			return null;

		layout.Arms.Sort((a, b) => MathF.Atan2(a.Direction.y, a.Direction.x).CompareTo(MathF.Atan2(b.Direction.y, b.Direction.x)));

		float maxWidth = layout.Arms.Max(arm => arm.Width);

		foreach (var arm in layout.Arms)
			layout.BuildSamples(arm, maxWidth);

		layout.FindCorners();

		// Nothing was trimmed: the arms run straight into each other and the plain strips already join up.
		if (layout.Arms.All(arm => arm.RawTrim <= 0.01f))
			return null;

		layout.ResetTrims();

		return layout;
	}



	public Arm FindArm(int _SegmentId)
	{
		foreach (var arm in Arms)
		{
			if (arm.Segment.Id == _SegmentId)
				return arm;
		}

		return null;
	}



	/// <summary>
	/// Puts the trims back to what the corners asked for, before the network gives short roads some back.
	/// </summary>
	public void ResetTrims()
	{
		foreach (var arm in Arms)
			arm.Trim = arm.RawTrim;
	}



	private void BuildSamples(Arm _Arm, float _MaxWidth)
	{
		float forward = MathF.Min(_Arm.Length, ForwardReach * _MaxWidth);
		float backward = BackwardReach * _MaxWidth;
		int forwardCount = Math.Max(1, (int)MathF.Ceiling(forward / SampleStep));
		int backwardCount = Math.Max(1, (int)MathF.Ceiling(backward / SampleStep));
		int total = backwardCount + forwardCount + 1;
		float halfWidth = _Arm.Width * 0.5f;

		_Arm.Distances = new float[total];
		_Arm.Centres = new Vector3[total];
		_Arm.Lefts = new Vector3[total];
		_Arm.NodeIndex = backwardCount;

		for (int k = 0; k <= forwardCount; k++)
		{
			float distance = k == forwardCount ? forward : forward * k / forwardCount;
			int index = backwardCount + k;

			RoadSurfaceBuilder.CrossSection(_Arm.Table, _Arm.CurveDistance(distance), halfWidth, m_Lift, out Vector3 curveLeft, out Vector3 curveRight);

			// Looking out of the node, an arm that ends here runs the other way, so its sides swap.
			Vector3 left = _Arm.AtStart ? curveLeft : curveRight;
			Vector3 right = _Arm.AtStart ? curveRight : curveLeft;

			_Arm.Distances[index] = distance;
			_Arm.Centres[index] = (left + right) * 0.5f;
			_Arm.Lefts[index] = (left - right).Normal;
		}

		// Behind the node the road does not exist; carry its frame at the node straight backwards.
		var direction = new Vector3(_Arm.Direction.x, _Arm.Direction.y, 0.0f);

		for (int k = 1; k <= backwardCount; k++)
		{
			float distance = -backward * k / backwardCount;
			int index = backwardCount - k;

			_Arm.Distances[index] = distance;
			_Arm.Centres[index] = _Arm.Centres[backwardCount] + direction * distance;
			_Arm.Lefts[index] = _Arm.Lefts[backwardCount];
		}
	}



	/// <summary>
	/// Meets each arm's left edge with the right edge of the next arm round, rounds the corner off and pushes the
	/// arm trims out past it.
	/// </summary>
	private void FindCorners()
	{
		int count = Arms.Count;

		for (int i = 0; i < count; i++)
		{
			int j = (i + 1) % count;
			var a = Arms[i];
			var b = Arms[j];
			float halfA = a.Width * 0.5f;
			float halfB = b.Width * 0.5f;

			if (!Intersect(a, halfA, b, -halfB, out Hit hit))
				continue;

			var corner = new Corner
			{
				ArmA = i,
				ArmB = j,
				Point = hit.Point,
				ParamA = hit.DistanceA,
				ParamB = hit.DistanceB
			};

			TryFillet(corner, a, b, hit);
			m_Corners.Add(corner);

			a.RawTrim = MathF.Max(a.RawTrim, corner.EndA);
			b.RawTrim = MathF.Max(b.RawTrim, corner.EndB);
		}
	}



	/// <summary>
	/// A circle of the node's radius tangent to both edges: its centre is where the edges, each pushed sideways by
	/// the radius (towards the angle between them), cross; the tangent points are the closest points of the edges
	/// to it. Works the same whether the corner is the inside of a junction or the outside of a bend.
	/// </summary>
	private void TryFillet(Corner _Corner, Arm _A, Arm _B, in Hit _Hit)
	{
		float radius = MathF.Max(0.0f, Node.CornerRadius);

		if (radius <= 0.0f)
			return;

		float angle = MathF.Acos(Math.Clamp(Vector2.Dot(_Hit.DirectionA, _Hit.DirectionB), -1.0f, 1.0f)).RadianToDegree();

		// Nearly straight through, or folded back on itself: nothing sensible to round.
		if (angle < 5.0f || angle > 175.0f)
			return;

		Vector2 bisector = (_Hit.DirectionA + _Hit.DirectionB).Normal;
		var leftNormalA = new Vector2(-_Hit.DirectionA.y, _Hit.DirectionA.x);
		var rightNormalB = new Vector2(_Hit.DirectionB.y, -_Hit.DirectionB.x);
		float signA = Vector2.Dot(bisector, leftNormalA) >= 0.0f ? 1.0f : -1.0f;
		float signB = Vector2.Dot(bisector, rightNormalB) >= 0.0f ? 1.0f : -1.0f;
		float halfA = _A.Width * 0.5f;
		float halfB = _B.Width * 0.5f;

		if (!Intersect(_A, halfA + signA * radius, _B, -(halfB + signB * radius), out Hit centre))
			return;

		var centre2 = new Vector2(centre.Point.x, centre.Point.y);

		ClosestPoint(_A, halfA, centre2, out Vector3 tangentA, out float paramA);
		ClosestPoint(_B, -halfB, centre2, out Vector3 tangentB, out float paramB);

		_Corner.HasFillet = true;
		_Corner.Centre = centre2;
		_Corner.Radius = radius;
		_Corner.TangentA = tangentA;
		_Corner.TangentB = tangentB;
		_Corner.TangentParamA = paramA;
		_Corner.TangentParamB = paramB;
		_Corner.Bisector = bisector;
		_Corner.HalfAngleSin = MathF.Max(0.01f, MathF.Sin(angle.DegreeToRadian() * 0.5f));
	}



	/// <summary>
	/// First crossing of two arms' offset polylines, scanning outwards from behind the node so the crossing nearest
	/// the node wins. Two edges that start on the same point are a road running straight through: they meet at the
	/// node and nothing needs rounding.
	/// </summary>
	private static bool Intersect(Arm _A, float _OffsetA, Arm _B, float _OffsetB, out Hit _Hit)
	{
		var polyA = _A.OffsetPolyline(_OffsetA);
		var polyB = _B.OffsetPolyline(_OffsetB);

		Vector3 startA = polyA[_A.NodeIndex];
		Vector3 startB = polyB[_B.NodeIndex];

		if (Flat(startA).Distance(Flat(startB)) < 1.0f)
		{
			_Hit = new Hit((startA + startB) * 0.5f, 0.0f, 0.0f, SegmentDirection(polyA, _A.NodeIndex), SegmentDirection(polyB, _B.NodeIndex));
			return true;
		}

		for (int i = 0; i < polyA.Length - 1; i++)
		{
			Vector2 p = Flat(polyA[i]);
			Vector2 r = Flat(polyA[i + 1]) - p;

			for (int j = 0; j < polyB.Length - 1; j++)
			{
				Vector2 q = Flat(polyB[j]);
				Vector2 s = Flat(polyB[j + 1]) - q;
				float denominator = Cross(r, s);

				if (MathF.Abs(denominator) < 0.000001f)
					continue;

				Vector2 offset = q - p;
				float t = Cross(offset, s) / denominator;
				float u = Cross(offset, r) / denominator;

				if (t < -0.0001f || t > 1.0001f || u < -0.0001f || u > 1.0001f)
					continue;

				Vector3 pointA = Vector3.Lerp(polyA[i], polyA[i + 1], t);
				Vector3 pointB = Vector3.Lerp(polyB[j], polyB[j + 1], u);

				_Hit = new Hit((pointA + pointB) * 0.5f,
					MathX.Lerp(_A.Distances[i], _A.Distances[i + 1], t),
					MathX.Lerp(_B.Distances[j], _B.Distances[j + 1], u),
					r.Normal, s.Normal);

				return true;
			}
		}

		_Hit = default;
		return false;
	}



	/// <summary>
	/// The point of an arm's offset polyline nearest to a point, and its distance from the node.
	/// </summary>
	private static void ClosestPoint(Arm _Arm, float _Offset, Vector2 _Target, out Vector3 _Point, out float _Distance)
	{
		var poly = _Arm.OffsetPolyline(_Offset);
		float best = float.MaxValue;

		_Point = poly[_Arm.NodeIndex];
		_Distance = 0.0f;

		for (int i = 0; i < poly.Length - 1; i++)
		{
			Vector2 p = Flat(poly[i]);
			Vector2 segment = Flat(poly[i + 1]) - p;
			float lengthSquared = segment.LengthSquared;

			if (lengthSquared <= 0.0f)
				continue;

			float t = Math.Clamp(Vector2.Dot(_Target - p, segment) / lengthSquared, 0.0f, 1.0f);
			float distance = (p + segment * t).Distance(_Target);

			if (distance >= best)
				continue;

			best = distance;
			_Point = Vector3.Lerp(poly[i], poly[i + 1], t);
			_Distance = MathX.Lerp(_Arm.Distances[i], _Arm.Distances[i + 1], t);
		}
	}



	private static Vector2 SegmentDirection(Vector3[] _Poly, int _Index)
	{
		int from = Math.Min(_Index, _Poly.Length - 2);

		return (Flat(_Poly[from + 1]) - Flat(_Poly[from])).Normal;
	}



	/// <summary>
	/// Builds the outline once the arm trims are final: round the node counter-clockwise, each arm contributes the
	/// row its mesh starts with, then its left edge back to the corner it shares with the next arm, the corner (or
	/// its fillet arc), and the next arm's right edge out to that arm's row.
	/// </summary>
	public void BuildPolygon()
	{
		Polygon.Clear();
		CornerPaths.Clear();
		Centre = Node.Position + Vector3.Up * m_Lift;

		for (int i = 0; i < Arms.Count; i++)
		{
			var arm = Arms[i];

			RowPoints(arm, out Vector3 rowRight, out Vector3 rowLeft, out _);
			AddVertex(Polygon, rowRight);

			var corner = m_Corners.Find(candidate => candidate.ArmA == i);

			// No corner: nothing joins this side to the next arm, the outline just cuts across to its row.
			if (corner is null)
			{
				AddVertex(Polygon, rowLeft);
				continue;
			}

			var path = BuildCornerPath(corner, arm, Arms[corner.ArmB]);
			CornerPaths.Add(path);

			// The path ends on the next arm's row, which the next arm adds itself.
			for (int k = 0; k < path.Points.Count - 1; k++)
				AddVertex(Polygon, path.Points[k]);
		}

		if (Polygon.Count > 1 && Flat(Polygon[^1]).Distance(Flat(Polygon[0])) < 0.5f)
			Polygon.RemoveAt(Polygon.Count - 1);
	}



	/// <summary>
	/// The row where an arm's mesh starts, as seen looking out of the node, plus the arm's left direction there.
	/// </summary>
	private void RowPoints(Arm _Arm, out Vector3 _Right, out Vector3 _Left, out Vector3 _LeftDirection)
	{
		RoadSurfaceBuilder.CrossSection(_Arm.Table, _Arm.CurveDistance(_Arm.Trim), _Arm.Width * 0.5f, m_Lift, out Vector3 curveLeft, out Vector3 curveRight);

		_Left = _Arm.AtStart ? curveLeft : curveRight;
		_Right = _Arm.AtStart ? curveRight : curveLeft;
		_LeftDirection = (_Left - _Right).Normal;
	}



	private CornerPath BuildCornerPath(Corner _Corner, Arm _A, Arm _B)
	{
		var path = new CornerPath { ArmA = _A, ArmB = _B };
		var points = path.Points;

		RowPoints(_A, out _, out Vector3 rowLeftA, out Vector3 leftDirectionA);
		RowPoints(_B, out Vector3 rowRightB, out _, out Vector3 leftDirectionB);

		path.StartOutward = leftDirectionA;
		path.EndOutward = -leftDirectionB;

		AddVertex(points, rowLeftA);

		// Down A's left edge from its row to the corner...
		for (int k = _A.SampleCount - 1; k >= 0; k--)
		{
			if (_A.Distances[k] < _A.Trim && _A.Distances[k] > _Corner.EndA)
				AddVertex(points, _A.EdgePoint(k, _A.Width * 0.5f));
		}

		path.MidIndex = AddCorner(points, _Corner, out path.ArcFirst, out path.ArcLast);

		// ...and out along B's right edge to its row.
		for (int k = 0; k < _B.SampleCount; k++)
		{
			if (_B.Distances[k] > _Corner.EndB && _B.Distances[k] < _B.Trim)
				AddVertex(points, _B.EdgePoint(k, -_B.Width * 0.5f));
		}

		AddVertex(points, rowRightB);

		// The border runs on the right of the outline. When that is the side the fillet centre is on, the
		// border wraps the inside of the corner and can fold; note what it needs to square the corner off.
		if (_Corner.HasFillet && path.ArcFirst >= 0 && path.MidIndex > 0 && path.MidIndex < points.Count - 1)
		{
			Vector3 mid = points[path.MidIndex];
			Vector3 direction = points[path.MidIndex + 1] - points[path.MidIndex - 1];
			var outward = new Vector2(direction.y, -direction.x);
			var toCentre = _Corner.Centre - new Vector2(mid.x, mid.y);

			if (Vector2.Dot(outward, toCentre) > 0.0f)
			{
				path.Fold = new CornerFold
				{
					Apex = new Vector2(_Corner.Point.x, _Corner.Point.y),
					Bisector = _Corner.Bisector,
					HalfAngleSin = _Corner.HalfAngleSin,
					Radius = _Corner.Radius
				};
			}
		}

		return path;
	}



	/// <summary>
	/// Adds the corner itself, or its fillet arc, and returns the index of its middle: where one arm's border
	/// hands over to the other's.
	/// </summary>
	private static int AddCorner(List<Vector3> _Points, Corner _Corner, out int _ArcFirst, out int _ArcLast)
	{
		_ArcFirst = -1;
		_ArcLast = -1;

		if (!_Corner.HasFillet)
			return AddVertex(_Points, _Corner.Point);

		AddVertex(_Points, _Corner.TangentA);

		float angle0 = MathF.Atan2(_Corner.TangentA.y - _Corner.Centre.y, _Corner.TangentA.x - _Corner.Centre.x);
		float angle1 = MathF.Atan2(_Corner.TangentB.y - _Corner.Centre.y, _Corner.TangentB.x - _Corner.Centre.x);
		float delta = angle1 - angle0;

		// Shortest way round: a fillet arc is always less than a half turn.
		if (delta > MathF.PI)
			delta -= MathF.PI * 2.0f;
		else if (delta < -MathF.PI)
			delta += MathF.PI * 2.0f;

		// An even number of steps puts a point exactly halfway round.
		int steps = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(delta) / ArcStepDegrees.DegreeToRadian()));
		steps += steps % 2;
		int midIndex = -1;

		for (int k = 1; k < steps; k++)
		{
			float fraction = (float)k / steps;
			float angle = angle0 + delta * fraction;
			var point = _Corner.Centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * _Corner.Radius;
			int index = AddVertex(_Points, new Vector3(point.x, point.y, MathX.Lerp(_Corner.TangentA.z, _Corner.TangentB.z, fraction)));

			if (_ArcFirst < 0)
				_ArcFirst = index;

			_ArcLast = index;

			if (k == steps / 2)
				midIndex = index;
		}

		AddVertex(_Points, _Corner.TangentB);

		return midIndex >= 0 ? midIndex : _Points.Count - 1;
	}



	/// <summary>
	/// Appends a vertex unless it sits on the previous one (a corner exactly on the row it trimmed would make a
	/// degenerate triangle), and returns the index it ended up at.
	/// </summary>
	private static int AddVertex(List<Vector3> _Points, Vector3 _Vertex)
	{
		if (_Points.Count > 0 && Flat(_Points[^1]).Distance(Flat(_Vertex)) < 0.5f)
			return _Points.Count - 1;

		_Points.Add(_Vertex);

		return _Points.Count - 1;
	}



	private static Vector2 Flat(Vector3 _Vector)
	{
		return new Vector2(_Vector.x, _Vector.y);
	}



	private static float Cross(Vector2 _A, Vector2 _B)
	{
		return _A.x * _B.y - _A.y * _B.x;
	}
}
