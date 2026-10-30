using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// The surface of a junction where one road splits in two around a nose (a highway exit or entry, a fork), for a road
/// texture fitted to the road's width. Fanned out from the node like any junction, such a junction reaches its nose
/// thousands of units out in slivers, and no mapping laid over those can wrap the texture's edge round the nose: it
/// smears, and the branches' lane edges show as lines across the gore. Here only the trunk's share (the road the
/// split comes from) is fanned. Each branch is laid like a road of its own, in rows across it from the node out to
/// its row, between its outer kerb and a spine from the node through the apex (where the branches' inner edges
/// cross) to the nose's tip; its texture is fitted across that, edge on the outer kerb and round the nose, and held
/// just short of its edge along the spine, which is no kerb.
/// </summary>
internal static class RoadSplitBuilder
{
	/// <summary>
	/// How far short of its edge (in U) a branch's texture is held along the spine.
	/// </summary>
	private const float SpineMargin = 0.05f;

	/// <summary>
	/// A nose sharper than this (the sine of its corner's half angle) is what makes a split.
	/// </summary>
	private const float MaxHalfAngleSin = 0.5f;

	/// <summary>
	/// A nose nearer the node than this many times the wider branch's width is fanned like any other corner.
	/// </summary>
	private const float MinReach = 2.0f;

	/// <summary>
	/// The spine's points from the node to the apex are this share of the wider branch's width apart; from the apex
	/// to the tip, and along each row, this share of the branch's own. Close enough that the surface strays no more
	/// than a few units from the plain fan the ground under a junction is shaped to, even round a steep nose.
	/// </summary>
	private const float RowStep = 0.25f;
	private const float PointStep = 0.0625f;

	/// <summary>
	/// How far (as a share of the junction's area) the triangles may cover more or less than its outline before
	/// something is taken to have gone wrong and the junction is fanned instead.
	/// </summary>
	private const double AreaTolerance = 0.001;

	/// <summary>
	/// A point of a branch's strip and its texture coordinates.
	/// </summary>
	private readonly record struct Mark(Vector3 Position, Vector2 Uv);



	/// <summary>
	/// Builds the junction's surface as a split, when it is one: three arms, two of which (the branches) meet at a
	/// sharp, filleted nose well out from the node, beyond the point their inner edges cross. Anything else, or
	/// anything the rows would not lay out cleanly, is left to the fan. <paramref name="_Mappings"/> are the arms' own
	/// mappings, in the layout's order.
	/// </summary>
	public static bool TryBuild(RoadJunctionLayout _Layout, IReadOnlyList<RoadArmMapping> _Mappings, Material _Material, out PolygonMesh _Mesh)
	{
		_Mesh = null;

		var polygon = _Layout.Polygon;
		var rows = _Layout.RowStarts;
		int count = polygon.Count;

		if (_Layout.Arms.Count != 3 || rows is null || rows.Count != 3 || _Mappings.Count != 3 || count < 3)
			return false;

		foreach (int row in rows)
		{
			if (row < 0 || row >= count)
				return false;
		}

		RoadJunctionLayout.CornerPath nose = null;

		foreach (var path in _Layout.CornerPaths)
		{
			if (path.Fold is null || path.Fold.HalfAngleSin >= MaxHalfAngleSin)
				continue;

			if (nose is not null)
				return false;

			nose = path;
		}

		if (nose is null)
			return false;

		// Counter-clockwise: the trunk, then the branch whose left edge runs to the nose, then the other.
		int first = _Layout.Arms.IndexOf(nose.ArmA);
		int second = _Layout.Arms.IndexOf(nose.ArmB);
		int trunk = 3 - first - second;

		if (first < 0 || second != (first + 1) % 3 || trunk != (second + 1) % 3)
			return false;

		// The trunk comes in from the far side: a branch at a right angle to it is a corner, not a split.
		var trunkDirection = _Layout.Arms[trunk].Direction;

		if (Vector2.Dot(trunkDirection, _Layout.Arms[first].Direction) > -0.5f || Vector2.Dot(trunkDirection, _Layout.Arms[second].Direction) > -0.5f)
			return false;

		// The nose's points are the polygon's from the first branch's row on, its middle the tip.
		int noseStart = (rows[first] + 1) % count;

		if (Wrap(rows[second] - noseStart, count) != nose.Points.Count - 1 || nose.MidIndex <= 0 || nose.MidIndex >= nose.Points.Count - 1)
			return false;

		int tip = (noseStart + nose.MidIndex) % count;
		var node = _Layout.Centre;
		Vector3 tipPoint = polygon[tip];
		var apex = OnCone(_Layout, new Vector3(nose.Fold.Apex.x, nose.Fold.Apex.y, node.z));
		float reach = Flat(tipPoint - node).Length;
		float width = MathF.Max(RowWidth(_Layout.Arms[first]), RowWidth(_Layout.Arms[second]));

		// The apex has to lie between the node and the nose, the nose well out.
		if (reach < MinReach * width || Vector2.Dot(Flat(apex - node), Flat(tipPoint - node)) <= 0.0f || Flat(apex - node).Length >= reach)
			return false;

		// Where the trunk's share ends on its two kerbs: their points nearest the node.
		int outerFirst = Nearest(polygon, (rows[trunk] + 1) % count, rows[first], node);
		int outerSecond = Nearest(polygon, (rows[second] + 1) % count, rows[trunk], node);

		if (outerFirst < 0 || outerSecond < 0)
			return false;

		// The spine, shared by both branches: from the node to the apex, and on down the gore to the tip.
		var spine = new List<Vector3> { node };

		AddSpan(spine, node, apex, width * RowStep, _Layout);
		AddSpan(spine, apex, tipPoint, width * PointStep, _Layout);
		spine.RemoveAt(spine.Count - 1);

		// Each branch's two sides, from the node's end out to its row: its outer kerb, and the spine and its half of
		// the nose.
		var firstOuter = Walk(polygon, outerFirst, rows[first]);
		var firstInner = Walk(polygon, noseStart, tip);
		var secondOuter = Walk(polygon, (rows[second] + 1) % count, outerSecond);
		var secondInner = Walk(polygon, tip, rows[second]);

		firstInner.Reverse();
		secondOuter.Reverse();
		firstInner.InsertRange(0, spine);
		secondInner.InsertRange(0, spine);

		// Rows across a branch need both its sides to run steadily away from the node; anything else is fanned.
		if (!Outward(firstOuter, _Layout.Arms[first], _Mappings[first]) || !Outward(firstInner, _Layout.Arms[first], _Mappings[first])
			|| !Outward(secondOuter, _Layout.Arms[second], _Mappings[second]) || !Outward(secondInner, _Layout.Arms[second], _Mappings[second]))
			return false;

		var firstTriangles = BuildBranch(_Layout, _Layout.Arms[first], _Mappings[first], firstOuter, firstInner, spine.Count);
		var secondTriangles = BuildBranch(_Layout, _Layout.Arms[second], _Mappings[second], secondOuter, secondInner, spine.Count);

		if (firstTriangles is null || secondTriangles is null)
			return false;

		// The trunk's share fanned from the node, and both branches, have to cover the outline exactly: no more (two
		// triangles over one spot) and no less (a hole).
		var share = Walk(polygon, outerSecond, outerFirst);
		double outline = 0.0;
		double covered = 0.0;

		for (int i = 0; i < count; i++)
			outline += Cross(polygon[i], polygon[(i + 1) % count], node);

		for (int i = 0; i < share.Count - 1; i++)
			covered += Cross(share[i], share[i + 1], node);

		foreach (var triangle in firstTriangles)
			covered += Cross(triangle[1].Position, triangle[2].Position, triangle[0].Position);

		foreach (var triangle in secondTriangles)
			covered += Cross(triangle[1].Position, triangle[2].Position, triangle[0].Position);

		if (outline <= 0.0 || Math.Abs(covered - outline) > outline * AreaTolerance)
			return false;

		var mesh = new PolygonMesh();
		var vertices = new Dictionary<Vector2, HalfEdgeMesh.VertexHandle>();

		HalfEdgeMesh.VertexHandle Vertex(Vector3 _Position)
		{
			var key = Flat(_Position);

			if (!vertices.TryGetValue(key, out var handle))
			{
				handle = mesh.AddVertices(_Position)[0];
				vertices[key] = handle;
			}

			return handle;
		}

		// The trunk first, then each branch from the node out, so every face joins on to those before it.
		RoadFanBuilder.Build(mesh, _Material, node, share, new int[share.Count], new IFanMapping[] { _Mappings[trunk] }, new[] { true }, Vertex, false);

		foreach (var triangle in firstTriangles)
			MeshUtility.AddTexturedTriangle(mesh, _Material, Vertex(triangle[0].Position), Vertex(triangle[1].Position), Vertex(triangle[2].Position), triangle[0].Uv, triangle[1].Uv, triangle[2].Uv);

		foreach (var triangle in secondTriangles)
			MeshUtility.AddTexturedTriangle(mesh, _Material, Vertex(triangle[0].Position), Vertex(triangle[1].Position), Vertex(triangle[2].Position), triangle[0].Uv, triangle[1].Uv, triangle[2].Uv);

		_Mesh = mesh;

		return true;
	}



	/// <summary>
	/// One branch's triangles, facing up, in the order they join on to one another. <paramref name="_Outer"/> runs
	/// along its outer kerb from the trunk's share to its row; <paramref name="_Inner"/> along the spine and then, from
	/// <paramref name="_KerbFrom"/> on, round the nose's half from its tip to the row. A row crosses the branch from each
	/// point of the inner side to the point of the outer kerb as far along it (as a share of the way, so the first row is
	/// the trunk's fan's end spoke and the last the branch's own row); the band between two rows is zipped from the kerb
	/// in, with whatever of the kerb's own points lie between them fanned from the first row's first point in. Null when
	/// the rows would fold over one another anywhere.
	/// </summary>
	private static List<Mark[]> BuildBranch(RoadJunctionLayout _Layout, RoadJunctionLayout.Arm _Arm, RoadArmMapping _Mapping, List<Vector3> _Outer, List<Vector3> _Inner,
		int _KerbFrom)
	{
		if (_Outer.Count < 2 || _Inner.Count - _KerbFrom < 2)
			return null;

		// The branch's texture runs from its edge on the outer kerb to its other edge round the nose.
		float outerU = _Mapping.KerbUv(_Outer[^1]).x;
		float innerEdgeU = _Mapping.KerbUv(_Inner[^1]).x;
		bool innerHigh = innerEdgeU > outerU;

		float InnerU(int _Index)
		{
			if (_Index >= _KerbFrom)
				return innerEdgeU;

			float u = _Mapping.Uv(_Inner[_Index]).x;

			return innerHigh ? MathF.Min(u, innerEdgeU - SpineMargin) : MathF.Max(u, innerEdgeU + SpineMargin);
		}

		Mark MarkAt(Vector3 _Point, float _U)
		{
			return new Mark(_Point, new Vector2(_U, _Mapping.Uv(_Point).y));
		}

		float[] outerAt = Levels(_Outer, _Arm, _Mapping);
		float[] innerAt = Levels(_Inner, _Arm, _Mapping);
		float outerSpan = outerAt[^1] - outerAt[0];
		float innerSpan = innerAt[^1] - innerAt[0];

		if (outerSpan < 1.0f || innerSpan < 1.0f)
			return null;

		float step = MathF.Max(1.0f, RowWidth(_Arm) * PointStep);
		var rows = new List<(Mark[] Points, int Next)>(_Inner.Count);

		for (int k = 0; k < _Inner.Count; k++)
		{
			bool last = k == _Inner.Count - 1;
			Vector3 outerPoint;
			int next;

			if (k == 0)
			{
				outerPoint = _Outer[0];
				next = 1;
			}
			else if (last)
			{
				outerPoint = _Outer[^1];
				next = _Outer.Count;
			}
			else
			{
				outerPoint = PointAt(_Outer, outerAt, outerAt[0] + (innerAt[k] - innerAt[0]) / innerSpan * outerSpan, out next);
			}

			Vector3 innerPoint = _Inner[k];
			float innerU = InnerU(k);

			// The first row is the trunk's fan's end spoke, whole.
			int pieces = k == 0 ? 1 : Math.Max(1, (int)MathF.Ceiling(Flat(innerPoint - outerPoint).Length / step));
			var row = new Mark[pieces + 1];

			for (int j = 0; j <= pieces; j++)
			{
				float f = (float)j / pieces;
				Vector3 point = j == 0 ? outerPoint : j == pieces ? innerPoint : Vector3.Lerp(outerPoint, innerPoint, f);

				// Inside, on the surface the plain fan would have; the branch's own row straight, as its road's is.
				if (j > 0 && j < pieces && !last)
					point = OnCone(_Layout, point);

				row[j] = MarkAt(point, MathX.Lerp(outerU, innerU, f));
			}

			rows.Add((row, next));
		}

		var triangles = new List<Mark[]>();

		for (int k = 0; k < rows.Count - 1; k++)
		{
			var (a, aNext) = rows[k];
			var (b, bNext) = rows[k + 1];

			// The kerb's own points between the two rows, fanned from the first row's first point in.
			var kerb = new List<Mark> { a[0] };

			for (int i = aNext; i < bNext && i < _Outer.Count; i++)
			{
				if (Flat(_Outer[i] - a[0].Position).Length > 0.01f && Flat(_Outer[i] - b[0].Position).Length > 0.01f)
					kerb.Add(MarkAt(_Outer[i], outerU));
			}

			if (kerb.Count > 1)
			{
				kerb.Add(b[0]);

				for (int j = 0; j < kerb.Count - 1; j++)
					Add(triangles, kerb[j + 1], kerb[j], a[1]);

				Zip(triangles, a, 1, b);
			}
			else
			{
				Zip(triangles, a, 0, b);
			}
		}

		// One winding for the whole branch, facing up; a triangle against it means the rows fold over somewhere.
		double area = 0.0;

		foreach (var triangle in triangles)
			area += Cross(triangle[1].Position, triangle[2].Position, triangle[0].Position);

		if (area < 0.0)
		{
			foreach (var triangle in triangles)
				(triangle[1], triangle[2]) = (triangle[2], triangle[1]);
		}

		foreach (var triangle in triangles)
		{
			if (Cross(triangle[1].Position, triangle[2].Position, triangle[0].Position) <= 0.0)
				return null;
		}

		return triangles;
	}



	/// <summary>
	/// Triangles between two rows of a band (<paramref name="_A"/> from <paramref name="_From"/> on, and
	/// <paramref name="_B"/>), both running from the kerb in, in the order their points come along them.
	/// </summary>
	private static void Zip(List<Mark[]> _Triangles, Mark[] _A, int _From, Mark[] _B)
	{
		float[] atA = Fractions(_A, _From);
		float[] atB = Fractions(_B, 0);
		int a = _From;
		int b = 0;

		while (a < _A.Length - 1 || b < _B.Length - 1)
		{
			if (b == _B.Length - 1 || (a < _A.Length - 1 && atA[a + 1] <= atB[b + 1]))
			{
				Add(_Triangles, _A[a], _A[a + 1], _B[b]);
				a++;
			}
			else
			{
				Add(_Triangles, _A[a], _B[b + 1], _B[b]);
				b++;
			}
		}
	}



	/// <summary>
	/// Adds a triangle unless its points are in a line (rows that share a point on the kerb, points along a straight
	/// kerb), which would make no face.
	/// </summary>
	private static void Add(List<Mark[]> _Triangles, Mark _A, Mark _B, Mark _C)
	{
		if (Math.Abs(Cross(_B.Position, _C.Position, _A.Position)) < 0.002)
			return;

		_Triangles.Add(new[] { _A, _B, _C });
	}



	/// <summary>
	/// How far along a row each of its points is from <paramref name="_From"/>, as a share of the rest of its length.
	/// </summary>
	private static float[] Fractions(Mark[] _Row, int _From)
	{
		var at = new float[_Row.Length];

		for (int i = _From + 1; i < _Row.Length; i++)
			at[i] = at[i - 1] + Flat(_Row[i].Position - _Row[i - 1].Position).Length;

		float length = at[^1];

		for (int i = _From; i < at.Length; i++)
			at[i] = length > 0.0f ? at[i] / length : 0.0f;

		return at;
	}



	/// <summary>
	/// Points from <paramref name="_From"/> to <paramref name="_To"/> along a straight line, the first left out (it is
	/// already there) and the rest on the junction's surface.
	/// </summary>
	private static void AddSpan(List<Vector3> _Points, Vector3 _From, Vector3 _To, float _Step, RoadJunctionLayout _Layout)
	{
		int pieces = Math.Max(1, (int)MathF.Ceiling(Flat(_To - _From).Length / MathF.Max(1.0f, _Step)));

		for (int j = 1; j <= pieces; j++)
			_Points.Add(j == pieces ? _To : OnCone(_Layout, Vector3.Lerp(_From, _To, (float)j / pieces)));
	}



	/// <summary>
	/// Whether a line runs steadily out from the node along an arm: no point of it more than a unit back from the
	/// furthest out before it.
	/// </summary>
	private static bool Outward(List<Vector3> _Points, RoadJunctionLayout.Arm _Arm, RoadArmMapping _Mapping)
	{
		float furthest = float.MinValue;

		foreach (var point in _Points)
		{
			float fromNode = FromNode(point, _Arm, _Mapping);

			if (fromNode < furthest - 1.0f)
				return false;

			furthest = MathF.Max(furthest, fromNode);
		}

		return true;
	}



	/// <summary>
	/// How far out from the node along the arm each point of a line is, never less than the point before's.
	/// </summary>
	private static float[] Levels(List<Vector3> _Points, RoadJunctionLayout.Arm _Arm, RoadArmMapping _Mapping)
	{
		var at = new float[_Points.Count];

		for (int i = 0; i < _Points.Count; i++)
		{
			float fromNode = FromNode(_Points[i], _Arm, _Mapping);

			at[i] = i == 0 ? fromNode : MathF.Max(fromNode, at[i - 1]);
		}

		return at;
	}



	private static float FromNode(Vector3 _Point, RoadJunctionLayout.Arm _Arm, RoadArmMapping _Mapping)
	{
		float distance = _Mapping.Measure(_Point).y;

		return _Arm.AtStart ? distance : _Arm.Length - distance;
	}



	/// <summary>
	/// The point of a line a distance out from the node, and the index of the line's next point after it.
	/// </summary>
	private static Vector3 PointAt(List<Vector3> _Points, float[] _At, float _Distance, out int _Next)
	{
		for (int i = 1; i < _Points.Count; i++)
		{
			if (_At[i] < _Distance)
				continue;

			float span = _At[i] - _At[i - 1];
			float t = span > 0.0001f ? (_Distance - _At[i - 1]) / span : 1.0f;

			_Next = i;

			return Vector3.Lerp(_Points[i - 1], _Points[i], Math.Clamp(t, 0.0f, 1.0f));
		}

		_Next = _Points.Count;

		return _Points[^1];
	}



	/// <summary>
	/// A point brought to the height the junction's plain fan has there: on the line from the node out through it to
	/// the outline, the node's height and the outline's in proportion.
	/// </summary>
	private static Vector3 OnCone(RoadJunctionLayout _Layout, Vector3 _Point)
	{
		var polygon = _Layout.Polygon;
		var node = _Layout.Centre;
		Vector2 ray = Flat(_Point - node);
		float nearest = float.MaxValue;
		float height = node.z;

		for (int i = 0; i < polygon.Count; i++)
		{
			Vector3 a = polygon[i];
			Vector3 b = polygon[(i + 1) % polygon.Count];
			Vector2 edge = Flat(b - a);
			Vector2 toA = Flat(a - node);
			float denominator = Cross(ray, edge);

			if (MathF.Abs(denominator) < 0.0001f)
				continue;

			float along = Cross(toA, edge) / denominator;
			float on = Cross(toA, ray) / denominator;

			if (along > 0.0001f && on >= -0.0001f && on <= 1.0001f && along < nearest)
			{
				nearest = along;
				height = MathX.Lerp(a.z, b.z, Math.Clamp(on, 0.0f, 1.0f));
			}
		}

		if (nearest == float.MaxValue)
			return _Point;

		return new Vector3(_Point.x, _Point.y, node.z + (height - node.z) / nearest);
	}



	/// <summary>
	/// The outline's points from one index round to another, both included.
	/// </summary>
	private static List<Vector3> Walk(IReadOnlyList<Vector3> _Polygon, int _From, int _To)
	{
		var points = new List<Vector3>();
		int count = _Polygon.Count;

		for (int i = _From; ; i = (i + 1) % count)
		{
			points.Add(_Polygon[i]);

			if (i == _To || points.Count > count)
				break;
		}

		return points;
	}



	/// <summary>
	/// The index of the outline's point nearest a position, strictly between two indices (going round); -1 when there
	/// is none between them.
	/// </summary>
	private static int Nearest(IReadOnlyList<Vector3> _Polygon, int _After, int _Before, Vector3 _Position)
	{
		int count = _Polygon.Count;
		int best = -1;
		float nearest = float.MaxValue;

		for (int i = (_After + 1) % count; i != _Before; i = (i + 1) % count)
		{
			float distance = Flat(_Polygon[i] - _Position).Length;

			if (distance < nearest)
			{
				nearest = distance;
				best = i;
			}
		}

		return best;
	}



	private static float RowWidth(RoadJunctionLayout.Arm _Arm)
	{
		float distance = _Arm.CurveDistance(_Arm.Trim);

		return _Arm.Widths.WidthAt(distance);
	}



	private static int Wrap(int _Index, int _Count)
	{
		return ((_Index % _Count) + _Count) % _Count;
	}



	private static Vector2 Flat(Vector3 _Vector)
	{
		return new Vector2(_Vector.x, _Vector.y);
	}



	private static float Cross(Vector2 _A, Vector2 _B)
	{
		return _A.x * _B.y - _A.y * _B.x;
	}



	/// <summary>
	/// Twice the area (in plan, counter-clockwise positive) of the triangle from an origin to two points, worked out
	/// from the origin so far-out coordinates lose nothing.
	/// </summary>
	private static double Cross(Vector3 _A, Vector3 _B, Vector3 _Origin)
	{
		double ax = (double)_A.x - _Origin.x;
		double ay = (double)_A.y - _Origin.y;
		double bx = (double)_B.x - _Origin.x;
		double by = (double)_B.y - _Origin.y;

		return ax * by - ay * bx;
	}
}
