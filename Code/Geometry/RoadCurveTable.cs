using System;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Arc-length table over a <see cref="RoadCurve"/>, so the road can be sampled at even distances (mesh rows, chunk
/// boundaries) instead of at even parameter values, which bunch up around tight bends. The samples double as a
/// polyline for picking.
/// </summary>
public sealed class RoadCurveTable
{
	private readonly Vector3[] m_Positions;
	private readonly float[] m_Distances;

	public RoadCurve Curve { get; }

	public float Length => m_Distances[^1];

	public int SampleCount => m_Positions.Length;

	/// <summary>
	/// The box the curve itself runs through. Picking widens it by the road's own reach and skips the road
	/// altogether when the cursor's ray misses it, which is most of them in a city.
	/// </summary>
	public BBox Bounds { get; }



	public RoadCurveTable(RoadCurve _Curve, int _Samples)
	{
		Curve = _Curve;

		int count = Math.Max(1, _Samples);
		m_Positions = new Vector3[count + 1];
		m_Distances = new float[count + 1];
		m_Positions[0] = _Curve.P0;

		var bounds = new BBox(_Curve.P0, _Curve.P0);

		for (int i = 1; i <= count; i++)
		{
			m_Positions[i] = _Curve.Evaluate((float)i / count);
			m_Distances[i] = m_Distances[i - 1] + Vector3.DistanceBetween(m_Positions[i - 1], m_Positions[i]);
			bounds = bounds.AddPoint(m_Positions[i]);
		}

		Bounds = bounds;
	}



	/// <summary>
	/// Builds a table with a sample density that suits the curve's length.
	/// </summary>
	public static RoadCurveTable Create(RoadCurve _Curve)
	{
		int samples = Math.Clamp((int)(_Curve.ApproximateLength() / 20.0f), 16, 1024);

		return new RoadCurveTable(_Curve, samples);
	}



	public Vector3 SamplePosition(int _Index)
	{
		return m_Positions[_Index];
	}



	public float SampleDistance(int _Index)
	{
		return m_Distances[_Index];
	}



	public float DistanceToT(float _Distance)
	{
		if (_Distance <= 0.0f)
			return 0.0f;

		if (_Distance >= Length)
			return 1.0f;

		int low = 0;
		int high = m_Distances.Length - 1;

		while (high - low > 1)
		{
			int mid = (low + high) / 2;

			if (m_Distances[mid] <= _Distance)
				low = mid;
			else
				high = mid;
		}

		float span = m_Distances[high] - m_Distances[low];
		float fraction = span > 0.0f ? (_Distance - m_Distances[low]) / span : 0.0f;

		return (low + fraction) / (m_Distances.Length - 1);
	}



	public Vector3 PositionAt(float _Distance)
	{
		return Curve.Evaluate(DistanceToT(_Distance));
	}



	public Vector3 TangentAt(float _Distance)
	{
		return Curve.Tangent(DistanceToT(_Distance));
	}



	/// <summary>
	/// Where the road is at a distance and which way it runs there, in one go: everything that lays something
	/// across the road wants both, and asking for them separately searches the table twice.
	/// </summary>
	public void Frame(float _Distance, out Vector3 _Position, out Vector3 _Tangent)
	{
		float t = DistanceToT(_Distance);

		_Position = Curve.Evaluate(t);
		_Tangent = Curve.Tangent(t);
	}
}
