using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Where a segment's trails rise out of the road: flat at <see cref="StartFlat"/> and <see cref="EndFlat"/> (distances
/// along the segment's curve, where its surface stops at a junction, a parking lot or a rounded end), at full depth
/// <see cref="Length"/> in from each. An end the trails carry straight on across (into the same road type, at a plain
/// pass-through node) has no fade. The default is a segment with no trails at all.
/// </summary>
public readonly record struct RoadTrailFade(bool StartCarries, float StartFlat, bool EndCarries, float EndFlat, float Length)
{
	/// <summary>
	/// The steps a fade is laid in, between where the trails are flat and where they are at full depth.
	/// </summary>
	private const int Steps = 4;

	/// <summary>
	/// True when the trails reach nowhere along the segment: none at all, or no room between the two flat ends.
	/// </summary>
	public bool IsNone => !StartCarries && !EndCarries && EndFlat - StartFlat <= 0.01f;



	/// <summary>
	/// How much of their depth the trails have at a distance along the segment, easing in and out of each fade.
	/// </summary>
	public float StrengthAt(float _Distance)
	{
		if (IsNone)
			return 0.0f;

		float strength = 1.0f;

		if (!StartCarries)
			strength *= Rise(_Distance - StartFlat);

		if (!EndCarries)
			strength *= Rise(EndFlat - _Distance);

		return strength;
	}



	/// <summary>
	/// The distances a surface needs rows at for the fades to be laid smoothly: each fade's two ends and the steps
	/// between them.
	/// </summary>
	public IEnumerable<float> Knots()
	{
		if (IsNone)
			yield break;

		for (int step = 0; step <= Steps; step++)
		{
			float into = Length * step / Steps;

			if (!StartCarries)
				yield return StartFlat + into;

			if (!EndCarries)
				yield return EndFlat - into;
		}
	}



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(StartCarries ? 1 : 0);
		_Hash.Add(StartCarries ? 0.0f : StartFlat);
		_Hash.Add(EndCarries ? 1 : 0);
		_Hash.Add(EndCarries ? 0.0f : EndFlat);
		_Hash.Add(Length);
	}



	private float Rise(float _Into)
	{
		if (Length <= 0.01f)
			return _Into > 0.0f ? 1.0f : 0.0f;

		float t = Math.Clamp(_Into / Length, 0.0f, 1.0f);

		return t * t * (3.0f - 2.0f * t);
	}
}
