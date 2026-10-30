using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// How a surface fanned out from a centre (a junction, a parking lot) is textured over one owner's stretch of its
/// outline.
/// </summary>
internal interface IFanMapping
{
	/// <summary>
	/// The texture coordinates at a point.
	/// </summary>
	Vector2 Uv(Vector3 _Point);

	/// <summary>
	/// The texture coordinates at a point of the outline held to its kerb: where whatever runs along the edge of the
	/// road in the texture sits.
	/// </summary>
	Vector2 KerbUv(Vector3 _Point);

	/// <summary>
	/// What the fan's centre takes on this owner's triangles.
	/// </summary>
	Vector2 CentreUv(Vector3 _Centre);

	/// <summary>
	/// Where a point sits in the mapping's own terms, in world units: what a straight run along a spoke is measured
	/// against to tell whether the spoke needs cutting into pieces.
	/// </summary>
	Vector2 Measure(Vector3 _Point);
}

/// <summary>
/// Fans a surface out from its centre to its outline, each edge of the outline textured by the mapping of the owner
/// it belongs to. The owners meet on the spokes between their stretches, each side of a spoke taking its own owner's
/// coordinates, the way the arms of a junction meet on lines from the node to the middle of its corners. Spokes
/// along which a mapping bends (a road curving in, an edge's texture turning back on itself) are cut into pieces so
/// the texture follows it. The pieces lie on the spokes, so the fan's triangles keep their planes and the surface is
/// the same however finely it is cut.
/// </summary>
internal static class RoadFanBuilder
{
	/// <summary>
	/// How far (in world units) a straight run of a mapping along a spoke may drift from the mapping itself before
	/// the spoke is cut into pieces.
	/// </summary>
	private const float SpokeTolerance = 2.0f;

	/// <summary>
	/// The most pieces a spoke is cut into.
	/// </summary>
	private const int MaxSpokePieces = 8;



	/// <summary>
	/// Adds the fan to a mesh. <paramref name="_Owners"/> names, for each outline point, the owner of the edge leaving
	/// it; <paramref name="_Kerbs"/> whether that owner's outline points are held to their kerb. Vertices come from
	/// <paramref name="_Vertex"/> when given (to share them with the rest of a mesh, or set their heights), straight
	/// from the mesh otherwise. An open fan (<paramref name="_Closed"/> false) stops at its last point instead of
	/// closing back to its first: a fan from a point on the edge of something else, along which its two end spokes lie.
	/// </summary>
	public static void Build(PolygonMesh _Mesh, Material _Material, Vector3 _Centre, IReadOnlyList<Vector3> _Points, IReadOnlyList<int> _Owners,
		IReadOnlyList<IFanMapping> _Mappings, IReadOnlyList<bool> _Kerbs, Func<Vector3, HalfEdgeMesh.VertexHandle> _Vertex = null, bool _Closed = true)
	{
		int count = _Points.Count;

		if (count < (_Closed ? 3 : 2))
			return;

		_Vertex ??= position => _Mesh.AddVertices(position)[0];

		// The spokes, each cut into as many pieces as either of the owners it borders needs.
		var centreHandle = _Vertex(_Centre);
		var spokes = new HalfEdgeMesh.VertexHandle[count][];

		for (int k = 0; k < count; k++)
		{
			int entering = _Owners[(k + count - 1) % count];
			int pieces = Math.Max(SpokePieces(_Mappings[entering], _Centre, _Points[k]), SpokePieces(_Mappings[_Owners[k]], _Centre, _Points[k]));

			// An open fan's end spokes are shared with whatever it is fanned from the edge of, which knows nothing of
			// any pieces they are cut into: they stay whole, so the two meet point for point.
			if (!_Closed && (k == 0 || k == count - 1))
				pieces = 1;

			var spoke = new HalfEdgeMesh.VertexHandle[pieces + 1];

			spoke[0] = centreHandle;

			for (int j = 1; j <= pieces; j++)
				spoke[j] = _Vertex(j == pieces ? _Points[k] : Vector3.Lerp(_Centre, _Points[k], (float)j / pieces));

			spokes[k] = spoke;
		}

		for (int k = 0; k < (_Closed ? count : count - 1); k++)
		{
			int next = (k + 1) % count;
			var mapping = _Mappings[_Owners[k]];
			bool kerbs = _Kerbs[_Owners[k]];

			Zip(_Mesh, _Material,
				spokes[k], SpokeUvs(mapping, kerbs, _Centre, _Points[k], spokes[k].Length - 1),
				spokes[next], SpokeUvs(mapping, kerbs, _Centre, _Points[next], spokes[next].Length - 1));
		}
	}



	/// <summary>
	/// How many pieces the spoke from the centre to an outline point needs for a straight run of a mapping along each
	/// to stay within <see cref="SpokeTolerance"/> of the mapping itself.
	/// </summary>
	private static int SpokePieces(IFanMapping _Mapping, Vector3 _Centre, Vector3 _End)
	{
		Vector2 from = _Mapping.Measure(_Centre);
		Vector2 to = _Mapping.Measure(_End);
		float drift = 0.0f;

		for (int i = 1; i < 4; i++)
		{
			float t = i * 0.25f;

			drift = MathF.Max(drift, (_Mapping.Measure(Vector3.Lerp(_Centre, _End, t)) - Vector2.Lerp(from, to, t)).Length);
		}

		// A straight run's drift from a curve falls with the square of its length.
		return Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(drift / SpokeTolerance)), 1, MaxSpokePieces);
	}



	/// <summary>
	/// The mapping at each point of a spoke cut into pieces: the owner's own, with whatever the centre and the outline
	/// point are held to instead worked in evenly from nothing at the other end, so the texture never folds back on
	/// itself on the way.
	/// </summary>
	private static Vector2[] SpokeUvs(IFanMapping _Mapping, bool _Kerbs, Vector3 _Centre, Vector3 _End, int _Pieces)
	{
		var uvs = new Vector2[_Pieces + 1];
		Vector2 centre = _Mapping.Uv(_Centre);
		Vector2 centreHeld = _Mapping.CentreUv(_Centre);
		Vector2 end = _Mapping.Uv(_End);
		Vector2 endHeld = _Kerbs ? _Mapping.KerbUv(_End) : end;

		for (int j = 0; j <= _Pieces; j++)
		{
			float t = (float)j / _Pieces;
			Vector2 uv = j == 0 ? centre : j == _Pieces ? end : _Mapping.Uv(Vector3.Lerp(_Centre, _End, t));

			uvs[j] = uv + (centreHeld - centre) * (1.0f - t) + (endHeld - end) * t;
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
}
