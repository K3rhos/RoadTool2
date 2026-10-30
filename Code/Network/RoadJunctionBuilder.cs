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
	/// Each arm's texture runs on into the junction over the arm's own stretch of the outline (see
	/// <see cref="RoadJunctionLayout.PolygonArms"/>) and meets the next arm's on a line from the node to the middle
	/// of the corner between them, where the borders meet too (or, for a corner on one of the rows, to where the outline
	/// comes closest to the node). A road texture with something across its width (lanes,
	/// worn wheel tracks, dark edges) turns the corner with the road instead of being cut by a projection laid over
	/// it, and nothing is stretched; a road that curves on its way in has its texture bend with it (see
	/// <see cref="RoadFanBuilder"/>). An arm with a different road surface from the junction's is not the junction's
	/// to continue: its share takes the main road's mapping as it is, as if that road ran on into it, with no kerb of
	/// its own to follow.
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

		var main = Mapping(_Main, _TextureRepeat, _FitWidth);
		var mappings = new RoadArmMapping[_Layout.Arms.Count];
		var own = new bool[_Layout.Arms.Count];

		for (int i = 0; i < mappings.Length; i++)
		{
			own[i] = i == mainIndex || _SameSurface(_Layout.Arms[i]);
			mappings[i] = i == mainIndex || !own[i] ? main : Mapping(_Layout.Arms[i], _TextureRepeat, _FitWidth);
		}

		// A road splitting in two round a nose far out from the node (a highway's exit or entry, a fork) is laid out
		// as roads of its own instead (see RoadSplitBuilder), when its texture's edges are what has to wrap the nose.
		if (_FitWidth && Array.TrueForAll(own, sameSurface => sameSurface) && RoadSplitBuilder.TryBuild(_Layout, mappings, _Material, out var split))
			return split;

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
