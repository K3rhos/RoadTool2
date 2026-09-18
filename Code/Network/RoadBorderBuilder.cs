using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Utility;

namespace RedSnail.RoadTool2;

/// <summary>
/// Which ends of a road stretch's borders are open (nothing continues them) and need closing off.
/// </summary>
[Flags]
internal enum BorderCaps
{
	None = 0,
	StartLeft = 1,
	StartRight = 2,
	EndLeft = 4,
	EndRight = 8
}

/// <summary>
/// Generates road borders (sidewalks, country side verges) by sweeping a profile's cross-section along a path of
/// road-edge points: the sides of a road chunk, or the outline of a junction between two arms.
/// </summary>
internal static class RoadBorderBuilder
{
	/// <summary>
	/// A point of a border run: the road edge, the direction away from the road, and the along-road coordinate
	/// the textures run on.
	/// </summary>
	public readonly struct PathPoint
	{
		public readonly Vector3 Position;
		public readonly Vector3 Outward;
		public readonly float Along;

		/// <summary>
		/// Set on points of a fillet arc that the border wraps the inside of: profile nodes further out than the
		/// fillet radius land on the block corner instead of folding through the centre.
		/// </summary>
		public readonly RoadJunctionLayout.CornerFold Fold;

		public PathPoint(Vector3 _Position, Vector3 _Outward, float _Along, RoadJunctionLayout.CornerFold _Fold = null)
		{
			Position = _Position;
			Outward = _Outward;
			Along = _Along;
			Fold = _Fold;
		}
	}

	/// <summary>
	/// One surface of the profile, between two of its nodes. U runs across the profile: fixed per node, or
	/// accumulated along the cross-section from <see cref="AccumulateFrom"/> when the shape varies (verges).
	/// Planar faces take a top-down projection instead, like the road they extend.
	/// </summary>
	private readonly struct Face
	{
		public readonly int NodeA;
		public readonly int NodeB;
		public readonly Material Material;
		public readonly float Repeat;
		public readonly bool Planar;
		public readonly int AccumulateFrom;
		public readonly float U0;
		public readonly float U1;

		public Face(int _NodeA, int _NodeB, Material _Material, float _Repeat, bool _Planar = false, int _AccumulateFrom = -1, float _U0 = 0.0f, float _U1 = 1.0f)
		{
			NodeA = _NodeA;
			NodeB = _NodeB;
			Material = _Material;
			Repeat = _Repeat;
			Planar = _Planar;
			AccumulateFrom = _AccumulateFrom;
			U0 = _U0;
			U1 = _U1;
		}
	}

	// Separate regions of the noise field for each thing that wanders, so they don't move in step.
	private const float BandShoulder = 0.0f;
	private const float BandOuter = 1000.0f;
	private const float BandRoll = 2000.0f;



	/// <summary>
	/// Both borders of a stretch of road, rows at the same distances as the road surface.
	/// </summary>
	public static PolygonMesh BuildSegment(RoadCurveTable _Table, float _StartDistance, float _EndDistance, float _Width, RoadProfile _Profile, float _Spacing, float _Lift, BorderCaps _Caps)
	{
		var mesh = new PolygonMesh();

		float length = MathF.Max(0.0f, _EndDistance - _StartDistance);
		int steps = Math.Max(1, (int)MathF.Ceiling(length / MathF.Max(1.0f, _Spacing)));
		float halfWidth = _Width * 0.5f;

		var left = new List<PathPoint>(steps + 1);
		var right = new List<PathPoint>(steps + 1);

		for (int i = 0; i <= steps; i++)
		{
			float distance = i == steps ? _EndDistance : _StartDistance + length * i / steps;
			Vector3 rightDirection = RoadCurve.RightOf(_Table.TangentAt(distance));

			RoadSurfaceBuilder.CrossSection(_Table, distance, halfWidth, _Lift, out Vector3 leftPos, out Vector3 rightPos);

			left.Add(new PathPoint(leftPos, -rightDirection, distance));
			right.Add(new PathPoint(rightPos, rightDirection, distance));
		}

		Sweep(mesh, _Profile, left, _Caps.HasFlag(BorderCaps.StartLeft), _Caps.HasFlag(BorderCaps.EndLeft));
		Sweep(mesh, _Profile, right, _Caps.HasFlag(BorderCaps.StartRight), _Caps.HasFlag(BorderCaps.EndRight));

		return mesh;
	}



	/// <summary>
	/// The borders round a junction: each corner path is swept in two halves, one in each neighbouring arm's
	/// profile, so a road's border carries on round the corner in its own style. Where the two styles differ the
	/// halves are closed off against each other.
	/// </summary>
	public static PolygonMesh BuildJunction(RoadJunctionLayout _Layout, Func<RoadSegment, RoadProfile> _ProfileOf)
	{
		var mesh = new PolygonMesh();

		foreach (var path in _Layout.CornerPaths)
		{
			var profileA = _ProfileOf(path.ArmA.Segment);
			var profileB = _ProfileOf(path.ArmB.Segment);
			bool differ = !ReferenceEquals(profileA, profileB);

			if (profileA.HasBorder)
				Sweep(mesh, profileA, HalfPath(path, true), false, differ || !profileB.HasBorder);

			if (profileB.HasBorder)
				Sweep(mesh, profileB, HalfPath(path, false), differ || !profileA.HasBorder, false);
		}

		return mesh;
	}



	/// <summary>
	/// One half of a corner path as a border run. Away-from-road directions are taken from the outline (the
	/// interior is on its left), except at the rows, which reuse the arm's own frame so the arm chunk's border
	/// meets this one exactly. The along coordinate carries the arm's curve distance on round the corner.
	/// </summary>
	private static List<PathPoint> HalfPath(RoadJunctionLayout.CornerPath _Path, bool _FirstHalf)
	{
		var points = _Path.Points;
		int first = _FirstHalf ? 0 : _Path.MidIndex;
		int last = _FirstHalf ? _Path.MidIndex : points.Count - 1;
		var arm = _FirstHalf ? _Path.ArmA : _Path.ArmB;
		int count = last - first + 1;
		var result = new List<PathPoint>(count);

		if (count < 2)
			return result;

		// Walking towards the node runs against an arm that starts there, and with one that ends there.
		float sign = _FirstHalf ? (arm.AtStart ? -1.0f : 1.0f) : (arm.AtStart ? 1.0f : -1.0f);
		var along = new float[count];

		if (_FirstHalf)
		{
			along[0] = arm.CurveDistance(arm.Trim);

			for (int k = 1; k < count; k++)
				along[k] = along[k - 1] + sign * Vector3.DistanceBetween(points[first + k - 1], points[first + k]);
		}
		else
		{
			along[count - 1] = arm.CurveDistance(arm.Trim);

			for (int k = count - 1; k > 0; k--)
				along[k - 1] = along[k] - sign * Vector3.DistanceBetween(points[first + k - 1], points[first + k]);
		}

		for (int k = 0; k < count; k++)
		{
			int index = first + k;
			Vector3 outward;

			if (index == 0)
			{
				outward = _Path.StartOutward;
			}
			else if (index == points.Count - 1)
			{
				outward = _Path.EndOutward;
			}
			else
			{
				Vector3 direction = points[index + 1] - points[index - 1];
				outward = new Vector3(direction.y, -direction.x, 0.0f).Normal;
			}

			var fold = index >= _Path.ArcFirst && index <= _Path.ArcLast ? _Path.Fold : null;

			result.Add(new PathPoint(points[index], outward, along[k], fold));
		}

		return result;
	}



	/// <summary>
	/// Sweeps the profile's cross-section along the path, one row of vertices per point, and closes the open ends.
	/// Every face is emitted as two triangles: a verge that rolls with noise is nowhere near planar.
	/// </summary>
	public static void Sweep(PolygonMesh _Mesh, RoadProfile _Profile, IReadOnlyList<PathPoint> _Path, bool _CapStart, bool _CapEnd)
	{
		if (!_Profile.HasBorder)
			return;

		var path = Compact(_Path);

		if (path.Count < 2)
			return;

		var faces = BuildFaces(_Profile);
		int nodeCount = NodeCount(_Profile);

		var shapes = new Vector2[path.Count][];
		var positions = new Vector3[path.Count][];
		var across = new float[path.Count][];
		var handles = new HalfEdgeMesh.VertexHandle[path.Count][];

		for (int i = 0; i < path.Count; i++)
		{
			var point = path[i];
			var shape = new Vector2[nodeCount];
			var rows = new Vector3[nodeCount];
			var distances = new float[nodeCount];

			EvaluateProfile(_Profile, point, shape);

			for (int k = 0; k < nodeCount; k++)
			{
				rows[k] = NodePosition(point, shape[k]);

				if (k > 0)
					distances[k] = distances[k - 1] + Vector3.DistanceBetween(rows[k - 1], rows[k]);
			}

			shapes[i] = shape;
			positions[i] = rows;
			across[i] = distances;
			handles[i] = _Mesh.AddVertices(rows);
		}

		// One winding for the whole run, from which side of its path the border lies on. Deciding it per quad from
		// the quad's own normal goes wrong on thin or folded quads, and a quad wound against its neighbours makes
		// the mesh builder drop whichever face comes second: a hole.
		bool flip = Vector3.Dot(Vector3.Cross(path[^1].Position - path[0].Position, path[0].Outward), Vector3.Up) > 0.0f;

		for (int i = 0; i < path.Count - 1; i++)
		{
			foreach (var face in faces)
			{
				int a = face.NodeA;
				int b = face.NodeB;

				Vector2 uv0 = FaceUv(face, path[i], positions[i][a], across[i], a);
				Vector2 uv1 = FaceUv(face, path[i], positions[i][b], across[i], b);
				Vector2 uv2 = FaceUv(face, path[i + 1], positions[i + 1][b], across[i + 1], b);
				Vector2 uv3 = FaceUv(face, path[i + 1], positions[i + 1][a], across[i + 1], a);

				AddQuad(_Mesh, face.Material, flip,
					handles[i][a], handles[i][b], handles[i + 1][b], handles[i + 1][a],
					uv0, uv1, uv2, uv3);
			}
		}

		var capMaterial = _Profile.Border == RoadBorderStyle.CountrySide ? _Profile.ResolvedRoadMaterial : _Profile.ResolvedBorderMaterial;
		float capRepeat = _Profile.Border == RoadBorderStyle.CountrySide ? _Profile.RoadTextureRepeat : _Profile.BorderTextureRepeat;

		if (_CapStart)
			AddCap(_Mesh, capMaterial, capRepeat, handles[0], positions[0], shapes[0], path[0].Position - path[1].Position);

		if (_CapEnd)
			AddCap(_Mesh, capMaterial, capRepeat, handles[^1], positions[^1], shapes[^1], path[^1].Position - path[^2].Position);
	}



	private static List<PathPoint> Compact(IReadOnlyList<PathPoint> _Path)
	{
		var result = new List<PathPoint>(_Path.Count);

		foreach (var point in _Path)
		{
			if (result.Count > 0 && Vector3.DistanceBetween(result[^1].Position, point.Position) < 0.01f)
				continue;

			result.Add(point);
		}

		return result;
	}



	private static int NodeCount(RoadProfile _Profile)
	{
		return _Profile.Border switch
		{
			RoadBorderStyle.Sidewalk => 4,
			RoadBorderStyle.CountrySide => 3 + _Profile.SlopeSegments,
			_ => 0
		};
	}



	private static Face[] BuildFaces(RoadProfile _Profile)
	{
		switch (_Profile.Border)
		{
			case RoadBorderStyle.Sidewalk:
			{
				var material = _Profile.ResolvedBorderMaterial;
				float repeat = _Profile.BorderTextureRepeat;

				// Kerb face, pavement, outer face. The pavement spans one texture repeat across, whatever its width;
				// the kerb texture follows the slope of a bevelled kerb instead of stretching.
				float bevelU = new Vector2(_Profile.CurbBevel, _Profile.CurbHeight).Length / repeat;
				float heightU = _Profile.CurbHeight / repeat;

				return new[]
				{
					new Face(0, 1, material, repeat, _U0: bevelU, _U1: 0.0f),
					new Face(1, 2, material, repeat, _U0: 0.0f, _U1: 1.0f),
					new Face(2, 3, material, repeat, _U0: 1.0f - heightU, _U1: 1.0f)
				};
			}

			case RoadBorderStyle.CountrySide:
			{
				var roadMaterial = _Profile.ResolvedRoadMaterial;
				var vergeMaterial = _Profile.ResolvedBorderMaterial;
				float roadRepeat = _Profile.RoadTextureRepeat;
				float vergeRepeat = _Profile.BorderTextureRepeat;

				// Shoulder (an extension of the road, so it tiles like one), the drop (the road's exposed thickness),
				// then the slope down to the terrain.
				var faces = new List<Face>
				{
					new Face(0, 1, roadMaterial, roadRepeat, _Planar: true),
					new Face(1, 2, roadMaterial, roadRepeat, _U0: 0.0f, _U1: _Profile.Drop / roadRepeat)
				};

				for (int k = 0; k < _Profile.SlopeSegments; k++)
					faces.Add(new Face(2 + k, 3 + k, vergeMaterial, vergeRepeat, _AccumulateFrom: 2));

				return faces.ToArray();
			}

			default:
				return Array.Empty<Face>();
		}
	}



	/// <summary>
	/// The cross-section at a point as (distance from the road edge, height above the road surface) per node.
	/// Country side borders read noise at the point's position, so the wander is continuous across chunk and
	/// junction seams and differs between the two sides of the road.
	/// </summary>
	private static void EvaluateProfile(RoadProfile _Profile, in PathPoint _Point, Vector2[] _Shape)
	{
		switch (_Profile.Border)
		{
			case RoadBorderStyle.Sidewalk:
				_Shape[0] = new Vector2(0.0f, 0.0f);
				_Shape[1] = new Vector2(_Profile.CurbBevel, _Profile.CurbHeight);
				_Shape[2] = new Vector2(_Profile.BorderWidth, _Profile.CurbHeight);
				_Shape[3] = new Vector2(_Profile.BorderWidth, 0.0f);
				break;

			case RoadBorderStyle.CountrySide:
			{
				float scale = _Profile.ChaosScale;
				float amplitude = _Profile.Chaos;
				float x = _Point.Position.x * scale;
				float y = _Point.Position.y * scale;

				// Noise is 0..1; centre it to roughly plus or minus the chaos amount.
				float meanderShoulder = (Noise.Perlin(x + BandShoulder, y) - 0.5f) * 2.0f * amplitude;
				float meanderOuter = (Noise.Perlin(x + BandOuter, y) - 0.5f) * 2.0f * amplitude;

				// Floor the widths so a strong inward wander can never fold two nodes onto each other.
				float shoulder = MathF.Max(1.0f, _Profile.BorderWidth + meanderShoulder);
				float span = MathF.Max(1.0f, _Profile.VergeWidth + meanderOuter);

				_Shape[0] = new Vector2(0.0f, 0.0f);
				_Shape[1] = new Vector2(shoulder, 0.0f);
				_Shape[2] = new Vector2(shoulder, -_Profile.Drop);

				for (int k = 1; k <= _Profile.SlopeSegments; k++)
				{
					float fraction = (float)k / _Profile.SlopeSegments;

					// The roll varies along the road and across the slope, so the verge undulates in 3D.
					float roll = (Noise.Fbm(3, x, y, BandRoll + fraction * _Profile.VergeWidth * scale) - 0.5f) * 2.0f * amplitude;

					_Shape[2 + k] = new Vector2(shoulder + fraction * span, -_Profile.Drop - fraction * _Profile.VergeDepth + roll);
				}

				break;
			}
		}
	}



	private static Vector2 FaceUv(in Face _Face, in PathPoint _Point, Vector3 _Position, float[] _Across, int _Node)
	{
		if (_Face.Planar)
			return new Vector2(_Position.x, _Position.y) / _Face.Repeat;

		float u = _Face.AccumulateFrom >= 0
			? (_Across[_Node] - _Across[_Face.AccumulateFrom]) / _Face.Repeat
			: (_Node == _Face.NodeA ? _Face.U0 : _Face.U1);

		return new Vector2(u, _Point.Along / _Face.Repeat);
	}



	/// <summary>
	/// Where a profile node lands for a path point. Along a fillet the border wraps the inside of, a node further
	/// out than the radius would pass through the centre and come back folded; it goes to the block corner for
	/// its offset instead, so the outline turns square once the border is wider than the fillet.
	/// </summary>
	private static Vector3 NodePosition(in PathPoint _Point, Vector2 _Node)
	{
		if (_Point.Fold is { } fold && _Node.x >= fold.Radius)
		{
			Vector2 mitre = fold.MitrePoint(_Node.x);

			return new Vector3(mitre.x, mitre.y, _Point.Position.z + _Node.y);
		}

		return _Point.Position + _Point.Outward * _Node.x + Vector3.Up * _Node.y;
	}



	/// <summary>
	/// A quad as two triangles, in the run's winding.
	/// </summary>
	private static void AddQuad(PolygonMesh _Mesh, Material _Material, bool _Flip,
		HalfEdgeMesh.VertexHandle _HA, HalfEdgeMesh.VertexHandle _HB, HalfEdgeMesh.VertexHandle _HC, HalfEdgeMesh.VertexHandle _HD,
		Vector2 _UvA, Vector2 _UvB, Vector2 _UvC, Vector2 _UvD)
	{
		if (!_Flip)
		{
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, _HA, _HB, _HC, _UvA, _UvB, _UvC);
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, _HA, _HC, _HD, _UvA, _UvC, _UvD);
		}
		else
		{
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, _HA, _HC, _HB, _UvA, _UvC, _UvB);
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, _HA, _HD, _HC, _UvA, _UvD, _UvC);
		}
	}



	/// <summary>
	/// Closes a run's end with the cross-section itself, fanned from the road edge and facing along the road.
	/// </summary>
	private static void AddCap(PolygonMesh _Mesh, Material _Material, float _Repeat, HalfEdgeMesh.VertexHandle[] _Handles, Vector3[] _Positions, Vector2[] _Shape, Vector3 _Facing)
	{
		for (int k = 1; k < _Positions.Length - 1; k++)
		{
			Vector3 normal = Vector3.Cross(_Positions[k] - _Positions[0], _Positions[k + 1] - _Positions[0]);
			Vector2 uv0 = _Shape[0] / _Repeat;
			Vector2 uv1 = _Shape[k] / _Repeat;
			Vector2 uv2 = _Shape[k + 1] / _Repeat;

			if (Vector3.Dot(normal, _Facing) >= 0.0f)
				MeshUtility.AddTexturedTriangle(_Mesh, _Material, _Handles[0], _Handles[k], _Handles[k + 1], uv0, uv1, uv2);
			else
				MeshUtility.AddTexturedTriangle(_Mesh, _Material, _Handles[0], _Handles[k + 1], _Handles[k], uv0, uv2, uv1);
		}
	}
}
