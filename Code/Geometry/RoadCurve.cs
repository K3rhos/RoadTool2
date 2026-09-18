using System;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// The cubic Bezier of one road segment, in network-local space. P1 and P2 are the tangent handles hanging off the
/// two end nodes.
/// </summary>
public readonly struct RoadCurve : IEquatable<RoadCurve>
{
	public readonly Vector3 P0;
	public readonly Vector3 P1;
	public readonly Vector3 P2;
	public readonly Vector3 P3;



	public RoadCurve(Vector3 _P0, Vector3 _P1, Vector3 _P2, Vector3 _P3)
	{
		P0 = _P0;
		P1 = _P1;
		P2 = _P2;
		P3 = _P3;
	}



	public Vector3 Evaluate(float _T)
	{
		float u = 1.0f - _T;
		float uu = u * u;
		float tt = _T * _T;

		return P0 * (uu * u) + P1 * (3.0f * uu * _T) + P2 * (3.0f * u * tt) + P3 * (tt * _T);
	}



	/// <summary>
	/// Unit direction of travel at <paramref name="_T"/>. Falls back to the chord when the derivative vanishes (a
	/// handle sitting on its node) so callers always get a usable frame.
	/// </summary>
	public Vector3 Tangent(float _T)
	{
		float u = 1.0f - _T;
		Vector3 derivative = (P1 - P0) * (3.0f * u * u) + (P2 - P1) * (6.0f * u * _T) + (P3 - P2) * (3.0f * _T * _T);

		if (derivative.IsNearZeroLength)
			derivative = P3 - P0;

		if (derivative.IsNearZeroLength)
			return Vector3.Forward;

		return derivative.Normal;
	}



	/// <summary>
	/// Chord-summed length, good enough to size sampling tables and count chunks.
	/// </summary>
	public float ApproximateLength(int _Steps = 32)
	{
		float length = 0.0f;
		Vector3 previous = P0;

		for (int i = 1; i <= _Steps; i++)
		{
			Vector3 point = Evaluate((float)i / _Steps);
			length += Vector3.DistanceBetween(previous, point);
			previous = point;
		}

		return length;
	}



	/// <summary>
	/// De Casteljau subdivision: the two halves trace exactly the same path as the whole, so a road can be split
	/// without moving.
	/// </summary>
	public void Split(float _T, out RoadCurve _Left, out RoadCurve _Right)
	{
		Vector3 q0 = Vector3.Lerp(P0, P1, _T);
		Vector3 q1 = Vector3.Lerp(P1, P2, _T);
		Vector3 q2 = Vector3.Lerp(P2, P3, _T);
		Vector3 r0 = Vector3.Lerp(q0, q1, _T);
		Vector3 r1 = Vector3.Lerp(q1, q2, _T);
		Vector3 s = Vector3.Lerp(r0, r1, _T);

		_Left = new RoadCurve(P0, q0, r0, s);
		_Right = new RoadCurve(s, r1, q2, P3);
	}



	/// <summary>
	/// The road's right-hand direction for a direction of travel: flat, perpendicular to the tangent, and
	/// consistent with <see cref="Rotation.Right"/> for a rotation looking down the tangent with world up.
	/// </summary>
	public static Vector3 RightOf(Vector3 _Tangent)
	{
		Vector3 right = Vector3.Cross(_Tangent, Vector3.Up);

		// Vertical tangent (a ramp seen from nowhere useful): any horizontal axis will do.
		if (right.IsNearZeroLength)
			return Vector3.Right;

		return right.Normal;
	}



	public bool Equals(RoadCurve _Other)
	{
		return P0 == _Other.P0 && P1 == _Other.P1 && P2 == _Other.P2 && P3 == _Other.P3;
	}



	public override bool Equals(object _Obj)
	{
		return _Obj is RoadCurve other && Equals(other);
	}



	public override int GetHashCode()
	{
		return HashCode.Combine(P0, P1, P2, P3);
	}
}
