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

		/// <summary>
		/// The road's edges along the segment; which of them is the arm's left depends on which way the segment runs.
		/// </summary>
		public RoadWidth Widths;

		/// <summary>
		/// The trim the corners ask for; <see cref="Trim"/> is this unless a short road had to give some back.
		/// <see cref="SharpTrim"/> is what the corners would ask for with no fillet at all: the least an arm can be
		/// trimmed and still stop at its corners.
		/// </summary>
		public float RawTrim;
		public float SharpTrim;

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
		/// How far out from the centre each sample's left and right edges sit (looking out of the node).
		/// </summary>
		public float[] LeftEdges;
		public float[] RightEdges;

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
		/// (negative for the right).
		/// </summary>
		public Vector3 EdgePoint(int _Index, float _Offset)
		{
			return Centres[_Index] + Lefts[_Index] * _Offset;
		}

		/// <summary>
		/// How far out from the centre the edge on one side (+1 left, -1 right, looking out of the node) sits at a
		/// distance from the node; the node's own value behind it, the far end's past it.
		/// </summary>
		public float Edge(float _Side, float _FromNode)
		{
			float curveDistance = CurveDistance(Math.Clamp(_FromNode, 0.0f, Length));
			bool left = _Side > 0.0f == AtStart;

			return left ? Widths.LeftAt(curveDistance) : Widths.RightAt(curveDistance);
		}

		/// <summary>
		/// The signed offset (left positive) of an edge line at a distance from the node.
		/// </summary>
		public float OffsetAt(in EdgeRef _Edge, float _FromNode)
		{
			return _Edge.Side * (Edge(_Edge.Side, _FromNode) + _Edge.Extra);
		}

		public float OffsetAt(in EdgeRef _Edge, int _Index)
		{
			return _Edge.Side * ((_Edge.Side > 0.0f ? LeftEdges[_Index] : RightEdges[_Index]) + _Edge.Extra);
		}

		public Vector3[] OffsetPolyline(in EdgeRef _Edge)
		{
			var points = new Vector3[SampleCount];

			for (int i = 0; i < SampleCount; i++)
				points[i] = EdgePoint(i, OffsetAt(_Edge, i));

			return points;
		}
	}

	/// <summary>
	/// A line running along one of an arm's edges (+1 its left, -1 its right, looking out of the node), pushed
	/// outward from that edge by <see cref="Extra"/> (inward when negative). Edges move in and out along a road
	/// with extra width, so a line is named by the edge it follows rather than by a fixed offset.
	/// </summary>
	public readonly record struct EdgeRef(float Side, float Extra)
	{
		public static readonly EdgeRef Left = new(1.0f, 0.0f);
		public static readonly EdgeRef Right = new(-1.0f, 0.0f);
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
		/// Directions the two edges leave the corner point along, the angle between them, the unit direction from
		/// the corner point into that angle (towards a fillet centre), and the sine of half the angle: together
		/// they place where lines offset from the two road edges meet.
		/// </summary>
		public Vector2 DirectionA;
		public Vector2 DirectionB;
		public Vector2 Bisector;
		public float HalfAngleSin;
		public float Angle;

		/// <summary>
		/// Which way each edge is pushed sideways to move into the corner's angle: +1 along arm A's left / arm B's
		/// right (the border side of an inside corner), -1 the other way (a convex corner, whose fillet sits on the
		/// road).
		/// </summary>
		public float SignA = 1.0f;
		public float SignB = 1.0f;

		/// <summary>
		/// The corner is the tip between two roads diverging from one point on the node: a ramp's nose.
		/// </summary>
		public bool IsNose;

		public float EndA => HasFillet ? TangentParamA : ParamA;

		public float EndB => HasFillet ? TangentParamB : ParamB;
	}

	/// <summary>
	/// How a border wraps a corner it lies on the inside of (the inside corner of a junction, or the inside of a
	/// bend). The pavement there is two regions: a pie between the kerb arc and the fillet centre, whose rows all
	/// end at the centre, and beyond the centre the flat block corner: the square between the two straight outer
	/// edges, which meet at the mitre point for their offset. Offsetting rows past the centre instead would send
	/// them through it and across each other. A sharp corner is the same with a zero radius: its pie is nothing.
	///
	/// The centre is one point in plan whatever the road's slope, so it carries one height, halfway between the
	/// two tangent points; the difference to each arm's real height is worked off along the whole arm (see the
	/// border builder's side blends), so a sloping road's pavement tilts gently into the corner instead of stepping.
	/// </summary>
	public sealed class CornerFold
	{
		public Vector2 Apex;
		public Vector2 Centre;
		public Vector2 Bisector;
		public float HalfAngleSin;
		public float Radius;

		/// <summary>
		/// The two arms and which way each edge is pushed into the corner, so the outer corner of a border can be
		/// found on the arms' real edges the same way the fillet centre was.
		/// </summary>
		public RoadJunctionLayout Layout;
		public Arm ArmA;
		public Arm ArmB;
		public float SignA = 1.0f;
		public float SignB = 1.0f;

		/// <summary>
		/// Beyond this many offsets from the corner point the mitre is cut off: an acute corner's block corner
		/// would otherwise run out to a needle.
		/// </summary>
		private const float MitreLimit = 3.0f;

		/// <summary>
		/// Where the tangent points sit along each arm (distance from the node), the road-edge height there, and
		/// the one height the centre takes.
		/// </summary>
		public float TangentParamA;
		public float TangentParamB;
		public float TangentHeightA;
		public float TangentHeightB;
		public float ApexHeight;

		private readonly Dictionary<float, Vector2> m_Mitres = new();

		/// <summary>
		/// Where the two road edges, each pushed out by <paramref name="_Offset"/>, cross: the outer corner of a
		/// border that wide, bevelled once the corner is too acute for a point. Found on the arms' sampled edges,
		/// exactly like the fillet centre (which this returns for an offset equal to the radius); the straight-line
		/// formula is only a fallback, since on a curved arm it lands tens of units off.
		/// </summary>
		public Vector2 MitrePoint(float _Offset)
		{
			if (m_Mitres.TryGetValue(_Offset, out var cached))
				return cached;

			Vector2 point;

			var edgeA = new EdgeRef(1.0f, SignA * _Offset);
			var edgeB = new EdgeRef(-1.0f, SignB * _Offset);

			if (ArmA is not null && ArmB is not null && Intersect(ArmA, edgeA, ArmB, edgeB, out Hit hit))
			{
				Layout?.RefineCrossing(ArmA, edgeA, ArmB, edgeB, ref hit);
				point = Flat(hit.Point);
			}
			else
			{
				point = Apex + Bisector * (_Offset / HalfAngleSin);
			}

			// The limit applies to the part beyond the centre, so the mitre never falls back inside the pie.
			float beyond = MathF.Max(0.0f, _Offset - Radius);
			float limit = Centre.Distance(Apex) + beyond * MitreLimit;
			Vector2 fromApex = point - Apex;
			float distance = fromApex.Length;

			if (distance > limit && distance > 0.001f)
				point = Apex + fromApex / distance * limit;

			m_Mitres[_Offset] = point;

			return point;
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
	/// The most a nose (the tip between a ramp and the road it leaves) is rounded by: a street corner's radius
	/// there would reach hundreds of units down both roads.
	/// </summary>
	private const float NoseRadius = 15.0f;

	/// <summary>
	/// How far past a sharp corner's point the arm rows sit; a little more than the path's merge distance.
	/// </summary>
	private const float SharpMargin = 3.0f;

	/// <summary>
	/// The stretch of outline between two neighbouring arms: from arm A's row along its left edge to the corner,
	/// round the corner, and out along arm B's right edge to B's row. Borders follow these; the half up to
	/// <see cref="MidIndex"/> belongs to arm A, the rest to arm B.
	/// </summary>
	public sealed class CornerPath
	{
		/// <summary>
		/// Points closer than this are one point: the tangent point on a sampled edge lands a hair off the row
		/// the curve puts there, and must not become a sliver of its own.
		/// </summary>
		private const float MergeDistance = 1.5f;

		public Arm ArmA;
		public Arm ArmB;
		public List<Vector3> Points = new();

		/// <summary>
		/// Away-from-road direction at each point, from the frame it was taken from (zero where the border has to
		/// work one out from the neighbours).
		/// </summary>
		public List<Vector3> Outwards = new();

		public int MidIndex;

		/// <summary>
		/// Appends a point unless it sits on the previous one, and returns the index it ended up at.
		/// </summary>
		public int Add(Vector3 _Point, Vector3 _Outward)
		{
			if (Points.Count > 0)
			{
				var last = Points[^1];

				if (new Vector2(last.x, last.y).Distance(new Vector2(_Point.x, _Point.y)) < MergeDistance)
					return Points.Count - 1;
			}

			Points.Add(_Point);
			Outwards.Add(_Outward);

			return Points.Count - 1;
		}

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

	public RoadNode Node { get; private set; }

	/// <summary>
	/// The fillet radius the corners were built with: the node's, unless a short road forced it smaller.
	/// </summary>
	public float Radius { get; }

	public List<Arm> Arms { get; } = new();

	/// <summary>
	/// The filled area, in lifted local space, counter-clockwise seen from above. Empty until <see cref="BuildPolygon"/>.
	/// </summary>
	public List<Vector3> Polygon { get; } = new();

	public List<CornerPath> CornerPaths { get; } = new();

	public Vector3 Centre { get; private set; }

	private readonly List<Corner> m_Corners = new();
	private readonly float m_Lift;



	private RoadJunctionLayout(RoadNode _Node, float _Lift, float _Radius)
	{
		Node = _Node;
		m_Lift = _Lift;
		Radius = _Radius;
	}



	/// <summary>
	/// The layout of the node's junction, or null when there is nothing to fill: fewer than two usable arms, or
	/// arms running straight through each other (a smooth pass-through node).
	/// </summary>
	public static RoadJunctionLayout Create(RoadNetwork _Network, RoadNode _Node, float _Lift, float _Radius)
	{
		var layout = new RoadJunctionLayout(_Node, _Lift, MathF.Max(0.0f, _Radius));

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
				Widths = new RoadWidth(segment, table.Length)
			});
		}

		if (layout.Arms.Count < 2)
			return null;

		layout.Arms.Sort((a, b) => MathF.Atan2(a.Direction.y, a.Direction.x).CompareTo(MathF.Atan2(b.Direction.y, b.Direction.x)));

		float maxWidth = layout.Arms.Max(arm => arm.Widths.MaxHalf * 2.0f);

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
	/// Points a cached layout at the network's current node and segment objects. Undo and reloads replace those
	/// objects while the geometry (what the layout is keyed on) stays the same; anything the layout is asked about
	/// afterwards, like a road's profile, has to come from the live objects. False if any of them is gone.
	/// </summary>
	public bool Refresh(RoadNetwork _Network)
	{
		var node = _Network.FindNode(Node.Id);

		if (node is null)
			return false;

		Node = node;

		foreach (var arm in Arms)
		{
			var segment = _Network.FindSegment(arm.Segment.Id);

			if (segment is null)
				return false;

			arm.Segment = segment;
		}

		return true;
	}



	/// <summary>
	/// Writes everything the layout worked out (arms, corners, outline paths) into the debug record.
	/// </summary>
	public void Describe(BorderDebug _Debug)
	{
		for (int i = 0; i < Arms.Count; i++)
		{
			var arm = Arms[i];

			_Debug.Note($"ARM {i} segment={arm.Segment.Id} atStart={(arm.AtStart ? 1 : 0)} direction={BorderDebug.Format(arm.Direction)} width={arm.Widths.Base} edgesAtNode={arm.Edge(1.0f, 0.0f):0.#}/{arm.Edge(-1.0f, 0.0f):0.#} length={arm.Length:0.###} rawTrim={arm.RawTrim:0.###} sharpTrim={arm.SharpTrim:0.###} trim={arm.Trim:0.###} samples={arm.SampleCount} nodeIndex={arm.NodeIndex} firstDistance={arm.Distances[0]:0.###} lastDistance={arm.Distances[^1]:0.###}");
		}

		foreach (var corner in m_Corners)
		{
			_Debug.Note($"CORNER armA={corner.ArmA} armB={corner.ArmB} point={BorderDebug.Format(corner.Point)} paramA={corner.ParamA:0.###} paramB={corner.ParamB:0.###} angle={corner.Angle:0.###} directionA={BorderDebug.Format(corner.DirectionA)} directionB={BorderDebug.Format(corner.DirectionB)} bisector={BorderDebug.Format(corner.Bisector)} halfSin={corner.HalfAngleSin:0.####} signA={corner.SignA} signB={corner.SignB} fillet={(corner.HasFillet ? 1 : 0)} centre={BorderDebug.Format(corner.Centre)} radius={corner.Radius:0.###} tangentA={BorderDebug.Format(corner.TangentA)} tangentB={BorderDebug.Format(corner.TangentB)} tangentParamA={corner.TangentParamA:0.###} tangentParamB={corner.TangentParamB:0.###} endA={corner.EndA:0.###} endB={corner.EndB:0.###}");
		}

		foreach (var path in CornerPaths)
		{
			_Debug.Note($"PATH armA={path.ArmA.Segment.Id} armB={path.ArmB.Segment.Id} points={path.Points.Count} mid={path.MidIndex} arcFirst={path.ArcFirst} arcLast={path.ArcLast} startOutward={BorderDebug.Format(path.StartOutward)} endOutward={BorderDebug.Format(path.EndOutward)}");

			if (path.Fold is { } fold)
				_Debug.Note($"FOLD apex={BorderDebug.Format(fold.Apex)} centre={BorderDebug.Format(fold.Centre)} bisector={BorderDebug.Format(fold.Bisector)} halfSin={fold.HalfAngleSin:0.####} radius={fold.Radius:0.###} tangentParamA={fold.TangentParamA:0.###} tangentParamB={fold.TangentParamB:0.###} tangentHeightA={fold.TangentHeightA:0.###} tangentHeightB={fold.TangentHeightB:0.###} apexHeight={fold.ApexHeight:0.###}");

			for (int i = 0; i < path.Points.Count; i++)
				_Debug.Note($"P {i} {BorderDebug.Format(path.Points[i])} out={BorderDebug.Format(path.Outwards[i])}");
		}

		_Debug.Note($"POLYGON vertices={Polygon.Count}");

		for (int i = 0; i < Polygon.Count; i++)
			_Debug.Note($"V {i} {BorderDebug.Format(Polygon[i])}");
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

		_Arm.Distances = new float[total];
		_Arm.Centres = new Vector3[total];
		_Arm.Lefts = new Vector3[total];
		_Arm.LeftEdges = new float[total];
		_Arm.RightEdges = new float[total];
		_Arm.NodeIndex = backwardCount;

		for (int k = 0; k <= forwardCount; k++)
		{
			float distance = k == forwardCount ? forward : forward * k / forwardCount;
			int index = backwardCount + k;

			ArmFrame(_Arm, distance, out _Arm.Centres[index], out _Arm.Lefts[index]);

			_Arm.Distances[index] = distance;
			_Arm.LeftEdges[index] = _Arm.Edge(1.0f, distance);
			_Arm.RightEdges[index] = _Arm.Edge(-1.0f, distance);
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
			_Arm.LeftEdges[index] = _Arm.LeftEdges[backwardCount];
			_Arm.RightEdges[index] = _Arm.RightEdges[backwardCount];
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

			if (!Intersect(a, EdgeRef.Left, b, EdgeRef.Right, out Hit hit))
				continue;

			// Two edges leaving one point on the node (a ramp peeling off the side of a road) diverge at a nose,
			// which is rounded far more tightly than a street corner, if at all.
			bool nose = hit.DistanceA < 0.5f && hit.DistanceB < 0.5f;

			RefineCrossing(a, EdgeRef.Left, b, EdgeRef.Right, ref hit);

			// The angle between the edges where they leave the corner, and the way into it: used by the fillet, and
			// by borders squaring off a corner that is sharp or tighter than they are wide.
			float angle = MathF.Acos(Math.Clamp(Vector2.Dot(hit.DirectionA, hit.DirectionB), -1.0f, 1.0f)).RadianToDegree();

			var bisector = (hit.DirectionA + hit.DirectionB).Normal;
			var leftNormalA = new Vector2(-hit.DirectionA.y, hit.DirectionA.x);
			var rightNormalB = new Vector2(hit.DirectionB.y, -hit.DirectionB.x);

			var corner = new Corner
			{
				ArmA = i,
				ArmB = j,
				Point = hit.Point,
				ParamA = hit.DistanceA,
				ParamB = hit.DistanceB,
				DirectionA = hit.DirectionA,
				DirectionB = hit.DirectionB,
				Bisector = bisector,
				HalfAngleSin = MathF.Max(0.01f, MathF.Sin(angle.DegreeToRadian() * 0.5f)),
				Angle = angle,
				SignA = Vector2.Dot(bisector, leftNormalA) >= 0.0f ? 1.0f : -1.0f,
				SignB = Vector2.Dot(bisector, rightNormalB) >= 0.0f ? 1.0f : -1.0f,
				IsNose = nose && angle < 90.0f
			};

			TryFillet(corner, a, b);
			m_Corners.Add(corner);

			// A sharp corner keeps the rows a hair past its point, so the stretch from row to corner exists as a
			// (tiny) run of its own: that is where the border squares the corner off from.
			float margin = !corner.HasFillet && angle >= 5.0f && angle <= 175.0f ? SharpMargin : 0.0f;

			a.RawTrim = MathF.Max(a.RawTrim, corner.EndA + margin);
			b.RawTrim = MathF.Max(b.RawTrim, corner.EndB + margin);
			a.SharpTrim = MathF.Max(a.SharpTrim, corner.ParamA);
			b.SharpTrim = MathF.Max(b.SharpTrim, corner.ParamB);
		}
	}



	/// <summary>
	/// A circle of the node's radius tangent to both edges: its centre is where the edges, each pushed sideways by
	/// the radius (towards the angle between them), cross; the tangent points are the closest points of the edges
	/// to it. Works the same whether the corner is the inside of a junction or the outside of a bend.
	/// </summary>
	private void TryFillet(Corner _Corner, Arm _A, Arm _B)
	{
		float radius = _Corner.IsNose ? MathF.Min(Radius, NoseRadius) : Radius;

		if (radius <= 0.0f)
			return;

		// Nearly straight through, or folded back on itself: nothing sensible to round.
		if (_Corner.Angle < 5.0f || _Corner.Angle > 175.0f)
			return;

		var pushedA = new EdgeRef(1.0f, _Corner.SignA * radius);
		var pushedB = new EdgeRef(-1.0f, _Corner.SignB * radius);

		if (!Intersect(_A, pushedA, _B, pushedB, out Hit centre))
			return;

		var centre2 = new Vector2(centre.Point.x, centre.Point.y);
		Vector3 tangentA;
		Vector3 tangentB;
		float paramA;
		float paramB;

		// On the curves, the centre is exactly a radius from each edge at the crossing's own distances, so those
		// are the tangent points: the row there reaches the centre exactly. Off the polylines, look for them.
		if (RefineCrossing(_A, pushedA, _B, pushedB, ref centre))
		{
			centre2 = new Vector2(centre.Point.x, centre.Point.y);
			paramA = centre.DistanceA;
			paramB = centre.DistanceB;

			ArmFrame(_A, paramA, out Vector3 centreA, out Vector3 leftA);
			ArmFrame(_B, paramB, out Vector3 centreB, out Vector3 leftB);

			tangentA = centreA + leftA * _A.OffsetAt(EdgeRef.Left, paramA);
			tangentB = centreB + leftB * _B.OffsetAt(EdgeRef.Right, paramB);
		}
		else
		{
			ClosestPoint(_A, EdgeRef.Left, centre2, out tangentA, out paramA);
			ClosestPoint(_B, EdgeRef.Right, centre2, out tangentB, out paramB);
		}

		_Corner.HasFillet = true;
		_Corner.Centre = centre2;
		_Corner.Radius = radius;
		_Corner.TangentA = tangentA;
		_Corner.TangentB = tangentB;
		_Corner.TangentParamA = paramA;
		_Corner.TangentParamB = paramB;
	}



	/// <summary>
	/// The centreline point and left direction of an arm at a distance from the node along it, straight on behind
	/// the node and past the arm's end, computed exactly as the arm chunks compute their rows.
	/// </summary>
	private void ArmFrame(Arm _Arm, float _FromNode, out Vector3 _Centre, out Vector3 _Left)
	{
		if (_FromNode < 0.0f)
		{
			_Centre = _Arm.Centres[_Arm.NodeIndex] + new Vector3(_Arm.Direction.x, _Arm.Direction.y, 0.0f) * _FromNode;
			_Left = _Arm.Lefts[_Arm.NodeIndex];
			return;
		}

		float onCurve = MathF.Min(_FromNode, _Arm.Length);
		float curveDistance = _Arm.CurveDistance(onCurve);
		Vector3 right = RoadCurve.RightOf(_Arm.Table.TangentAt(curveDistance));

		// The centreline itself: the edges are measured out from it, each its own distance. Looking out of the
		// node, an arm that ends here runs the other way, so its sides swap.
		_Centre = _Arm.Table.PositionAt(curveDistance) + Vector3.Up * m_Lift;
		_Left = _Arm.AtStart ? -right : right;

		if (_FromNode > _Arm.Length)
			_Centre += Vector3.Cross(_Left, Vector3.Up) * (_FromNode - _Arm.Length);
	}



	private Vector2 OffsetPoint(Arm _Arm, float _FromNode, in EdgeRef _Edge)
	{
		ArmFrame(_Arm, _FromNode, out Vector3 centre, out Vector3 left);

		return Flat(centre + left * _Arm.OffsetAt(_Edge, _FromNode));
	}



	/// <summary>
	/// Moves a crossing found on the sampled polylines onto the curves themselves (Newton on the two distances
	/// along the arms), so corner points, fillet centres and mitres are exact rather than a sample step off. On a
	/// curved arm the polyline answer is a few units out, which is enough for the border pieces meeting at a
	/// corner not to close. False, with the hit untouched, when it does not converge: parallel offsets, or a bend
	/// so tight the offset curve has a cusp.
	/// </summary>
	private bool RefineCrossing(Arm _A, in EdgeRef _OffsetA, Arm _B, in EdgeRef _OffsetB, ref Hit _Hit)
	{
		const float step = 0.5f;
		const float maxStep = 200.0f;

		float distanceA = _Hit.DistanceA;
		float distanceB = _Hit.DistanceB;

		for (int iteration = 0; iteration < 10; iteration++)
		{
			Vector2 error = OffsetPoint(_A, distanceA, _OffsetA) - OffsetPoint(_B, distanceB, _OffsetB);

			if (error.Length < 0.002f)
				break;

			Vector2 slopeA = (OffsetPoint(_A, distanceA + step, _OffsetA) - OffsetPoint(_A, distanceA - step, _OffsetA)) / (2.0f * step);
			Vector2 slopeB = (OffsetPoint(_B, distanceB + step, _OffsetB) - OffsetPoint(_B, distanceB - step, _OffsetB)) / (2.0f * step);

			// Solve slopeA * deltaA - slopeB * deltaB = -error.
			float determinant = slopeB.x * slopeA.y - slopeA.x * slopeB.y;

			if (MathF.Abs(determinant) < 0.000001f)
				return false;

			float deltaA = (error.x * slopeB.y - slopeB.x * error.y) / determinant;
			float deltaB = (error.x * slopeA.y - slopeA.x * error.y) / determinant;

			if (MathF.Abs(deltaA) > maxStep || MathF.Abs(deltaB) > maxStep)
				return false;

			distanceA = Math.Clamp(distanceA + deltaA, _A.Distances[0], _A.Distances[^1]);
			distanceB = Math.Clamp(distanceB + deltaB, _B.Distances[0], _B.Distances[^1]);
		}

		if ((OffsetPoint(_A, distanceA, _OffsetA) - OffsetPoint(_B, distanceB, _OffsetB)).Length > 0.05f)
			return false;

		ArmFrame(_A, distanceA, out Vector3 centreA, out Vector3 leftA);
		ArmFrame(_B, distanceB, out Vector3 centreB, out Vector3 leftB);

		Vector3 point = (centreA + leftA * _A.OffsetAt(_OffsetA, distanceA) + centreB + leftB * _B.OffsetAt(_OffsetB, distanceB)) * 0.5f;
		Vector2 directionA = Flat(Vector3.Cross(leftA, Vector3.Up)).Normal;
		Vector2 directionB = Flat(Vector3.Cross(leftB, Vector3.Up)).Normal;

		_Hit = new Hit(point, distanceA, distanceB, directionA, directionB);

		return true;
	}



	/// <summary>
	/// First crossing of two arms' offset polylines, scanning outwards from behind the node so the crossing nearest
	/// the node wins. Two edges that start on the same point are a road running straight through: they meet at the
	/// node and nothing needs rounding.
	/// </summary>
	private static bool Intersect(Arm _A, in EdgeRef _OffsetA, Arm _B, in EdgeRef _OffsetB, out Hit _Hit)
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
	private static void ClosestPoint(Arm _Arm, in EdgeRef _Offset, Vector2 _Target, out Vector3 _Point, out float _Distance)
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



	/// <summary>
	/// Lifted road-surface height on an arm at a distance from the node, from the curve itself. The border
	/// builders use the same call for the arm chunks, so blends agree on both sides of a seam.
	/// </summary>
	public float SurfaceHeight(Arm _Arm, float _FromNode)
	{
		return SurfaceHeight(_Arm.Table, _Arm.CurveDistance(Math.Clamp(_FromNode, 0.0f, _Arm.Length)), m_Lift);
	}



	public static float SurfaceHeight(RoadCurveTable _Table, float _CurveDistance, float _Lift)
	{
		return _Table.PositionAt(_CurveDistance).z + _Lift;
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

			// No corner, or a corner the arms had to be trimmed short of (two junctions too close together for
			// it): nothing joins this side to the next arm, the outline just cuts across to its row. Running the
			// outline out past a row would lay the junction over the arm's own chunk.
			if (corner is null || corner.EndA > arm.Trim + 0.01f || corner.EndB > Arms[corner.ArmB].Trim + 0.01f)
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
		RoadSurfaceBuilder.CrossSection(_Arm.Table, _Arm.CurveDistance(_Arm.Trim), _Arm.Widths, m_Lift, out Vector3 curveLeft, out Vector3 curveRight);

		_Left = _Arm.AtStart ? curveLeft : curveRight;
		_Right = _Arm.AtStart ? curveRight : curveLeft;
		_LeftDirection = (_Left - _Right).Normal;
	}



	private CornerPath BuildCornerPath(Corner _Corner, Arm _A, Arm _B)
	{
		var path = new CornerPath { ArmA = _A, ArmB = _B };

		RowPoints(_A, out _, out Vector3 rowLeftA, out Vector3 leftDirectionA);
		RowPoints(_B, out Vector3 rowRightB, out _, out Vector3 leftDirectionB);

		path.StartOutward = leftDirectionA;
		path.EndOutward = -leftDirectionB;

		// Every point carries the away-from-road direction of the arm frame it comes from, so a border swept along
		// the path lands exactly where the arm chunks put theirs. The tangent point is usually the row itself; when
		// the two are a hair apart (curve versus sampled edge) the row stands in for it.
		path.Add(rowLeftA, leftDirectionA);

		// Down A's left edge from its row to the corner...
		for (int k = _A.SampleCount - 1; k >= 0; k--)
		{
			if (_A.Distances[k] < _A.Trim && _A.Distances[k] > _Corner.EndA)
				path.Add(_A.EdgePoint(k, _A.LeftEdges[k]), _A.Lefts[k]);
		}

		path.MidIndex = AddCorner(path, _Corner, _A, _B, out path.ArcFirst, out path.ArcLast);

		// ...and out along B's right edge to its row.
		for (int k = 0; k < _B.SampleCount; k++)
		{
			if (_B.Distances[k] > _Corner.EndB && _B.Distances[k] < _B.Trim)
				path.Add(_B.EdgePoint(k, -_B.RightEdges[k]), -_B.Lefts[k]);
		}

		path.Add(rowRightB, -leftDirectionB);

		var points = path.Points;

		// A sharp corner has no arc; its one point plays the arc's part (a pie of zero radius).
		if (!_Corner.HasFillet)
		{
			path.ArcFirst = path.MidIndex;
			path.ArcLast = path.MidIndex;
		}

		// The border runs on A's left. When that is the side the corner's angle opens on (where a fillet centre
		// sits), the border wraps the inside of the corner; note what it needs to square it off. A road running
		// straight through (angle near 180) has no corner to wrap.
		if (_Corner.Angle >= 5.0f && _Corner.Angle <= 175.0f && points.Count >= 2)
		{
			var leftNormalA = new Vector2(-_Corner.DirectionA.y, _Corner.DirectionA.x);

			if (Vector2.Dot(leftNormalA, _Corner.Bisector) > 0.0f)
			{
				float paramA = _Corner.EndA;
				float paramB = _Corner.EndB;
				float heightA = SurfaceHeight(_A, paramA);
				float heightB = SurfaceHeight(_B, paramB);
				var apex = new Vector2(_Corner.Point.x, _Corner.Point.y);

				path.Fold = new CornerFold
				{
					Apex = apex,
					Centre = _Corner.HasFillet ? _Corner.Centre : apex,
					Bisector = _Corner.Bisector,
					HalfAngleSin = _Corner.HalfAngleSin,
					Radius = _Corner.HasFillet ? _Corner.Radius : 0.0f,
					Layout = this,
					ArmA = _A,
					ArmB = _B,
					SignA = _Corner.SignA,
					SignB = _Corner.SignB,
					TangentParamA = paramA,
					TangentParamB = paramB,
					TangentHeightA = heightA,
					TangentHeightB = heightB,
					ApexHeight = (heightA + heightB) * 0.5f
				};
			}
		}

		return path;
	}



	/// <summary>
	/// Adds the corner itself, or its fillet arc, and returns the index of its middle: where one arm's border
	/// hands over to the other's. A sharp corner point gets no direction of its own; the border works one out
	/// from its neighbours.
	/// </summary>
	private int AddCorner(CornerPath _Path, Corner _Corner, Arm _A, Arm _B, out int _ArcFirst, out int _ArcLast)
	{
		_ArcFirst = -1;
		_ArcLast = -1;

		if (!_Corner.HasFillet)
			return _Path.Add(_Corner.Point, Vector3.Zero);

		// The tangent points take the exact frame at their distance, so a border row there reaches the centre.
		ArmFrame(_A, _Corner.TangentParamA, out _, out Vector3 leftA);
		ArmFrame(_B, _Corner.TangentParamB, out _, out Vector3 leftB);

		_Path.Add(_Corner.TangentA, leftA);

		float angle0 = MathF.Atan2(_Corner.TangentA.y - _Corner.Centre.y, _Corner.TangentA.x - _Corner.Centre.x);
		float angle1 = MathF.Atan2(_Corner.TangentB.y - _Corner.Centre.y, _Corner.TangentB.x - _Corner.Centre.x);
		float delta = angle1 - angle0;

		// Shortest way round: a fillet arc is always less than a half turn.
		if (delta > MathF.PI)
			delta -= MathF.PI * 2.0f;
		else if (delta < -MathF.PI)
			delta += MathF.PI * 2.0f;

		// Going round the centre counter-clockwise, the right-hand side (where the border is) points away from
		// the centre; clockwise, towards it.
		float radialSign = delta >= 0.0f ? 1.0f : -1.0f;

		// An even number of steps puts a point exactly halfway round.
		int steps = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(delta) / ArcStepDegrees.DegreeToRadian()));
		steps += steps % 2;
		int midIndex = -1;

		for (int k = 1; k < steps; k++)
		{
			float fraction = (float)k / steps;
			float angle = angle0 + delta * fraction;
			var radial = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
			var point = _Corner.Centre + radial * _Corner.Radius;
			int index = _Path.Add(new Vector3(point.x, point.y, MathX.Lerp(_Corner.TangentA.z, _Corner.TangentB.z, fraction)), new Vector3(radial.x, radial.y, 0.0f) * radialSign);

			if (_ArcFirst < 0)
				_ArcFirst = index;

			_ArcLast = index;

			if (k == steps / 2)
				midIndex = index;
		}

		_Path.Add(_Corner.TangentB, -leftB);

		return midIndex >= 0 ? midIndex : _Path.Points.Count - 1;
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
