using System;
using System.Collections.Generic;
using System.Text;
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
		/// How far the outer part of the profile is lifted above the road edge here (the kerb stays on the road,
		/// the outer edge takes all of it). Along a road it runs linearly from one end's corner height difference
		/// to the other's; on a corner's arc (<see cref="IsArc"/>) it is whatever brings the row to the corner's
		/// one height, and nodes further out than the fillet radius collapse onto the centre.
		/// </summary>
		public readonly float Lift;
		public readonly RoadJunctionLayout.CornerFold Fold;
		public readonly bool IsArc;

		public PathPoint(Vector3 _Position, Vector3 _Outward, float _Along, float _Lift = 0.0f, RoadJunctionLayout.CornerFold _Fold = null, bool _IsArc = false)
		{
			Position = _Position;
			Outward = _Outward;
			Along = _Along;
			Lift = _Lift;
			Fold = _Fold;
			IsArc = _IsArc;
		}

		/// <summary>
		/// True when a node at this offset sits on the corner's centre rather than at its own offset.
		/// </summary>
		public bool IsFolded(float _Offset)
		{
			return IsArc && Fold is not null && _Offset >= Fold.Radius;
		}
	}

	/// <summary>
	/// The height the outer edge of one side's border is lifted along a segment: the corner height difference at
	/// each end (0 where the end has no corner on that side), interpolated linearly between the two tangent
	/// points. Arm chunks and the junction runs evaluate this same line, so the seam between them is exact and
	/// nothing depends on how long the road is.
	/// </summary>
	public readonly record struct SideBlend(bool Active, float StartDistance, float StartDelta, float EndDistance, float EndDelta)
	{
		public float DeltaAt(float _Distance)
		{
			if (!Active)
				return 0.0f;

			float span = EndDistance - StartDistance;

			if (span <= 0.001f)
				return StartDelta;

			return MathX.Lerp(StartDelta, EndDelta, Math.Clamp((_Distance - StartDistance) / span, 0.0f, 1.0f));
		}

		public void AddToHash(ref StableHash _Hash)
		{
			_Hash.Add(Active ? 1 : 0);

			if (!Active)
				return;

			_Hash.Add(StartDistance);
			_Hash.Add(StartDelta);
			_Hash.Add(EndDistance);
			_Hash.Add(EndDelta);
		}
	}

	/// <summary>
	/// The border lifts of a segment's two sides.
	/// </summary>
	public readonly record struct SegmentBorderBlends(SideBlend Left, SideBlend Right)
	{
		public void AddToHash(ref StableHash _Hash)
		{
			Left.AddToHash(ref _Hash);
			Right.AddToHash(ref _Hash);
		}
	}

	/// <summary>
	/// One surface of the profile, between two of its nodes. U runs across the profile: fixed per node, or
	/// accumulated along the cross-section from <see cref="AccumulateFrom"/> when the shape varies (verges).
	/// Planar faces take a top-down projection instead, like the road they extend. A blendable face is the one a
	/// neighbouring country road's gravel spills onto (the pavement), painted with blend weights where it does.
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
		public readonly bool Blendable;

		public Face(int _NodeA, int _NodeB, Material _Material, float _Repeat, bool _Planar = false, int _AccumulateFrom = -1, float _U0 = 0.0f, float _U1 = 1.0f, bool _Blendable = false)
		{
			NodeA = _NodeA;
			NodeB = _NodeB;
			Material = _Material;
			Repeat = _Repeat;
			Planar = _Planar;
			AccumulateFrom = _AccumulateFrom;
			U0 = _U0;
			U1 = _U1;
			Blendable = _Blendable;
		}
	}

	// Separate regions of the noise field for each thing that wanders, so they don't move in step.
	private const float BandShoulder = 0.0f;
	private const float BandOuter = 1000.0f;
	private const float BandRoll = 2000.0f;
	private const float BandSpill = 3000.0f;

	/// <summary>
	/// Two corners of a face closer than this are one point: the face is a triangle.
	/// </summary>
	private const float JoinDistance = 1.0f;

	/// <summary>
	/// How much of the spilled gravel reaches the kerb side of the pavement, relative to its outer edge: the verge
	/// it comes from lies beyond the neighbour's shoulder, so it creeps in from the outside.
	/// </summary>
	private const float SpillInnerFactor = 0.2f;

	/// <summary>
	/// How far the gravel's front wanders back and forth along the road, as a fraction of the spill length, and
	/// the size of the noise it wanders by (about a wavelength per hundred units).
	/// </summary>
	private const float SpillWobble = 0.35f;
	private const float SpillNoiseScale = 0.01f;



	/// <summary>
	/// Both borders of a stretch of road, rows at the same distances as the road surface.
	/// </summary>
	public static PolygonMesh BuildSegment(RoadCurveTable _Table, float _StartDistance, float _EndDistance, float _Width, RoadProfile _Profile, float _Spacing, float _Lift, BorderCaps _Caps, in SegmentBorderBlends _Blends, in RoadBorderTaper _Taper, BorderDebug _Debug = null)
	{
		var mesh = new PolygonMesh();

		float length = MathF.Max(0.0f, _EndDistance - _StartDistance);
		int steps = Math.Max(1, (int)MathF.Ceiling(length / MathF.Max(1.0f, _Spacing)));
		float halfWidth = _Width * 0.5f;

		var left = new List<PathPoint>(steps + 1);
		var right = new List<PathPoint>(steps + 1);

		_Debug?.Note($"SEGMENT start={_StartDistance:0.###} end={_EndDistance:0.###} length={_Table.Length:0.###} width={_Width} lift={_Lift} spacing={_Spacing} caps={_Caps} profile={_Profile.ResourceName} border={_Profile.Border} borderWidth={_Profile.BorderWidth} curb={_Profile.CurbHeight} bevel={_Profile.CurbBevel}");
		_Debug?.Note($"BLEND left {Describe(_Blends.Left)}");
		_Debug?.Note($"BLEND right {Describe(_Blends.Right)}");
		_Debug?.Note($"TAPER start={_Taper.StartLength:0.###}/{_Taper.StartWidth:0.###} end={_Taper.EndLength:0.###}/{_Taper.EndWidth:0.###}");

		for (int i = 0; i <= steps; i++)
		{
			float distance = i == steps ? _EndDistance : _StartDistance + length * i / steps;
			Vector3 rightDirection = RoadCurve.RightOf(_Table.TangentAt(distance));

			RoadSurfaceBuilder.CrossSection(_Table, distance, halfWidth, _Lift, out Vector3 leftPos, out Vector3 rightPos);

			left.Add(new PathPoint(leftPos, -rightDirection, distance, _Blends.Left.DeltaAt(distance)));
			right.Add(new PathPoint(rightPos, rightDirection, distance, _Blends.Right.DeltaAt(distance)));
		}

		var shape = new ProfileShape(_Profile, _Taper, _Table.Length);

		Sweep(mesh, shape, left, _Caps.HasFlag(BorderCaps.StartLeft), _Caps.HasFlag(BorderCaps.EndLeft), _Debug, BorderDebug.ArmColor, "arm L");
		Sweep(mesh, shape, right, _Caps.HasFlag(BorderCaps.StartRight), _Caps.HasFlag(BorderCaps.EndRight), _Debug, BorderDebug.ArmColor, "arm R");

		return mesh;
	}



	/// <summary>
	/// A profile as swept along one segment: the profile itself, plus the taper at the segment's ends and the
	/// segment's length the taper is measured against.
	/// </summary>
	public readonly struct ProfileShape
	{
		public readonly RoadProfile Profile;
		public readonly RoadBorderTaper Taper;
		public readonly float Length;

		public ProfileShape(RoadProfile _Profile, in RoadBorderTaper _Taper, float _Length)
		{
			Profile = _Profile;
			Taper = _Taper;
			Length = _Length;
		}
	}



	/// <summary>
	/// The borders round a junction: each corner path is swept in two halves, one in each neighbouring arm's
	/// profile, so a road's border carries on round the corner in its own style. Where the two styles differ the
	/// halves are closed off against each other. The arms' segment blends are what their chunks were built with,
	/// so the straight stretch of each half continues the arm's border exactly.
	/// </summary>
	public static PolygonMesh BuildJunction(RoadJunctionLayout _Layout, Func<RoadSegment, RoadProfile> _ProfileOf, Func<RoadSegment, SegmentBorderBlends> _BlendsOf, Func<RoadSegment, RoadBorderTaper> _TaperOf, BorderDebug _Debug = null)
	{
		var mesh = new PolygonMesh();

		if (_Debug is not null)
		{
			_Debug.Note($"JUNCTION node={_Layout.Node.Id} position={BorderDebug.Format(_Layout.Node.Position)} radius={_Layout.Radius} centre={BorderDebug.Format(_Layout.Centre)}");
			_Layout.Describe(_Debug);

			foreach (var arm in _Layout.Arms)
				_Debug.Note($"BLEND arm={arm.Segment.Id} left {Describe(_BlendsOf(arm.Segment).Left)} right {Describe(_BlendsOf(arm.Segment).Right)}");
		}

		foreach (var path in _Layout.CornerPaths)
		{
			var profileA = _ProfileOf(path.ArmA.Segment);
			var profileB = _ProfileOf(path.ArmB.Segment);
			bool differ = !SameProfile(profileA, profileB);
			var shapeA = new ProfileShape(profileA, _TaperOf(path.ArmA.Segment), path.ArmA.Length);
			var shapeB = new ProfileShape(profileB, _TaperOf(path.ArmB.Segment), path.ArmB.Length);

			if (profileA.HasBorder)
				Sweep(mesh, shapeA, HalfPath(path, true, _BlendsOf(path.ArmA.Segment)), false, differ || !profileB.HasBorder, _Debug, BorderDebug.FirstHalfColor, $"J{_Layout.Node.Id} A{path.ArmA.Segment.Id}");

			if (profileB.HasBorder)
				Sweep(mesh, shapeB, HalfPath(path, false, _BlendsOf(path.ArmB.Segment)), differ || !profileA.HasBorder, false, _Debug, BorderDebug.SecondHalfColor, $"J{_Layout.Node.Id} B{path.ArmB.Segment.Id}");
		}

		return mesh;
	}



	private static string Describe(in SideBlend _Blend)
	{
		return _Blend.Active
			? $"active from={_Blend.StartDistance:0.###} delta={_Blend.StartDelta:0.###} to={_Blend.EndDistance:0.###} delta={_Blend.EndDelta:0.###}"
			: "inactive";
	}



	/// <summary>
	/// The same road type, whether or not the two references are the same loaded instance of the asset.
	/// </summary>
	public static bool SameProfile(RoadProfile _A, RoadProfile _B)
	{
		if (ReferenceEquals(_A, _B))
			return true;

		if (_A is null || _B is null)
			return false;

		return !string.IsNullOrEmpty(_A.ResourcePath) && _A.ResourcePath == _B.ResourcePath;
	}



	/// <summary>
	/// One half of a corner path as a border run. Away-from-road directions come with the path points (the frame
	/// of the arm or the arc they are on), so the arm chunk's border meets this one exactly at the row. The along
	/// coordinate carries the arm's curve distance on round the corner.
	/// </summary>
	private static List<PathPoint> HalfPath(RoadJunctionLayout.CornerPath _Path, bool _FirstHalf, in SegmentBorderBlends _Blends)
	{
		var points = _Path.Points;
		int first = _FirstHalf ? 0 : _Path.MidIndex;
		int last = _FirstHalf ? _Path.MidIndex : points.Count - 1;
		var arm = _FirstHalf ? _Path.ArmA : _Path.ArmB;
		int count = last - first + 1;
		var result = new List<PathPoint>(count);

		if (count < 2)
			return result;

		// The first half runs on its arm's left (looking out of the node), the second on its arm's right; which
		// side of the segment that is depends on which way the segment runs.
		bool segmentLeft = _FirstHalf == arm.AtStart;
		var side = segmentLeft ? _Blends.Left : _Blends.Right;

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
			Vector3 outward = _Path.Outwards[index];

			// A sharp corner point has no frame of its own: take the right of the way through it.
			if (outward.IsNearZeroLength && index > 0 && index < points.Count - 1)
			{
				Vector3 direction = points[index + 1] - points[index - 1];
				outward = new Vector3(direction.y, -direction.x, 0.0f).Normal;
			}

			var fold = _Path.Fold;
			bool onArc = fold is not null && index >= _Path.ArcFirst && index <= _Path.ArcLast;

			// Straight stretches take the segment's lift line, exactly like the arm chunk's rows do.
			result.Add(new PathPoint(points[index], outward, along[k], side.DeltaAt(along[k]), fold, onArc));
		}

		return result;
	}



	/// <summary>
	/// Sweeps the profile's cross-section along the path, one row of vertices per point, and closes the open ends.
	/// Every face is emitted as two triangles: a verge that rolls with noise is nowhere near planar.
	/// </summary>
	public static void Sweep(PolygonMesh _Mesh, in ProfileShape _Shape, IReadOnlyList<PathPoint> _Path, bool _CapStart, bool _CapEnd, BorderDebug _Debug = null, Color _DebugColor = default, string _DebugName = null)
	{
		var profile = _Shape.Profile;

		if (!profile.HasBorder)
			return;

		var path = Compact(_Path);

		if (path.Count < 2)
			return;

		var faces = BuildFaces(profile);
		int nodeCount = NodeCount(profile);

		var shapes = new Vector2[path.Count][];
		var positions = new Vector3[path.Count][];
		var across = new float[path.Count][];
		var handles = new HalfEdgeMesh.VertexHandle[path.Count][];
		var reliefs = new float[path.Count];
		var spills = new float[path.Count];

		// Gravel spilling over from a country road on the other side of a node is painted onto the pavement as
		// vertex blend weights, which need a material that mixes gravel in by them.
		var blendMaterial = profile.Border == RoadBorderStyle.Sidewalk ? profile.BlendMaterial : null;

		// One winding for the whole run, from which side of its path the border lies on. Deciding it per quad from
		// the quad's own normal goes wrong on thin or folded quads, and a quad wound against its neighbours makes
		// the mesh builder drop whichever face comes second: a hole.
		bool flip = Vector3.Dot(Vector3.Cross(path[^1].Position - path[0].Position, path[0].Outward), Vector3.Up) > 0.0f;

		_Debug?.Note($"RUN name=\"{_DebugName}\" rows={path.Count} (given {_Path.Count}) capStart={(_CapStart ? 1 : 0)} capEnd={(_CapEnd ? 1 : 0)} flip={(flip ? 1 : 0)}");

		for (int i = 0; i < path.Count; i++)
		{
			var shape = new Vector2[nodeCount];
			var rows = new Vector3[nodeCount];

			reliefs[i] = EvaluateProfile(_Shape, path[i], shape, out spills[i]);

			for (int k = 0; k < nodeCount; k++)
				rows[k] = NodePosition(path[i], shape[k], profile.BorderWidth);

			shapes[i] = shape;
			positions[i] = rows;
		}

		// A border tapered to nothing at its end has no face to close.
		_CapStart &= reliefs[0] > 0.01f;
		_CapEnd &= reliefs[^1] > 0.01f;

		var stalls = RemoveFolds(path, positions, nodeCount);

		for (int i = 0; i < path.Count; i++)
		{
			var rows = positions[i];
			var distances = new float[nodeCount];

			for (int k = 1; k < nodeCount; k++)
				distances[k] = distances[k - 1] + Vector3.DistanceBetween(rows[k - 1], rows[k]);

			across[i] = distances;
			handles[i] = new HalfEdgeMesh.VertexHandle[nodeCount];

			for (int k = 0; k < nodeCount; k++)
			{
				if (stalls[i][k] < 0)
					handles[i][k] = _Mesh.AddVertices(rows[k])[0];
			}
		}

		// A stalled node shares the vertex it stalled on, so the faces round it come out as triangles instead of
		// zero-area quads. Backward stalls point at later rows, hence the second pass.
		for (int i = 0; i < path.Count; i++)
		{
			for (int k = 0; k < nodeCount; k++)
			{
				int source = i;

				while (stalls[source][k] >= 0)
					source = stalls[source][k];

				handles[i][k] = handles[source][k];
			}
		}

		if (_Debug is not null)
		{
			for (int i = 0; i < path.Count; i++)
			{
				var point = path[i];
				var rows = positions[i];

				_Debug.Line(rows[0], rows[nodeCount - 1], _DebugColor);

				if (i == 0 || i == path.Count - 1)
					_Debug.Marker(rows[nodeCount - 1], _DebugColor);

				var nodes = new StringBuilder();

				for (int k = 0; k < nodeCount; k++)
					nodes.Append(' ').Append(BorderDebug.Format(rows[k])).Append(point.IsFolded(shapes[i][k].x) ? "F" : "").Append(stalls[i][k] >= 0 ? $"S{stalls[i][k]}" : "");

				_Debug.Note($"R {i} pos={BorderDebug.Format(point.Position)} out={BorderDebug.Format(point.Outward)} along={point.Along:0.###} lift={point.Lift:0.###} arc={(point.IsArc ? 1 : 0)} fold={(point.Fold is not null ? 1 : 0)} relief={reliefs[i]:0.###} spill={spills[i]:0.###} |{nodes}");
			}
		}

		if (_Debug is not null && _CapStart)
			_Debug.Line(positions[0][0], positions[0][nodeCount - 1], BorderDebug.CapColor);

		if (_Debug is not null && _CapEnd)
			_Debug.Line(positions[^1][0], positions[^1][nodeCount - 1], BorderDebug.CapColor);

		for (int i = 0; i < path.Count - 1; i++)
		{
			foreach (var face in faces)
			{
				int a = face.NodeA;
				int b = face.NodeB;

				// A face whose nodes have both collapsed onto the corner centre has nothing left between them;
				// its stretch across the corner is the block corner patch instead.
				if (IsFoldedFace(path[i], shapes[i], a, b) || IsFoldedFace(path[i + 1], shapes[i + 1], a, b))
					continue;

				// Corners of the two rows that sit on one point (the row before a corner's arc reaches the centre
				// the arc rows fold onto; every arc row's outer node is the centre) make the face a triangle. Cut
				// as a quad it would also give a needle-thin sliver with a random normal, which shows through from
				// below and z-fights from above.
				bool outerJoined = positions[i][b].Distance(positions[i + 1][b]) < JoinDistance;
				bool innerJoined = positions[i][a].Distance(positions[i + 1][a]) < JoinDistance;

				if (outerJoined && innerJoined)
					continue;

				Vector2 uv0 = FaceUv(face, path[i], positions[i][a], across[i], a);
				Vector2 uv1 = FaceUv(face, path[i], positions[i][b], across[i], b);
				Vector2 uv2 = FaceUv(face, path[i + 1], positions[i + 1][b], across[i + 1], b);
				Vector2 uv3 = FaceUv(face, path[i + 1], positions[i + 1][a], across[i + 1], a);

				var material = face.Material;
				Color32[] blends = null;

				// Only the faces the gravel reaches take the blend material; the rest of the pavement stays on the
				// plain one, and a weight of nothing on their shared row makes the change invisible.
				if (face.Blendable && blendMaterial is not null && (spills[i] > 0.0f || spills[i + 1] > 0.0f))
				{
					material = blendMaterial;
					blends = new[]
					{
						SpillBlend(spills[i], SpillInnerFactor),
						SpillBlend(spills[i], 1.0f),
						SpillBlend(spills[i + 1], 1.0f),
						SpillBlend(spills[i + 1], SpillInnerFactor)
					};
				}

				if (outerJoined)
				{
					// The folded row's vertex is the exact centre; keep that one.
					bool keepNext = path[i + 1].IsFolded(shapes[i + 1][b].x) || !path[i].IsFolded(shapes[i][b].x);
					var outer = keepNext ? handles[i + 1][b] : handles[i][b];
					Vector2 uvOuter = keepNext ? uv2 : uv1;

					AddTriangle(_Mesh, material, flip, handles[i][a], outer, handles[i + 1][a], uv0, uvOuter, uv3,
						blends is null ? null : new[] { blends[0], keepNext ? blends[2] : blends[1], blends[3] });
				}
				else if (innerJoined)
				{
					AddTriangle(_Mesh, material, flip, handles[i][a], handles[i][b], handles[i + 1][b], uv0, uv1, uv2,
						blends is null ? null : new[] { blends[0], blends[1], blends[2] });
				}
				else
				{
					AddQuad(_Mesh, material, flip,
						handles[i][a], handles[i][b], handles[i + 1][b], handles[i + 1][a],
						uv0, uv1, uv2, uv3, blends);
				}
			}
		}

		AddCornerPatches(_Mesh, faces, path, shapes, positions, across, _Debug);

		var capMaterial = profile.Border == RoadBorderStyle.CountrySide ? profile.ResolvedRoadMaterial : profile.ResolvedBorderMaterial;
		float capRepeat = profile.Border == RoadBorderStyle.CountrySide ? profile.RoadTextureRepeat : profile.BorderTextureRepeat;

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
					new Face(1, 2, material, repeat, _U0: 0.0f, _U1: 1.0f, _Blendable: true),
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
	/// junction seams and differs between the two sides of the road. Near a tapered end the relief (kerb height,
	/// drop, verge depth, roll) is scaled down and the width run towards the neighbour's, so the border comes
	/// down to a flat strip of the neighbour's width at the node. Returns the relief in force at the point, and
	/// how much of a neighbouring country road's gravel has spilled this far (1 at the node, 0 beyond the taper).
	/// </summary>
	private static float EvaluateProfile(in ProfileShape _Shape, in PathPoint _Point, Vector2[] _Nodes, out float _Spill)
	{
		var profile = _Shape.Profile;
		float relief = _Shape.Taper.ReliefAt(_Point.Along, _Shape.Length, out float towards, out float targetWidth, out bool spill);
		bool tapering = towards > 0.0f;

		_Spill = spill ? SpillAt(towards, _Point.Position) : 0.0f;

		switch (profile.Border)
		{
			case RoadBorderStyle.Sidewalk:
			{
				float width = tapering ? MathX.Lerp(profile.BorderWidth, targetWidth, towards) : profile.BorderWidth;
				float height = profile.CurbHeight * relief;

				_Nodes[0] = new Vector2(0.0f, 0.0f);
				_Nodes[1] = new Vector2(MathF.Min(profile.CurbBevel * relief, width), height);
				_Nodes[2] = new Vector2(width, height);
				_Nodes[3] = new Vector2(width, 0.0f);
				break;
			}

			case RoadBorderStyle.CountrySide:
			{
				float scale = profile.ChaosScale;
				float amplitude = profile.Chaos * relief;
				float x = _Point.Position.x * scale;
				float y = _Point.Position.y * scale;

				// Noise is 0..1; centre it to roughly plus or minus the chaos amount.
				float meanderShoulder = (Noise.Perlin(x + BandShoulder, y) - 0.5f) * 2.0f * amplitude;
				float meanderOuter = (Noise.Perlin(x + BandOuter, y) - 0.5f) * 2.0f * amplitude;

				// Floor the widths so a strong inward wander can never fold two nodes onto each other.
				float shoulder = MathF.Max(1.0f, profile.BorderWidth + meanderShoulder);
				float span = MathF.Max(1.0f, profile.VergeWidth + meanderOuter);

				// Towards a different road type the whole strip runs to that type's width, keeping its proportions.
				if (tapering)
				{
					float total = shoulder + span;
					float ratio = MathX.Lerp(total, targetWidth, towards) / total;

					shoulder = MathF.Max(0.5f, shoulder * ratio);
					span = MathF.Max(0.5f, span * ratio);
				}

				_Nodes[0] = new Vector2(0.0f, 0.0f);
				_Nodes[1] = new Vector2(shoulder, 0.0f);
				_Nodes[2] = new Vector2(shoulder, -profile.Drop * relief);

				for (int k = 1; k <= profile.SlopeSegments; k++)
				{
					float fraction = (float)k / profile.SlopeSegments;

					// The roll varies along the road and across the slope, so the verge undulates in 3D.
					float roll = (Noise.Fbm(3, x, y, BandRoll + fraction * profile.VergeWidth * scale) - 0.5f) * 2.0f * amplitude;

					_Nodes[2 + k] = new Vector2(shoulder + fraction * span, (-profile.Drop - fraction * profile.VergeDepth) * relief + roll);
				}

				break;
			}
		}

		return relief;
	}



	/// <summary>
	/// How much gravel has spilled to a point a fraction of the way into a taper (1 at the node): the fraction
	/// itself, with its front pushed back and forth by noise read at the point, most in the middle of the run so
	/// the node stays fully covered and the far end clean.
	/// </summary>
	private static float SpillAt(float _Towards, Vector3 _Position)
	{
		float wobble = (Noise.Perlin(_Position.x * SpillNoiseScale + BandSpill, _Position.y * SpillNoiseScale) - 0.5f) * 2.0f * SpillWobble;

		return Math.Clamp(_Towards + wobble * 4.0f * _Towards * (1.0f - _Towards), 0.0f, 1.0f);
	}



	/// <summary>
	/// A spill amount as the vertex blend a blendable material reads: its red channel is how much of the second
	/// layer (the gravel) shows.
	/// </summary>
	private static Color32 SpillBlend(float _Spill, float _Factor)
	{
		byte weight = (byte)Math.Clamp(MathF.Round(_Spill * _Factor * 255.0f), 0.0f, 255.0f);

		return new Color32(weight, 0, 0, 255);
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
	/// On the inside of a bend tighter than the road is wide plus the border, the border's outer edge runs
	/// backwards: a row's outer node lands behind the previous row's and the face between them folds over itself.
	/// The offset curve has a loop there, and the fix is the one for offset curves: cut the loop out. A node whose
	/// face would fold instead stays on the previous row's node (it shares that vertex), so the outer edge runs
	/// straight from the last good node to the next one. The first and last rows are never moved: they are the
	/// rows a run shares with its neighbouring piece, and the seam stays exact; a fold against the last row is
	/// taken out by stalling the rows before it onto it instead. Arc rows are left to the corner fold. Returns,
	/// per row and node, the row whose node it stalled onto (-1 when it kept its own).
	/// </summary>
	private static int[][] RemoveFolds(List<PathPoint> _Path, Vector3[][] _Positions, int _NodeCount)
	{
		int count = _Path.Count;
		var stalls = new int[count][];

		for (int i = 0; i < count; i++)
		{
			stalls[i] = new int[_NodeCount];
			Array.Fill(stalls[i], -1);
		}

		if (count < 3)
			return stalls;

		for (int k = 1; k < _NodeCount; k++)
		{
			// Forward: each row against the last row that kept its own node.
			for (int i = 1; i < count - 1; i++)
			{
				if (_Path[i].IsArc || _Path[i - 1].IsArc)
					continue;

				if (FaceIsSimple(_Positions[i - 1][0], _Positions[i][0], _Positions[i][k], _Positions[i - 1][k]))
					continue;

				_Positions[i][k] = _Positions[i - 1][k];
				stalls[i][k] = i - 1;
			}

			// Backward: the last row cannot move, so rows folding against it move onto it.
			for (int i = count - 2; i >= 1; i--)
			{
				if (_Path[i].IsArc || _Path[i + 1].IsArc)
					continue;

				if (FaceIsSimple(_Positions[i][0], _Positions[i + 1][0], _Positions[i + 1][k], _Positions[i][k]))
					break;

				_Positions[i][k] = _Positions[i + 1][k];
				stalls[i][k] = i + 1;
			}
		}

		return stalls;
	}



	/// <summary>
	/// True when the face with these corners (kerb, next kerb, next node, node) does not cross itself in plan.
	/// </summary>
	private static bool FaceIsSimple(Vector3 _A, Vector3 _B, Vector3 _C, Vector3 _D)
	{
		return !SegmentsCross(Flat(_A), Flat(_B), Flat(_C), Flat(_D)) && !SegmentsCross(Flat(_B), Flat(_C), Flat(_D), Flat(_A));
	}



	/// <summary>
	/// Proper crossing of the segments p-q and r-s; touching at an end does not count.
	/// </summary>
	private static bool SegmentsCross(Vector2 _P, Vector2 _Q, Vector2 _R, Vector2 _S)
	{
		Vector2 pq = _Q - _P;
		Vector2 rs = _S - _R;
		float denominator = pq.x * rs.y - pq.y * rs.x;

		if (MathF.Abs(denominator) < 0.000001f)
			return false;

		Vector2 pr = _R - _P;
		float t = (pr.x * rs.y - pr.y * rs.x) / denominator;
		float u = (pr.x * pq.y - pr.y * pq.x) / denominator;

		return t > 0.001f && t < 0.999f && u > 0.001f && u < 0.999f;
	}



	/// <summary>
	/// Where a profile node lands for a path point. The lift is taken in full by the outer edge of the border and
	/// not at all by the kerb, with the nodes in between sharing it by their offset, so a lifted row tilts across
	/// its width. On a corner's arc the lift is whatever brings the row to the corner's one height, and a node
	/// further out than the fillet radius would pass through the centre and come back folded, so it stops at the
	/// centre instead: the rows of the arc make a pie, and the block corner beyond the centre is patched in
	/// separately.
	/// </summary>
	private static Vector3 NodePosition(in PathPoint _Point, Vector2 _Node, float _BorderWidth)
	{
		if (_Point.IsFolded(_Node.x))
			return new Vector3(_Point.Fold.Centre.x, _Point.Fold.Centre.y, _Point.Fold.ApexHeight + _Node.y);

		float weight = MathF.Min(_Node.x / MathF.Max(_BorderWidth, 1.0f), 1.0f);
		float lift = _Point.IsArc && _Point.Fold is { } fold ? fold.ApexHeight - _Point.Position.z : _Point.Lift;
		Vector3 flat = _Point.Position + _Point.Outward * _Node.x;

		return new Vector3(flat.x, flat.y, _Point.Position.z + weight * lift + _Node.y);
	}



	private static bool IsFoldedFace(in PathPoint _Point, Vector2[] _Shape, int _NodeA, int _NodeB)
	{
		return _Point.IsFolded(_Shape[_NodeA].x) && _Point.IsFolded(_Shape[_NodeB].x);
	}



	/// <summary>
	/// The block corner: what lies beyond the corner centre, out to where the two straight outer edges of the
	/// border meet. For a face whose outer node collapsed onto the centre it is the triangle from the centre to
	/// that node on the last full row and to the mitre point; for a face whose nodes both collapsed it is the
	/// strip between their two mitre points. Both halves of a corner contribute their triangle, together making
	/// the square. Fresh vertices: these faces share no edges with the sweep.
	/// </summary>
	private static void AddCornerPatches(PolygonMesh _Mesh, Face[] _Faces, List<PathPoint> _Path, Vector2[][] _Shapes, Vector3[][] _Positions, float[][] _Across, BorderDebug _Debug)
	{
		int arcStart = -1;
		int arcEnd = -1;

		for (int i = 0; i < _Path.Count; i++)
		{
			if (!_Path[i].IsArc)
				continue;

			if (arcStart < 0)
				arcStart = i;

			arcEnd = i;
		}

		if (arcStart < 0 || _Path[arcStart].Fold is not { } fold)
			return;

		// The last full row before the arc (first half of a corner) or the first after it (second half).
		int t = arcStart > 0 ? arcStart - 1 : (arcEnd < _Path.Count - 1 ? arcEnd + 1 : -1);

		if (t < 0)
			return;

		var row = _Path[t];
		var shape = _Shapes[t];
		var bisector = new Vector3(fold.Bisector.x, fold.Bisector.y, 0.0f);

		// Which way the along-the-road coordinate runs towards the corner, for the faces that keep it.
		float towardsCorner = MathF.Sign(_Path[arcStart].Along - row.Along);

		_Debug?.Note($"PATCHES arcStart={arcStart} arcEnd={arcEnd} row={t} apex={BorderDebug.Format(fold.Apex)} centre={BorderDebug.Format(fold.Centre)} bisector={BorderDebug.Format(fold.Bisector)} halfSin={fold.HalfAngleSin:0.####} radius={fold.Radius:0.###} apexHeight={fold.ApexHeight:0.###}");

		foreach (var face in _Faces)
		{
			int a = face.NodeA;
			int b = face.NodeB;

			// Nothing collapsed: the sweep covered this face all the way round.
			if (shape[b].x < fold.Radius)
				continue;

			_Debug?.Note($"PATCH face={a}-{b} offsetA={shape[a].x:0.###} offsetB={shape[b].x:0.###} mitreA={BorderDebug.Format(fold.MitrePoint(shape[a].x))} mitreB={BorderDebug.Format(fold.MitrePoint(shape[b].x))} rowA={BorderDebug.Format(_Positions[t][a])} rowB={BorderDebug.Format(_Positions[t][b])}");

			Vector2 edge = shape[b] - shape[a];
			Vector3 expected = bisector * -edge.y + Vector3.Up * edge.x;

			// The profile's texture runs across the border and along the road; a block corner keeps the row's
			// across coordinate and carries the along coordinate on to the mitre.
			Vector2 mitreB = fold.MitrePoint(shape[b].x);
			var pointB = _Positions[t][b];
			var cornerB = new Vector3(mitreB.x, mitreB.y, fold.ApexHeight + shape[b].y);
			Vector2 uvB = FaceUv(face, row, pointB, _Across[t], b);
			Vector2 uvCornerB = face.Planar ? PlanarUv(cornerB, face.Repeat) : uvB + new Vector2(0.0f, towardsCorner * Flat(pointB).Distance(Flat(cornerB)) / face.Repeat);

			if (shape[a].x < fold.Radius)
			{
				var centre = new Vector3(fold.Centre.x, fold.Centre.y, fold.ApexHeight + shape[b].y);

				// A border exactly as wide as the fillet: the block corner is a point.
				if (Flat(pointB).Distance(Flat(centre)) < 0.5f || Flat(cornerB).Distance(Flat(centre)) < 0.5f)
					continue;

				Vector2 uvCentre = face.Planar ? PlanarUv(centre, face.Repeat) : uvB;

				AddPatchTriangle(_Mesh, face.Material, expected, centre, pointB, cornerB, uvCentre, uvB, uvCornerB);

				if (_Debug is not null)
				{
					_Debug.Line(centre, pointB, BorderDebug.PatchColor);
					_Debug.Line(pointB, cornerB, BorderDebug.PatchColor);
					_Debug.Line(cornerB, centre, BorderDebug.PatchColor);
				}

				continue;
			}

			Vector2 mitreA = fold.MitrePoint(shape[a].x);
			var pointA = _Positions[t][a];
			var cornerA = new Vector3(mitreA.x, mitreA.y, fold.ApexHeight + shape[a].y);

			if (Flat(pointA).Distance(Flat(cornerA)) < 0.5f && Flat(pointB).Distance(Flat(cornerB)) < 0.5f)
				continue;

			Vector2 uvA = FaceUv(face, row, pointA, _Across[t], a);
			Vector2 uvCornerA = face.Planar ? PlanarUv(cornerA, face.Repeat) : uvA + new Vector2(0.0f, towardsCorner * Flat(pointA).Distance(Flat(cornerA)) / face.Repeat);

			AddPatchQuad(_Mesh, face.Material, expected, pointA, cornerA, cornerB, pointB, uvA, uvCornerA, uvCornerB, uvB);

			if (_Debug is not null)
			{
				_Debug.Line(pointA, cornerA, BorderDebug.PatchColor);
				_Debug.Line(cornerA, cornerB, BorderDebug.PatchColor);
				_Debug.Line(cornerB, pointB, BorderDebug.PatchColor);
			}
		}
	}



	private static void AddPatchTriangle(PolygonMesh _Mesh, Material _Material, Vector3 _Expected, Vector3 _A, Vector3 _B, Vector3 _C, Vector2 _UvA, Vector2 _UvB, Vector2 _UvC)
	{
		var handles = _Mesh.AddVertices(_A, _B, _C);

		if (Vector3.Dot(Vector3.Cross(_B - _A, _C - _A), _Expected) >= 0.0f)
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, handles[0], handles[1], handles[2], _UvA, _UvB, _UvC);
		else
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, handles[0], handles[2], handles[1], _UvA, _UvC, _UvB);
	}



	private static void AddPatchQuad(PolygonMesh _Mesh, Material _Material, Vector3 _Expected, Vector3 _A, Vector3 _B, Vector3 _C, Vector3 _D, Vector2 _UvA, Vector2 _UvB, Vector2 _UvC, Vector2 _UvD)
	{
		var handles = _Mesh.AddVertices(_A, _B, _C, _D);
		Vector3 normal = Vector3.Cross(_B - _A, _C - _A);

		if (normal.IsNearZeroLength)
			normal = Vector3.Cross(_C - _A, _D - _A);

		// One decision for both triangles: they share an edge and must agree.
		if (Vector3.Dot(normal, _Expected) >= 0.0f)
		{
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, handles[0], handles[1], handles[2], _UvA, _UvB, _UvC);
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, handles[0], handles[2], handles[3], _UvA, _UvC, _UvD);
		}
		else
		{
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, handles[0], handles[2], handles[1], _UvA, _UvC, _UvB);
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, handles[0], handles[3], handles[2], _UvA, _UvD, _UvC);
		}
	}



	private static Vector2 PlanarUv(Vector3 _Position, float _Repeat)
	{
		return new Vector2(_Position.x, _Position.y) / _Repeat;
	}



	private static Vector2 Flat(Vector3 _Vector)
	{
		return new Vector2(_Vector.x, _Vector.y);
	}



	/// <summary>
	/// One triangle in the run's winding: the corners in the same order a quad's would be.
	/// </summary>
	/// <summary>
	/// A triangle in the run's winding, with blend weights per corner (in the given, unflipped order) when the
	/// face is painted.
	/// </summary>
	private static void AddTriangle(PolygonMesh _Mesh, Material _Material, bool _Flip,
		HalfEdgeMesh.VertexHandle _HA, HalfEdgeMesh.VertexHandle _HB, HalfEdgeMesh.VertexHandle _HC,
		Vector2 _UvA, Vector2 _UvB, Vector2 _UvC, Color32[] _Blends = null)
	{
		HalfEdgeMesh.FaceHandle face;

		if (!_Flip)
		{
			face = MeshUtility.AddTexturedTriangle(_Mesh, _Material, _HA, _HB, _HC, _UvA, _UvB, _UvC);

			if (_Blends is not null)
				MeshUtility.SetFaceBlends(_Mesh, face, _Blends);
		}
		else
		{
			face = MeshUtility.AddTexturedTriangle(_Mesh, _Material, _HA, _HC, _HB, _UvA, _UvC, _UvB);

			if (_Blends is not null)
				MeshUtility.SetFaceBlends(_Mesh, face, new[] { _Blends[0], _Blends[2], _Blends[1] });
		}
	}



	/// <summary>
	/// A quad as two triangles, in the run's winding.
	/// </summary>
	private static void AddQuad(PolygonMesh _Mesh, Material _Material, bool _Flip,
		HalfEdgeMesh.VertexHandle _HA, HalfEdgeMesh.VertexHandle _HB, HalfEdgeMesh.VertexHandle _HC, HalfEdgeMesh.VertexHandle _HD,
		Vector2 _UvA, Vector2 _UvB, Vector2 _UvC, Vector2 _UvD, Color32[] _Blends = null)
	{
		AddTriangle(_Mesh, _Material, _Flip, _HA, _HB, _HC, _UvA, _UvB, _UvC, _Blends is null ? null : new[] { _Blends[0], _Blends[1], _Blends[2] });
		AddTriangle(_Mesh, _Material, _Flip, _HA, _HC, _HD, _UvA, _UvC, _UvD, _Blends is null ? null : new[] { _Blends[0], _Blends[2], _Blends[3] });
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
