using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the surface of a junction: the layout's polygon fanned out from the node. Each triangle of the fan is
/// planar, so none can be dropped by the mesh builder whatever the arms' heights do, and a spoke of the fan cut into
/// pieces still lies on the triangles either side of it: cutting the fan finer never moves the surface.
/// </summary>
internal static class RoadJunctionBuilder
{
	/// <summary>
	/// How far (in world units, across and along the road) a straight run of the mapping along a spoke may drift from
	/// the mapping itself before the spoke is cut into pieces.
	/// </summary>
	private const float SpokeTolerance = 2.0f;

	/// <summary>
	/// The most pieces a spoke is cut into, for a road that curves hard on its way in to the node.
	/// </summary>
	private const int MaxSpokePieces = 8;

	/// <summary>
	/// An arm's own texture mapping carried on past the row its mesh starts with, in to the node: U across the road
	/// and V along it, measured from the arm's own centreline (the curve itself, carried on straight past either
	/// end), exactly as the arm's chunks lay them (see <see cref="RoadSurfaceBuilder.Build"/>). The road's texture
	/// runs on into the junction without a seam and follows the road wherever it curves on its way in; carried on
	/// straight from the row instead, it would drift off a curving road and be stretched to meet its kerb.
	/// </summary>
	private sealed class ArmMapping
	{
		private readonly RoadJunctionLayout.Arm m_Arm;
		private readonly float m_Low;
		private readonly float m_High;
		private readonly float m_Repeat;
		private readonly bool m_FitWidth;

		public ArmMapping(RoadJunctionLayout.Arm _Arm, float _Repeat, bool _FitWidth)
		{
			m_Arm = _Arm;
			m_Repeat = _Repeat;
			m_FitWidth = _FitWidth;

			float node = _Arm.CurveDistance(0.0f);
			float row = _Arm.CurveDistance(_Arm.Trim);

			m_Low = MathF.Min(node, row);
			m_High = MathF.Max(node, row);
		}

		public Vector2 Uv(Vector3 _Point)
		{
			Locate(_Point, out float distance, out float right);

			return new Vector2(Across(distance, right), distance / m_Repeat);
		}

		/// <summary>
		/// The mapping at a point of the outline, which is kerb all round: U held at the road's edge on that side,
		/// where the texture has its edge, so whatever runs along the edge of the road in the texture follows the
		/// kerb round a corner however it curves, instead of running on and missing it. V as everywhere.
		/// </summary>
		public Vector2 KerbUv(Vector3 _Point)
		{
			Locate(_Point, out float distance, out float right);

			float along = Math.Clamp(distance, 0.0f, m_Arm.Length);
			float edge = right < 0.0f ? -m_Arm.Widths.LeftAt(along) : m_Arm.Widths.RightAt(along);

			return new Vector2(Across(distance, edge), distance / m_Repeat);
		}

		/// <summary>
		/// Where a point sits on the road, in world units: how far to the right of the centreline, and how far along
		/// the curve.
		/// </summary>
		public Vector2 Road(Vector3 _Point)
		{
			Locate(_Point, out float distance, out float right);

			return new Vector2(right, distance);
		}

		/// <summary>
		/// U at a point an offset to the right of the centreline, a distance along it: at the texture's own size out
		/// from the centre line, or fitted between the road's two edges there.
		/// </summary>
		private float Across(float _Distance, float _Right)
		{
			if (!m_FitWidth)
				return _Right / m_Repeat;

			float along = Math.Clamp(_Distance, 0.0f, m_Arm.Length);
			float left = m_Arm.Widths.LeftAt(along);
			float width = left + m_Arm.Widths.RightAt(along);

			return width > 0.001f ? (_Right + left) / width : 0.5f;
		}

		/// <summary>
		/// Where a point sits in the road's own terms: the curve distance of the foot of its perpendicular on the
		/// centreline between the node and the row, and how far to the road's right of that foot it lies. A point
		/// behind the node or past the row is measured from the centreline carried on straight there.
		/// </summary>
		private void Locate(Vector3 _Point, out float _Distance, out float _Right)
		{
			var point = new Vector2(_Point.x, _Point.y);
			float low = m_Low;
			float high = m_High;

			if (Ahead(point, low) <= 0.0f)
			{
				Measure(point, low, out _Distance, out _Right);
				return;
			}

			if (Ahead(point, high) >= 0.0f)
			{
				Measure(point, high, out _Distance, out _Right);
				return;
			}

			// The foot is somewhere between: halve the stretch it is in until it is pinned down.
			for (int i = 0; i < 40 && high - low > 0.001f; i++)
			{
				float middle = (low + high) * 0.5f;

				if (Ahead(point, middle) > 0.0f)
					low = middle;
				else
					high = middle;
			}

			Measure(point, (low + high) * 0.5f, out _Distance, out _Right);
		}

		/// <summary>
		/// How far ahead of the centreline's point at a distance a point lies, the way the road runs there.
		/// </summary>
		private float Ahead(Vector2 _Point, float _Distance)
		{
			Frame(_Distance, out Vector2 position, out Vector2 along);

			return Vector2.Dot(_Point - position, along);
		}

		/// <summary>
		/// A point's distance and right offset measured from the centreline's point at a distance and the way the road
		/// runs there: exact at the foot of the point's perpendicular, and the centreline carried on straight elsewhere.
		/// </summary>
		private void Measure(Vector2 _Point, float _From, out float _Distance, out float _Right)
		{
			Frame(_From, out Vector2 position, out Vector2 along);

			Vector2 offset = _Point - position;

			_Distance = _From + Vector2.Dot(offset, along);
			_Right = offset.x * along.y - offset.y * along.x;
		}

		private void Frame(float _Distance, out Vector2 _Position, out Vector2 _Along)
		{
			m_Arm.Table.Frame(_Distance, out Vector3 position, out Vector3 tangent);

			// The road's right as its chunks take it: flat and square to the way it runs, which is a quarter turn
			// back from it.
			Vector3 right = RoadCurve.RightOf(tangent);

			_Position = new Vector2(position.x, position.y);
			_Along = new Vector2(-right.y, right.x);
		}
	}



	/// <summary>
	/// Each arm's texture runs on into the junction over the arm's own stretch of the outline (see
	/// <see cref="RoadJunctionLayout.PolygonArms"/>) and meets the next arm's on a line from the node to the middle
	/// of the corner between them, where the borders meet too. A road texture with something across its width (lanes,
	/// worn wheel tracks, dark edges) turns the corner with the road instead of being cut by a projection laid over
	/// it, and nothing is stretched. The spokes of a road that curves on its way in are cut into pieces, so the
	/// texture bends with it rather than cutting across. An arm with a different road surface from the junction's is
	/// not the junction's to continue: its share takes the main road's mapping as it is, as if that road ran on into
	/// it, with no kerb of its own to follow.
	/// </summary>
	public static PolygonMesh Build(RoadJunctionLayout _Layout, RoadJunctionLayout.Arm _Main, Func<RoadJunctionLayout.Arm, bool> _SameSurface, float _TextureRepeat, bool _FitWidth, Material _Material)
	{
		var mesh = new PolygonMesh();
		var polygon = _Layout.Polygon;
		var owners = _Layout.PolygonArms;

		if (polygon.Count < 3)
			return mesh;

		int mainIndex = _Main is null ? -1 : _Layout.Arms.IndexOf(_Main);

		if (owners is null || owners.Count != polygon.Count || mainIndex < 0)
			return Build(_Layout, _TextureRepeat, _Material);

		float repeat = MathF.Max(1.0f, _TextureRepeat);
		var main = new ArmMapping(_Main, repeat, _FitWidth);
		var mappings = new ArmMapping[_Layout.Arms.Count];
		var own = new bool[_Layout.Arms.Count];

		for (int i = 0; i < mappings.Length; i++)
		{
			own[i] = i == mainIndex || _SameSurface(_Layout.Arms[i]);
			mappings[i] = i == mainIndex || !own[i] ? main : new ArmMapping(_Layout.Arms[i], repeat, _FitWidth);
		}

		// The outline, with an edge cut straight across from one arm's row to the next arm's halved: each arm takes
		// the half on its side.
		var points = new List<Vector3>();
		var arms = new List<int>();

		for (int i = 0; i < polygon.Count; i++)
		{
			int next = (i + 1) % polygon.Count;

			points.Add(polygon[i]);

			if (owners[i] >= 0)
			{
				arms.Add(owners[i]);
				continue;
			}

			int before = OwnerNear(owners, i, -1);
			int after = OwnerNear(owners, next, 1);

			arms.Add(before >= 0 ? before : mainIndex);

			if (before >= 0 && after >= 0)
			{
				points.Add((polygon[i] + polygon[next]) * 0.5f);
				arms.Add(after);
			}
		}

		// The spokes, each cut into as many pieces as either of the arms it borders needs. The pieces lie on the
		// spoke, so the fan's triangles either side of it keep their planes.
		Vector3 centre = _Layout.Centre;
		var centreHandle = mesh.AddVertices(centre)[0];
		var spokes = new HalfEdgeMesh.VertexHandle[points.Count][];

		for (int k = 0; k < points.Count; k++)
		{
			int entering = arms[(k + points.Count - 1) % points.Count];
			int pieces = Math.Max(SpokePieces(mappings[entering], centre, points[k]), SpokePieces(mappings[arms[k]], centre, points[k]));
			var spoke = new HalfEdgeMesh.VertexHandle[pieces + 1];

			spoke[0] = centreHandle;

			for (int j = 1; j <= pieces; j++)
				spoke[j] = mesh.AddVertices(j == pieces ? points[k] : Vector3.Lerp(centre, points[k], (float)j / pieces))[0];

			spokes[k] = spoke;
		}

		for (int k = 0; k < points.Count; k++)
		{
			int next = (k + 1) % points.Count;
			var mapping = mappings[arms[k]];
			bool kerbs = own[arms[k]];

			Zip(mesh, _Material,
				spokes[k], SpokeUvs(mapping, kerbs, centre, points[k], spokes[k].Length - 1),
				spokes[next], SpokeUvs(mapping, kerbs, centre, points[next], spokes[next].Length - 1));
		}

		return mesh;
	}



	/// <summary>
	/// The plain top-down projection, for an outline whose arms were never noted (built before they were).
	/// </summary>
	public static PolygonMesh Build(RoadJunctionLayout _Layout, float _TextureRepeat, Material _Material)
	{
		var mesh = new PolygonMesh();
		var polygon = _Layout.Polygon;

		if (polygon.Count < 3)
			return mesh;

		var centre = mesh.AddVertices(_Layout.Centre)[0];
		var ring = mesh.AddVertices(polygon.ToArray());
		Vector2 centreUv = PlanarUv(_Layout.Centre, _TextureRepeat);

		for (int i = 0; i < polygon.Count; i++)
		{
			int next = (i + 1) % polygon.Count;

			MeshUtility.AddTexturedTriangle(mesh, _Material, centre, ring[i], ring[next],
				centreUv, PlanarUv(polygon[i], _TextureRepeat), PlanarUv(polygon[next], _TextureRepeat));
		}

		return mesh;
	}



	/// <summary>
	/// How many pieces the spoke from the centre to an outline point needs for a straight run of an arm's mapping
	/// along each to stay within <see cref="SpokeTolerance"/> of the mapping itself. A straight road's mapping is
	/// straight everywhere and needs one; a road curving in to the node needs more.
	/// </summary>
	private static int SpokePieces(ArmMapping _Mapping, Vector3 _Centre, Vector3 _End)
	{
		Vector2 from = _Mapping.Road(_Centre);
		Vector2 to = _Mapping.Road(_End);
		float drift = 0.0f;

		for (int i = 1; i < 4; i++)
		{
			float t = i * 0.25f;

			drift = MathF.Max(drift, (_Mapping.Road(Vector3.Lerp(_Centre, _End, t)) - Vector2.Lerp(from, to, t)).Length);
		}

		// A straight run's drift from a curve falls with the square of its length.
		return Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(drift / SpokeTolerance)), 1, MaxSpokePieces);
	}



	/// <summary>
	/// The mapping at each point of a spoke cut into pieces: the arm's own, and for an outline point held to its kerb,
	/// the difference that makes worked in evenly from nothing at the centre, so the texture never folds back on
	/// itself on the way out to the kerb.
	/// </summary>
	private static Vector2[] SpokeUvs(ArmMapping _Mapping, bool _Kerbs, Vector3 _Centre, Vector3 _End, int _Pieces)
	{
		var uvs = new Vector2[_Pieces + 1];
		Vector2 end = _Mapping.Uv(_End);
		Vector2 held = _Kerbs ? _Mapping.KerbUv(_End) : end;

		for (int j = 0; j <= _Pieces; j++)
		{
			float t = (float)j / _Pieces;
			Vector2 uv = j == _Pieces ? end : _Mapping.Uv(Vector3.Lerp(_Centre, _End, t));

			uvs[j] = uv + (held - end) * t;
		}

		return uvs;
	}



	/// <summary>
	/// Fills one triangle of the fan, between two spokes that start at the centre, with triangles from one to the other
	/// in the order their pieces reach out, wound the way the fan triangle is.
	/// </summary>
	private static void Zip(PolygonMesh _Mesh, Material _Material, HalfEdgeMesh.VertexHandle[] _A, Vector2[] _UvA, HalfEdgeMesh.VertexHandle[] _B, Vector2[] _UvB)
	{
		int lastA = _A.Length - 1;
		int lastB = _B.Length - 1;
		int a = 1;
		int b = 1;

		MeshUtility.AddTexturedTriangle(_Mesh, _Material, _A[0], _A[1], _B[1], _UvA[0], _UvA[1], _UvB[1]);

		while (a < lastA || b < lastB)
		{
			// Step out along whichever spoke is further behind, as a share of its length.
			if (b == lastB || (a < lastA && (a + 1) * lastB <= (b + 1) * lastA))
			{
				MeshUtility.AddTexturedTriangle(_Mesh, _Material, _A[a], _A[a + 1], _B[b], _UvA[a], _UvA[a + 1], _UvB[b]);
				a++;
			}
			else
			{
				MeshUtility.AddTexturedTriangle(_Mesh, _Material, _A[a], _B[b + 1], _B[b], _UvA[a], _UvB[b + 1], _UvB[b]);
				b++;
			}
		}
	}



	/// <summary>
	/// The arm owning the nearest edge that has one, walking round the outline one way from an edge.
	/// </summary>
	private static int OwnerNear(IReadOnlyList<int> _Owners, int _From, int _Step)
	{
		for (int k = 0; k < _Owners.Count; k++)
		{
			int owner = _Owners[((_From + _Step * k) % _Owners.Count + _Owners.Count) % _Owners.Count];

			if (owner >= 0)
				return owner;
		}

		return -1;
	}



	private static Vector2 PlanarUv(Vector3 _Position, float _TextureRepeat)
	{
		return new Vector2(_Position.x, _Position.y) / _TextureRepeat;
	}
}
