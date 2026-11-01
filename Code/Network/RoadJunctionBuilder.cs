using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the surface of a junction: the layout's polygon fanned out from the node. Each triangle of the fan is
/// planar, so none can be dropped by the mesh builder whatever the arms' heights do.
/// </summary>
internal static class RoadJunctionBuilder
{
	/// <summary>
	/// How far below its foot a skirt's closed-off end reaches: well under any ground dipping beside it.
	/// </summary>
	private const float SkirtCloseDepth = 20.0f;

	/// <summary>
	/// Each arm's texture runs on into the junction over the arm's own stretch of the outline (see
	/// <see cref="RoadJunctionLayout.PolygonArms"/>) and meets the next arm's on a line from the node to the middle
	/// of the corner between them, where the borders meet too (or, for a corner on one of the rows, to where the outline
	/// comes closest to the node). A road texture with something across its width (lanes,
	/// worn wheel tracks, dark edges) turns the corner with the road instead of being cut by a projection laid over
	/// it, and nothing is stretched; a road that curves on its way in has its texture bend with it (see
	/// <see cref="RoadFanBuilder"/>). An arm with a different road surface from the junction's still has the
	/// junction's surface turn into it the same way (carried on across it instead, a texture fitted to the main road's
	/// width would run off its edges and start again); a road that blends the junction's surface into its own (see
	/// <see cref="RoadProfile.RoadBlendMaterial"/>) picks it up from there. An arm whose road has banks has the
	/// junction's surface carried on down under the ground past its half of each corner, as its road's own is past its
	/// edges. <paramref name="_ProfileOf"/> gives each arm's road type.
	/// </summary>
	public static PolygonMesh Build(RoadJunctionLayout _Layout, RoadJunctionLayout.Arm _Main, Func<RoadJunctionLayout.Arm, bool> _SameSurface, float _TextureRepeat, bool _FitWidth, Material _Material,
		Func<RoadJunctionLayout.Arm, RoadProfile> _ProfileOf = null)
	{
		var mesh = new PolygonMesh();
		var polygon = _Layout.Polygon;
		var owners = _Layout.PolygonArms;

		if (polygon.Count < 3)
			return mesh;

		int mainIndex = _Main is null ? -1 : _Layout.Arms.IndexOf(_Main);

		if (owners is null || owners.Count != polygon.Count || mainIndex < 0)
			return Build(_Layout, _TextureRepeat, _Material);

		var armMappings = new RoadArmMapping[_Layout.Arms.Count];
		var mappings = new IFanMapping[_Layout.Arms.Count];
		var own = new bool[_Layout.Arms.Count];
		bool allSame = true;

		for (int i = 0; i < mappings.Length; i++)
		{
			var arm = _Layout.Arms[i];

			armMappings[i] = Mapping(arm, _TextureRepeat, _FitWidth);
			mappings[i] = armMappings[i];
			own[i] = true;
			allSame &= i == mainIndex || _SameSurface(arm);
		}

		// A road splitting in two round a nose far out from the node (a highway's exit or entry, a fork) is laid out
		// as roads of its own instead (see RoadSplitBuilder), when its texture's edges are what has to wrap the nose.
		if (_FitWidth && allSame && RoadSplitBuilder.TryBuild(_Layout, armMappings, _Material, out var split))
		{
			AddSkirts(split, _Layout, mappings, _ProfileOf, _Material);

			return split;
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

		RoadFanBuilder.Build(mesh, _Material, _Layout.Centre, points, arms, mappings, own);
		AddSkirts(mesh, _Layout, mappings, _ProfileOf, _Material);

		return mesh;
	}



	/// <summary>
	/// Under banks (see <see cref="RoadProfile.SkirtWidth"/>), each stretch of the outline an arm with a skirt owns (its
	/// half of the corner either side of it) gets a skirt carried on out past it and down, so the ground coming over the
	/// kerb never shows a gap beneath it: the arm's road's own skirt carried on round the corner, in that road's surface
	/// and mapping, so whatever of it the ground leaves showing matches the road's. Its end on the arm's row heads out
	/// along the row, where the road's skirt carries on from it; where it meets the next arm's half of the corner
	/// instead it is closed off, down past its foot, so nothing can be seen under it from that side. A strip of its
	/// own, so the mesh never has to join it to the fan's rim.
	/// </summary>
	private static void AddSkirts(PolygonMesh _Mesh, RoadJunctionLayout _Layout, IReadOnlyList<IFanMapping> _Mappings, Func<RoadJunctionLayout.Arm, RoadProfile> _ProfileOf, Material _Material)
	{
		if (_ProfileOf is null)
			return;

		var polygon = _Layout.Polygon;
		var owners = _Layout.PolygonArms;
		int count = polygon.Count;
		var rows = new HashSet<int>(_Layout.RowStarts);
		var profiles = new RoadProfile[_Layout.Arms.Count];

		// A bridge's deck has no skirt, so nor does its share of the corners.
		for (int i = 0; i < profiles.Length; i++)
			profiles[i] = _Layout.Arms[i].Segment.Bridge is null ? _ProfileOf(_Layout.Arms[i]) : null;

		for (int first = 0; first < count; first++)
		{
			int owner = owners[first];

			// A stretch starts where an arm's half of a corner does.
			if (!Skirted(first) || (Skirted(Previous(first)) && owners[Previous(first)] == owner))
				continue;

			var profile = profiles[owner];
			var arm = _Layout.Arms[owner];
			var mapping = new RoadArmMapping(arm.Table, arm.AtStart, arm.Trim, arm.Widths, profile.RoadTextureRepeat, profile.RoadTextureFitsWidth, true);
			var material = profile.ResolvedRoadMaterial;
			bool fit = profile.RoadTextureFitsWidth;
			var vertices = new List<Vector3> { polygon[first] };
			int last = first;

			while (Next(last) != first && Skirted(Next(last)) && owners[Next(last)] == owner)
				last = Next(last);

			for (int e = first; ; e = Next(e))
			{
				vertices.Add(polygon[Next(e)]);

				if (e == last)
					break;
			}

			// Out from each point square to the outline there; along the row at a row's end, as the road's skirt goes.
			var outwards = new Vector3[vertices.Count];

			for (int j = 0; j < vertices.Count; j++)
			{
				int before = j == 0 ? Previous(first) : -1;
				int after = j == vertices.Count - 1 ? Next(last) : -1;

				if (before >= 0 && rows.Contains(before))
					outwards[j] = Flat(polygon[first] - polygon[before]).Normal;
				else if (after >= 0 && rows.Contains(after))
					outwards[j] = Flat(polygon[after] - polygon[Next(after)]).Normal;
				else
					outwards[j] = (EdgeOutward(j == 0 ? polygon[before] : vertices[j - 1], vertices[j]) + EdgeOutward(vertices[j], j == vertices.Count - 1 ? polygon[Next(after)] : vertices[j + 1])).Normal;
			}

			var top = new HalfEdgeMesh.VertexHandle[vertices.Count];
			var bottom = new HalfEdgeMesh.VertexHandle[vertices.Count];
			var bottoms = new Vector3[vertices.Count];

			for (int j = 0; j < vertices.Count; j++)
			{
				bottoms[j] = vertices[j] + outwards[j] * profile.SkirtWidth - Vector3.Up * profile.SkirtDepth;
				top[j] = _Mesh.AddVertices(vertices[j])[0];
				bottom[j] = _Mesh.AddVertices(bottoms[j])[0];
			}

			// The outline runs counter-clockwise seen from above, the junction on its left: wound so the strip faces up.
			for (int j = 0; j < vertices.Count - 1; j++)
			{
				Vector2 topA = SkirtUv(mapping, vertices[j], fit);
				Vector2 topB = SkirtUv(mapping, vertices[j + 1], fit);
				Vector2 bottomA = SkirtUv(mapping, bottoms[j], fit);
				Vector2 bottomB = SkirtUv(mapping, bottoms[j + 1], fit);

				MeshUtility.AddTexturedTriangle(_Mesh, material, top[j], bottom[j], bottom[j + 1], topA, bottomA, bottomB);
				MeshUtility.AddTexturedTriangle(_Mesh, material, top[j], bottom[j + 1], top[j + 1], topA, bottomB, topB);
			}

			// Closed off where it stops beside another arm's half of the corner.
			if (!rows.Contains(Previous(first)))
				CloseSkirt(_Mesh, material, vertices[0], bottoms[0], vertices[1] - vertices[0], profile, mapping, fit);

			if (!rows.Contains(Next(last)))
				CloseSkirt(_Mesh, material, vertices[^1], bottoms[^1], vertices[^2] - vertices[^1], profile, mapping, fit);
		}

		bool Skirted(int _Edge) => owners[_Edge] >= 0 && !rows.Contains(_Edge) && profiles[owners[_Edge]] is { HasSkirt: true };
		int Previous(int _Edge) => (_Edge + count - 1) % count;
		int Next(int _Edge) => (_Edge + 1) % count;
	}



	/// <summary>
	/// A point of a skirt in its road's own mapping: carried on out past the edge at the texture's own size, a texture
	/// fitted to the width turned back on itself there (see <see cref="RoadSurfaceBuilder.SkirtShare"/>).
	/// </summary>
	private static Vector2 SkirtUv(RoadArmMapping _Mapping, Vector3 _Point, bool _FitWidth)
	{
		Vector2 uv = _Mapping.Uv(_Point);

		return _FitWidth ? new Vector2(RoadSurfaceBuilder.SkirtShare(uv.x), uv.y) : uv;
	}



	/// <summary>
	/// Closes off a skirt where it stops beside another arm's half of a corner: a face straight down from its end (its
	/// top on the kerb, its bottom out at its foot) to well under its foot, facing away from it (<paramref name="_Into"/>
	/// runs back along it), so the ground dipping beside it never shows a way in under it.
	/// </summary>
	private static void CloseSkirt(PolygonMesh _Mesh, Material _Material, Vector3 _Top, Vector3 _Bottom, Vector3 _Into, RoadProfile _Profile, RoadArmMapping _Mapping, bool _FitWidth)
	{
		float repeat = MathF.Max(1.0f, _Profile.RoadTextureRepeat);
		float topDrop = _Profile.SkirtDepth + SkirtCloseDepth;
		Vector3 topFoot = _Top - Vector3.Up * topDrop;
		Vector3 bottomFoot = _Bottom - Vector3.Up * SkirtCloseDepth;
		Vector2 topUv = SkirtUv(_Mapping, _Top, _FitWidth);
		Vector2 bottomUv = SkirtUv(_Mapping, _Bottom, _FitWidth);
		Vector2 topFootUv = topUv + new Vector2(0.0f, topDrop / repeat);
		Vector2 bottomFootUv = bottomUv + new Vector2(0.0f, SkirtCloseDepth / repeat);

		var top = _Mesh.AddVertices(_Top)[0];
		var bottom = _Mesh.AddVertices(_Bottom)[0];
		var below = _Mesh.AddVertices(bottomFoot)[0];
		var under = _Mesh.AddVertices(topFoot)[0];

		// Wound to face away from the skirt it closes.
		if (Vector3.Dot(Vector3.Cross(_Bottom - _Top, bottomFoot - _Top), _Into) > 0.0f)
		{
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, top, below, bottom, topUv, bottomFootUv, bottomUv);
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, top, under, below, topUv, topFootUv, bottomFootUv);
		}
		else
		{
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, top, bottom, below, topUv, bottomUv, bottomFootUv);
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, top, below, under, topUv, bottomFootUv, topFootUv);
		}
	}



	/// <summary>
	/// Out of the junction, flat, from the edge of its outline between two points (which runs counter-clockwise).
	/// </summary>
	private static Vector3 EdgeOutward(Vector3 _From, Vector3 _To)
	{
		Vector3 along = Flat(_To - _From).Normal;

		return new Vector3(along.y, -along.x, 0.0f);
	}



	private static Vector3 Flat(Vector3 _Vector)
	{
		return _Vector.WithZ(0.0f);
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
	/// An arm's mapping, with the node held inside the arm's texture: a lane that sits off to one side of the road's
	/// centreline (a ramp starting from the side of a highway) can end beside the node rather than across it, and its
	/// texture's edges would otherwise be drawn out across the junction from the node.
	/// </summary>
	private static RoadArmMapping Mapping(RoadJunctionLayout.Arm _Arm, float _TextureRepeat, bool _FitWidth)
	{
		return new RoadArmMapping(_Arm.Table, _Arm.AtStart, _Arm.Trim, _Arm.Widths, _TextureRepeat, _FitWidth, true);
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
