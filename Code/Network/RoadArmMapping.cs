using System;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// A road's own texture mapping carried on past the row its mesh starts with, in to the node it ends at: U across
/// the road and V along it, measured from the road's own centreline (the curve itself, carried on straight past
/// either end), exactly as the road's chunks lay them (see <see cref="RoadSurfaceBuilder.Build"/>). The road's texture
/// runs on into a junction or a lot without a seam and follows the road wherever it curves on its way in; carried on
/// straight from the row instead, it would drift off a curving road and be stretched to meet its kerb.
/// </summary>
internal sealed class RoadArmMapping : IFanMapping
{
	private readonly RoadCurveTable m_Table;
	private readonly RoadWidth m_Widths;
	private readonly float m_Low;
	private readonly float m_High;
	private readonly float m_Repeat;
	private readonly bool m_FitWidth;
	private readonly bool m_HoldCentre;

	/// <summary>
	/// <paramref name="_Trim"/> is how far from the node, along the road, its row is. <paramref name="_HoldCentre"/>
	/// keeps a centre that lies off to the side of the road's lane (a parking lot's middle, the node a ramp's offset
	/// lane ends beside) inside the road's texture, so the road converges on it rather than running its edges on
	/// across the surface.
	/// </summary>
	public RoadArmMapping(RoadCurveTable _Table, bool _AtStart, float _Trim, in RoadWidth _Widths, float _Repeat, bool _FitWidth, bool _HoldCentre = false)
	{
		m_Table = _Table;
		m_Widths = _Widths;
		m_Repeat = MathF.Max(1.0f, _Repeat);
		m_FitWidth = _FitWidth;
		m_HoldCentre = _HoldCentre;

		float node = _AtStart ? 0.0f : _Table.Length;
		float row = _AtStart ? _Trim : _Table.Length - _Trim;

		m_Low = MathF.Min(node, row);
		m_High = MathF.Max(node, row);
	}

	public Vector2 Uv(Vector3 _Point)
	{
		Locate(_Point, out float distance, out float right);

		return new Vector2(Across(distance, right), distance / m_Repeat);
	}

	/// <summary>
	/// The mapping at a point of the outline, which is kerb all round: U held at the road's edge on that side, where
	/// the texture has its edge, so whatever runs along the edge of the road in the texture follows the kerb round a
	/// corner however it curves, instead of running on and missing it. V as everywhere.
	/// </summary>
	public Vector2 KerbUv(Vector3 _Point)
	{
		Locate(_Point, out float distance, out float right);

		float along = Math.Clamp(distance, 0.0f, m_Table.Length);
		float edge = right < 0.0f ? -m_Widths.LeftAt(along) : m_Widths.RightAt(along);

		return new Vector2(Across(distance, edge), distance / m_Repeat);
	}

	/// <summary>
	/// The mapping at the fan's centre: as everywhere, or, held, with U kept to the middle half of a texture fitted to
	/// the road's width, so the texture between the road and a centre off to its side never reaches its edges.
	/// </summary>
	public Vector2 CentreUv(Vector3 _Centre)
	{
		Vector2 uv = Uv(_Centre);

		if (m_HoldCentre && m_FitWidth)
			uv.x = Math.Clamp(uv.x, 0.25f, 0.75f);

		return uv;
	}

	/// <summary>
	/// Where a point sits on the road, in world units: how far to the right of the centreline, and how far along the
	/// curve.
	/// </summary>
	public Vector2 Measure(Vector3 _Point)
	{
		Locate(_Point, out float distance, out float right);

		return new Vector2(right, distance);
	}

	/// <summary>
	/// The road's lane where a point is along it: U at its left and right edges there, and its width.
	/// </summary>
	public void Lane(Vector3 _Point, out float _LeftU, out float _RightU, out float _Width)
	{
		Locate(_Point, out float distance, out _);

		float along = Math.Clamp(distance, 0.0f, m_Table.Length);
		float left = m_Widths.LeftAt(along);
		float right = m_Widths.RightAt(along);

		_LeftU = Across(distance, -left);
		_RightU = Across(distance, right);
		_Width = left + right;
	}

	/// <summary>
	/// U at a point an offset to the right of the centreline, a distance along it: at the texture's own size out from
	/// the centre line, or fitted between the road's two edges there.
	/// </summary>
	private float Across(float _Distance, float _Right)
	{
		if (!m_FitWidth)
			return _Right / m_Repeat;

		float along = Math.Clamp(_Distance, 0.0f, m_Table.Length);
		float left = m_Widths.LeftAt(along);
		float width = left + m_Widths.RightAt(along);

		return width > 0.001f ? (_Right + left) / width : 0.5f;
	}

	/// <summary>
	/// Where a point sits in the road's own terms: the curve distance of the foot of its perpendicular on the
	/// centreline between the node and the row, and how far to the road's right of that foot it lies. A point behind
	/// the node or past the row is measured from the centreline carried on straight there.
	/// </summary>
	private void Locate(Vector3 _Point, out float _Distance, out float _Right)
	{
		var point = new Vector2(_Point.x, _Point.y);
		float low = m_Low;
		float high = m_High;

		if (Ahead(point, low) <= 0.0f)
		{
			Measure(point, low, out _Distance, out _Right);
			return;
		}

		if (Ahead(point, high) >= 0.0f)
		{
			Measure(point, high, out _Distance, out _Right);
			return;
		}

		// The foot is somewhere between: halve the stretch it is in until it is pinned down.
		for (int i = 0; i < 40 && high - low > 0.001f; i++)
		{
			float middle = (low + high) * 0.5f;

			if (Ahead(point, middle) > 0.0f)
				low = middle;
			else
				high = middle;
		}

		Measure(point, (low + high) * 0.5f, out _Distance, out _Right);
	}

	/// <summary>
	/// How far ahead of the centreline's point at a distance a point lies, the way the road runs there.
	/// </summary>
	private float Ahead(Vector2 _Point, float _Distance)
	{
		Frame(_Distance, out Vector2 position, out Vector2 along);

		return Vector2.Dot(_Point - position, along);
	}

	/// <summary>
	/// A point's distance and right offset measured from the centreline's point at a distance and the way the road
	/// runs there: exact at the foot of the point's perpendicular, and the centreline carried on straight elsewhere.
	/// </summary>
	private void Measure(Vector2 _Point, float _From, out float _Distance, out float _Right)
	{
		Frame(_From, out Vector2 position, out Vector2 along);

		Vector2 offset = _Point - position;

		_Distance = _From + Vector2.Dot(offset, along);
		_Right = offset.x * along.y - offset.y * along.x;
	}

	private void Frame(float _Distance, out Vector2 _Position, out Vector2 _Along)
	{
		m_Table.Frame(_Distance, out Vector3 position, out Vector3 tangent);

		// The road's right as its chunks take it: flat and square to the way it runs, which is a quarter turn back
		// from it.
		Vector3 right = RoadCurve.RightOf(tangent);

		_Position = new Vector2(position.x, position.y);
		_Along = new Vector2(-right.y, right.x);
	}
}
