using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Where another surface runs on into a segment's own and gives way to it: from each end that meets one (at
/// <see cref="StartAt"/> or <see cref="EndAt"/>, distances along the segment's curve where its surface stops), all of
/// the other surface there, none of it <see cref="Length"/> in. <see cref="StartSource"/> and <see cref="EndSource"/>
/// say how that surface is laid where it stops, so it carries straight on into the blend. The default is a segment
/// that blends into nothing.
/// </summary>
public readonly record struct RoadSurfaceBlend(bool AtStart, float StartAt, bool AtEnd, float EndAt, float Length, RoadBlendSource StartSource = default, RoadBlendSource EndSource = default)
{
	/// <summary>
	/// The steps a blend is laid in, so its rows give the material a smooth ramp to mix by.
	/// </summary>
	private const int Steps = 4;

	public bool IsNone => (!AtStart && !AtEnd) || Length <= 0.01f;



	/// <summary>
	/// How much of the other surface there is at a distance along the segment: 1 where the segment's surface stops, 0
	/// from a blend's length in, easing between.
	/// </summary>
	public float WeightAt(float _Distance)
	{
		return MathF.Max(StartWeightAt(_Distance), EndWeightAt(_Distance));
	}

	/// <summary>
	/// How much of the surface at the segment's start there is at a distance along it.
	/// </summary>
	public float StartWeightAt(float _Distance)
	{
		return IsNone || !AtStart ? 0.0f : Fall(_Distance - StartAt);
	}

	/// <summary>
	/// How much of the surface at the segment's end there is at a distance along it.
	/// </summary>
	public float EndWeightAt(float _Distance)
	{
		return IsNone || !AtEnd ? 0.0f : Fall(EndAt - _Distance);
	}



	/// <summary>
	/// The distances a surface needs rows at for its blends to be laid smoothly.
	/// </summary>
	public IEnumerable<float> Knots()
	{
		if (IsNone)
			yield break;

		for (int step = 0; step <= Steps; step++)
		{
			float into = Length * step / Steps;

			if (AtStart)
				yield return StartAt + into;

			if (AtEnd)
				yield return EndAt - into;
		}
	}



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(AtStart ? 1 : 0);
		_Hash.Add(AtStart ? StartAt : 0.0f);
		_Hash.Add(AtEnd ? 1 : 0);
		_Hash.Add(AtEnd ? EndAt : 0.0f);
		_Hash.Add(Length);

		if (AtStart)
			StartSource.AddToHash(ref _Hash);

		if (AtEnd)
			EndSource.AddToHash(ref _Hash);
	}



	private float Fall(float _Into)
	{
		float t = Math.Clamp(_Into / Length, 0.0f, 1.0f);

		return 1.0f - t * t * (3.0f - 2.0f * t);
	}
}
