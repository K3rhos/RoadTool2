using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

public static class MeshUtility
{
	public static HalfEdgeMesh.VertexHandle GetOrAddVertex(PolygonMesh _Mesh, Dictionary<Vector3, HalfEdgeMesh.VertexHandle> _Cache, Vector3 _Pos)
	{
		if (!_Cache.TryGetValue(_Pos, out var handle))
		{
			handle = _Mesh.AddVertices(_Pos)[0];
			_Cache[_Pos] = handle;
		}

		return handle;
	}



	/// <summary>
	/// Adds a quad face; returns an invalid handle when the mesh refused it (a duplicate vertex, a topology clash).
	/// </summary>
	public static HalfEdgeMesh.FaceHandle AddTexturedQuad(PolygonMesh _Mesh, Material _Material,
		HalfEdgeMesh.VertexHandle _A, HalfEdgeMesh.VertexHandle _B,
		HalfEdgeMesh.VertexHandle _C, HalfEdgeMesh.VertexHandle _D,
		Vector2 _UvA, Vector2 _UvB, Vector2 _UvC, Vector2 _UvD)
	{
		if (HasDuplicateVertex(_A, _B, _C, _D))
			return default;

		var face = _Mesh.AddFace(_A, _B, _C, _D);

		if (!face.IsValid)
			return default;

		_Mesh.SetFaceMaterial(face, _Material);
		_Mesh.SetFaceTextureCoords(face, new List<Vector2> { _UvA, _UvB, _UvC, _UvD });

		return face;
	}



	/// <summary>
	/// Adds a triangle face; returns an invalid handle when the mesh refused it.
	/// </summary>
	public static HalfEdgeMesh.FaceHandle AddTexturedTriangle(PolygonMesh _Mesh, Material _Material,
		HalfEdgeMesh.VertexHandle _A, HalfEdgeMesh.VertexHandle _B,
		HalfEdgeMesh.VertexHandle _C,
		Vector2 _UvA, Vector2 _UvB, Vector2 _UvC)
	{
		if (HasDuplicateVertex(_A, _B, _C))
			return default;

		var face = _Mesh.AddFace(_A, _B, _C);

		if (!face.IsValid)
			return default;

		_Mesh.SetFaceMaterial(face, _Material);
		_Mesh.SetFaceTextureCoords(face, new List<Vector2> { _UvA, _UvB, _UvC });

		return face;
	}



	/// <summary>
	/// Paints a face's vertices with blend weights (what a blendable material mixes its layers by), in the order
	/// the face's vertices were given.
	/// </summary>
	public static void SetFaceBlends(PolygonMesh _Mesh, HalfEdgeMesh.FaceHandle _Face, IReadOnlyList<Color32> _Blends)
	{
		if (!_Face.IsValid || !_Mesh.GetFaceVerticesConnectedToFace(_Face, out var edges))
			return;

		int count = System.Math.Min(edges.Length, _Blends.Count);

		for (int i = 0; i < count; i++)
			_Mesh.SetVertexBlend(edges[i], _Blends[i]);
	}



	private static bool HasDuplicateVertex(HalfEdgeMesh.VertexHandle _A, HalfEdgeMesh.VertexHandle _B, HalfEdgeMesh.VertexHandle _C)
	{
		return _A.Equals(_B) || _A.Equals(_C) || _B.Equals(_C);
	}



	private static bool HasDuplicateVertex(HalfEdgeMesh.VertexHandle _A, HalfEdgeMesh.VertexHandle _B, HalfEdgeMesh.VertexHandle _C, HalfEdgeMesh.VertexHandle _D)
	{
		return _A.Equals(_B) || _A.Equals(_C) || _A.Equals(_D) ||
			_B.Equals(_C) || _B.Equals(_D) ||
			_C.Equals(_D);
	}
}
