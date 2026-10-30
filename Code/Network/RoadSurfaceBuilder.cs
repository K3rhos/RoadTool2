using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the road surface of one chunk: a strip of cells between cross-section frames at the rows the chunk is
/// built on (even distances along the segment's curve, thinned where the road type allows it).
/// </summary>
internal static class RoadSurfaceBuilder
{
	/// <summary>
	/// How far one corner of a cell may sit off the plane of the other three before two triangles stop doing for it.
	/// A road that climbs as it turns has level rows that swing round from one to the next, so its inside edge
	/// climbs more steeply than its outside edge and every cell is twisted. Two triangles then give the middle of the
	/// road one slope up to the diagonal and another after it: a step in every cell, which the centre line, lying on
	/// the surface as built, shows as a staircase.
	/// </summary>
	private const float MaxTwist = 0.25f;

	/// <summary>
	/// The widest strip a twisted road is cut into lengthways.
	/// </summary>
	private const float MaxColumnWidth = 125.0f;



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



	/// <summary>
	/// How a stretch of road is cut into triangles: 0 when two per cell will do, which is everywhere the road does not
	/// climb and turn at once, otherwise the number of columns every cell is cut into lengthways, each fanned into four
	/// triangles round its middle. In its own column a fan climbs evenly from one row to the next however much the
	/// column twists, and narrow columns twist little, so the surface follows the road's true shape. The columns fall
	/// on the lane boundaries, so the lines painted there run along edges of the mesh itself. Worked out from the
	/// corners without the surface's lift, so the paint, which lays itself on these cells, comes to the same answer.
	/// </summary>
	public static int Columns(IReadOnlyList<Vector3> _Left, IReadOnlyList<Vector3> _Right, in RoadWidth _Width, int _Lanes)
	{
		bool twisted = false;

		for (int i = 0; i + 1 < _Left.Count && !twisted; i++)
			twisted = Twist(_Left[i], _Right[i], _Right[i + 1], _Left[i + 1]) > MaxTwist;

		if (!twisted)
			return 0;

		int lanes = Math.Max(1, _Lanes);

		return lanes * Math.Max(1, (int)MathF.Ceiling(_Width.MaxHalf * 2.0f / (lanes * MaxColumnWidth)));
	}



	/// <summary>
	/// The same, for the rows of a stretch of road rather than their corners.
	/// </summary>
	public static int Columns(RoadCurveTable _Table, IReadOnlyList<float> _Rows, in RoadWidth _Width, int _Lanes)
	{
		var left = new Vector3[_Rows.Count];
		var right = new Vector3[_Rows.Count];

		for (int i = 0; i < _Rows.Count; i++)
			CrossSection(_Table, _Rows[i], _Width, 0.0f, out left[i], out right[i]);

		return Columns(left, right, _Width, _Lanes);
	}



	public static PolygonMesh Build(RoadCurveTable _Table, float _StartDistance, float _EndDistance, in RoadWidth _Width, float _Spacing, in RoadSimplify _Simplify, int _Lanes, float _TextureRepeat, bool _FitWidth, float _SurfaceOffset, Material _Material)
	{
		var mesh = new PolygonMesh();

		// The rows the borders and the bridge are built on too, so the pieces meet exactly.
		var rows = RoadRows.Build(_Table, _StartDistance, _EndDistance, _Spacing, _Width, _Simplify);
		float repeat = MathF.Max(1.0f, _TextureRepeat);
		var lefts = new Vector3[rows.Count];
		var rights = new Vector3[rows.Count];

		for (int i = 0; i < rows.Count; i++)
			CrossSection(_Table, rows[i], _Width, 0.0f, out lefts[i], out rights[i]);

		int columns = Columns(lefts, rights, _Width, _Lanes);
		int points = Math.Max(1, columns) + 1;
		Vector3 lift = Vector3.Up * _SurfaceOffset;

		var positions = new Vector3[rows.Count, points];
		var handles = new HalfEdgeMesh.VertexHandle[rows.Count, points];
		var uvs = new Vector2[rows.Count, points];

		for (int i = 0; i < rows.Count; i++)
		{
			float distance = rows[i];

			// U runs across the road from the centreline, V along it, so a tiling material follows the road instead
			// of the world, and an edge that moves out takes its texture with it. Fitted to the width, U runs from 0
			// on the left edge to 1 on the right instead, so a texture painted kerb to kerb spans the road once.
			float v = distance / repeat;
			var uvLeft = new Vector2(_FitWidth ? 0.0f : -_Width.LeftAt(distance) / repeat, v);
			var uvRight = new Vector2(_FitWidth ? 1.0f : _Width.RightAt(distance) / repeat, v);

			for (int k = 0; k < points; k++)
			{
				positions[i, k] = ColumnPoint(lefts[i], rights[i], k, points - 1) + lift;
				handles[i, k] = mesh.AddVertices(positions[i, k])[0];
				uvs[i, k] = k == 0 ? uvLeft : k == points - 1 ? uvRight : Vector2.Lerp(uvLeft, uvRight, (float)k / (points - 1));
			}
		}

		for (int i = 0; i < rows.Count - 1; i++)
		{
			if (columns == 0)
			{
				// Two triangles rather than a quad: the cell is flat to within a hair, but a quad that is not quite flat
				// is the mesh's to split (or to drop), and the paint laid on top has to know which way it went.
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i, 0], handles[i, 1], handles[i + 1, 1], uvs[i, 0], uvs[i, 1], uvs[i + 1, 1]);
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i, 0], handles[i + 1, 1], handles[i + 1, 0], uvs[i, 0], uvs[i + 1, 1], uvs[i + 1, 0]);
				continue;
			}

			for (int k = 0; k < columns; k++)
			{
				var middle = (positions[i, k] + positions[i, k + 1] + positions[i + 1, k + 1] + positions[i + 1, k]) * 0.25f;
				var middleUv = (uvs[i, k] + uvs[i, k + 1] + uvs[i + 1, k + 1] + uvs[i + 1, k]) * 0.25f;
				var centre = mesh.AddVertices(middle)[0];

				// Round the column the way the plain cell is wound, each side of it with the middle.
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i, k], handles[i, k + 1], centre, uvs[i, k], uvs[i, k + 1], middleUv);
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i, k + 1], handles[i + 1, k + 1], centre, uvs[i, k + 1], uvs[i + 1, k + 1], middleUv);
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i + 1, k + 1], handles[i + 1, k], centre, uvs[i + 1, k + 1], uvs[i + 1, k], middleUv);
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i + 1, k], handles[i, k], centre, uvs[i + 1, k], uvs[i, k], middleUv);
			}
		}

		return mesh;
	}



	/// <summary>
	/// The height of one cell of road surface, as it is built, over a point in plan. A cell with no columns is split
	/// from its near left corner to its far right one, so which of its two triangles the point falls on is which side
	/// of that diagonal it lies; a cut-up cell is found the column the point lies in, then the triangle of that
	/// column's fan. The triangle's plane is used even for a point just off the cell (paint hanging over the road's
	/// edge), which keeps that paint on the surface's own plane.
	/// </summary>
	public static float CellHeight(Vector3 _NearLeft, Vector3 _NearRight, Vector3 _FarRight, Vector3 _FarLeft, int _Columns, Vector2 _Point)
	{
		if (_Columns <= 0)
		{
			Vector2 diagonal = Flat(_FarRight) - Flat(_NearLeft);
			float side = Cross(diagonal, _Point - Flat(_NearLeft));
			float nearRightSide = Cross(diagonal, Flat(_NearRight) - Flat(_NearLeft));

			return side * nearRightSide >= 0.0f
				? PlaneHeight(_NearLeft, _NearRight, _FarRight, _Point)
				: PlaneHeight(_NearLeft, _FarRight, _FarLeft, _Point);
		}

		// The first column line with the point on its left (the cell's left edge's side) closes the point's column;
		// past the last line it is the rightmost column, edge or no edge.
		int column = _Columns - 1;

		for (int k = 1; k < _Columns; k++)
		{
			Vector2 near = Flat(ColumnPoint(_NearLeft, _NearRight, k, _Columns));
			Vector2 line = Flat(ColumnPoint(_FarLeft, _FarRight, k, _Columns)) - near;

			if (Cross(line, _Point - near) * Cross(line, Flat(_NearLeft) - near) > 0.0f)
			{
				column = k - 1;
				break;
			}
		}

		Vector3 nearLeft = ColumnPoint(_NearLeft, _NearRight, column, _Columns);
		Vector3 nearRight = ColumnPoint(_NearLeft, _NearRight, column + 1, _Columns);
		Vector3 farRight = ColumnPoint(_FarLeft, _FarRight, column + 1, _Columns);
		Vector3 farLeft = ColumnPoint(_FarLeft, _FarRight, column, _Columns);
		Vector3 middle = (nearLeft + nearRight + farRight + farLeft) * 0.25f;

		// The fan's triangle the point is inside has no weight below zero; off the column, the one it is least
		// outside of.
		float best = float.MinValue;
		float height = middle.z;

		FanTriangle(nearLeft, nearRight, middle, _Point, ref best, ref height);
		FanTriangle(nearRight, farRight, middle, _Point, ref best, ref height);
		FanTriangle(farRight, farLeft, middle, _Point, ref best, ref height);
		FanTriangle(farLeft, nearLeft, middle, _Point, ref best, ref height);

		return height;
	}



	/// <summary>
	/// Where a row meets one of the lines that cut a cell into columns: the row's own ends for the outermost two, so
	/// the surface meets its borders exactly, evenly between them for the rest.
	/// </summary>
	private static Vector3 ColumnPoint(Vector3 _Left, Vector3 _Right, int _Column, int _Columns)
	{
		if (_Column <= 0)
			return _Left;

		if (_Column >= _Columns)
			return _Right;

		return Vector3.Lerp(_Left, _Right, (float)_Column / _Columns);
	}



	/// <summary>
	/// How far a cell's far left corner sits above or below the plane through its other three: nothing for a road that
	/// is level, or straight, there.
	/// </summary>
	private static float Twist(Vector3 _NearLeft, Vector3 _NearRight, Vector3 _FarRight, Vector3 _FarLeft)
	{
		return MathF.Abs(_FarLeft.z - PlaneHeight(_NearLeft, _NearRight, _FarRight, Flat(_FarLeft)));
	}



	/// <summary>
	/// Takes one triangle of a fan as the point's when the point lies further inside it (its least weight is larger)
	/// than inside any tried before.
	/// </summary>
	private static void FanTriangle(Vector3 _A, Vector3 _B, Vector3 _C, Vector2 _Point, ref float _Best, ref float _Height)
	{
		Vector2 a = Flat(_A);
		Vector2 b = Flat(_B);
		Vector2 c = Flat(_C);
		float area = Cross(b - a, c - a);

		if (MathF.Abs(area) < 0.0001f)
			return;

		float weightA = Cross(b - _Point, c - _Point) / area;
		float weightB = Cross(c - _Point, a - _Point) / area;
		float weightC = 1.0f - weightA - weightB;
		float least = MathF.Min(weightA, MathF.Min(weightB, weightC));

		if (least <= _Best)
			return;

		_Best = least;
		_Height = weightA * _A.z + weightB * _B.z + weightC * _C.z;
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
