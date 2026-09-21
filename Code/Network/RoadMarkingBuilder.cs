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
	/// Row spacing along a line; finer than the surface so a curved line stays on the curved road.
	/// </summary>
	private const float RowSpacing = 40.0f;

	/// <summary>
	/// The share of a lane's width a side's extra has to reach before the lane line along the old edge is painted.
	/// </summary>
	private const float ExtraLaneShare = 0.6f;

	/// <summary>
	/// A line to paint: at a fixed distance across the road (from the centreline, negative to the left), or, with
	/// <see cref="Follow"/> set, following that edge (-1 left, +1 right) at <see cref="Inset"/> in from it. A fixed
	/// line is only painted where it lies on the surface, and one with <see cref="MinExtra"/> only where that side's
	/// extra width has reached it.
	/// </summary>
	private readonly record struct Line(float Offset, int Follow, float Inset, int ExtraSide, float MinExtra, RoadLineStyle Style, Material Material)
	{
		public float OffsetAt(in RoadWidth _Width, float _Distance)
		{
			return Follow switch
			{
				-1 => -_Width.LeftAt(_Distance) + Inset,
				1 => _Width.RightAt(_Distance) - Inset,
				_ => Offset
			};
		}

		public bool VisibleAt(in RoadWidth _Width, float _Distance)
		{
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
	public static void Build(PolygonMesh _Mesh, RoadCurveTable _Table, float _StartDistance, float _EndDistance, RoadSegment _Segment, in RoadWidth _Width, RoadProfile _Profile, float _SurfaceOffset)
	{
		if (_EndDistance - _StartDistance < 1.0f)
			return;

		foreach (var line in CollectLines(_Segment, _Profile))
		{
			if (line.Style.IsDouble)
			{
				float spacing = line.Style.DoubleSpacing * 0.5f;

				// Following an edge, the pair moves in from it together; fixed, it straddles the boundary.
				BuildLine(_Mesh, _Table, _StartDistance, _EndDistance, line with { Offset = line.Offset - spacing, Inset = line.Inset + (line.Follow < 0 ? -spacing : spacing) }, _Width, _SurfaceOffset);
				BuildLine(_Mesh, _Table, _StartDistance, _EndDistance, line with { Offset = line.Offset + spacing, Inset = line.Inset + (line.Follow < 0 ? spacing : -spacing) }, _Width, _SurfaceOffset);
			}
			else
			{
				BuildLine(_Mesh, _Table, _StartDistance, _EndDistance, line, _Width, _SurfaceOffset);
			}
		}
	}



	/// <summary>
	/// The lines a road is due, from its lanes: between lanes going the same way a lane line, where the direction
	/// changes the centre line, along both edges the edge line, and along each base edge a lane line for the extra
	/// lane a side may carry.
	/// </summary>
	private static List<Line> CollectLines(RoadSegment _Segment, RoadProfile _Profile)
	{
		var lines = new List<Line>();
		int lanes = _Segment.LaneCount;
		float laneWidth = _Segment.Width / lanes;
		float halfWidth = _Segment.Width * 0.5f;

		for (int boundary = 1; boundary < lanes; boundary++)
		{
			bool sameWay = _Segment.IsLaneForward(boundary - 1) == _Segment.IsLaneForward(boundary);
			var style = sameWay ? _Profile.LaneLine : _Profile.CentreLine;

			if (style is null || !style.Enabled || style.Width <= 0.0f)
				continue;

			var material = sameWay ? _Profile.ResolvedLaneLineMaterial : _Profile.ResolvedCentreLineMaterial;

			lines.Add(new Line(-halfWidth + boundary * laneWidth, 0, 0.0f, 0, 0.0f, style, material));
		}

		var lane = _Profile.LaneLine;

		if (lane is not null && lane.Enabled && lane.Width > 0.0f && HasExtra(_Segment))
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



	private static void BuildLine(PolygonMesh _Mesh, RoadCurveTable _Table, float _StartDistance, float _EndDistance, in Line _Line, in RoadWidth _Width, float _SurfaceOffset)
	{
		if (_EndDistance - _StartDistance < 0.01f)
			return;

		if (!_Line.Style.IsDashed)
		{
			BuildStrip(_Mesh, _Table, _StartDistance, _EndDistance, _Line, _Width, _SurfaceOffset);
			return;
		}

		// Dashes are counted from the segment's start, so the one this chunk starts inside is found by its index.
		float period = _Line.Style.DashLength + _Line.Style.GapLength;
		int first = (int)MathF.Floor(_StartDistance / period);

		for (int index = first; ; index++)
		{
			float dashStart = index * period;

			if (dashStart >= _EndDistance)
				break;

			float from = MathF.Max(dashStart, _StartDistance);
			float to = MathF.Min(dashStart + _Line.Style.DashLength, _EndDistance);

			if (to - from > 0.5f)
				BuildStrip(_Mesh, _Table, from, to, _Line, _Width, _SurfaceOffset);
		}
	}



	/// <summary>
	/// One painted strip: a quad strip of the line's width, sitting <see cref="Lift"/> above the surface, with the
	/// quads left out wherever the line has run off the surface.
	/// </summary>
	private static void BuildStrip(PolygonMesh _Mesh, RoadCurveTable _Table, float _StartDistance, float _EndDistance, in Line _Line, in RoadWidth _Width, float _SurfaceOffset)
	{
		float length = _EndDistance - _StartDistance;
		int steps = Math.Max(1, (int)MathF.Ceiling(length / RowSpacing));
		float halfWidth = _Line.Style.Width * 0.5f;
		float lift = _SurfaceOffset + Lift;
		var material = _Line.Material;

		var left = new HalfEdgeMesh.VertexHandle[steps + 1];
		var right = new HalfEdgeMesh.VertexHandle[steps + 1];
		var along = new float[steps + 1];
		var visible = new bool[steps + 1];

		for (int i = 0; i <= steps; i++)
		{
			float distance = i == steps ? _EndDistance : _StartDistance + length * i / steps;

			visible[i] = _Line.VisibleAt(_Width, distance);

			if (!visible[i])
				continue;

			Vector3 centre = _Table.PositionAt(distance) + Vector3.Up * lift;
			Vector3 rightDirection = RoadCurve.RightOf(_Table.TangentAt(distance));
			Vector3 onLine = centre + rightDirection * _Line.OffsetAt(_Width, distance);

			var handles = _Mesh.AddVertices(onLine - rightDirection * halfWidth, onLine + rightDirection * halfWidth);
			left[i] = handles[0];
			right[i] = handles[1];
			along[i] = distance / MathF.Max(_Line.Style.Width, 1.0f);
		}

		for (int i = 0; i < steps; i++)
		{
			if (!visible[i] || !visible[i + 1])
				continue;

			MeshUtility.AddTexturedQuad(_Mesh, material, left[i], right[i], right[i + 1], left[i + 1],
				new Vector2(0.0f, along[i]), new Vector2(1.0f, along[i]), new Vector2(1.0f, along[i + 1]), new Vector2(0.0f, along[i + 1]));
		}
	}
}
