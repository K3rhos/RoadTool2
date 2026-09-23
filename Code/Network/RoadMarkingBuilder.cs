using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the painted lines of a stretch of road: thin strips laid just above the surface along the lane
/// boundaries. Which line goes where follows from the road's lanes; how each kind looks comes from the profile.
/// Dashes are phased from the start of the segment, not the chunk, so a chunk boundary never breaks the rhythm.
/// The lane grid divides the road's base width; where a side carries extra width (a lane added over a taper) the
/// edge line follows the edge out and a lane line appears along the old edge once the extra is most of a lane.
/// </summary>
internal static class RoadMarkingBuilder
{
	/// <summary>
	/// How far above the road surface the paint sits, so it never fights the asphalt for the pixel.
	/// </summary>
	public const float Lift = 0.6f;

	/// <summary>
	/// The road surface as the mesh really is: flat between the rows the chunk was built on, not the curve those
	/// rows were taken from. Paint placed on the curve instead hangs in the air over a crest and disappears into
	/// the road in a dip, by as much as the surface's own chords cut the corner — which is why the paint's rows
	/// are the road's rows, and anything in between (a dash ending mid-row) is placed on the chord.
	/// </summary>
	private readonly struct Surface
	{
		/// <summary>
		/// Two rows this close together are one: a dash ending a hair from a row must not make a sliver quad.
		/// </summary>
		private const float RowMargin = 0.5f;

		/// <summary>
		/// How far a painted line may cut the corner of its own path. Far tighter than the road's own tolerance:
		/// a road cutting a couple of units off a bend is invisible across hundreds of units of asphalt, the same
		/// couple of units in a line a few units wide is a kink.
		/// </summary>
		private const float LineDeviation = 0.25f;

		/// <summary>
		/// How many times a stretch of line may be halved to stay within that.
		/// </summary>
		private const int MaxSplits = 6;

		private readonly RoadCurveTable m_Table;
		private readonly IReadOnlyList<float> m_Rows;
		private readonly RoadWidth m_Width;
		private readonly float m_Lift;

		/// <summary>
		/// The road's two edges at each row, worked out once: every point of paint stands on one of these cells, and
		/// re-deriving its corners per point costs four searches of the curve table each time.
		/// </summary>
		private readonly Vector3[] m_Left;
		private readonly Vector3[] m_Right;

		public Surface(RoadCurveTable _Table, IReadOnlyList<float> _Rows, in RoadWidth _Width, float _Lift)
		{
			m_Table = _Table;
			m_Rows = _Rows;
			m_Width = _Width;
			m_Lift = _Lift;
			m_Left = new Vector3[_Rows.Count];
			m_Right = new Vector3[_Rows.Count];

			for (int i = 0; i < _Rows.Count; i++)
				RoadSurfaceBuilder.CrossSection(_Table, _Rows[i], _Width, 0.0f, out m_Left[i], out m_Right[i]);
		}

		/// <summary>
		/// Where a point of a line at an offset across the road goes, and the way across the road there: along the
		/// road's own curve in plan, so the line reads smooth, at the height of the road cell it lies over, so it
		/// lies flat on the surface as built.
		/// </summary>
		public void Place(float _Distance, float _Offset, out Vector3 _Point, out Vector3 _Right)
		{
			m_Table.Frame(_Distance, out Vector3 centre, out Vector3 tangent);

			_Right = RoadCurve.RightOf(tangent);

			Vector3 point = centre + _Right * _Offset;

			_Point = point.WithZ(CellHeight(_Distance, new Vector2(point.x, point.y)) + m_Lift);
		}

		/// <summary>
		/// The rows a stretch of paint is built on: its own two ends, every road row in between and, in each cell,
		/// the place where the line crosses that cell's diagonal — so no part of the line spans a fold in the road
		/// — and between those, wherever the line's own bend asks for one.
		/// </summary>
		public List<float> RowsBetween(float _From, float _To, in Line _Line)
		{
			var rows = new List<float> { _From };
			float previous = _From;

			foreach (float sticky in FoldsBetween(_From, _To, _Line))
			{
				Subdivide(rows, previous, sticky, _Line, MaxSplits);
				previous = sticky;
			}

			Subdivide(rows, previous, _To, _Line, MaxSplits);

			return rows;
		}

		/// <summary>
		/// The places between two distances where the road folds under the line: its rows, and the diagonals of
		/// the cells between them.
		/// </summary>
		private IEnumerable<float> FoldsBetween(float _From, float _To, Line _Line)
		{
			for (int i = 0; i + 1 < m_Rows.Count; i++)
			{
				float from = m_Rows[i];
				float to = m_Rows[i + 1];

				if (to <= _From || from >= _To)
					continue;

				if (from > _From + RowMargin && from < _To - RowMargin)
					yield return from;

				// The cell is split from its near left corner to its far right one, so a line a fraction of the way
				// across the road crosses that diagonal the same fraction of the way along the cell.
				float middle = (from + to) * 0.5f;
				float width = m_Width.WidthAt(middle);
				float fraction = width > 0.001f ? Math.Clamp((_Line.OffsetAt(m_Width, middle) + m_Width.LeftAt(middle)) / width, 0.0f, 1.0f) : 0.5f;
				float crossing = MathX.Lerp(from, to, fraction);

				if (crossing > MathF.Max(_From, from) + RowMargin && crossing < MathF.Min(_To, to) - RowMargin)
					yield return crossing;
			}
		}

		/// <summary>
		/// Adds the rows a stretch of line needs to stay on its path, and its far end. A stretch straight enough
		/// to be drawn in one go adds nothing but that end, which is what makes a dash on a straight road a single
		/// quad however long it is.
		/// </summary>
		private void Subdivide(List<float> _Rows, float _From, float _To, in Line _Line, int _Splits)
		{
			if (_Splits > 0 && _To - _From > RowMargin * 2.0f)
			{
				float middle = (_From + _To) * 0.5f;
				Vector2 from = Offset(_From, _Line);
				Vector2 to = Offset(_To, _Line);
				Vector2 centre = Offset(middle, _Line);

				if (centre.Distance(Nearest(from, to, centre)) > LineDeviation)
				{
					Subdivide(_Rows, _From, middle, _Line, _Splits - 1);
					Subdivide(_Rows, middle, _To, _Line, _Splits - 1);

					return;
				}
			}

			_Rows.Add(_To);
		}

		/// <summary>
		/// Where the line runs in plan at a distance.
		/// </summary>
		private Vector2 Offset(float _Distance, in Line _Line)
		{
			m_Table.Frame(_Distance, out Vector3 centre, out Vector3 tangent);

			Vector3 point = centre + RoadCurve.RightOf(tangent) * _Line.OffsetAt(m_Width, _Distance);

			return new Vector2(point.x, point.y);
		}

		private static Vector2 Nearest(Vector2 _From, Vector2 _To, Vector2 _Point)
		{
			Vector2 span = _To - _From;
			float lengthSquared = span.LengthSquared;
			float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(_Point - _From, span) / lengthSquared, 0.0f, 1.0f) : 0.0f;

			return _From + span * t;
		}

		/// <summary>
		/// The height of the road cell a distance falls in, at a point in plan.
		/// </summary>
		private float CellHeight(float _Distance, Vector2 _Point)
		{
			int index = Bracket(_Distance);

			return RoadSurfaceBuilder.CellHeight(m_Left[index], m_Right[index], m_Right[index + 1], m_Left[index + 1], _Point);
		}

		/// <summary>
		/// The cell a distance falls in: the last row at or before it, never the very last row (which has no cell
		/// beyond it).
		/// </summary>
		private int Bracket(float _Distance)
		{
			int low = 0;
			int high = m_Rows.Count - 2;

			while (low < high)
			{
				int mid = (low + high + 1) / 2;

				if (m_Rows[mid] <= _Distance)
					low = mid;
				else
					high = mid - 1;
			}

			return low;
		}
	}

	/// <summary>
	/// The share of a lane's width a side's extra has to reach before the lane line along the old edge is painted.
	/// </summary>
	private const float ExtraLaneShare = 0.6f;

	/// <summary>
	/// Where a segment sits in the run of road its dashes are counted along: the run distance at the segment's
	/// start, and whether the run goes against the segment (so its distance counts down from the end). Dashes are
	/// counted from the start of the run, not the segment, so a road drawn in several segments dashes as one.
	/// </summary>
	public readonly record struct DashPhase(float Offset, bool Reversed)
	{
		public void AddToHash(ref StableHash _Hash)
		{
			_Hash.Add(Offset);
			_Hash.Add(Reversed ? 1 : 0);
		}
	}

	/// <summary>
	/// A line to paint: at a fixed distance across the road (from the centreline, negative to the left), or, with
	/// <see cref="Follow"/> set, following that edge (-1 left, +1 right) at <see cref="Inset"/> in from it, or (2)
	/// at a fraction of the way across the surface from its left edge (<see cref="Offset"/> then holds the fraction).
	/// A fixed line is only painted where it lies on the surface, and one with <see cref="MinExtra"/> only where that
	/// side's extra width has reached it.
	/// </summary>
	private readonly record struct Line(float Offset, int Follow, float Inset, int ExtraSide, float MinExtra, RoadLineStyle Style, Material Material)
	{
		public const int Across = 2;

		public float OffsetAt(in RoadWidth _Width, float _Distance)
		{
			return Follow switch
			{
				-1 => -_Width.LeftAt(_Distance) + Inset,
				1 => _Width.RightAt(_Distance) - Inset,
				Across => -_Width.LeftAt(_Distance) + Offset * _Width.WidthAt(_Distance) + Inset,
				_ => Offset
			};
		}

		public bool VisibleAt(in RoadWidth _Width, float _Distance)
		{
			if (Follow == Across)
				return _Width.WidthAt(_Distance) > Style.Width;

			if (Follow != 0)
				return _Width.WidthAt(_Distance) > 2.0f * Inset;

			float half = Style.Width * 0.5f;

			if (Offset - half < -_Width.LeftAt(_Distance) || Offset + half > _Width.RightAt(_Distance))
				return false;

			if (ExtraSide == 0)
				return true;

			float extra = ExtraSide < 0 ? _Width.LeftExtraAt(_Distance) : _Width.RightExtraAt(_Distance);

			return extra >= MinExtra;
		}
	}



	/// <summary>
	/// True when the road gets any paint at all, so roads without get no marking chunk.
	/// </summary>
	public static bool HasMarkings(RoadSegment _Segment, RoadProfile _Profile)
	{
		return CollectLines(_Segment, _Profile).Count > 0;
	}



	/// <summary>
	/// Adds the lines over one stretch of the road to the mesh. An empty or inverted stretch adds nothing.
	/// </summary>
	public static void Build(PolygonMesh _Mesh, RoadCurveTable _Table, IReadOnlyList<float> _RoadRows, float _StartDistance, float _EndDistance, RoadSegment _Segment, in RoadWidth _Width, RoadProfile _Profile, float _SurfaceOffset, in DashPhase _Phase)
	{
		if (_EndDistance - _StartDistance < 1.0f || _RoadRows.Count < 2)
			return;

		var surface = new Surface(_Table, _RoadRows, _Width, _SurfaceOffset + Lift);

		foreach (var line in CollectLines(_Segment, _Profile))
		{
			if (line.Style.IsDouble)
			{
				float spacing = line.Style.DoubleSpacing * 0.5f;

				// The pair straddles the line's place: a fixed line by its offset, the others by their inset (which
				// counts inward from the right edge, so it runs the other way there).
				var (first, second) = line.Follow switch
				{
					0 => (line with { Offset = line.Offset - spacing }, line with { Offset = line.Offset + spacing }),
					1 => (line with { Inset = line.Inset + spacing }, line with { Inset = line.Inset - spacing }),
					_ => (line with { Inset = line.Inset - spacing }, line with { Inset = line.Inset + spacing })
				};

				BuildLine(_Mesh, _Table, surface, _StartDistance, _EndDistance, first, _Width, _Phase);
				BuildLine(_Mesh, _Table, surface, _StartDistance, _EndDistance, second, _Width, _Phase);
			}
			else
			{
				BuildLine(_Mesh, _Table, surface, _StartDistance, _EndDistance, line, _Width, _Phase);
			}
		}
	}



	/// <summary>
	/// Paints a pedestrian crossing over a stretch of the road: stripes laid along the way traffic runs, side by
	/// side across the road, as many as the road's width has room for. Each one is built exactly as a solid line
	/// is, so it sits on the surface the way the rest of the paint does.
	/// </summary>
	public static void BuildCrossing(PolygonMesh _Mesh, RoadCurveTable _Table, IReadOnlyList<float> _RoadRows, float _From, float _To, in RoadWidth _Width, RoadProfile _Profile, float _SurfaceOffset)
	{
		if (_To - _From < 1.0f || _RoadRows.Count < 2)
			return;

		var material = _Profile.ResolvedCrossingMaterial;

		if (material is null)
			return;

		float stripe = MathF.Max(1.0f, _Profile.CrossingStripeWidth);
		float pitch = stripe + MathF.Max(0.0f, _Profile.CrossingStripeGap);

		// The band the stripes fill: the road as it is halfway through the crossing, less the margin its ends keep
		// from the kerbs.
		float middle = (_From + _To) * 0.5f;
		float left = -_Width.LeftAt(middle) + _Profile.CrossingInset;
		float right = _Width.RightAt(middle) - _Profile.CrossingInset;
		float usable = right - left;

		if (usable < stripe)
			return;

		// As many as fit, spread about the middle of the band, so a wider road simply gets more of them and a
		// narrower one fewer without anything being said about the number.
		int count = Math.Max(1, (int)MathF.Floor((usable + pitch - stripe) / pitch));
		float span = count * pitch - (pitch - stripe);
		float first = left + (usable - span) * 0.5f + stripe * 0.5f;

		var surface = new Surface(_Table, _RoadRows, _Width, _SurfaceOffset + Lift);
		var style = new RoadLineStyle { Width = stripe };

		for (int i = 0; i < count; i++)
			BuildStrip(_Mesh, surface, _From, _To, new Line(first + i * pitch, 0, 0.0f, 0, 0.0f, style, material), _Width);
	}



	/// <summary>
	/// The lines a road is due, from its lanes: between lanes going the same way a lane line, where the direction
	/// changes the centre line, and along both edges the edge line. A road that only ever grows (extras of zero or
	/// more) keeps its lanes on the base width and takes each side's extra as a lane of its own, with a lane line
	/// along the old edge once the extra is most of a lane; a road that is pulled in anywhere (a negative extra)
	/// has been reshaped or moved off its centreline, and its lanes divide whatever surface there is.
	/// </summary>
	private static List<Line> CollectLines(RoadSegment _Segment, RoadProfile _Profile)
	{
		var lines = new List<Line>();
		int lanes = _Segment.LaneCount;
		float laneWidth = _Segment.Width / lanes;
		float halfWidth = _Segment.Width * 0.5f;
		bool reshaped = IsReshaped(_Segment);

		for (int boundary = 1; boundary < lanes; boundary++)
		{
			bool sameWay = _Segment.IsLaneForward(boundary - 1) == _Segment.IsLaneForward(boundary);
			var style = sameWay ? _Profile.LaneLine : _Profile.CentreLine;

			if (style is null || !style.Enabled || style.Width <= 0.0f)
				continue;

			var material = sameWay ? _Profile.ResolvedLaneLineMaterial : _Profile.ResolvedCentreLineMaterial;

			if (reshaped)
				lines.Add(new Line((float)boundary / lanes, Line.Across, 0.0f, 0, 0.0f, style, material));
			else
				lines.Add(new Line(-halfWidth + boundary * laneWidth, 0, 0.0f, 0, 0.0f, style, material));
		}

		var lane = _Profile.LaneLine;

		if (!reshaped && lane is not null && lane.Enabled && lane.Width > 0.0f && HasExtra(_Segment))
		{
			float minExtra = laneWidth * ExtraLaneShare;

			lines.Add(new Line(-halfWidth, 0, 0.0f, -1, minExtra, lane, _Profile.ResolvedLaneLineMaterial));
			lines.Add(new Line(halfWidth, 0, 0.0f, 1, minExtra, lane, _Profile.ResolvedLaneLineMaterial));
		}

		var edge = _Profile.EdgeLine;

		if (edge is not null && edge.Enabled && edge.Width > 0.0f)
		{
			// The line's outer side sits the margin in from the road's edge.
			float inset = _Profile.EdgeLineMargin + edge.Width * 0.5f + (edge.IsDouble ? edge.DoubleSpacing * 0.5f : 0.0f);

			if (inset < halfWidth)
			{
				lines.Add(new Line(0.0f, -1, inset, 0, 0.0f, edge, _Profile.ResolvedEdgeLineMaterial));
				lines.Add(new Line(0.0f, 1, inset, 0, 0.0f, edge, _Profile.ResolvedEdgeLineMaterial));
			}
		}

		return lines;
	}



	private static bool HasExtra(RoadSegment _Segment)
	{
		return _Segment.LeftExtraStart > 0.0f || _Segment.LeftExtraEnd > 0.0f || _Segment.RightExtraStart > 0.0f || _Segment.RightExtraEnd > 0.0f;
	}



	private static bool IsReshaped(RoadSegment _Segment)
	{
		return _Segment.LeftExtraStart < 0.0f || _Segment.LeftExtraEnd < 0.0f || _Segment.RightExtraStart < 0.0f || _Segment.RightExtraEnd < 0.0f;
	}



	private static void BuildLine(PolygonMesh _Mesh, RoadCurveTable _Table, in Surface _Surface, float _StartDistance, float _EndDistance, in Line _Line, in RoadWidth _Width, in DashPhase _Phase)
	{
		if (_EndDistance - _StartDistance < 0.01f)
			return;

		if (!_Line.Style.IsDashed)
		{
			BuildStrip(_Mesh, _Surface, _StartDistance, _EndDistance, _Line, _Width);
			return;
		}

		// Dashes are counted along the run the segment belongs to. In run distance this stretch spans
		// [runStart, runEnd); the dashes there are found by index, and each is mapped back onto the segment.
		float period = _Line.Style.DashLength + _Line.Style.GapLength;
		float length = _Table.Length;
		float runStart = _Phase.Reversed ? _Phase.Offset + length - _EndDistance : _Phase.Offset + _StartDistance;
		float runEnd = _Phase.Reversed ? _Phase.Offset + length - _StartDistance : _Phase.Offset + _EndDistance;
		int first = (int)MathF.Floor(runStart / period);

		for (int index = first; ; index++)
		{
			float dashStart = index * period;

			if (dashStart >= runEnd)
				break;

			float runFrom = MathF.Max(dashStart, runStart);
			float runTo = MathF.Min(dashStart + _Line.Style.DashLength, runEnd);

			if (runTo - runFrom <= 0.5f)
				continue;

			float from = _Phase.Reversed ? _Phase.Offset + length - runTo : runFrom - _Phase.Offset;
			float to = _Phase.Reversed ? _Phase.Offset + length - runFrom : runTo - _Phase.Offset;

			BuildStrip(_Mesh, _Surface, from, to, _Line, _Width);
		}
	}



	/// <summary>
	/// One painted strip: a quad strip of the line's width, sitting <see cref="Lift"/> above the surface, on the
	/// road's own rows so it lies flat on the road however coarse the road is, with the quads left out wherever
	/// the line has run off the surface.
	/// </summary>
	private static void BuildStrip(PolygonMesh _Mesh, in Surface _Surface, float _StartDistance, float _EndDistance, in Line _Line, in RoadWidth _Width)
	{
		var rows = _Surface.RowsBetween(_StartDistance, _EndDistance, _Line);
		int count = rows.Count;
		float halfWidth = _Line.Style.Width * 0.5f;
		float repeat = MathF.Max(_Line.Style.Width, 1.0f);
		var material = _Line.Material;

		var left = new HalfEdgeMesh.VertexHandle[count];
		var right = new HalfEdgeMesh.VertexHandle[count];
		var along = new float[count];
		var visible = new bool[count];

		for (int i = 0; i < count; i++)
		{
			float distance = rows[i];

			visible[i] = _Line.VisibleAt(_Width, distance);

			if (!visible[i])
				continue;

			_Surface.Place(distance, _Line.OffsetAt(_Width, distance), out Vector3 onLine, out Vector3 across);

			var handles = _Mesh.AddVertices(onLine - across * halfWidth, onLine + across * halfWidth);

			left[i] = handles[0];
			right[i] = handles[1];
			along[i] = distance / repeat;
		}

		for (int i = 0; i < count - 1; i++)
		{
			if (!visible[i] || !visible[i + 1])
				continue;

			MeshUtility.AddTexturedQuad(_Mesh, material, left[i], right[i], right[i + 1], left[i + 1],
				new Vector2(0.0f, along[i]), new Vector2(1.0f, along[i]), new Vector2(1.0f, along[i + 1]), new Vector2(0.0f, along[i + 1]));
		}
	}
}
