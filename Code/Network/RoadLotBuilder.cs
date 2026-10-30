using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates a parking lot: the slab of surface inside its edge, the kerb round that edge with a gap at every road
/// that opens into it, and the bays painted inside.
/// </summary>
internal static class RoadLotBuilder
{
	/// <summary>
	/// An opening narrower than this is not worth cutting the kerb for.
	/// </summary>
	private const float MinOpening = 1.0f;



	/// <summary>
	/// The lot's texture over one side's strip: the road texture laid along the side as if the side were a road with
	/// its kerb on the lot's edge. U is how far in from the side's kerb a point is (from its arc, round a corner), the
	/// texture's edge on the kerb and a texture's width taking it out to three quarters across; further in it turns
	/// back and forth across the texture's middle half, so the texture's other edge never shows in the lot. In front of
	/// a gap a road opens into the lot through, U is lifted by how far into the gap a point is, so the kerb's edge stops
	/// at the gap's ends rather than running on across the road's way in. The lift is gone before the texture first
	/// turns and by the lines the side meets its neighbours on, so the turns still fall on rows and the sides still meet
	/// without a seam, and it never falls away as fast as U climbs in from the kerb, so the texture is never smeared.
	/// V runs along the side, the way that keeps the texture the right way round with its edge on the kerb.
	/// </summary>
	private sealed class LotSideMapping : IFanMapping
	{
		/// <summary>
		/// How fast the lift in front of a gap falls away, as a share of how fast U climbs in from the kerb.
		/// </summary>
		private const float LiftFalloff = 0.5f;

		private readonly Vector2 m_Centre;
		private readonly Vector2 m_Along;
		private readonly Vector2 m_Outward;
		private readonly float m_Half;
		private readonly float m_Radius;
		private readonly float m_Width;

		/// <summary>
		/// How far in from the kerb the lift is gone by: where the texture first turns (three quarters across it), or
		/// where the sides meet if that is nearer.
		/// </summary>
		private readonly float m_LiftDepth;

		private readonly List<(float From, float To)> m_Gaps = new();

		public LotSideMapping(RoadLot _Lot, int _Side, IReadOnlyList<RoadLotOpening> _Openings, float _Width)
		{
			_Lot.EdgeFrame(_Side, out Vector3 centre, out Vector3 direction, out Vector3 outward, out float half);

			m_Centre = new Vector2(centre.x, centre.y);
			m_Along = new Vector2(direction.x, direction.y).Normal;
			m_Outward = new Vector2(outward.x, outward.y).Normal;
			m_Half = half;
			m_Radius = _Lot.Radius;
			m_Width = MathF.Max(1.0f, _Width);
			m_LiftDepth = MathF.Min(0.75f * m_Width, MathF.Min(_Lot.HalfWidth, _Lot.HalfDepth));

			foreach (var opening in _Openings)
			{
				if (opening.Edge != _Side || opening.OutlineFrom < 0)
					continue;

				float reach = opening.HalfWidth + MathF.Max(0.0f, opening.Flare);

				m_Gaps.Add((opening.Along - reach, opening.Along + reach));
			}
		}

		public Vector2 Uv(Vector3 _Point)
		{
			var offset = new Vector2(_Point.x, _Point.y) - m_Centre;
			float along = Vector2.Dot(offset, m_Along);
			float depth = -Vector2.Dot(offset, m_Outward);
			float corner = MathF.Abs(along) - (m_Half - m_Radius);

			// Round a corner the kerb is its arc, so the depth is how far in from that.
			if (corner > 0.0f && depth < m_Radius)
				depth = m_Radius - new Vector2(corner, m_Radius - depth).Length;

			return new Vector2(Turn((depth + Lift(along, depth)) / m_Width), -along / m_Width);
		}

		/// <summary>
		/// The kerb is no distance from itself, so its points are already on the texture's edge.
		/// </summary>
		public Vector2 KerbUv(Vector3 _Point)
		{
			return Uv(_Point);
		}

		public Vector2 CentreUv(Vector3 _Centre)
		{
			return Uv(_Centre);
		}

		public Vector2 Measure(Vector3 _Point)
		{
			return Uv(_Point) * m_Width;
		}

		/// <summary>
		/// How far U is lifted at a point in front of a gap in the kerb: how far into the gap it is from the nearer of
		/// its ends, but never more than a share of how far the point still is from where the lift has to be gone, in
		/// from the kerb and along to the line the side meets its neighbour on.
		/// </summary>
		private float Lift(float _Along, float _Depth)
		{
			float lift = 0.0f;
			float room = LiftFalloff * MathF.Min(m_LiftDepth - _Depth, m_Half - _Depth - MathF.Abs(_Along));

			foreach (var (from, to) in m_Gaps)
				lift = MathF.Max(lift, MathF.Min(MathF.Min(_Along - from, to - _Along), room));

			return lift;
		}

		/// <summary>
		/// U for a distance in from the kerb, in texture widths: straight out to three quarters across, then back and
		/// forth between a quarter and three quarters.
		/// </summary>
		private static float Turn(float _Across)
		{
			float across = MathF.Max(0.0f, _Across);

			if (across <= 0.75f)
				return across;

			float beyond = (across - 0.75f) % 1.0f;

			return beyond <= 0.5f ? 0.75f - beyond : 0.25f + (beyond - 0.5f);
		}
	}



	/// <summary>
	/// A parking lot's surface for a road texture fitted to the road's width, laid out the way a road is: each side a
	/// strip of rows parallel to its kerb, from the kerb in to where it meets its neighbours, which is on lines at
	/// forty-five degrees from the corners and, on a lot longer one way than the other, along its middle. Each road
	/// opening into the lot is a fan of its own from its node on the lot's edge out to its flares and its first row.
	/// Every row is a distance in from the kerb and the texture's turns (see <see cref="LotSideMapping"/>) fall on rows,
	/// so the texture is exact along them; every seam between one stretch's texture and the next is an edge of the
	/// mesh, with the stretches sharing their vertices along it. The heights are those of the lot's plain fan (its
	/// middle out to its edge), which is what the ground under a lot is shaped to.
	/// </summary>
	private sealed class LotSurface
	{
		/// <summary>
		/// The most room between two rows, and between two points along a row.
		/// </summary>
		private const float Cell = 100.0f;

		private readonly RoadLot m_Lot;
		private readonly RoadLotOutline m_Outline;
		private readonly IReadOnlyList<RoadLotOpening> m_Openings;
		private readonly PolygonMesh m_Mesh;
		private readonly Material m_Material;
		private readonly float m_Repeat;
		private readonly float m_Lift;
		private readonly float m_Radius;

		/// <summary>
		/// How far in from the kerb the sides meet: half the lot's narrower way across.
		/// </summary>
		private readonly float m_Medial;

		/// <summary>
		/// The distances in from the kerb the rows are at, the kerb first and where the sides meet last.
		/// </summary>
		private readonly List<float> m_Depths = new();

		/// <summary>
		/// Each corner (at the end of the side with its number) as the outline rounds it: its centre, the way out of
		/// the side it ends and out of the next, and the way from its centre to the sharp corner it rounds off.
		/// </summary>
		private readonly Vector3[] m_CornerCentres = new Vector3[RoadLotOutline.SideCount];
		private readonly Vector3[] m_CornerFrom = new Vector3[RoadLotOutline.SideCount];
		private readonly Vector3[] m_CornerTo = new Vector3[RoadLotOutline.SideCount];
		private readonly Vector3[] m_CornerDiagonals = new Vector3[RoadLotOutline.SideCount];

		/// <summary>
		/// Where the sides meet in the middle: a line down a lot longer one way than the other (its two ends, and the
		/// points the two long sides share along it), a point in the middle of a square one.
		/// </summary>
		private readonly Vector3 m_MedialAxis;
		private readonly Vector3[] m_MedialEnds = new Vector3[2];
		private readonly List<Vector3> m_MedialPoints = new();

		private readonly Dictionary<(int Corner, int Row), Vector3> m_Mitres = new();
		private readonly Dictionary<Vector2, HalfEdgeMesh.VertexHandle> m_Vertices = new();
		private readonly Vector3[] m_Fan;

		public LotSurface(RoadLot _Lot, RoadLotOutline _Outline, RoadProfile _Profile, IReadOnlyList<RoadLotOpening> _Openings, float _Lift, PolygonMesh _Mesh)
		{
			m_Lot = _Lot;
			m_Outline = _Outline;
			m_Openings = _Openings;
			m_Mesh = _Mesh;
			m_Material = _Profile.ResolvedRoadMaterial;
			m_Repeat = MathF.Max(1.0f, _Profile.RoadTextureRepeat);
			m_Lift = _Lift;
			m_Radius = _Lot.Radius;
			m_Medial = MathF.Min(_Lot.HalfWidth, _Lot.HalfDepth);
			m_Fan = _Outline.Points.ToArray();

			for (int k = 0; k < RoadLotOutline.SideCount; k++)
			{
				_Lot.EdgeFrame(k, out Vector3 centre, out Vector3 direction, out Vector3 outward, out float half);
				_Lot.EdgeFrame(k + 1, out _, out _, out Vector3 nextOutward, out _);

				// Exactly as the outline rounds it, so the kerb row is the outline itself.
				m_CornerCentres[k] = centre + direction * (half - m_Radius) - outward * m_Radius;
				m_CornerFrom[k] = outward;
				m_CornerTo[k] = nextOutward;
				m_CornerDiagonals[k] = Vector3.Lerp(outward, nextOutward, 0.5f).Normal;
			}

			float excess = MathF.Abs(_Lot.HalfWidth - _Lot.HalfDepth);

			m_MedialAxis = _Lot.HalfWidth >= _Lot.HalfDepth ? _Lot.Across : _Lot.Along;
			m_MedialEnds[0] = _Lot.Centre - m_MedialAxis * excess;
			m_MedialEnds[1] = _Lot.Centre + m_MedialAxis * excess;

			int pieces = Math.Max(1, (int)MathF.Ceiling(excess * 2.0f / Cell));

			for (int j = 0; j <= pieces; j++)
				m_MedialPoints.Add(j == 0 ? m_MedialEnds[0] : j == pieces ? m_MedialEnds[1] : Vector3.Lerp(m_MedialEnds[0], m_MedialEnds[1], (float)j / pieces));

			// The rows: on the kerb, where the corners' rounding runs out, on each of the texture's turns and where
			// the sides meet, and no more than a cell apart between them.
			var marks = new List<float> { 0.0f, m_Medial };

			if (m_Radius > 1.0f && m_Radius < m_Medial - 1.0f)
				marks.Add(m_Radius);

			for (float turn = 0.75f * m_Repeat; turn < m_Medial - 1.0f; turn += 0.5f * m_Repeat)
				marks.Add(turn);

			marks.Sort();

			for (int i = 0; i < marks.Count; i++)
			{
				if (i == 0)
				{
					m_Depths.Add(marks[0]);
					continue;
				}

				float gap = marks[i] - m_Depths[^1];

				if (gap < 1.0f)
				{
					// Too close to the row before to be one of its own; where the sides meet always is the last.
					if (i == marks.Count - 1)
						m_Depths[^1] = marks[i];

					continue;
				}

				int fill = (int)MathF.Ceiling(gap / Cell);
				float from = m_Depths[^1];

				for (int j = 1; j < fill; j++)
					m_Depths.Add(from + gap * j / fill);

				m_Depths.Add(marks[i]);
			}
		}



		public void Build()
		{
			for (int side = 0; side < RoadLotOutline.SideCount; side++)
				BuildSide(side);

			for (int i = 0; i < m_Openings.Count; i++)
				BuildMouth(m_Openings[i]);
		}



		/// <summary>
		/// One side's strip: its rows zipped together from the kerb in, with the side's own mapping.
		/// </summary>
		private void BuildSide(int _Side)
		{
			var mapping = new LotSideMapping(m_Lot, _Side, m_Openings, m_Repeat);
			var outer = Row(_Side, 0);

			for (int row = 1; row < m_Depths.Count; row++)
			{
				var inner = Row(_Side, row);

				Zip(outer, inner, mapping);
				outer = inner;
			}
		}



		/// <summary>
		/// A road's way into the lot: a fan from its node on the lot's edge out over its flares and its first row, laid
		/// out the way the road is, so the road's texture runs on in through it with its edges round the flares. A mouth
		/// that was squared off rather than following its road is laid out as a straight road out the way it faces.
		/// </summary>
		private void BuildMouth(RoadLotOpening _Opening)
		{
			// Without flares the mouth's row is on the lot's edge, and there is nothing in front of it to fill.
			if (_Opening.OutlineFrom < 0 || _Opening.OutlineTo <= _Opening.OutlineFrom || _Opening.Flare <= 0.01f)
				return;

			RoadArmMapping mapping;

			if (_Opening.Table is not null)
			{
				mapping = new RoadArmMapping(_Opening.Table, _Opening.AtStart, _Opening.Flare, _Opening.Width, m_Repeat, true);
			}
			else
			{
				// A straight road maps the same however long it is, so it is made long enough for its way to be exact.
				Vector3 end = _Opening.Mouth + _Opening.Away * MathF.Max(_Opening.Flare, 100.0f);
				var straight = RoadCurveTable.Create(new RoadCurve(_Opening.Mouth, Vector3.Lerp(_Opening.Mouth, end, 1.0f / 3.0f), Vector3.Lerp(_Opening.Mouth, end, 2.0f / 3.0f), end));

				mapping = new RoadArmMapping(straight, true, straight.Length, RoadWidth.Uniform(_Opening.HalfWidth * 2.0f), m_Repeat, true);
			}

			var points = new List<Vector3>();

			for (int k = _Opening.OutlineFrom; k <= _Opening.OutlineTo; k++)
				points.Add(m_Outline.Points[k]);

			RoadFanBuilder.Build(m_Mesh, m_Material, _Opening.Mouth, points, new int[points.Count], new IFanMapping[] { mapping }, new[] { true }, Vertex, false);
		}



		/// <summary>
		/// The points of one of a side's rows, from where it meets the side before to where it meets the side after: a
		/// distance in from the kerb, round the corners' arcs pulled in to that distance and straight between them. The
		/// kerb row runs straight across the gaps the roads open into the lot through, where it meets their fans.
		/// </summary>
		private List<Vector3> Row(int _Side, int _Row)
		{
			int start = (_Side + RoadLotOutline.SideCount - 1) % RoadLotOutline.SideCount;
			var row = new List<Vector3>();

			if (_Row == m_Depths.Count - 1)
			{
				// Where the sides meet: the line down the middle for a long side, a point for a short one.
				Vector3 from = MedialEnd(start);
				Vector3 to = MedialEnd(_Side);

				if (from == to)
				{
					row.Add(from);
					return row;
				}

				bool forward = from == m_MedialPoints[0];

				for (int j = 0; j < m_MedialPoints.Count; j++)
					row.Add(m_MedialPoints[forward ? j : m_MedialPoints.Count - 1 - j]);

				return row;
			}

			m_Lot.EdgeFrame(_Side, out Vector3 centre, out Vector3 direction, out Vector3 outward, out float half);

			float depth = m_Depths[_Row];
			bool rounded = depth < m_Radius;
			float reach = rounded ? half - m_Radius : half - depth;
			int steps = RoadLotOutline.CornerSteps;

			row.Add(Mitre(start, _Row));

			// Round the start corner from its middle to the straight...
			if (rounded)
			{
				for (int i = steps / 2 + 1; i < steps; i++)
					row.Add(ArcPoint(start, i, depth));
			}

			// ...along the straight, evenly...
			var along = new List<(float At, Vector3 Point)>();
			int pieces = Math.Max(1, (int)MathF.Ceiling(reach * 2.0f / Cell));

			for (int j = 0; j <= pieces; j++)
			{
				// Without a rounding the straight runs out to the mitres, which are already the row's ends.
				if (!rounded && (j == 0 || j == pieces))
					continue;

				float at = -reach + reach * 2.0f * j / pieces;

				along.Add((at, centre + direction * at - outward * depth));
			}

			if (_Row == 0)
				AddMouths(_Side, along, reach);

			along.Sort((a, b) => a.At.CompareTo(b.At));

			foreach (var (_, point) in along)
				row.Add(point);

			// ...and round the end corner to its middle.
			if (rounded)
			{
				for (int i = 1; i < steps / 2; i++)
					row.Add(ArcPoint(_Side, i, depth));
			}

			row.Add(Mitre(_Side, _Row));

			return Deduplicate(row);
		}



		/// <summary>
		/// Puts the openings on a side's kerb row: where the kerb leaves the edge for each, its node, and where the
		/// kerb comes back, exactly as the outline and the opening's fan have them, with nothing else in the gap. The
		/// ends of the straight (out to <paramref name="_Reach"/> either way) stay whatever is near them, as the outline
		/// turns into its corners there.
		/// </summary>
		private void AddMouths(int _Side, List<(float At, Vector3 Point)> _Along, float _Reach)
		{
			foreach (var opening in m_Openings)
			{
				if (opening.Edge != _Side || opening.OutlineFrom < 0 || opening.OutlineTo <= opening.OutlineFrom)
					continue;

				float flare = MathF.Max(0.0f, opening.Flare);
				float leaves = opening.Along - opening.HalfWidth - flare;
				float returns = opening.Along + opening.HalfWidth + flare;
				float clear = Cell * 0.25f;

				_Along.RemoveAll(entry => entry.At > leaves - clear && entry.At < returns + clear && MathF.Abs(entry.At) < _Reach - 0.01f);
				_Along.Add((leaves, m_Outline.Points[opening.OutlineFrom]));
				_Along.Add((opening.Along, opening.Mouth));
				_Along.Add((returns, m_Outline.Points[opening.OutlineTo]));
			}
		}



		/// <summary>
		/// A point on a corner's arc pulled in from the kerb by a distance: one of the steps the outline turns the
		/// corner in, about the corner's centre, the radius less the distance out from it.
		/// </summary>
		private Vector3 ArcPoint(int _Corner, int _Step, float _Depth)
		{
			Vector3 radial = Vector3.Lerp(m_CornerFrom[_Corner], m_CornerTo[_Corner], (float)_Step / RoadLotOutline.CornerSteps).Normal;

			return m_CornerCentres[_Corner] + radial * (m_Radius - _Depth);
		}



		/// <summary>
		/// Where a row meets the next side's: on the line in from a corner at forty-five degrees, a row's distance in
		/// from the kerb. Worked out once, so both sides share the very point.
		/// </summary>
		private Vector3 Mitre(int _Corner, int _Row)
		{
			if (m_Mitres.TryGetValue((_Corner, _Row), out var cached))
				return cached;

			float depth = m_Depths[_Row];
			Vector3 point;

			if (_Row == m_Depths.Count - 1)
				point = MedialEnd(_Corner);
			else if (depth < m_Radius)
				point = m_CornerCentres[_Corner] + m_CornerDiagonals[_Corner] * (m_Radius - depth);
			else
				point = m_CornerCentres[_Corner] - m_CornerDiagonals[_Corner] * ((depth - m_Radius) * MathF.Sqrt(2.0f));

			m_Mitres[(_Corner, _Row)] = point;

			return point;
		}



		/// <summary>
		/// The end of the line down the middle on a corner's side of the lot.
		/// </summary>
		private Vector3 MedialEnd(int _Corner)
		{
			return Vector3.Dot(m_CornerCentres[_Corner] - m_Lot.Centre, m_MedialAxis) >= 0.0f ? m_MedialEnds[1] : m_MedialEnds[0];
		}



		/// <summary>
		/// Fills the band between two of a side's rows (the kerb's side first) with triangles from one to the other in
		/// the order their points come along them, wound to face up as the lot does.
		/// </summary>
		private void Zip(List<Vector3> _Outer, List<Vector3> _Inner, IFanMapping _Mapping)
		{
			float[] outerAt = Fractions(_Outer);
			float[] innerAt = Fractions(_Inner);
			int a = 0;
			int b = 0;

			while (a < _Outer.Count - 1 || b < _Inner.Count - 1)
			{
				if (b == _Inner.Count - 1 || (a < _Outer.Count - 1 && outerAt[a + 1] <= innerAt[b + 1]))
				{
					Triangle(_Outer[a], _Outer[a + 1], _Inner[b], _Mapping);
					a++;
				}
				else
				{
					Triangle(_Outer[a], _Inner[b + 1], _Inner[b], _Mapping);
					b++;
				}
			}
		}



		private void Triangle(Vector3 _A, Vector3 _B, Vector3 _C, IFanMapping _Mapping)
		{
			MeshUtility.AddTexturedTriangle(m_Mesh, m_Material, Vertex(_A), Vertex(_B), Vertex(_C), _Mapping.Uv(_A), _Mapping.Uv(_B), _Mapping.Uv(_C));
		}



		/// <summary>
		/// The vertex at a point of the plan, made the first time it is asked for, at the height the lot's fan has
		/// there; asked for again (a mitre, a gap's ends, a fan's spokes) it is the same one, so the mesh holds together.
		/// </summary>
		private HalfEdgeMesh.VertexHandle Vertex(Vector3 _Point)
		{
			var key = new Vector2(_Point.x, _Point.y);

			if (m_Vertices.TryGetValue(key, out var handle))
				return handle;

			handle = m_Mesh.AddVertices(new Vector3(_Point.x, _Point.y, Height(key) + m_Lift))[0];
			m_Vertices[key] = handle;

			return handle;
		}



		/// <summary>
		/// The height of the lot's plain fan (its middle out to every point of its outline) over a point, as the ground
		/// under the lot is shaped to it; the nearest outline point's height should the point fall a hair outside.
		/// </summary>
		private float Height(Vector2 _Point)
		{
			var centre = new Vector2(m_Lot.Centre.x, m_Lot.Centre.y);
			float nearest = float.MaxValue;
			float fallback = m_Lot.Centre.z;

			for (int i = 0; i < m_Fan.Length; i++)
			{
				Vector3 v0 = m_Fan[i];
				Vector3 v1 = m_Fan[(i + 1) % m_Fan.Length];
				var p0 = new Vector2(v0.x, v0.y);
				var p1 = new Vector2(v1.x, v1.y);
				float distance = p0.Distance(_Point);

				if (distance < nearest)
				{
					nearest = distance;
					fallback = v0.z;
				}

				float area = Cross(p0 - centre, p1 - centre);

				if (MathF.Abs(area) < 0.0001f)
					continue;

				float w0 = Cross(p1 - _Point, centre - _Point) / area;
				float w1 = Cross(centre - _Point, p0 - _Point) / area;
				float w2 = 1.0f - w0 - w1;

				if (w0 < -0.001f || w1 < -0.001f || w2 < -0.001f)
					continue;

				return w0 * v0.z + w1 * v1.z + w2 * m_Lot.Centre.z;
			}

			return fallback;
		}



		/// <summary>
		/// How far along a row each of its points is, as a share of the row's length.
		/// </summary>
		private static float[] Fractions(List<Vector3> _Row)
		{
			var at = new float[_Row.Count];

			for (int i = 1; i < _Row.Count; i++)
				at[i] = at[i - 1] + new Vector2(_Row[i].x - _Row[i - 1].x, _Row[i].y - _Row[i - 1].y).Length;

			float length = at[^1];

			for (int i = 0; i < at.Length; i++)
				at[i] = length > 0.0f ? at[i] / length : 0.0f;

			return at;
		}



		private static List<Vector3> Deduplicate(List<Vector3> _Row)
		{
			var result = new List<Vector3>(_Row.Count);

			foreach (var point in _Row)
			{
				if (result.Count > 0 && new Vector2(point.x - result[^1].x, point.y - result[^1].y).Length < 0.01f)
					continue;

				result.Add(point);
			}

			return result;
		}



		private static float Cross(Vector2 _A, Vector2 _B)
		{
			return _A.x * _B.y - _A.y * _B.x;
		}
	}



	/// <summary>
	/// The lot's surface. A road texture fitted to the road's width (dark edges, gutters or lanes painted in) is laid
	/// the way a junction lays it, with its edge on the kerb all round and each road's own carried on in at its
	/// opening (see <see cref="LotSurface"/>). A texture laid at its own size tiles, and is simply laid flat from
	/// above over a fan from the lot's middle out to its edge.
	/// </summary>
	public static PolygonMesh BuildSurface(RoadLot _Lot, RoadLotOutline _Outline, RoadProfile _Profile, IReadOnlyList<RoadLotOpening> _Openings, float _Lift)
	{
		var mesh = new PolygonMesh();

		if (_Outline.Count < 3)
			return mesh;

		if (_Profile.RoadTextureFitsWidth && _Outline.Owners.Count == _Outline.Count)
		{
			new LotSurface(_Lot, _Outline, _Profile, _Openings, _Lift, mesh).Build();

			return mesh;
		}

		Vector3 lift = Vector3.Up * _Lift;
		float repeat = _Profile.RoadTextureRepeat;
		var material = _Profile.ResolvedRoadMaterial;
		var centre = mesh.AddVertices(_Lot.Centre + lift)[0];
		var ring = new HalfEdgeMesh.VertexHandle[_Outline.Count];

		for (int i = 0; i < _Outline.Count; i++)
			ring[i] = mesh.AddVertices(_Outline.Points[i] + lift)[0];

		Vector2 centreUv = PlanarUv(_Lot.Centre, repeat);

		for (int i = 0; i < _Outline.Count; i++)
		{
			int next = (i + 1) % _Outline.Count;

			MeshUtility.AddTexturedTriangle(mesh, material, centre, ring[i], ring[next],
				centreUv, PlanarUv(_Outline.Points[i], repeat), PlanarUv(_Outline.Points[next], repeat));
		}

		return mesh;
	}



	/// <summary>
	/// The kerb round the lot: one run of border between each pair of openings, closed off at both its ends. A lot
	/// with no openings at all gets the whole ring in one run.
	/// </summary>
	public static PolygonMesh BuildBorder(RoadLot _Lot, RoadLotOutline _Outline, RoadProfile _Profile, IReadOnlyList<RoadLotOpening> _Openings, float _Lift, BorderDebug _Debug = null)
	{
		var mesh = new PolygonMesh();

		if (!_Profile.HasBorder || _Outline.Count < 3)
			return mesh;

		var gaps = new List<(float From, float To)>();

		foreach (var opening in _Openings)
		{
			if (_Outline.Wrap(opening.GapTo - opening.GapFrom) > MinOpening)
				gaps.Add((opening.GapFrom, opening.GapTo));
		}

		gaps.Sort((a, b) => a.From.CompareTo(b.From));

		if (gaps.Count == 0)
		{
			// No way in: the ring is one run, closed where it started.
			AddRun(mesh, _Lot, _Outline, _Profile, 0.0f, _Outline.Length, _Lift, _Debug);

			return mesh;
		}

		for (int i = 0; i < gaps.Count; i++)
		{
			float from = gaps[i].To;
			float to = gaps[(i + 1) % gaps.Count].From;

			if (_Outline.Wrap(to - from) > MinOpening)
				AddRun(mesh, _Lot, _Outline, _Profile, from, to, _Lift, _Debug);
		}

		return mesh;
	}



	private static void AddRun(PolygonMesh _Mesh, RoadLot _Lot, RoadLotOutline _Outline, RoadProfile _Profile, float _From, float _To, float _Lift, BorderDebug _Debug)
	{
		var run = _Outline.Between(_From, _To);
		var path = new List<RoadBorderBuilder.PathPoint>(run.Count);

		foreach (var (point, outward, along) in run)
			path.Add(new RoadBorderBuilder.PathPoint(point + Vector3.Up * _Lift, outward, along));

		RoadBorderBuilder.BuildPath(_Mesh, _Profile, path, true, true, _Debug);
	}



	/// <summary>
	/// The bays painted inside the lot: a row along the inside of each edge, and back to back rows down the middle
	/// where the lot has the room. Nothing is painted over an opening or over an aisle.
	/// </summary>
	public static PolygonMesh BuildMarkings(RoadLot _Lot, ParkingStyle _Parking, IReadOnlyList<RoadLotOpening> _Openings, float _Lift)
	{
		var mesh = new PolygonMesh();

		if (_Parking is null || _Parking.Line is null || !_Parking.Line.Enabled || _Parking.Line.Width <= 0.0f)
			return mesh;

		float lift = _Lift + RoadMarkingBuilder.Lift;

		if (_Parking.EdgeBays)
		{
			// Along each edge, from the corner: bays lean into the way traffic runs round the lot.
			AddEdgeBays(mesh, _Lot, _Parking, _Openings, lift, new Vector2(1.0f, 0.0f), new Vector2(0.0f, 1.0f));
			AddEdgeBays(mesh, _Lot, _Parking, _Openings, lift, new Vector2(0.0f, 1.0f), new Vector2(-1.0f, 0.0f));
			AddEdgeBays(mesh, _Lot, _Parking, _Openings, lift, new Vector2(-1.0f, 0.0f), new Vector2(0.0f, -1.0f));
			AddEdgeBays(mesh, _Lot, _Parking, _Openings, lift, new Vector2(0.0f, -1.0f), new Vector2(1.0f, 0.0f));
		}

		if (_Parking.CentreRows)
			AddCentreRows(mesh, _Lot, _Parking, _Openings, lift);

		return mesh;
	}



	/// <summary>
	/// One edge's worth of bays: dividers every bay along the straight part of the edge, reaching into the lot.
	/// </summary>
	private static void AddEdgeBays(PolygonMesh _Mesh, RoadLot _Lot, ParkingStyle _Parking, IReadOnlyList<RoadLotOpening> _Openings, float _Lift, Vector2 _Outward, Vector2 _Along)
	{
		bool across = MathF.Abs(_Outward.x) > 0.5f;
		float half = across ? _Lot.HalfWidth : _Lot.HalfDepth;
		float span = across ? _Lot.HalfDepth : _Lot.HalfWidth;

		// The bays stop a corner and a bay's depth short of each end, so the row on the next edge round has room
		// for its own bays and nothing is painted into the corner itself.
		float usable = (span - _Lot.Radius - _Parking.BayDepth) * 2.0f;
		float pitch = MathF.Max(1.0f, _Parking.Pitch);
		int bays = (int)MathF.Floor(usable / pitch);

		if (bays < 1 || _Parking.BayDepth >= half)
			return;

		// Centred on the edge, so the row of bays is even at both corners.
		float start = -(bays * pitch) * 0.5f;
		Vector2 inward = -_Outward;
		var direction = Rotate(inward, _Parking.BayAngle);

		for (int i = 0; i <= bays; i++)
		{
			Vector2 edge = _Outward * half + _Along * (start + i * pitch);
			Vector2 back = edge + direction * _Parking.BayDepth;

			AddLine(_Mesh, _Lot, _Parking, _Openings, edge, back, _Lift);
		}

		if (!_Parking.BackLine)
			return;

		Vector2 backFrom = _Outward * half + _Along * start + direction * _Parking.BayDepth;
		Vector2 backTo = _Outward * half + _Along * (start + bays * pitch) + direction * _Parking.BayDepth;

		AddLine(_Mesh, _Lot, _Parking, _Openings, backFrom, backTo, _Lift);
	}



	/// <summary>
	/// Rows of bays back to back down the middle of the lot, as many as fit between the edge bays with an aisle
	/// either side of each row. Each row's two halves lean opposite ways, as they do where you drive down an aisle.
	/// </summary>
	private static void AddCentreRows(PolygonMesh _Mesh, RoadLot _Lot, ParkingStyle _Parking, IReadOnlyList<RoadLotOpening> _Openings, float _Lift)
	{
		float edgeDepth = _Parking.EdgeBays ? _Parking.BayDepth : 0.0f;
		float rowDepth = _Parking.BayDepth * 2.0f;
		float step = rowDepth + _Parking.AisleWidth;

		// The band left between the edge bays and their aisles.
		float from = -_Lot.HalfDepth + edgeDepth + _Parking.AisleWidth;
		float to = _Lot.HalfDepth - edgeDepth - _Parking.AisleWidth;
		int rows = (int)MathF.Floor((to - from + _Parking.AisleWidth) / step);

		if (rows < 1)
			return;

		float used = rows * step - _Parking.AisleWidth;
		float first = from + (to - from - used) * 0.5f;

		// The rows stop short of the edge bays on the left and right so there is a way round them.
		float reach = _Lot.HalfWidth - (_Parking.EdgeBays ? _Parking.BayDepth : 0.0f) - _Parking.AisleWidth;

		if (reach <= _Parking.Pitch)
			return;

		float pitch = MathF.Max(1.0f, _Parking.Pitch);
		int bays = (int)MathF.Floor(reach * 2.0f / pitch);

		if (bays < 1)
			return;

		float start = -(bays * pitch) * 0.5f;

		for (int row = 0; row < rows; row++)
		{
			float middle = first + row * step + _Parking.BayDepth;

			for (int i = 0; i <= bays; i++)
			{
				float across = start + i * pitch;
				var backward = Rotate(new Vector2(0.0f, -1.0f), -_Parking.BayAngle);
				var forward = Rotate(new Vector2(0.0f, 1.0f), _Parking.BayAngle);

				AddLine(_Mesh, _Lot, _Parking, _Openings, new Vector2(across, middle), new Vector2(across, middle) + backward * _Parking.BayDepth, _Lift);
				AddLine(_Mesh, _Lot, _Parking, _Openings, new Vector2(across, middle), new Vector2(across, middle) + forward * _Parking.BayDepth, _Lift);
			}

			// The line the two halves back onto.
			AddLine(_Mesh, _Lot, _Parking, _Openings, new Vector2(start, middle), new Vector2(start + bays * pitch, middle), _Lift);
		}
	}



	/// <summary>
	/// One painted line of the layout, in the lot's own coordinates, unless a road opens into the lot over it.
	/// </summary>
	private static void AddLine(PolygonMesh _Mesh, RoadLot _Lot, ParkingStyle _Parking, IReadOnlyList<RoadLotOpening> _Openings, Vector2 _From, Vector2 _To, float _Lift)
	{
		Vector3 from = _Lot.Point(_From.x, _From.y) + Vector3.Up * _Lift;
		Vector3 to = _Lot.Point(_To.x, _To.y) + Vector3.Up * _Lift;

		foreach (var opening in _Openings)
		{
			// Nothing painted where a road comes in, nor in the flares either side of it.
			float clearance = opening.Reach + _Parking.BayWidth * 0.5f;

			if (NearestOn(from, to, opening.Mouth).Distance(opening.Mouth) < clearance)
				return;
		}

		Vector3 along = to - from;

		if (along.IsNearZeroLength)
			return;

		Vector3 across = Vector3.Cross(along.Normal, Vector3.Up) * (_Parking.Line.Width * 0.5f);
		var handles = _Mesh.AddVertices(from - across, from + across, to + across, to - across);

		MeshUtility.AddTexturedQuad(_Mesh, _Parking.ResolvedLineMaterial, handles[0], handles[1], handles[2], handles[3],
			new Vector2(0.0f, 0.0f), new Vector2(1.0f, 0.0f), new Vector2(1.0f, along.Length / MathF.Max(1.0f, _Parking.Line.Width)), new Vector2(0.0f, along.Length / MathF.Max(1.0f, _Parking.Line.Width)));
	}



	private static Vector3 NearestOn(Vector3 _From, Vector3 _To, Vector3 _Point)
	{
		Vector3 span = _To - _From;
		float lengthSquared = span.LengthSquared;
		float t = lengthSquared > 0.0f ? Math.Clamp(Vector3.Dot(_Point - _From, span) / lengthSquared, 0.0f, 1.0f) : 0.0f;

		return _From + span * t;
	}



	private static Vector2 Rotate(Vector2 _Vector, float _Degrees)
	{
		float radians = _Degrees.DegreeToRadian();
		float cos = MathF.Cos(radians);
		float sin = MathF.Sin(radians);

		return new Vector2(_Vector.x * cos - _Vector.y * sin, _Vector.x * sin + _Vector.y * cos);
	}



	private static Vector2 PlanarUv(Vector3 _Position, float _Repeat)
	{
		return new Vector2(_Position.x, _Position.y) / MathF.Max(1.0f, _Repeat);
	}
}
