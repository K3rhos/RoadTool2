using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates a parking lot: the slab of surface inside its edge, the kerb round that edge with a gap at every road
/// that opens into it, and the bays painted inside.
/// </summary>
internal static class RoadLotBuilder
{
	/// <summary>
	/// An opening narrower than this is not worth cutting the kerb for.
	/// </summary>
	private const float MinOpening = 1.0f;



	/// <summary>
	/// The lot's surface: a fan from its middle out to its edge, so every triangle is planar.
	/// </summary>
	public static PolygonMesh BuildSurface(RoadLot _Lot, RoadLotOutline _Outline, RoadProfile _Profile, float _Lift)
	{
		var mesh = new PolygonMesh();

		if (_Outline.Count < 3)
			return mesh;

		Vector3 lift = Vector3.Up * _Lift;
		float repeat = _Profile.RoadTextureRepeat;
		var material = _Profile.ResolvedRoadMaterial;

		var centre = mesh.AddVertices(_Lot.Centre + lift)[0];
		var ring = new HalfEdgeMesh.VertexHandle[_Outline.Count];

		for (int i = 0; i < _Outline.Count; i++)
			ring[i] = mesh.AddVertices(_Outline.Points[i] + lift)[0];

		Vector2 centreUv = PlanarUv(_Lot.Centre, repeat);

		for (int i = 0; i < _Outline.Count; i++)
		{
			int next = (i + 1) % _Outline.Count;

			MeshUtility.AddTexturedTriangle(mesh, material, centre, ring[i], ring[next],
				centreUv, PlanarUv(_Outline.Points[i], repeat), PlanarUv(_Outline.Points[next], repeat));
		}

		return mesh;
	}



	/// <summary>
	/// The kerb round the lot: one run of border between each pair of openings, closed off at both its ends. A lot
	/// with no openings at all gets the whole ring in one run.
	/// </summary>
	public static PolygonMesh BuildBorder(RoadLot _Lot, RoadLotOutline _Outline, RoadProfile _Profile, IReadOnlyList<RoadLotOpening> _Openings, float _Lift, BorderDebug _Debug = null)
	{
		var mesh = new PolygonMesh();

		if (!_Profile.HasBorder || _Outline.Count < 3)
			return mesh;

		var gaps = new List<(float From, float To)>();

		foreach (var opening in _Openings)
		{
			if (_Outline.Wrap(opening.GapTo - opening.GapFrom) > MinOpening)
				gaps.Add((opening.GapFrom, opening.GapTo));
		}

		gaps.Sort((a, b) => a.From.CompareTo(b.From));

		if (gaps.Count == 0)
		{
			// No way in: the ring is one run, closed where it started.
			AddRun(mesh, _Lot, _Outline, _Profile, 0.0f, _Outline.Length, _Lift, _Debug);

			return mesh;
		}

		for (int i = 0; i < gaps.Count; i++)
		{
			float from = gaps[i].To;
			float to = gaps[(i + 1) % gaps.Count].From;

			if (_Outline.Wrap(to - from) > MinOpening)
				AddRun(mesh, _Lot, _Outline, _Profile, from, to, _Lift, _Debug);
		}

		return mesh;
	}



	private static void AddRun(PolygonMesh _Mesh, RoadLot _Lot, RoadLotOutline _Outline, RoadProfile _Profile, float _From, float _To, float _Lift, BorderDebug _Debug)
	{
		var run = _Outline.Between(_From, _To);
		var path = new List<RoadBorderBuilder.PathPoint>(run.Count);

		foreach (var (point, outward, along) in run)
			path.Add(new RoadBorderBuilder.PathPoint(point + Vector3.Up * _Lift, outward, along));

		RoadBorderBuilder.BuildPath(_Mesh, _Profile, path, true, true, _Debug);
	}



	/// <summary>
	/// The bays painted inside the lot: a row along the inside of each edge, and back to back rows down the middle
	/// where the lot has the room. Nothing is painted over an opening or over an aisle.
	/// </summary>
	public static PolygonMesh BuildMarkings(RoadLot _Lot, ParkingStyle _Parking, IReadOnlyList<RoadLotOpening> _Openings, float _Lift)
	{
		var mesh = new PolygonMesh();

		if (_Parking is null || _Parking.Line is null || !_Parking.Line.Enabled || _Parking.Line.Width <= 0.0f)
			return mesh;

		float lift = _Lift + RoadMarkingBuilder.Lift;

		if (_Parking.EdgeBays)
		{
			// Along each edge, from the corner: bays lean into the way traffic runs round the lot.
			AddEdgeBays(mesh, _Lot, _Parking, _Openings, lift, new Vector2(1.0f, 0.0f), new Vector2(0.0f, 1.0f));
			AddEdgeBays(mesh, _Lot, _Parking, _Openings, lift, new Vector2(0.0f, 1.0f), new Vector2(-1.0f, 0.0f));
			AddEdgeBays(mesh, _Lot, _Parking, _Openings, lift, new Vector2(-1.0f, 0.0f), new Vector2(0.0f, -1.0f));
			AddEdgeBays(mesh, _Lot, _Parking, _Openings, lift, new Vector2(0.0f, -1.0f), new Vector2(1.0f, 0.0f));
		}

		if (_Parking.CentreRows)
			AddCentreRows(mesh, _Lot, _Parking, _Openings, lift);

		return mesh;
	}



	/// <summary>
	/// One edge's worth of bays: dividers every bay along the straight part of the edge, reaching into the lot.
	/// </summary>
	private static void AddEdgeBays(PolygonMesh _Mesh, RoadLot _Lot, ParkingStyle _Parking, IReadOnlyList<RoadLotOpening> _Openings, float _Lift, Vector2 _Outward, Vector2 _Along)
	{
		bool across = MathF.Abs(_Outward.x) > 0.5f;
		float half = across ? _Lot.HalfWidth : _Lot.HalfDepth;
		float span = across ? _Lot.HalfDepth : _Lot.HalfWidth;

		// The bays stop a corner and a bay's depth short of each end, so the row on the next edge round has room
		// for its own bays and nothing is painted into the corner itself.
		float usable = (span - _Lot.Radius - _Parking.BayDepth) * 2.0f;
		float pitch = MathF.Max(1.0f, _Parking.Pitch);
		int bays = (int)MathF.Floor(usable / pitch);

		if (bays < 1 || _Parking.BayDepth >= half)
			return;

		// Centred on the edge, so the row of bays is even at both corners.
		float start = -(bays * pitch) * 0.5f;
		Vector2 inward = -_Outward;
		var direction = Rotate(inward, _Parking.BayAngle);

		for (int i = 0; i <= bays; i++)
		{
			Vector2 edge = _Outward * half + _Along * (start + i * pitch);
			Vector2 back = edge + direction * _Parking.BayDepth;

			AddLine(_Mesh, _Lot, _Parking, _Openings, edge, back, _Lift);
		}

		if (!_Parking.BackLine)
			return;

		Vector2 backFrom = _Outward * half + _Along * start + direction * _Parking.BayDepth;
		Vector2 backTo = _Outward * half + _Along * (start + bays * pitch) + direction * _Parking.BayDepth;

		AddLine(_Mesh, _Lot, _Parking, _Openings, backFrom, backTo, _Lift);
	}



	/// <summary>
	/// Rows of bays back to back down the middle of the lot, as many as fit between the edge bays with an aisle
	/// either side of each row. Each row's two halves lean opposite ways, as they do where you drive down an aisle.
	/// </summary>
	private static void AddCentreRows(PolygonMesh _Mesh, RoadLot _Lot, ParkingStyle _Parking, IReadOnlyList<RoadLotOpening> _Openings, float _Lift)
	{
		float edgeDepth = _Parking.EdgeBays ? _Parking.BayDepth : 0.0f;
		float rowDepth = _Parking.BayDepth * 2.0f;
		float step = rowDepth + _Parking.AisleWidth;

		// The band left between the edge bays and their aisles.
		float from = -_Lot.HalfDepth + edgeDepth + _Parking.AisleWidth;
		float to = _Lot.HalfDepth - edgeDepth - _Parking.AisleWidth;
		int rows = (int)MathF.Floor((to - from + _Parking.AisleWidth) / step);

		if (rows < 1)
			return;

		float used = rows * step - _Parking.AisleWidth;
		float first = from + (to - from - used) * 0.5f;

		// The rows stop short of the edge bays on the left and right so there is a way round them.
		float reach = _Lot.HalfWidth - (_Parking.EdgeBays ? _Parking.BayDepth : 0.0f) - _Parking.AisleWidth;

		if (reach <= _Parking.Pitch)
			return;

		float pitch = MathF.Max(1.0f, _Parking.Pitch);
		int bays = (int)MathF.Floor(reach * 2.0f / pitch);

		if (bays < 1)
			return;

		float start = -(bays * pitch) * 0.5f;

		for (int row = 0; row < rows; row++)
		{
			float middle = first + row * step + _Parking.BayDepth;

			for (int i = 0; i <= bays; i++)
			{
				float across = start + i * pitch;
				var backward = Rotate(new Vector2(0.0f, -1.0f), -_Parking.BayAngle);
				var forward = Rotate(new Vector2(0.0f, 1.0f), _Parking.BayAngle);

				AddLine(_Mesh, _Lot, _Parking, _Openings, new Vector2(across, middle), new Vector2(across, middle) + backward * _Parking.BayDepth, _Lift);
				AddLine(_Mesh, _Lot, _Parking, _Openings, new Vector2(across, middle), new Vector2(across, middle) + forward * _Parking.BayDepth, _Lift);
			}

			// The line the two halves back onto.
			AddLine(_Mesh, _Lot, _Parking, _Openings, new Vector2(start, middle), new Vector2(start + bays * pitch, middle), _Lift);
		}
	}



	/// <summary>
	/// One painted line of the layout, in the lot's own coordinates, unless a road opens into the lot over it.
	/// </summary>
	private static void AddLine(PolygonMesh _Mesh, RoadLot _Lot, ParkingStyle _Parking, IReadOnlyList<RoadLotOpening> _Openings, Vector2 _From, Vector2 _To, float _Lift)
	{
		Vector3 from = _Lot.Point(_From.x, _From.y) + Vector3.Up * _Lift;
		Vector3 to = _Lot.Point(_To.x, _To.y) + Vector3.Up * _Lift;

		foreach (var opening in _Openings)
		{
			// Nothing painted where a road comes in, nor in the flares either side of it.
			float clearance = opening.Reach + _Parking.BayWidth * 0.5f;

			if (NearestOn(from, to, opening.Mouth).Distance(opening.Mouth) < clearance)
				return;
		}

		Vector3 along = to - from;

		if (along.IsNearZeroLength)
			return;

		Vector3 across = Vector3.Cross(along.Normal, Vector3.Up) * (_Parking.Line.Width * 0.5f);
		var handles = _Mesh.AddVertices(from - across, from + across, to + across, to - across);

		MeshUtility.AddTexturedQuad(_Mesh, _Parking.ResolvedLineMaterial, handles[0], handles[1], handles[2], handles[3],
			new Vector2(0.0f, 0.0f), new Vector2(1.0f, 0.0f), new Vector2(1.0f, along.Length / MathF.Max(1.0f, _Parking.Line.Width)), new Vector2(0.0f, along.Length / MathF.Max(1.0f, _Parking.Line.Width)));
	}



	private static Vector3 NearestOn(Vector3 _From, Vector3 _To, Vector3 _Point)
	{
		Vector3 span = _To - _From;
		float lengthSquared = span.LengthSquared;
		float t = lengthSquared > 0.0f ? Math.Clamp(Vector3.Dot(_Point - _From, span) / lengthSquared, 0.0f, 1.0f) : 0.0f;

		return _From + span * t;
	}



	private static Vector2 Rotate(Vector2 _Vector, float _Degrees)
	{
		float radians = _Degrees.DegreeToRadian();
		float cos = MathF.Cos(radians);
		float sin = MathF.Sin(radians);

		return new Vector2(_Vector.x * cos - _Vector.y * sin, _Vector.x * sin + _Vector.y * cos);
	}



	private static Vector2 PlanarUv(Vector3 _Position, float _Repeat)
	{
		return new Vector2(_Position.x, _Position.y) / MathF.Max(1.0f, _Repeat);
	}
}
