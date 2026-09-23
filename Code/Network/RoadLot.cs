using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// A flat piece of road that is not a road: a rectangle of surface with a kerb round it, roads opening into it
/// where their nodes sit on its edge, and a parking layout painted inside. Like a junction, it is a place the
/// roads end at rather than pass through, which is why it carries its own profile and its own ids.
/// </summary>
public sealed class RoadLot
{
	public int Id { get; set; }

	/// <summary>
	/// The middle of the lot, in network space. A lot is flat: this is the height of all of it.
	/// </summary>
	public Vector3 Centre { get; set; }

	/// <summary>
	/// Which way the lot is turned. Its width runs across this, its depth along it.
	/// </summary>
	[Range(0.0f, 360.0f)] public float Yaw { get; set; }

	[Range(200.0f, 20000.0f)] public float Width { get; set { field = value.Clamp(MinSize, 1000000.0f); } } = 2000.0f;

	[Range(200.0f, 20000.0f)] public float Depth { get; set { field = value.Clamp(MinSize, 1000000.0f); } } = 1500.0f;

	/// <summary>
	/// How far the lot's own corners are rounded off.
	/// </summary>
	[Range(0.0f, 1000.0f)] public float CornerRadius { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 150.0f;

	/// <summary>
	/// How far the kerb flares out into each road that opens into the lot, the way a junction's corners are
	/// rounded. The lot's surface fills the corner, so the road starts this far out from the lot's edge.
	/// </summary>
	[Range(0.0f, 1000.0f)] public float EntranceRadius { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 250.0f;

	/// <summary>
	/// The road type the lot is surfaced and kerbed as. Null falls back to the network's default profile.
	/// </summary>
	public RoadProfile Profile { get; set; }

	/// <summary>
	/// How the inside is laid out. Null leaves the lot as a bare yard.
	/// </summary>
	public ParkingStyle Parking { get; set; }

	/// <summary>
	/// The nodes on the lot's edge that roads open into it at. They are kept on the edge as the lot is moved and
	/// resized, and the roads at them are made to leave square to the edge.
	/// </summary>
	public List<int> Openings { get; set; } = new();

	/// <summary>
	/// No lot is smaller than this; below it the corners and the kerb have nothing to sit on.
	/// </summary>
	public const float MinSize = 200.0f;

	[Hide, JsonIgnore] public Rotation Rotation => Rotation.FromYaw(Yaw);

	/// <summary>
	/// The way across the lot (its width) and along it (its depth).
	/// </summary>
	[Hide, JsonIgnore] public Vector3 Across => Rotation.Right;

	[Hide, JsonIgnore] public Vector3 Along => Rotation.Forward;

	[Hide, JsonIgnore] public float HalfWidth => Width * 0.5f;

	[Hide, JsonIgnore] public float HalfDepth => Depth * 0.5f;

	/// <summary>
	/// The corner radius as the lot's own size allows it.
	/// </summary>
	[Hide, JsonIgnore] public float Radius => MathF.Max(0.0f, MathF.Min(CornerRadius, MathF.Min(HalfWidth, HalfDepth) - 1.0f));



	/// <summary>
	/// A point of the lot from its own coordinates: across, then along, from its middle.
	/// </summary>
	public Vector3 Point(float _Across, float _Along)
	{
		return Centre + Across * _Across + Along * _Along;
	}



	/// <summary>
	/// The lot's own coordinates of a point, its height ignored.
	/// </summary>
	public Vector2 Local(Vector3 _Point)
	{
		Vector3 offset = _Point - Centre;

		return new Vector2(Vector3.Dot(offset, Across), Vector3.Dot(offset, Along));
	}



	/// <summary>
	/// One of the lot's four edges, counter-clockwise from the one its width is measured to: the middle of the
	/// edge, the way along it, the way out of the lot across it, and half its length.
	/// </summary>
	public void EdgeFrame(int _Edge, out Vector3 _Centre, out Vector3 _Direction, out Vector3 _Outward, out float _Half)
	{
		switch (((_Edge % 4) + 4) % 4)
		{
			case 1:
				_Outward = Along;
				_Direction = -Across;
				_Half = HalfWidth;
				break;

			case 2:
				_Outward = -Across;
				_Direction = -Along;
				_Half = HalfDepth;
				break;

			case 3:
				_Outward = -Along;
				_Direction = Across;
				_Half = HalfWidth;
				break;

			default:
				_Outward = Across;
				_Direction = Along;
				_Half = HalfDepth;
				break;
		}

		_Centre = Centre + _Outward * (_Edge % 2 == 0 ? HalfWidth : HalfDepth);
	}



	/// <summary>
	/// The edge of the lot a point lies nearest to, and how far along that edge from its middle.
	/// </summary>
	public int NearestEdge(Vector3 _Point, out float _Along)
	{
		Vector2 local = Local(_Point);
		var distances = new[] { HalfWidth - local.x, HalfDepth - local.y, HalfWidth + local.x, HalfDepth + local.y };
		int edge = 0;

		for (int i = 1; i < 4; i++)
		{
			if (distances[i] < distances[edge])
				edge = i;
		}

		_Along = edge switch
		{
			1 => -local.x,
			2 => -local.y,
			3 => local.x,
			_ => local.y
		};

		return edge;
	}



	/// <summary>
	/// A point on one of the edges, from how far along it sits.
	/// </summary>
	public Vector3 EdgePoint(int _Edge, float _Along)
	{
		EdgeFrame(_Edge, out Vector3 centre, out Vector3 direction, out _, out _);

		return centre + direction * _Along;
	}



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(Centre);
		_Hash.Add(Yaw);
		_Hash.Add(Width);
		_Hash.Add(Depth);
		_Hash.Add(CornerRadius);
		_Hash.Add(EntranceRadius);
	}
}

/// <summary>
/// Where a road opens into a lot: which edge it meets and where along it, how wide the road is and how far the
/// kerb flares into it. The flares are part of the lot's outline, so the lot's surface fills the corners between
/// itself and the road, and the road starts at the far end of them.
/// </summary>
public sealed class RoadLotOpening
{
	public int NodeId;
	public int Edge;
	public float Along;
	public float HalfWidth;
	public float Flare;

	/// <summary>
	/// The middle of the opening on the edge, and the way out of the lot there.
	/// </summary>
	public Vector3 Mouth;
	public Vector3 Outward;

	/// <summary>
	/// The road's own first row, where the lot hands over to it: its two edge points in the order the lot's edge
	/// runs, and the way the road heads there. The lot is built to those points rather than to a line guessed
	/// from its edge, so the two surfaces meet exactly however the road turns or climbs as it leaves.
	/// </summary>
	public Vector3 MouthFrom;
	public Vector3 MouthTo;
	public Vector3 Away;

	/// <summary>
	/// The stretch of the outline the kerb leaves out for the road, once the outline is built.
	/// </summary>
	public float GapFrom;
	public float GapTo;

	/// <summary>
	/// How much room the opening and its flares take along the edge, either side of its middle.
	/// </summary>
	public float Reach => HalfWidth + Flare;

	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(Edge);
		_Hash.Add(Along);
		_Hash.Add(HalfWidth);
		_Hash.Add(Flare);
	}
}

/// <summary>
/// A lot's edge as a closed polyline: where every point is, which way is out of the lot there, and how far round
/// the edge it sits. Everything the lot is built from works in that distance round the edge, which is what lets
/// the openings cut the kerb and the surface fill what is left.
/// </summary>
public sealed class RoadLotOutline
{
	/// <summary>
	/// Corners and flares are rounded in steps of about this many degrees.
	/// </summary>
	private const float ArcStepDegrees = 15.0f;

	public readonly List<Vector3> Points = new();
	public readonly List<Vector3> Outwards = new();
	public readonly List<float> Distances = new();

	/// <summary>
	/// The whole way round.
	/// </summary>
	public float Length { get; private set; }

	public int Count => Points.Count;



	/// <summary>
	/// The lot's edge: its four sides, its rounded corners, and a flare out to each road that opens into it.
	/// </summary>
	public static RoadLotOutline Build(RoadLot _Lot, IReadOnlyList<RoadLotOpening> _Openings = null)
	{
		var outline = new RoadLotOutline();
		float radius = _Lot.Radius;

		for (int edge = 0; edge < 4; edge++)
		{
			_Lot.EdgeFrame(edge, out Vector3 centre, out Vector3 direction, out Vector3 outward, out float half);

			float from = -half + radius;
			float to = half - radius;

			// The openings on this edge, in the order they are met walking along it.
			var openings = new List<RoadLotOpening>();

			if (_Openings is not null)
			{
				foreach (var opening in _Openings)
				{
					if (opening.Edge == edge && opening.Along > from && opening.Along < to)
						openings.Add(opening);
				}

				openings.Sort((a, b) => a.Along.CompareTo(b.Along));
			}

			float walked = from;

			foreach (var opening in openings)
			{
				float flare = MathF.Max(0.0f, opening.Flare);
				float leaves = opening.Along - opening.HalfWidth - flare;
				float returns = opening.Along + opening.HalfWidth + flare;

				// Up to where the kerb leaves the edge...
				outline.Add(centre + direction * walked, outward);
				outline.Add(centre + direction * leaves, outward);

				// ...round into the road, across its first row, and back out to the edge on the other side.
				outline.AddCurve(centre + direction * leaves, direction, opening.MouthFrom, opening.Away);

				opening.GapFrom = outline.Distances[^1];

				outline.Add(opening.MouthTo, RoadCurve.RightOf(-opening.Away));

				opening.GapTo = outline.Distances[^1];

				outline.AddCurve(opening.MouthTo, -opening.Away, centre + direction * returns, direction);

				walked = returns;
			}

			outline.Add(centre + direction * walked, outward);
			outline.Add(centre + direction * to, outward);

			// The corner into the next edge.
			_Lot.EdgeFrame(edge + 1, out _, out Vector3 nextDirection, out Vector3 nextOutward, out _);
			outline.AddCorner(centre + direction * to, outward, nextOutward, radius);
		}

		outline.Length = outline.Distances[^1] + outline.Points[^1].Distance(outline.Points[0]);

		return outline;
	}



	/// <summary>
	/// Where the edge is at a distance round it, and which way is out of the lot there.
	/// </summary>
	public void At(float _Distance, out Vector3 _Point, out Vector3 _Outward)
	{
		float distance = Wrap(_Distance);

		for (int i = Count - 1; i >= 0; i--)
		{
			if (distance < Distances[i] && i > 0)
				continue;

			int next = (i + 1) % Count;
			float from = Distances[i];
			float to = i + 1 < Count ? Distances[next] : Length;
			float t = to > from ? Math.Clamp((distance - from) / (to - from), 0.0f, 1.0f) : 0.0f;

			_Point = Vector3.Lerp(Points[i], Points[next], t);
			_Outward = Vector3.Lerp(Outwards[i], Outwards[next], t).Normal;

			return;
		}

		_Point = Points[0];
		_Outward = Outwards[0];
	}



	/// <summary>
	/// The points of the edge between two distances round it, ends included, in order. A stretch that runs past
	/// the end of the loop carries on from its start.
	/// </summary>
	public List<(Vector3 Point, Vector3 Outward, float Along)> Between(float _From, float _To)
	{
		var run = new List<(Vector3, Vector3, float)>();

		At(_From, out Vector3 start, out Vector3 startOutward);
		run.Add((start, startOutward, 0.0f));

		float span = Wrap(_To - _From);

		if (span <= 0.01f)
			span = Length;

		// Every outline point strictly inside the stretch, in the order they are met going round.
		int first = IndexAfter(_From);

		for (int step = 0; step < Count; step++)
		{
			int index = (first + step) % Count;
			float along = Wrap(Distances[index] - _From);

			if (along <= 0.01f)
				continue;

			if (along >= span - 0.01f)
				break;

			run.Add((Points[index], Outwards[index], along));
		}

		At(_To, out Vector3 end, out Vector3 endOutward);
		run.Add((end, endOutward, span));

		return run;
	}



	/// <summary>
	/// How far round the edge the point nearest another one sits.
	/// </summary>
	public float NearestDistance(Vector3 _Point)
	{
		float best = float.MaxValue;
		float distance = 0.0f;

		for (int i = 0; i < Count; i++)
		{
			int next = (i + 1) % Count;
			Vector3 from = Points[i];
			Vector3 span = Points[next] - from;
			float lengthSquared = span.LengthSquared;
			float t = lengthSquared > 0.0f ? Math.Clamp(Vector3.Dot(_Point - from, span) / lengthSquared, 0.0f, 1.0f) : 0.0f;
			float offset = (from + span * t).Distance(_Point);

			if (offset >= best)
				continue;

			best = offset;
			distance = Distances[i] + span.Length * t;
		}

		return Wrap(distance);
	}



	public float Wrap(float _Distance)
	{
		if (Length <= 0.0f)
			return 0.0f;

		float distance = _Distance % Length;

		return distance < 0.0f ? distance + Length : distance;
	}



	private int IndexAfter(float _Distance)
	{
		for (int i = 0; i < Count; i++)
		{
			if (Distances[i] > _Distance + 0.01f)
				return i;
		}

		return 0;
	}



	/// <summary>
	/// The kerb turning out of the lot's edge into the side of a road (or back out of it): a curve that leaves
	/// along one direction and arrives along another, whatever the two ends are. The way out of the surface is
	/// always to the right of the way round, which is what keeps it pointing out of the lot all the way round.
	/// </summary>
	private void AddCurve(Vector3 _From, Vector3 _FromDirection, Vector3 _To, Vector3 _ToDirection)
	{
		float span = _From.Distance(_To);

		if (span <= 0.01f)
		{
			Add(_To, RoadCurve.RightOf(_ToDirection));
			return;
		}

		// Flat pulls: a road that leaves the lot on a slope would otherwise drag the curve's control points up or
		// down with it, and the kerb would sag under the lot (or hump over the road) between its two ends. Keeping
		// them level leaves the height a smooth run from one end to the other and nothing outside it.
		Vector3 fromDirection = Flat(_FromDirection);
		Vector3 toDirection = Flat(_ToDirection);

		// A quarter turn's worth of pull at each end, which is what makes the curve read as an arc.
		float pull = span * 0.39f;
		Vector3 controlFrom = _From + fromDirection * pull;
		Vector3 controlTo = _To - toDirection * pull;
		int steps = Math.Max(2, (int)MathF.Ceiling(90.0f / ArcStepDegrees));

		for (int i = 0; i <= steps; i++)
		{
			float t = (float)i / steps;
			float u = 1.0f - t;

			Vector3 point = u * u * u * _From + 3.0f * u * u * t * controlFrom + 3.0f * u * t * t * controlTo + t * t * t * _To;
			Vector3 tangent = 3.0f * u * u * (controlFrom - _From) + 6.0f * u * t * (controlTo - controlFrom) + 3.0f * t * t * (_To - controlTo);

			Add(point, RoadCurve.RightOf(tangent.IsNearZeroLength ? _ToDirection : tangent));
		}
	}



	/// <summary>
	/// A direction with its climb taken out, as a unit vector. A direction that is pure climb keeps what it had.
	/// </summary>
	private static Vector3 Flat(Vector3 _Direction)
	{
		Vector3 flat = _Direction.WithZ(0.0f);

		return flat.IsNearZeroLength ? _Direction.Normal : flat.Normal;
	}



	/// <summary>
	/// One of the lot's own corners, turning from one edge's outward direction to the next.
	/// </summary>
	private void AddCorner(Vector3 _Corner, Vector3 _Outward, Vector3 _NextOutward, float _Radius)
	{
		if (_Radius <= 0.01f)
			return;

		// The turn's centre is a radius in from the edge that ends here, which is also a radius in from the next.
		Vector3 pivot = _Corner - _Outward * _Radius;
		int steps = Math.Max(1, (int)MathF.Ceiling(90.0f / ArcStepDegrees));

		for (int i = 1; i < steps; i++)
		{
			float fraction = (float)i / steps;
			Vector3 radial = Vector3.Lerp(_Outward, _NextOutward, fraction).Normal;

			Add(pivot + radial * _Radius, radial);
		}
	}



	private void Add(Vector3 _Point, Vector3 _Outward)
	{
		if (Count > 0 && Points[^1].Distance(_Point) < 0.01f)
			return;

		Distances.Add(Count > 0 ? Distances[^1] + Points[^1].Distance(_Point) : 0.0f);
		Points.Add(_Point);
		Outwards.Add(_Outward.Normal);
	}
}
