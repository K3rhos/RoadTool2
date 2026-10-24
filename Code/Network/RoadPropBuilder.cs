using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Works out where the props of a road stand: each row of a prop set spread evenly along the segment's built range
/// (in as many equal bays as come nearest to the row's spacing, one prop per bay at the row's phase), and the set's
/// junction props a fixed distance before each junction the segment meets. Within the road's border a prop stands
/// on the border; beyond it, on whatever ground is found below. Everything random (chance, scatter, turn, scale) is
/// drawn from a hash of where the prop is, so a rebuild puts the same props back. A row can instead be laid end to
/// end, as a chain of straight pieces along the line it stands on (see <see cref="BuildChain"/>).
/// </summary>
internal static class RoadPropBuilder
{
	/// <summary>
	/// A prefab to stand as a child of the props piece, in network space. The scale is per axis of the prefab's own,
	/// so a piece laid end to end can be stretched along its length alone.
	/// </summary>
	public readonly record struct Placement(GameObject Prefab, Vector3 Position, Rotation Rotation, Vector3 Scale);

	/// <summary>
	/// A segment's whole built range (curve distances) and which of its ends meet a junction.
	/// </summary>
	public readonly record struct PropSpan(float RangeStart, float RangeEnd, bool JunctionAtStart, bool JunctionAtEnd)
	{
		public void AddToHash(ref StableHash _Hash)
		{
			_Hash.Add(RangeStart);
			_Hash.Add(RangeEnd);
			_Hash.Add(JunctionAtStart ? 1 : 0);
			_Hash.Add(JunctionAtEnd ? 1 : 0);
		}
	}

	/// <summary>
	/// Everything one piece's placements are computed from.
	/// </summary>
	public readonly struct Context
	{
		public readonly RoadCurveTable Table;
		public readonly float Start;
		public readonly float End;
		public readonly PropSpan Span;
		public readonly int SegmentId;
		public readonly RoadWidth Width;
		public readonly RoadProfile Profile;
		public readonly bool IsBridge;
		public readonly float Lift;
		public readonly Func<Vector3, float?> GroundAt;

		public Context(RoadCurveTable _Table, float _Start, float _End, in PropSpan _Span, int _SegmentId, in RoadWidth _Width, RoadProfile _Profile, bool _IsBridge, float _Lift, Func<Vector3, float?> _GroundAt)
		{
			Table = _Table;
			Start = _Start;
			End = _End;
			Span = _Span;
			SegmentId = _SegmentId;
			Width = _Width;
			Profile = _Profile;
			IsBridge = _IsBridge;
			Lift = _Lift;
			GroundAt = _GroundAt;
		}

		/// <summary>
		/// Whether a curve distance falls to this piece to build.
		/// </summary>
		public bool Owns(float _Distance)
		{
			return _Distance >= Start && _Distance < End;
		}
	}



	public static void Build(in Context _Context, RoadPropSet _Set, List<Placement> _Placements)
	{
		if (_Set.Rows is not null)
		{
			for (int r = 0; r < _Set.Rows.Count; r++)
				BuildRow(_Context, _Set, r, _Placements);
		}

		if (_Set.JunctionRows is not null)
		{
			for (int r = 0; r < _Set.JunctionRows.Count; r++)
				BuildJunctionRow(_Context, _Set, r, _Placements);
		}
	}



	private static void BuildRow(in Context _Context, RoadPropSet _Set, int _Row, List<Placement> _Placements)
	{
		var row = _Set.Rows[_Row];

		if (!row.Prefab.IsValid())
			return;

		if (row.Chain)
		{
			BuildChain(_Context, _Set, _Row, _Placements);
			return;
		}

		float rangeStart = _Context.Span.RangeStart;
		float range = MathF.Max(0.0f, _Context.Span.RangeEnd - rangeStart);
		int bays = Math.Max(1, (int)MathF.Round(range / MathF.Max(1.0f, row.Spacing)));
		float bay = range / bays;

		for (int k = 0; k < bays; k++)
		{
			float distance = rangeStart + (k + row.Phase) * bay;

			if (!_Context.Owns(distance))
				continue;

			foreach (bool left in Sides(row.Side))
			{
				var random = RandomFor(_Set.Seed, _Context.SegmentId, _Row, k, left);

				if (random.NextSingle() > row.Chance)
					continue;

				// Scatter shifts along the road and across it, up to the row's scatter either way.
				float along = distance + (random.NextSingle() * 2.0f - 1.0f) * row.Scatter;
				float across = (random.NextSingle() * 2.0f - 1.0f) * row.Scatter;
				float turn = row.RandomYaw ? random.NextSingle() * 360.0f : 0.0f;
				float scale = MathX.Lerp(row.MinScale, row.MaxScale, random.NextSingle());

				// The ranges are drawn after everything else, so a row that has none keeps the places, turns and sizes
				// it always had; and each draws whether it is a range or not, so making one a range leaves the others'
				// picks as they were.
				float offset = Pick(row.Offset, random);
				float yaw = Pick(row.Yaw, random) + turn;
				float pitch = Pick(row.Pitch, random);
				float roll = Pick(row.Roll, random);

				along = Math.Clamp(along, _Context.Span.RangeStart, _Context.Span.RangeEnd);

				_Placements.Add(Place(_Context, row.Prefab, along, left, row.Anchor, offset + across, row.HeightOffset, yaw, pitch, roll, scale, false, row.FollowRoad));
			}
		}
	}



	/// <summary>
	/// A row laid end to end: the line its pieces stand along (the road's edge or the border's, out by the row's
	/// offset) is cut into as many equal lengths as come nearest to the row's spacing, and each piece spans one of them
	/// as a straight chord, stretched or squeezed along its length to reach from one cut to the next. Measured on that
	/// line rather than along the road's middle, the pieces fill the outside of a bend and don't crowd its inside.
	/// </summary>
	private static void BuildChain(in Context _Context, RoadPropSet _Set, int _Row, List<Placement> _Placements)
	{
		var row = _Set.Rows[_Row];
		float start = _Context.Span.RangeStart;
		float end = _Context.Span.RangeEnd;

		if (end - start < 1.0f)
			return;

		float length = MathF.Max(1.0f, row.Spacing);

		// One line for the whole chain: pieces at different offsets could not meet, so a range stands it in its middle.
		float fromEdge = (row.Anchor == PropAnchor.BorderEdge ? _Context.Profile.FlatBorderWidth : 0.0f) + (row.Offset.Min + row.Offset.Max) * 0.5f;
		var extent = PrefabExtent(row.Prefab);

		foreach (bool left in Sides(row.Side))
		{
			var line = Line(_Context, left, fromEdge, start, end, length);
			float total = line[^1].Run;
			int pieces = Math.Max(1, (int)MathF.Round(total / length));

			for (int k = 0; k < pieces; k++)
			{
				var (fromDistance, from) = At(line, total * k / pieces);
				var (toDistance, to) = At(line, total * (k + 1) / pieces);

				// A piece belongs to the chunk its middle falls in; every chunk lays out the same chain.
				if (!_Context.Owns((fromDistance + toDistance) * 0.5f))
					continue;

				var random = RandomFor(_Set.Seed, _Context.SegmentId, _Row, k, left);

				if (random.NextSingle() > row.Chance)
					continue;

				float turn = row.RandomYaw ? random.NextSingle() * 360.0f : 0.0f;
				float scale = MathX.Lerp(row.MinScale, row.MaxScale, random.NextSingle());
				float yaw = Pick(row.Yaw, random) + turn;
				float pitch = Pick(row.Pitch, random);
				float roll = Pick(row.Roll, random);

				// Both ends on what they stand on: the border within it, the ground beyond.
				from.z += HeightAt(_Context, from, fromEdge);
				to.z += HeightAt(_Context, to, fromEdge);

				_Placements.Add(Link(row, extent, from, to, left, length, yaw, pitch, roll, scale));
			}
		}
	}



	/// <summary>
	/// The line a chained row stands along, at road height, sampled closely enough to measure it: each sample's curve
	/// distance, its point, and the length in plan run along the line so far.
	/// </summary>
	private static List<(float Distance, Vector3 Point, float Run)> Line(in Context _Context, bool _Left, float _FromEdge, float _Start, float _End, float _Length)
	{
		// Several samples to a piece, so a cut lands on the line rather than on a chord cutting across it.
		float step = Math.Clamp(_Length / 8.0f, 2.0f, 25.0f);
		int count = Math.Max(2, (int)MathF.Ceiling((_End - _Start) / step) + 1);
		var line = new List<(float Distance, Vector3 Point, float Run)>(count);
		Vector3 previous = default;
		float run = 0.0f;

		for (int i = 0; i < count; i++)
		{
			float distance = MathX.Lerp(_Start, _End, i / (float)(count - 1));

			_Context.Table.Frame(distance, out Vector3 point, out Vector3 tangent);

			Vector3 right = RoadCurve.RightOf(tangent);
			float edge = _Left ? _Context.Width.LeftAt(distance) : _Context.Width.RightAt(distance);
			Vector3 position = point + Vector3.Up * _Context.Lift + (_Left ? -right : right) * (edge + _FromEdge);

			if (i > 0)
				run += new Vector2(position.x - previous.x, position.y - previous.y).Length;

			line.Add((distance, position, run));
			previous = position;
		}

		return line;
	}



	/// <summary>
	/// Where a run along the line ends up, and the curve distance there.
	/// </summary>
	private static (float Distance, Vector3 Point) At(List<(float Distance, Vector3 Point, float Run)> _Line, float _Run)
	{
		int low = 0;
		int high = _Line.Count - 1;

		if (_Run <= _Line[low].Run)
			return (_Line[low].Distance, _Line[low].Point);

		if (_Run >= _Line[high].Run)
			return (_Line[high].Distance, _Line[high].Point);

		while (high - low > 1)
		{
			int middle = (low + high) / 2;

			if (_Line[middle].Run <= _Run)
				low = middle;
			else
				high = middle;
		}

		float span = _Line[high].Run - _Line[low].Run;
		float t = span > 0.0f ? (_Run - _Line[low].Run) / span : 0.0f;

		return (MathX.Lerp(_Line[low].Distance, _Line[high].Distance, t), Vector3.Lerp(_Line[low].Point, _Line[high].Point, t));
	}



	/// <summary>
	/// One piece of a chain, from one cut to the next: turned along that chord with its right away from the road (as
	/// a single prop's is), level, or with Follow Road Normal tilted from end to end; the prefab's own axis that runs
	/// along the road stretched to the chord; and its middle along that axis put on the chord's middle, wherever its
	/// pivot is.
	/// </summary>
	private static Placement Link(RoadPropRow _Row, BBox? _Extent, Vector3 _From, Vector3 _To, bool _Left, float _Length, float _Yaw, float _Pitch, float _Roll, float _Scale)
	{
		Vector3 chord = _To - _From;
		Vector3 flat = chord.WithZ(0.0f);
		Vector3 forward = flat.IsNearZeroLength ? Vector3.Forward : flat.Normal;
		Vector3 up = Vector3.Up;
		float span = flat.Length;

		if (_Row.FollowRoad && !chord.IsNearZeroLength)
		{
			forward = chord.Normal;
			up = Vector3.Cross(RoadCurve.RightOf(chord), forward).Normal;
			span = chord.Length;
		}

		if (_Left)
			forward = -forward;

		var turn = Rotation.From(_Pitch, _Yaw, _Roll);
		var rotation = Rotation.LookAt(forward, up) * turn;
		int axis = LengthAxis(turn);
		float stretch = span / _Length;
		var scale = new Vector3(axis == 0 ? stretch : 1.0f, axis == 1 ? stretch : 1.0f, axis == 2 ? stretch : 1.0f) * _Scale;
		Vector3 position = (_From + _To) * 0.5f + Vector3.Up * _Row.HeightOffset;

		if (_Extent is BBox extent)
		{
			Vector3 along = Axis(axis);
			float centre = Vector3.Dot(extent.Center, along) * Vector3.Dot(scale, along);

			position -= rotation * (along * centre);
		}

		return new Placement(_Row.Prefab, position, rotation, scale);
	}



	/// <summary>
	/// Which of the prefab's own axes (0 forward, 1 left, 2 up) runs nearest along the road once the prop's own turn
	/// is applied: the one a chained piece is stretched along.
	/// </summary>
	private static int LengthAxis(Rotation _Turn)
	{
		float forward = MathF.Abs((_Turn * Vector3.Forward).x);
		float left = MathF.Abs((_Turn * Vector3.Left).x);
		float up = MathF.Abs((_Turn * Vector3.Up).x);

		return forward >= left && forward >= up ? 0 : left >= up ? 1 : 2;
	}



	private static Vector3 Axis(int _Axis)
	{
		return _Axis == 0 ? Vector3.Forward : _Axis == 1 ? Vector3.Left : Vector3.Up;
	}



	/// <summary>
	/// The box round a prefab's models, in the frame it is placed in (its root's own scale included), or null when it
	/// has none: where a chained piece's middle lies along its length, wherever its pivot sits.
	/// </summary>
	private static BBox? PrefabExtent(GameObject _Prefab)
	{
		if (!_Prefab.IsValid())
			return null;

		var root = _Prefab.WorldTransform.WithScale(1.0f);
		BBox? extent = null;

		foreach (var renderer in _Prefab.Components.GetAll<ModelRenderer>(FindMode.EverythingInSelfAndDescendants))
		{
			if (renderer.Model is null)
				continue;

			var box = renderer.Model.Bounds.Transform(root.ToLocal(renderer.WorldTransform));

			extent = extent is BBox existing ? existing.AddBBox(box) : box;
		}

		return extent;
	}



	/// <summary>
	/// Junction props stand a set distance in from each end of the range that meets a junction, and face it.
	/// </summary>
	private static void BuildJunctionRow(in Context _Context, RoadPropSet _Set, int _Row, List<Placement> _Placements)
	{
		var row = _Set.JunctionRows[_Row];

		if (!row.Prefab.IsValid())
			return;

		for (int end = 0; end < 2; end++)
		{
			bool atStart = end == 0;

			if (atStart ? !_Context.Span.JunctionAtStart : !_Context.Span.JunctionAtEnd)
				continue;

			float distance = atStart ? _Context.Span.RangeStart + row.Distance : _Context.Span.RangeEnd - row.Distance;

			if (distance < _Context.Span.RangeStart || distance > _Context.Span.RangeEnd || !_Context.Owns(distance))
				continue;

			// The row's sides are given looking into the junction: at the start end that is looking back along
			// the road, which swaps them.
			foreach (bool leftIntoJunction in Sides(row.Side))
			{
				bool left = atStart ? !leftIntoJunction : leftIntoJunction;
				var random = RandomFor(_Set.Seed, _Context.SegmentId, 1000 + _Row, end, left);

				if (random.NextSingle() > row.Chance)
					continue;

				_Placements.Add(Place(_Context, row.Prefab, distance, left, row.Anchor, row.Offset, row.HeightOffset, row.Yaw, 0.0f, 0.0f, 1.0f, atStart, row.FollowRoad));
			}
		}
	}



	/// <summary>
	/// A prop's transform at a curve distance on one side of the road: its forward along the road (or back along
	/// it, for a junction prop at the start end), its right away from the road, its up straight up or, following the
	/// road, square to the road's surface; then turned by the yaw and, in that turned frame, tipped by the pitch and
	/// leant by the roll.
	/// </summary>
	private static Placement Place(in Context _Context, GameObject _Prefab, float _Distance, bool _Left, PropAnchor _Anchor, float _Offset, float _HeightOffset, float _Yaw, float _Pitch, float _Roll, float _Scale, bool _FaceBack, bool _FollowRoad)
	{
		var table = _Context.Table;
		var profile = _Context.Profile;

		table.Frame(_Distance, out Vector3 point, out Vector3 tangent);

		Vector3 centre = point + Vector3.Up * _Context.Lift;
		Vector3 right = RoadCurve.RightOf(tangent);
		Vector3 along = Vector3.Cross(Vector3.Up, right).Normal;
		Vector3 outward = _Left ? -right : right;

		float fromEdge = (_Anchor == PropAnchor.BorderEdge ? profile.FlatBorderWidth : 0.0f) + _Offset;
		Vector3 position = centre + outward * ((_Left ? _Context.Width.LeftAt(_Distance) : _Context.Width.RightAt(_Distance)) + fromEdge);

		position.z += HeightAt(_Context, position, fromEdge) + _HeightOffset;

		// Right pointing away from the road: forward runs along the road on the right side, back along it on
		// the left. A junction prop faces the junction instead, which turns it round at the start end.
		Vector3 forward = _Left ? -along : along;
		Vector3 up = Vector3.Up;

		// Square to the surface: forward climbs and falls with the road, and up leans back from it by as much. The
		// road is level across, so its right stays the prop's right and only the grade tilts it.
		if (_FollowRoad && !tangent.IsNearZeroLength)
		{
			Vector3 slope = tangent.Normal;

			up = Vector3.Cross(right, slope).Normal;
			forward = _Left ? -slope : slope;
		}

		if (_FaceBack)
			forward = -forward;

		var rotation = Rotation.LookAt(forward, up) * Rotation.From(_Pitch, _Yaw, _Roll);

		return new Placement(_Prefab, position, rotation, Vector3.One * _Scale);
	}



	/// <summary>
	/// A value from a range, from the prop's own random stream; a fixed value is itself (it still takes its draw).
	/// </summary>
	private static float Pick(RangedFloat _Range, Random _Random)
	{
		return MathX.Lerp(_Range.Min, _Range.Max, _Random.NextSingle());
	}



	/// <summary>
	/// How high above the road surface a prop stands at a distance out from the road's edge: on the border while
	/// within it (and anywhere on a bridge's deck), else on the ground found below, else level with the border's
	/// outer edge.
	/// </summary>
	private static float HeightAt(in Context _Context, Vector3 _Position, float _FromEdge)
	{
		var profile = _Context.Profile;

		if (_Context.IsBridge || _FromEdge <= profile.FlatBorderWidth + 0.01f)
			return BorderHeight(profile, _FromEdge);

		float? ground = _Context.GroundAt?.Invoke(_Position);

		return ground.HasValue ? ground.Value - _Position.z : BorderHeight(profile, _FromEdge);
	}



	/// <summary>
	/// The nominal height of the border's top at a distance out from the road's edge (the country verge's wander
	/// and any taper into another road type aside).
	/// </summary>
	private static float BorderHeight(RoadProfile _Profile, float _FromEdge)
	{
		switch (_Profile.Border)
		{
			case RoadBorderStyle.Sidewalk:
				return _FromEdge <= _Profile.BorderWidth + 0.01f ? _Profile.CurbHeight : 0.0f;

			case RoadBorderStyle.CountrySide:
				{
					if (_FromEdge <= _Profile.BorderWidth)
						return 0.0f;

					float fraction = Math.Clamp((_FromEdge - _Profile.BorderWidth) / MathF.Max(1.0f, _Profile.VergeWidth), 0.0f, 1.0f);

					return -_Profile.Drop - fraction * _Profile.VergeDepth;
				}

			default:
				return 0.0f;
		}
	}



	private static IEnumerable<bool> Sides(RoadSide _Side)
	{
		if (_Side != RoadSide.Right)
			yield return true;

		if (_Side != RoadSide.Left)
			yield return false;
	}



	/// <summary>
	/// A random stream for one prop place, the same every time the piece is rebuilt.
	/// </summary>
	private static Random RandomFor(int _Seed, int _SegmentId, int _Row, int _Index, bool _Left)
	{
		var hash = StableHash.Create();
		hash.Add(_Seed);
		hash.Add(_SegmentId);
		hash.Add(_Row);
		hash.Add(_Index);
		hash.Add(_Left ? 1 : 0);

		long value = hash.Value;

		return new Random(unchecked((int)value ^ (int)(value >> 32)));
	}
}
