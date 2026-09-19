using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the painted lines of a stretch of road: thin strips laid just above the surface along the lane
/// boundaries. Which line goes where follows from the road's lanes; how each kind looks comes from the profile.
/// Dashes are phased from the start of the segment, not the chunk, so a chunk boundary never breaks the rhythm.
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
	/// A line to paint: where across the road it runs (from the centreline, negative to the left) and how.
	/// </summary>
	private readonly record struct Line(float Offset, RoadLineStyle Style, Material Material);



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
	public static void Build(PolygonMesh _Mesh, RoadCurveTable _Table, float _StartDistance, float _EndDistance, RoadSegment _Segment, RoadProfile _Profile, float _SurfaceOffset)
	{
		if (_EndDistance - _StartDistance < 1.0f)
			return;

		foreach (var line in CollectLines(_Segment, _Profile))
		{
			if (line.Style.IsDouble)
			{
				BuildLine(_Mesh, _Table, _StartDistance, _EndDistance, line.Offset - line.Style.DoubleSpacing * 0.5f, line.Style, line.Material, _SurfaceOffset);
				BuildLine(_Mesh, _Table, _StartDistance, _EndDistance, line.Offset + line.Style.DoubleSpacing * 0.5f, line.Style, line.Material, _SurfaceOffset);
			}
			else
			{
				BuildLine(_Mesh, _Table, _StartDistance, _EndDistance, line.Offset, line.Style, line.Material, _SurfaceOffset);
			}
		}
	}



	/// <summary>
	/// The lines a road is due, from its lanes: between lanes going the same way a lane line, where the direction
	/// changes the centre line, and along both edges the edge line.
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

			lines.Add(new Line(-halfWidth + boundary * laneWidth, style, material));
		}

		var edge = _Profile.EdgeLine;

		if (edge is not null && edge.Enabled && edge.Width > 0.0f)
		{
			// The line's outer side sits the margin in from the road's edge.
			float inset = _Profile.EdgeLineMargin + edge.Width * 0.5f + (edge.IsDouble ? edge.DoubleSpacing * 0.5f : 0.0f);

			if (inset < halfWidth)
			{
				lines.Add(new Line(-halfWidth + inset, edge, _Profile.ResolvedEdgeLineMaterial));
				lines.Add(new Line(halfWidth - inset, edge, _Profile.ResolvedEdgeLineMaterial));
			}
		}

		return lines;
	}



	private static void BuildLine(PolygonMesh _Mesh, RoadCurveTable _Table, float _StartDistance, float _EndDistance, float _Offset, RoadLineStyle _Style, Material _Material, float _SurfaceOffset)
	{
		if (_EndDistance - _StartDistance < 0.01f)
			return;

		if (!_Style.IsDashed)
		{
			BuildStrip(_Mesh, _Table, _StartDistance, _EndDistance, _Offset, _Style.Width, _Material, _SurfaceOffset);
			return;
		}

		// Dashes are counted from the segment's start, so the one this chunk starts inside is found by its index.
		float period = _Style.DashLength + _Style.GapLength;
		int first = (int)MathF.Floor(_StartDistance / period);

		for (int index = first; ; index++)
		{
			float dashStart = index * period;

			if (dashStart >= _EndDistance)
				break;

			float from = MathF.Max(dashStart, _StartDistance);
			float to = MathF.Min(dashStart + _Style.DashLength, _EndDistance);

			if (to - from > 0.5f)
				BuildStrip(_Mesh, _Table, from, to, _Offset, _Style.Width, _Material, _SurfaceOffset);
		}
	}



	/// <summary>
	/// One painted strip: a quad strip of the line's width, sitting <see cref="Lift"/> above the surface.
	/// </summary>
	private static void BuildStrip(PolygonMesh _Mesh, RoadCurveTable _Table, float _StartDistance, float _EndDistance, float _Offset, float _Width, Material _Material, float _SurfaceOffset)
	{
		float length = _EndDistance - _StartDistance;
		int steps = Math.Max(1, (int)MathF.Ceiling(length / RowSpacing));
		float halfWidth = _Width * 0.5f;
		float lift = _SurfaceOffset + Lift;

		var left = new HalfEdgeMesh.VertexHandle[steps + 1];
		var right = new HalfEdgeMesh.VertexHandle[steps + 1];
		var along = new float[steps + 1];

		for (int i = 0; i <= steps; i++)
		{
			float distance = i == steps ? _EndDistance : _StartDistance + length * i / steps;
			Vector3 centre = _Table.PositionAt(distance) + Vector3.Up * lift;
			Vector3 rightDirection = RoadCurve.RightOf(_Table.TangentAt(distance));
			Vector3 onLine = centre + rightDirection * _Offset;

			var handles = _Mesh.AddVertices(onLine - rightDirection * halfWidth, onLine + rightDirection * halfWidth);
			left[i] = handles[0];
			right[i] = handles[1];
			along[i] = distance / MathF.Max(_Width, 1.0f);
		}

		for (int i = 0; i < steps; i++)
		{
			MeshUtility.AddTexturedQuad(_Mesh, _Material, left[i], right[i], right[i + 1], left[i + 1],
				new Vector2(0.0f, along[i]), new Vector2(1.0f, along[i]), new Vector2(1.0f, along[i + 1]), new Vector2(0.0f, along[i + 1]));
		}
	}
}
