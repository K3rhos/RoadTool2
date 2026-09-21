using System;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// How far a road's two edges sit from its centreline along a segment: half the road's width on each side, plus
/// whatever extra each side has at the segment's start and end, blended smoothly between the two. That is what lets
/// a lane appear on one side over a taper (a highway exit or entry), a ramp start as a point and grow, or a road
/// flare out to a wider one.
/// </summary>
public readonly struct RoadWidth
{
	/// <summary>
	/// No road is built narrower than this: two edges on one point make faces with no area.
	/// </summary>
	private const float MinWidth = 2.0f;

	public readonly float Base;
	public readonly float LeftStart;
	public readonly float LeftEnd;
	public readonly float RightStart;
	public readonly float RightEnd;
	public readonly float Length;

	public RoadWidth(float _Base, float _LeftStart, float _LeftEnd, float _RightStart, float _RightEnd, float _Length)
	{
		Base = _Base;
		LeftStart = _LeftStart;
		LeftEnd = _LeftEnd;
		RightStart = _RightStart;
		RightEnd = _RightEnd;
		Length = MathF.Max(0.001f, _Length);
	}

	public RoadWidth(RoadSegment _Segment, float _Length)
		: this(_Segment.Width, _Segment.LeftExtraStart, _Segment.LeftExtraEnd, _Segment.RightExtraStart, _Segment.RightExtraEnd, _Length)
	{
	}

	/// <summary>
	/// A plain road of one width all along.
	/// </summary>
	public static RoadWidth Uniform(float _Width)
	{
		return new RoadWidth(_Width, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f);
	}

	/// <summary>
	/// True when both edges sit at half the width all along: the road every piece of the tool was first built for.
	/// </summary>
	public bool IsUniform => LeftStart == 0.0f && LeftEnd == 0.0f && RightStart == 0.0f && RightEnd == 0.0f;

	/// <summary>
	/// The furthest either edge gets from the centreline anywhere along the segment.
	/// </summary>
	public float MaxHalf => MathF.Max(MathF.Max(Base * 0.5f + LeftStart, Base * 0.5f + LeftEnd), MathF.Max(Base * 0.5f + RightStart, Base * 0.5f + RightEnd));

	/// <summary>
	/// Distance of the left edge from the centreline at a curve distance, looking along the segment.
	/// </summary>
	public float LeftAt(float _Distance)
	{
		float left = Base * 0.5f + Blend(LeftStart, LeftEnd, _Distance);
		float right = Base * 0.5f + Blend(RightStart, RightEnd, _Distance);

		return left + right < MinWidth ? left + (MinWidth - left - right) * 0.5f : left;
	}

	public float RightAt(float _Distance)
	{
		float left = Base * 0.5f + Blend(LeftStart, LeftEnd, _Distance);
		float right = Base * 0.5f + Blend(RightStart, RightEnd, _Distance);

		return left + right < MinWidth ? right + (MinWidth - left - right) * 0.5f : right;
	}

	/// <summary>
	/// The road's full width at a curve distance.
	/// </summary>
	public float WidthAt(float _Distance)
	{
		return LeftAt(_Distance) + RightAt(_Distance);
	}

	/// <summary>
	/// How much wider than the base half width the left edge is at a curve distance (negative when narrower).
	/// </summary>
	public float LeftExtraAt(float _Distance)
	{
		return LeftAt(_Distance) - Base * 0.5f;
	}

	public float RightExtraAt(float _Distance)
	{
		return RightAt(_Distance) - Base * 0.5f;
	}



	private float Blend(float _Start, float _End, float _Distance)
	{
		if (_Start == _End)
			return _Start;

		float t = Math.Clamp(_Distance / Length, 0.0f, 1.0f);

		// Eased, so the edge leaves and arrives without a kink.
		t = t * t * (3.0f - 2.0f * t);

		return _Start + (_End - _Start) * t;
	}



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(Base);
		_Hash.Add(LeftStart);
		_Hash.Add(LeftEnd);
		_Hash.Add(RightStart);
		_Hash.Add(RightEnd);
	}
}
