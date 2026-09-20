using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Works out where the props of a road stand: each row of a prop set spread evenly along the segment's built range
/// (in as many equal bays as come nearest to the row's spacing, one prop per bay at the row's phase), and the set's
/// junction props a fixed distance before each junction the segment meets. Within the road's border a prop stands
/// on the border; beyond it, on whatever ground is found below. Everything random (chance, scatter, turn, scale) is
/// drawn from a hash of where the prop is, so a rebuild puts the same props back.
/// </summary>
internal static class RoadPropBuilder
{
	/// <summary>
	/// A prefab to stand as a child of the props piece, in network space.
	/// </summary>
	public readonly record struct Placement(GameObject Prefab, Vector3 Position, Rotation Rotation, float Scale);

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
		public readonly float HalfWidth;
		public readonly RoadProfile Profile;
		public readonly bool IsBridge;
		public readonly float Lift;
		public readonly Func<Vector3, float?> GroundAt;

		public Context(RoadCurveTable _Table, float _Start, float _End, in PropSpan _Span, int _SegmentId, float _Width, RoadProfile _Profile, bool _IsBridge, float _Lift, Func<Vector3, float?> _GroundAt)
		{
			Table = _Table;
			Start = _Start;
			End = _End;
			Span = _Span;
			SegmentId = _SegmentId;
			HalfWidth = _Width * 0.5f;
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

		float rangeStart = _Context.Span.RangeStart;
		float range = MathF.Max(0.0f, _Context.Span.RangeEnd - rangeStart);
		int bays = Math.Max(1, (int)MathF.Round(range / MathF.Max(1.0f, row.Spacing)));
		float pitch = range / bays;

		for (int k = 0; k < bays; k++)
		{
			float distance = rangeStart + (k + row.Phase) * pitch;

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
				float yaw = row.Yaw + (row.RandomYaw ? random.NextSingle() * 360.0f : 0.0f);
				float scale = MathX.Lerp(row.MinScale, row.MaxScale, random.NextSingle());

				along = Math.Clamp(along, _Context.Span.RangeStart, _Context.Span.RangeEnd);

				_Placements.Add(Place(_Context, row.Prefab, along, left, row.Anchor, row.Offset + across, row.HeightOffset, yaw, scale, false));
			}
		}
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

				_Placements.Add(Place(_Context, row.Prefab, distance, left, row.Anchor, row.Offset, row.HeightOffset, row.Yaw, 1.0f, atStart));
			}
		}
	}



	/// <summary>
	/// A prop's transform at a curve distance on one side of the road: its forward along the road (or back along
	/// it, for a junction prop at the start end), its right away from the road, plus the yaw.
	/// </summary>
	private static Placement Place(in Context _Context, GameObject _Prefab, float _Distance, bool _Left, PropAnchor _Anchor, float _Offset, float _HeightOffset, float _Yaw, float _Scale, bool _FaceBack)
	{
		var table = _Context.Table;
		var profile = _Context.Profile;

		Vector3 centre = table.PositionAt(_Distance) + Vector3.Up * _Context.Lift;
		Vector3 right = RoadCurve.RightOf(table.TangentAt(_Distance));
		Vector3 along = Vector3.Cross(Vector3.Up, right).Normal;
		Vector3 outward = _Left ? -right : right;

		float fromEdge = (_Anchor == PropAnchor.BorderEdge ? profile.FlatBorderWidth : 0.0f) + _Offset;
		Vector3 position = centre + outward * (_Context.HalfWidth + fromEdge);

		position.z += HeightAt(_Context, position, fromEdge) + _HeightOffset;

		// Right pointing away from the road: forward runs along the road on the right side, back along it on
		// the left. A junction prop faces the junction instead, which turns it round at the start end.
		Vector3 forward = _Left ? -along : along;

		if (_FaceBack)
			forward = -forward;

		var rotation = Rotation.LookAt(forward, Vector3.Up) * Rotation.FromYaw(_Yaw);

		return new Placement(_Prefab, position, rotation, _Scale);
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
