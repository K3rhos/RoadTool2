using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Whether a road type's geometry is thinned out where the road hardly bends, and by how much: a row is dropped
/// while the road turns less than <see cref="Angle"/> there and dropping it moves the road no further than
/// <see cref="Deviation"/> from its true curve. A straight street then costs two rows instead of dozens; a road
/// whose shape matters at every row (a country verge wandering with noise) keeps them all.
/// </summary>
/// <param name="MaxGap">
/// How far apart the rows may be spread at the most, whatever the two measures allow; 0 for no limit. It is what a
/// road type whose border has shape of its own between the rows (a verge wandering with noise) uses to stop the
/// thinning running away with detail the sampler cannot see.
/// </param>
public readonly record struct RoadSimplify(bool Enabled, float Angle, float Deviation, float MaxGap = 0.0f)
{
	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(Enabled ? 1 : 0);

		if (!Enabled)
			return;

		_Hash.Add(Angle);
		_Hash.Add(Deviation);
		_Hash.Add(MaxGap);
	}
}

/// <summary>
/// Where the rows of a stretch of road sit along its curve. Every builder of a chunk (surface, borders, bridge)
/// asks for the same rows, so the pieces meet exactly; the ends are always the stretch's own distances, which is
/// what the junctions on either side meet.
/// </summary>
internal static class RoadRows
{
	/// <summary>
	/// Rows every spacing along a stretch, its exact ends included.
	/// </summary>
	public static List<float> Even(float _Start, float _End, float _Spacing)
	{
		float length = MathF.Max(0.0f, _End - _Start);
		int steps = Math.Max(1, (int)MathF.Ceiling(length / MathF.Max(1.0f, _Spacing)));
		var rows = new List<float>(steps + 1);

		for (int i = 0; i <= steps; i++)
			rows.Add(i == steps ? _End : _Start + length * i / steps);

		return rows;
	}



	/// <summary>
	/// The rows a stretch of road is built on. The road's two edges are what is measured, not its centreline: an
	/// edge that moves out on its own (a lane appearing over a taper) has to keep its rows even where the
	/// centreline runs dead straight.
	/// </summary>
	public static List<float> Build(RoadCurveTable _Table, float _Start, float _End, float _Spacing, in RoadWidth _Width, in RoadSimplify _Simplify)
	{
		var rows = Even(_Start, _End, _Spacing);

		if (!_Simplify.Enabled || rows.Count < 3)
			return rows;

		var left = new Vector3[rows.Count];
		var right = new Vector3[rows.Count];

		for (int i = 0; i < rows.Count; i++)
			RoadSurfaceBuilder.CrossSection(_Table, rows[i], _Width, 0.0f, out left[i], out right[i]);

		var kept = Walk(rows, _Simplify, left, right);
		var distances = new List<float>(kept.Count);

		foreach (int index in kept)
			distances.Add(rows[index]);

		return distances;
	}



	/// <summary>
	/// Walks the rows from one end, each time reaching as far ahead as the rows in between can be dropped for on
	/// every track measured, and never further than the road type lets its rows be spread.
	/// </summary>
	private static List<int> Walk(IReadOnlyList<float> _Rows, in RoadSimplify _Simplify, params Vector3[][] _Tracks)
	{
		int last = _Rows.Count - 1;
		var kept = new List<int>(_Rows.Count) { 0 };
		int anchor = 0;

		while (anchor < last)
		{
			int next = anchor + 1;

			for (int candidate = anchor + 2; candidate <= last; candidate++)
			{
				// Two rows are never merged into one, however straight the road is, so the cap can only ever
				// stop the reach growing — the rows already there all stay.
				if (_Simplify.MaxGap > 0.0f && _Rows[candidate] - _Rows[anchor] > _Simplify.MaxGap)
					break;

				bool droppable = true;

				foreach (var track in _Tracks)
					droppable &= CanDrop(track, anchor, candidate, _Simplify);

				if (!droppable)
					break;

				next = candidate;
			}

			kept.Add(next);
			anchor = next;
		}

		return kept;
	}



	/// <summary>
	/// Whether every row between two others can go, as far as one edge is concerned: the edge has to turn less
	/// than the angle at each of them, and none of them may end up further than the deviation from where the edge
	/// would run without them.
	/// </summary>
	private static bool CanDrop(Vector3[] _Points, int _From, int _To, in RoadSimplify _Simplify)
	{
		Vector3 chord = _Points[_To] - _Points[_From];
		float chordLengthSquared = chord.LengthSquared;

		for (int k = _From + 1; k < _To; k++)
		{
			if (TurnAngle(_Points, k) >= _Simplify.Angle)
				return false;

			if (_Simplify.Deviation <= 0.0f)
				continue;

			Vector3 offset = _Points[k] - _Points[_From];
			float t = chordLengthSquared > 0.0f ? Math.Clamp(Vector3.Dot(offset, chord) / chordLengthSquared, 0.0f, 1.0f) : 0.0f;

			if ((offset - chord * t).Length > _Simplify.Deviation)
				return false;
		}

		return true;
	}



	/// <summary>
	/// How far the road turns at a row, in degrees.
	/// </summary>
	private static float TurnAngle(Vector3[] _Points, int _Index)
	{
		Vector3 incoming = _Points[_Index] - _Points[_Index - 1];
		Vector3 outgoing = _Points[_Index + 1] - _Points[_Index];

		if (incoming.IsNearZeroLength || outgoing.IsNearZeroLength)
			return 0.0f;

		return MathF.Acos(Math.Clamp(Vector3.Dot(incoming.Normal, outgoing.Normal), -1.0f, 1.0f)).RadianToDegree();
	}
}
