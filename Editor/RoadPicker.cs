using System;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// A road hit by the cursor: the segment, where along it (<see cref="T"/>) and the local-space point on its curve.
/// </summary>
internal readonly record struct RoadSegmentPick(RoadSegment Segment, float T, Vector3 LocalPoint);

/// <summary>
/// Cursor picking against the road graph, done in world space against the network's own geometry so it works the
/// same whether or not the chunks under the cursor have been built yet.
/// </summary>
internal static class RoadPicker
{
	/// <summary>
	/// Node handles keep a constant on-screen size, so their pick radius grows with distance from the camera.
	/// </summary>
	public static float NodeRadius(Vector3 _WorldPosition)
	{
		return MathF.Max(8.0f, Gizmo.Camera.Position.Distance(_WorldPosition) * 0.012f);
	}



	/// <summary>
	/// The node whose handle the ray passes through, nearest to the camera first.
	/// </summary>
	public static RoadNode PickNode(RoadNetwork _Network, Ray _Ray, float _RadiusScale = 1.0f)
	{
		var transform = _Network.WorldTransform;
		RoadNode best = null;
		float bestDistance = float.MaxValue;

		foreach (var node in _Network.Nodes)
		{
			Vector3 world = transform.PointToWorld(node.Position);
			var sphere = new Sphere(world, NodeRadius(world) * _RadiusScale);

			if (!sphere.Trace(_Ray, float.MaxValue, out float distance))
				continue;

			if (distance >= bestDistance)
				continue;

			best = node;
			bestDistance = distance;
		}

		return best;
	}



	/// <summary>
	/// The road under the cursor: the segment whose surface (its curve widened to the road width, plus a little
	/// margin) comes closest to the ray. Null when the ray misses every road.
	/// </summary>
	public static RoadSegmentPick? PickSegment(RoadNetwork _Network, Ray _Ray, float _Margin = 0.0f)
	{
		var transform = _Network.WorldTransform;
		var localRay = _Ray.ToLocal(transform);
		RoadSegmentPick? best = null;
		float bestLateral = float.MaxValue;

		foreach (var segment in _Network.Segments)
		{
			var table = _Network.GetCurveTable(segment);

			if (table is null)
				continue;

			// The table is the road's own, so its width comes from it rather than from another lookup.
			float reach = new RoadWidth(segment, table.Length).MaxHalf + _Margin;

			// Roads the cursor is nowhere near are most of them: one box test each beats walking their samples.
			if (!table.Bounds.Grow(reach).Trace(localRay, float.MaxValue, out _))
				continue;

			Vector3 previous = transform.PointToWorld(table.SamplePosition(0));

			for (int i = 1; i < table.SampleCount; i++)
			{
				Vector3 current = transform.PointToWorld(table.SamplePosition(i));
				var line = new Line(previous, current);

				if (line.ClosestPoint(_Ray, out Vector3 onLine, out Vector3 onRay))
				{
					float lateral = Vector3.DistanceBetween(onLine, onRay);

					if (lateral <= reach && lateral < bestLateral)
					{
						float span = Vector3.DistanceBetween(previous, current);
						float fraction = span > 0.0f ? Vector3.DistanceBetween(previous, onLine) / span : 0.0f;
						float distance = table.SampleDistance(i - 1) + fraction * (table.SampleDistance(i) - table.SampleDistance(i - 1));
						float t = table.DistanceToT(distance);

						best = new RoadSegmentPick(segment, t, table.Curve.Evaluate(t));
						bestLateral = lateral;
					}
				}

				previous = current;
			}
		}

		return best;
	}
}
