using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the rounded end of a road that stops: a half disc of surface carried on past the last row, with the
/// border swept round it, so a dead end reads as a turning head rather than a road sliced through. The piece is
/// built beyond the road's own end, on the end row itself, so its seam with the last chunk is exact and nothing
/// about the road's length changes.
/// </summary>
internal static class RoadCapBuilder
{
	/// <summary>
	/// The arc is drawn in steps of about this many degrees.
	/// </summary>
	private const float ArcStepDegrees = 9.0f;

	/// <summary>
	/// How deep a road's rounded end reaches past it, as a share of the road's half width. 1 is a half circle.
	/// </summary>
	public const float DepthFactor = 1.0f;



	/// <summary>
	/// How far the rounded end of a road reaches past the node, or 0 where the road type does not round its ends.
	/// </summary>
	public static float Depth(RoadProfile _Profile, in RoadWidth _Width, float _Distance)
	{
		if (_Profile is null || !_Profile.RoundedEnds)
			return 0.0f;

		return (_Width.LeftAt(_Distance) + _Width.RightAt(_Distance)) * 0.5f * DepthFactor;
	}



	/// <summary>
	/// The rounded end at one end of a road. <paramref name="_Distance"/> is the end's own distance along the curve
	/// (0 or the table's length) and <paramref name="_AtStart"/> which end it is; the cap reaches out from there.
	/// </summary>
	public static PolygonMesh Build(RoadCurveTable _Table, float _Distance, bool _AtStart, in RoadWidth _Width, RoadProfile _Profile, float _Lift, float _Depth)
	{
		var mesh = new PolygonMesh();

		if (_Depth <= 0.01f)
			return mesh;

		_Table.Frame(_Distance, out Vector3 centre, out Vector3 tangent);

		centre += Vector3.Up * _Lift;

		// Out of the road, and the road's own right at that end. The arc keeps the road's grade because the
		// direction it reaches along carries the curve's climb with it.
		Vector3 forward = (_AtStart ? -tangent : tangent).Normal;
		Vector3 right = RoadCurve.RightOf(tangent);
		float leftHalf = _Width.LeftAt(_Distance);
		float rightHalf = _Width.RightAt(_Distance);

		if (leftHalf + rightHalf <= 0.01f)
			return mesh;

		int steps = Math.Max(4, (int)MathF.Ceiling(180.0f / ArcStepDegrees));
		var outline = new List<Vector3>(steps + 1);
		var outwards = new List<Vector3>(steps + 1);
		var offsets = new List<float>(steps + 1);
		var aheads = new List<float>(steps + 1);

		Arc(centre, forward, right, leftHalf, rightHalf, _Depth, steps, outline, outwards, offsets, aheads);

		AddSurface(mesh, centre, outline, offsets, aheads, _Distance, _AtStart, _Profile);
		AddBorder(mesh, outline, outwards, _Profile);

		return mesh;
	}



	/// <summary>
	/// The outline of a road's rounded end in network space, lifted onto the road surface, for the tools that need
	/// to know where road is (the ground under it is held down and brought up to its kerb like a junction's).
	/// Empty where the road does not round that end.
	/// </summary>
	public static List<Vector3> Outline(RoadCurveTable _Table, float _Distance, bool _AtStart, in RoadWidth _Width, RoadProfile _Profile, float _Lift, out Vector3 _Centre)
	{
		var outline = new List<Vector3>();
		float depth = Depth(_Profile, _Width, _Distance);

		_Table.Frame(_Distance, out _Centre, out Vector3 tangent);

		_Centre += Vector3.Up * _Lift;

		float leftHalf = _Width.LeftAt(_Distance);
		float rightHalf = _Width.RightAt(_Distance);

		if (depth <= 0.01f || leftHalf + rightHalf <= 0.01f)
			return outline;

		int steps = Math.Max(4, (int)MathF.Ceiling(180.0f / ArcStepDegrees));

		Arc(_Centre, (_AtStart ? -tangent : tangent).Normal, RoadCurve.RightOf(tangent), leftHalf, rightHalf, depth, steps, outline, null, null, null);

		return outline;
	}



	/// <summary>
	/// The half ellipse from the road's left edge, round the tip, to its right edge: where each point is, which way
	/// is out of the road there, and how far across and ahead of the end row it sits.
	/// </summary>
	private static void Arc(Vector3 _Centre, Vector3 _Forward, Vector3 _Right, float _LeftHalf, float _RightHalf, float _Depth, int _Steps,
		List<Vector3> _Points, List<Vector3> _Outwards, List<float> _Offsets, List<float> _Aheads)
	{
		for (int i = 0; i <= _Steps; i++)
		{
			float angle = MathF.PI * (i / (float)_Steps - 0.5f);
			float sin = MathF.Sin(angle);
			float cos = MathF.Cos(angle);
			float offset = sin * (sin >= 0.0f ? _RightHalf : _LeftHalf);
			float ahead = _Depth * cos;

			_Points.Add(_Centre + _Right * offset + _Forward * ahead);
			_Outwards?.Add((_Right * sin + _Forward * cos).Normal);
			_Offsets?.Add(offset);
			_Aheads?.Add(ahead);
		}
	}



	/// <summary>
	/// The half disc of road surface: a fan from the middle of the end row out to the arc, so every triangle is
	/// planar and the row itself is the fan's own base.
	/// </summary>
	private static void AddSurface(PolygonMesh _Mesh, Vector3 _Centre, List<Vector3> _Outline, List<float> _Offsets, List<float> _Aheads,
		float _Distance, bool _AtStart, RoadProfile _Profile)
	{
		float repeat = MathF.Max(1.0f, _Profile.RoadTextureRepeat);
		var material = _Profile.ResolvedRoadMaterial;
		var centre = _Mesh.AddVertices(_Centre)[0];
		var ring = new HalfEdgeMesh.VertexHandle[_Outline.Count];

		for (int i = 0; i < _Outline.Count; i++)
			ring[i] = _Mesh.AddVertices(_Outline[i])[0];

		// U runs across the road from its centreline and V along it, the same as the road's own surface, so the
		// asphalt carries on through the seam instead of restarting at it.
		Vector2 centreUv = new(0.0f, _Distance / repeat);

		// Which way round the fan closes, so it faces up like the road it carries on from.
		Vector3 first = _Outline[0] - _Centre;
		Vector3 second = _Outline[1] - _Centre;
		bool flip = Vector3.Cross(first, second).z < 0.0f;

		for (int i = 0; i < _Outline.Count - 1; i++)
		{
			var uvA = new Vector2(_Offsets[i] / repeat, (_Distance + (_AtStart ? -_Aheads[i] : _Aheads[i])) / repeat);
			var uvB = new Vector2(_Offsets[i + 1] / repeat, (_Distance + (_AtStart ? -_Aheads[i + 1] : _Aheads[i + 1])) / repeat);

			if (flip)
				MeshUtility.AddTexturedTriangle(_Mesh, material, centre, ring[i + 1], ring[i], centreUv, uvB, uvA);
			else
				MeshUtility.AddTexturedTriangle(_Mesh, material, centre, ring[i], ring[i + 1], centreUv, uvA, uvB);
		}
	}



	/// <summary>
	/// The border round the arc, open at both ends: what closes it off is the road's own border, which runs into
	/// it at the end row.
	/// </summary>
	private static void AddBorder(PolygonMesh _Mesh, List<Vector3> _Outline, List<Vector3> _Outwards, RoadProfile _Profile)
	{
		if (!_Profile.HasBorder)
			return;

		var path = new List<RoadBorderBuilder.PathPoint>(_Outline.Count);
		float along = 0.0f;

		for (int i = 0; i < _Outline.Count; i++)
		{
			if (i > 0)
				along += _Outline[i].Distance(_Outline[i - 1]);

			path.Add(new RoadBorderBuilder.PathPoint(_Outline[i], _Outwards[i], along));
		}

		RoadBorderBuilder.BuildPath(_Mesh, _Profile, path, false, false);
	}
}
