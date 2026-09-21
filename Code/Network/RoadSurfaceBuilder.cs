using System;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the road surface of one chunk: a strip of quads between cross-section frames sampled at even distances
/// along the segment's curve.
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
		Vector3 position = _Table.PositionAt(_Distance) + Vector3.Up * _Lift;
		Vector3 right = RoadCurve.RightOf(_Table.TangentAt(_Distance));

		_Left = position - right * _Width.LeftAt(_Distance);
		_Right = position + right * _Width.RightAt(_Distance);
	}



	public static PolygonMesh Build(RoadCurveTable _Table, float _StartDistance, float _EndDistance, in RoadWidth _Width, float _Spacing, float _TextureRepeat, float _SurfaceOffset, Material _Material)
	{
		var mesh = new PolygonMesh();

		float length = MathF.Max(0.0f, _EndDistance - _StartDistance);
		int steps = Math.Max(1, (int)MathF.Ceiling(length / MathF.Max(1.0f, _Spacing)));
		float repeat = MathF.Max(1.0f, _TextureRepeat);

		var left = new HalfEdgeMesh.VertexHandle[steps + 1];
		var right = new HalfEdgeMesh.VertexHandle[steps + 1];
		var uvLeft = new Vector2[steps + 1];
		var uvRight = new Vector2[steps + 1];

		for (int i = 0; i <= steps; i++)
		{
			// The end rows use the exact distances so they match the junction polygons on either side.
			float distance = i == steps ? _EndDistance : _StartDistance + length * i / steps;

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

		for (int i = 0; i < steps; i++)
			MeshUtility.AddTexturedQuad(mesh, _Material, left[i], right[i], right[i + 1], left[i + 1], uvLeft[i], uvRight[i], uvRight[i + 1], uvLeft[i + 1]);

		return mesh;
	}
}
