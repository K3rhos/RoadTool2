using System;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the road surface of one chunk: a strip of quads between cross-section frames at the rows the chunk is
/// built on (even distances along the segment's curve, thinned where the road type allows it).
/// </summary>
internal static class RoadSurfaceBuilder
{
	/// <summary>
	/// The two edge points of the road at a distance along the curve, each its own distance out from the centreline
	/// there. Junctions use this for the rows they share with the arm chunks, so both sides of the seam are computed
	/// the same way.
	/// </summary>
	public static void CrossSection(RoadCurveTable _Table, float _Distance, in RoadWidth _Width, float _Lift, out Vector3 _Left, out Vector3 _Right)
	{
		_Table.Frame(_Distance, out Vector3 position, out Vector3 tangent);

		position += Vector3.Up * _Lift;

		Vector3 right = RoadCurve.RightOf(tangent);

		_Left = position - right * _Width.LeftAt(_Distance);
		_Right = position + right * _Width.RightAt(_Distance);
	}



	public static PolygonMesh Build(RoadCurveTable _Table, float _StartDistance, float _EndDistance, in RoadWidth _Width, float _Spacing, in RoadSimplify _Simplify, float _TextureRepeat, float _SurfaceOffset, Material _Material)
	{
		var mesh = new PolygonMesh();

		// The rows the borders and the bridge are built on too, so the pieces meet exactly.
		var rows = RoadRows.Build(_Table, _StartDistance, _EndDistance, _Spacing, _Width, _Simplify);
		float repeat = MathF.Max(1.0f, _TextureRepeat);

		var left = new HalfEdgeMesh.VertexHandle[rows.Count];
		var right = new HalfEdgeMesh.VertexHandle[rows.Count];
		var uvLeft = new Vector2[rows.Count];
		var uvRight = new Vector2[rows.Count];

		for (int i = 0; i < rows.Count; i++)
		{
			float distance = rows[i];

			CrossSection(_Table, distance, _Width, _SurfaceOffset, out Vector3 leftPos, out Vector3 rightPos);

			var handles = mesh.AddVertices(leftPos, rightPos);
			left[i] = handles[0];
			right[i] = handles[1];

			// U runs across the road from the centreline, V along it, so a tiling material follows the road instead
			// of the world, and an edge that moves out takes its texture with it.
			float v = distance / repeat;
			uvLeft[i] = new Vector2(-_Width.LeftAt(distance) / repeat, v);
			uvRight[i] = new Vector2(_Width.RightAt(distance) / repeat, v);
		}

		// Two triangles rather than a quad: a road that turns as it climbs has cells whose four corners are not
		// coplanar, and then it matters exactly how the cell is split, both here and for the paint laid on top.
		for (int i = 0; i < rows.Count - 1; i++)
		{
			MeshUtility.AddTexturedTriangle(mesh, _Material, left[i], right[i], right[i + 1], uvLeft[i], uvRight[i], uvRight[i + 1]);
			MeshUtility.AddTexturedTriangle(mesh, _Material, left[i], right[i + 1], left[i + 1], uvLeft[i], uvRight[i + 1], uvLeft[i + 1]);
		}

		return mesh;
	}



	/// <summary>
	/// The height of one cell of road surface, as it is built, over a point in plan: the cell is split from its
	/// near left corner to its far right one, so which of the two triangles the point falls on is which side of
	/// that diagonal it lies. The triangle's plane is used even for a point just off the cell (paint hanging over
	/// the road's edge), which keeps that paint on the surface's own plane.
	/// </summary>
	public static float CellHeight(Vector3 _NearLeft, Vector3 _NearRight, Vector3 _FarRight, Vector3 _FarLeft, Vector2 _Point)
	{
		Vector2 diagonal = Flat(_FarRight) - Flat(_NearLeft);
		float side = Cross(diagonal, _Point - Flat(_NearLeft));
		float nearRightSide = Cross(diagonal, Flat(_NearRight) - Flat(_NearLeft));

		return side * nearRightSide >= 0.0f
			? PlaneHeight(_NearLeft, _NearRight, _FarRight, _Point)
			: PlaneHeight(_NearLeft, _FarRight, _FarLeft, _Point);
	}



	/// <summary>
	/// The height of the plane through three points over a point in plan.
	/// </summary>
	private static float PlaneHeight(Vector3 _A, Vector3 _B, Vector3 _C, Vector2 _Point)
	{
		Vector2 a = Flat(_A);
		Vector2 b = Flat(_B);
		Vector2 c = Flat(_C);
		float area = Cross(b - a, c - a);

		if (MathF.Abs(area) < 0.0001f)
			return _A.z;

		float weightA = Cross(b - _Point, c - _Point) / area;
		float weightB = Cross(c - _Point, a - _Point) / area;

		return weightA * _A.z + weightB * _B.z + (1.0f - weightA - weightB) * _C.z;
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
