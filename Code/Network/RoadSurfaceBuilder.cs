using System;
using System.Collections.Generic;
using Sandbox;
using Sandbox.Utility;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the road surface of one chunk: a strip of cells between cross-section frames at the rows the chunk is
/// built on (even distances along the segment's curve, thinned where the road type allows it).
/// </summary>
internal static class RoadSurfaceBuilder
{
	/// <summary>
	/// How far one corner of a cell may sit off the plane of the other three before two triangles stop doing for it.
	/// A road that climbs as it turns has level rows that swing round from one to the next, so its inside edge
	/// climbs more steeply than its outside edge and every cell is twisted. Two triangles then give the middle of the
	/// road one slope up to the diagonal and another after it: a step in every cell, which the centre line, lying on
	/// the surface as built, shows as a staircase.
	/// </summary>
	private const float MaxTwist = 0.25f;

	/// <summary>
	/// The widest strip a twisted road is cut into lengthways.
	/// </summary>
	private const float MaxColumnWidth = 125.0f;

	/// <summary>
	/// A trail's cross-section, wall to wall, as (share of its width from its left wall's top, share of its depth):
	/// walls easing over a third of the width each way down to a flat bottom.
	/// </summary>
	private static readonly Vector2[] TrailShape =
	{
		new(0.0f, 0.0f), new(0.1167f, 0.2593f), new(0.2333f, 0.7407f), new(0.35f, 1.0f),
		new(0.65f, 1.0f), new(0.7667f, 0.7407f), new(0.8833f, 0.2593f), new(1.0f, 0.0f)
	};

	/// <summary>
	/// How close a trail's wall may come to the road's edge.
	/// </summary>
	private const float TrailMargin = 2.0f;

	/// <summary>
	/// The least room between two points across a row, so a trail squeezed against the edge or another trail never
	/// folds a row back on itself.
	/// </summary>
	private const float MinColumnGap = 0.5f;

	/// <summary>
	/// The least room between two rows: a row nearer than this to another is not worth having.
	/// </summary>
	private const float MinRowGap = 1.0f;

	/// <summary>
	/// The rows a wandering trail is laid on are this share of its wander's wavelength apart (one over its chaos
	/// scale), and never closer than <see cref="MinWanderStep"/>.
	/// </summary>
	private const float WanderShare = 0.25f;
	private const float MinWanderStep = 20.0f;

	/// <summary>
	/// The region of the noise field trails read their wander from, one more step along for each seed, clear of the
	/// country side border's regions.
	/// </summary>
	private const float TrailBand = 5000.0f;
	private const float TrailBandStep = 1000.0f;



	/// <summary>
	/// The two edge points of the road at a distance along the curve, each its own distance out from the centreline
	/// there. Junctions use this for the rows they share with the arm chunks, so both sides of the seam are computed
	/// the same way.
	/// </summary>
	public static void CrossSection(RoadCurveTable _Table, float _Distance, in RoadWidth _Width, float _Lift, out Vector3 _Left, out Vector3 _Right)
	{
		_Table.Frame(_Distance, out Vector3 position, out Vector3 tangent);

		position += Vector3.Up * _Lift;

		Vector3 right = RoadCurve.RightOf(tangent);

		_Left = position - right * _Width.LeftAt(_Distance);
		_Right = position + right * _Width.RightAt(_Distance);
	}



	/// <summary>
	/// How a stretch of road is cut into triangles: 0 when two per cell will do, which is everywhere the road does not
	/// climb and turn at once, otherwise the number of columns every cell is cut into lengthways, each fanned into four
	/// triangles round its middle. In its own column a fan climbs evenly from one row to the next however much the
	/// column twists, and narrow columns twist little, so the surface follows the road's true shape. The columns fall
	/// on the lane boundaries, so the lines painted there run along edges of the mesh itself. Worked out from the
	/// corners without the surface's lift, so the paint, which lays itself on these cells, comes to the same answer.
	/// </summary>
	public static int Columns(IReadOnlyList<Vector3> _Left, IReadOnlyList<Vector3> _Right, in RoadWidth _Width, int _Lanes)
	{
		bool twisted = false;

		for (int i = 0; i + 1 < _Left.Count && !twisted; i++)
			twisted = Twist(_Left[i], _Right[i], _Right[i + 1], _Left[i + 1]) > MaxTwist;

		if (!twisted)
			return 0;

		int lanes = Math.Max(1, _Lanes);

		return lanes * Math.Max(1, (int)MathF.Ceiling(_Width.MaxHalf * 2.0f / (lanes * MaxColumnWidth)));
	}



	/// <summary>
	/// The same, for the rows of a stretch of road rather than their corners.
	/// </summary>
	public static int Columns(RoadCurveTable _Table, IReadOnlyList<float> _Rows, in RoadWidth _Width, int _Lanes)
	{
		var left = new Vector3[_Rows.Count];
		var right = new Vector3[_Rows.Count];

		for (int i = 0; i < _Rows.Count; i++)
			CrossSection(_Table, _Rows[i], _Width, 0.0f, out left[i], out right[i]);

		return Columns(left, right, _Width, _Lanes);
	}



	/// <summary>
	/// The surface of a stretch of road. <paramref name="_Profile"/>'s trails and skirt shape it across, coming out of
	/// that shape where <paramref name="_Fade"/> says the segment stops; where <paramref name="_Blend"/> says another
	/// surface runs on into it, it is drawn with the profile's blend material. Without them the surface is flat across.
	/// </summary>
	public static PolygonMesh Build(RoadCurveTable _Table, float _StartDistance, float _EndDistance, in RoadWidth _Width, float _Spacing, in RoadSimplify _Simplify, int _Lanes, float _TextureRepeat, bool _FitWidth, float _SurfaceOffset, Material _Material,
		RoadProfile _Profile = null, in RoadTrailFade _Fade = default, in RoadSurfaceBlend _Blend = default)
	{
		var mesh = new PolygonMesh();

		// The rows the borders and the bridge are built on too, so the pieces meet exactly.
		var rows = RoadRows.Build(_Table, _StartDistance, _EndDistance, _Spacing, _Width, _Simplify);
		float repeat = MathF.Max(1.0f, _TextureRepeat);
		var lefts = new Vector3[rows.Count];
		var rights = new Vector3[rows.Count];

		for (int i = 0; i < rows.Count; i++)
			CrossSection(_Table, rows[i], _Width, 0.0f, out lefts[i], out rights[i]);

		int columns = Columns(lefts, rights, _Width, _Lanes);

		if (_Profile is not null)
		{
			var trails = _Fade.IsNone ? new List<RoadTrail>() : FittingTrails(_Profile.ActiveTrails, _Width);
			float skirt = !_Fade.IsNone && _Profile.HasSkirt ? _Profile.SkirtWidth : 0.0f;
			var blend = _Profile.RoadBlendMaterial is not null ? _Blend : default;

			if (trails.Count > 0 || skirt > 0.0f || !blend.IsNone)
			{
				var shape = new Shape(trails, _Fade, skirt, _Profile.SkirtDepth, blend, _Profile.RoadBlendMaterial);

				BuildShaped(mesh, rows, lefts, rights, _Width, columns > 0, repeat, _FitWidth, _SurfaceOffset, _Material, shape);

				return mesh;
			}
		}

		int points = Math.Max(1, columns) + 1;
		Vector3 lift = Vector3.Up * _SurfaceOffset;

		var positions = new Vector3[rows.Count, points];
		var handles = new HalfEdgeMesh.VertexHandle[rows.Count, points];
		var uvs = new Vector2[rows.Count, points];

		for (int i = 0; i < rows.Count; i++)
		{
			float distance = rows[i];

			// U runs across the road from the centreline, V along it, so a tiling material follows the road instead
			// of the world, and an edge that moves out takes its texture with it. Fitted to the width, U runs from 0
			// on the left edge to 1 on the right instead, so a texture painted kerb to kerb spans the road once.
			float v = distance / repeat;
			var uvLeft = new Vector2(_FitWidth ? 0.0f : -_Width.LeftAt(distance) / repeat, v);
			var uvRight = new Vector2(_FitWidth ? 1.0f : _Width.RightAt(distance) / repeat, v);

			for (int k = 0; k < points; k++)
			{
				positions[i, k] = ColumnPoint(lefts[i], rights[i], k, points - 1) + lift;
				handles[i, k] = mesh.AddVertices(positions[i, k])[0];
				uvs[i, k] = k == 0 ? uvLeft : k == points - 1 ? uvRight : Vector2.Lerp(uvLeft, uvRight, (float)k / (points - 1));
			}
		}

		for (int i = 0; i < rows.Count - 1; i++)
		{
			if (columns == 0)
			{
				// Two triangles rather than a quad: the cell is flat to within a hair, but a quad that is not quite flat
				// is the mesh's to split (or to drop), and the paint laid on top has to know which way it went.
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i, 0], handles[i, 1], handles[i + 1, 1], uvs[i, 0], uvs[i, 1], uvs[i + 1, 1]);
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i, 0], handles[i + 1, 1], handles[i + 1, 0], uvs[i, 0], uvs[i + 1, 1], uvs[i + 1, 0]);
				continue;
			}

			for (int k = 0; k < columns; k++)
			{
				var middle = (positions[i, k] + positions[i, k + 1] + positions[i + 1, k + 1] + positions[i + 1, k]) * 0.25f;
				var middleUv = (uvs[i, k] + uvs[i, k + 1] + uvs[i + 1, k + 1] + uvs[i + 1, k]) * 0.25f;
				var centre = mesh.AddVertices(middle)[0];

				// Round the column the way the plain cell is wound, each side of it with the middle.
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i, k], handles[i, k + 1], centre, uvs[i, k], uvs[i, k + 1], middleUv);
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i, k + 1], handles[i + 1, k + 1], centre, uvs[i, k + 1], uvs[i + 1, k + 1], middleUv);
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i + 1, k + 1], handles[i + 1, k], centre, uvs[i + 1, k + 1], uvs[i + 1, k], middleUv);
				MeshUtility.AddTexturedTriangle(mesh, _Material, handles[i + 1, k], handles[i, k], centre, uvs[i + 1, k], uvs[i, k], middleUv);
			}
		}

		return mesh;
	}



	/// <summary>
	/// The trails a road is wide enough for, left to right. Decided from the narrower of the segment's two ends (its
	/// width blends evenly from one to the other), so every chunk of the segment has the same trails and the chunks
	/// meet.
	/// </summary>
	private static List<RoadTrail> FittingTrails(IReadOnlyList<RoadTrail> _Trails, in RoadWidth _Width)
	{
		var fitting = new List<RoadTrail>();

		if (_Trails is null)
			return fitting;

		float narrowest = MathF.Min(_Width.WidthAt(0.0f), _Width.WidthAt(_Width.Length));

		foreach (var trail in _Trails)
		{
			if (trail is null || !trail.IsVisible || trail.Width + TrailMargin * 2.0f > narrowest)
				continue;

			// Left to right, keeping the listed order between two at the same offset.
			int at = fitting.Count;

			while (at > 0 && fitting[at - 1].Offset > trail.Offset)
				at--;

			fitting.Insert(at, trail);
		}

		return fitting;
	}



	/// <summary>
	/// What a shaped surface is built with: its trails, left to right; where they and the skirt come out of the road at
	/// the segment's ends; the skirt's width and depth under banks (0 wide for none); and where another surface runs on
	/// into the road, with the material that blend is drawn with.
	/// </summary>
	private readonly record struct Shape(List<RoadTrail> Trails, RoadTrailFade Fade, float SkirtWidth, float SkirtDepth, RoadSurfaceBlend Blend, Material BlendMaterial);



	/// <summary>
	/// The surface of a stretch of road with shape across it. Its rows are the plain surface's, with more between them
	/// wherever a trail wanders or anything comes out of the road or blends into it at an end; those are laid on the
	/// straight lines between the plain rows' edges, so the road's outline is exactly the plain one's and its borders
	/// meet it the same. Each row is cut across at the road's two edges and at the walls and bottom of every trail, and
	/// kept in order across, so a trail pushed against the edge or another trail flattens out rather than folding the
	/// surface over; under banks it carries on past each edge as a skirt, down under the ground, all the way along (the
	/// rounded end and the junction corners carry it on round). A trail wanders by noise read where the road is, which
	/// is the same in every chunk and on into the next road, so it never jumps.
	/// Where another surface blends into the road, the cells are drawn with the blend material, every point of a row
	/// mixed the same (a mix that changed across a cell would be read differently by its two triangles). The blend's
	/// coordinates start out where it meets that surface as that surface's own (the blend's second layer carries it
	/// straight on) and move over to the road's own as the road takes over (with the road material's texture transform
	/// applied, as the blend's first layer takes the coordinates as they come): each layer is laid exactly where it is
	/// all there is, and the two give way to each other over the same stretch they move over in.
	/// </summary>
	private static void BuildShaped(PolygonMesh _Mesh, List<float> _Rows, Vector3[] _Lefts, Vector3[] _Rights, in RoadWidth _Width, bool _Twisted, float _Repeat, bool _FitWidth, float _SurfaceOffset, Material _Material,
		in Shape _Shape)
	{
		// Where the centre line lies across each plain row, as a share of the way from its left edge.
		var centres = new float[_Rows.Count];

		for (int i = 0; i < _Rows.Count; i++)
		{
			float left = _Width.LeftAt(_Rows[i]);
			float right = _Width.RightAt(_Rows[i]);

			centres[i] = left + right > 0.001f ? left / (left + right) : 0.5f;
		}

		var trails = _Shape.Trails;
		var rows = ShapedRows(_Rows, _Shape);
		int trailCount = trails.Count;
		int shape = TrailShape.Length;
		bool skirted = _Shape.SkirtWidth > 0.0f;
		bool blending = _Shape.BlendMaterial is not null && !_Shape.Blend.IsNone;
		var frames = new (Vector3 Left, Vector3 Right, float Width, float Centre, float Distance, float Strength, float Weight)[rows.Count];
		var keys = new (float Across, float Depth)[rows.Count][];

		// Each row's frame, and its points across as (distance from its left edge, depth): for now the road's edges and
		// the trails' walls and bottoms.
		for (int r = 0; r < rows.Count; r++)
		{
			var (distance, index, t) = rows[r];
			int next = Math.Min(index + 1, _Rows.Count - 1);
			Vector3 left = Vector3.Lerp(_Lefts[index], _Lefts[next], t);
			Vector3 right = Vector3.Lerp(_Rights[index], _Rights[next], t);
			float width = Flat(right - left).Length;
			float centre = MathX.Lerp(centres[index], centres[next], t);
			float strength = _Shape.Fade.StrengthAt(distance);
			float weight = blending ? _Shape.Blend.WeightAt(distance) : 0.0f;
			Vector3 middle = Vector3.Lerp(left, right, centre);
			var key = new (float Across, float Depth)[trailCount * shape + 2];

			frames[r] = (left, right, width, centre, distance, strength, weight);
			key[0] = (0.0f, 0.0f);

			for (int k = 0; k < trailCount; k++)
			{
				var trail = trails[k];
				float half = trail.Width * 0.5f;
				float wander = 0.0f;

				// Noise is 0..1; centred, it wanders roughly as far as the chaos either way.
				if (trail.Chaos > 0.0f)
					wander = (Noise.Perlin(middle.x * trail.ChaosScale + TrailBand + trail.Seed * TrailBandStep, middle.y * trail.ChaosScale) - 0.5f) * 2.0f * trail.Chaos;

				// Kept inside the road, however far it wanders.
				float low = half + TrailMargin;
				float high = width - half - TrailMargin;
				float across = width * centre + trail.Offset + wander;

				across = high >= low ? Math.Clamp(across, low, high) : width * 0.5f;

				for (int p = 0; p < shape; p++)
					key[1 + k * shape + p] = (across - half + TrailShape[p].x * trail.Width, trail.Depth * strength * TrailShape[p].y);
			}

			key[^1] = (width, 0.0f);

			// In order across, never closer than a hair, with the road's own edges left where they are.
			for (int p = 1; p < key.Length - 1; p++)
				key[p].Across = MathF.Max(key[p].Across, key[p - 1].Across + MinColumnGap);

			for (int p = key.Length - 2; p >= 1; p--)
				key[p].Across = MathF.Min(key[p].Across, key[p + 1].Across - MinColumnGap);

			keys[r] = key;
		}

		// The flat stretch before each trail and after the last runs from one key point (a multiple of the shape's
		// length along) to the next. A road that twists has them cut lengthways like a plain twisted road's cells, into
		// as many columns on every row as the widest of them needs.
		var pieces = new int[trailCount + 1];

		for (int s = 0; s <= trailCount; s++)
		{
			pieces[s] = 1;

			if (!_Twisted)
				continue;

			float widest = 0.0f;

			foreach (var key in keys)
				widest = MathF.Max(widest, key[s * shape + 1].Across - key[s * shape].Across);

			pieces[s] = Math.Max(1, (int)MathF.Ceiling(widest / MaxColumnWidth));
		}

		Vector3 lift = Vector3.Up * _SurfaceOffset;
		var (textureScale, textureOffset) = RoadProfile.TextureTransform(_Material);
		Vector2 second = RoadProfile.SecondLayerScale(_Shape.BlendMaterial);
		var grid = new Vertex[rows.Count][];
		var points = new List<(float Across, float Depth)>();

		// Under banks, the skirt past each edge.
		float skirt = skirted ? MathF.Max(MinColumnGap, _Shape.SkirtWidth) : 0.0f;
		float drop = skirted ? _Shape.SkirtDepth : 0.0f;

		for (int r = 0; r < rows.Count; r++)
		{
			var (left, right, width, centre, distance, _, weight) = frames[r];
			var key = keys[r];
			float v = distance / _Repeat;
			float leftHalf = width * centre;

			// The surface blending in here, if any: the one at whichever end's blend reaches this far.
			var source = _Shape.Blend.StartWeightAt(distance) >= _Shape.Blend.EndWeightAt(distance) ? _Shape.Blend.StartSource : _Shape.Blend.EndSource;

			points.Clear();

			if (skirted)
				points.Add((-skirt, drop));

			int leftEdge = points.Count;

			for (int p = 0; p < key.Length; p++)
			{
				points.Add(key[p]);

				if (p % shape != 0 || p + 1 >= key.Length)
					continue;

				int span = p / shape;

				for (int c = 1; c < pieces[span]; c++)
					points.Add((MathX.Lerp(key[p].Across, key[p + 1].Across, (float)c / pieces[span]), 0.0f));
			}

			int rightEdge = points.Count - 1;

			if (skirted)
				points.Add((width + skirt, drop));

			var row = new Vertex[points.Count];

			for (int p = 0; p < points.Count; p++)
			{
				var (across, depth) = points[p];
				float share = width > 0.001f ? across / width : 0.0f;

				// The edges exactly where the plain surface has them; inside, on the row's straight line, sunk by the
				// trail there; the skirts on out along the same line, sunk under the banks.
				Vector3 position = p == leftEdge ? left : p == rightEdge ? right : left + (right - left) * share;

				position += lift - Vector3.Up * depth;

				// A texture fitted to the width is turned back on itself at the edge down the skirt, so it carries on at
				// its own size rather than starting again (or being drawn out from its edge).
				var uv = new Vector2(_FitWidth ? SkirtShare(share) : (across - leftHalf) / _Repeat, v);
				var blendUv = Vector2.Zero;

				if (blending)
				{
					blendUv = new Vector2(uv.x * textureScale.x + textureOffset.x, uv.y * textureScale.y + textureOffset.y);

					if (weight > 0.0f)
					{
						Vector2 sample = source.SampleAt(distance, across - leftHalf, leftHalf, width - leftHalf, position);

						blendUv = Vector2.Lerp(blendUv, new Vector2(sample.x / second.x, sample.y / second.y), weight);
					}
				}

				row[p] = new Vertex(position, uv, blendUv, weight, _Mesh.AddVertices(position)[0]);
			}

			grid[r] = row;
		}

		for (int r = 0; r < rows.Count - 1; r++)
		{
			var near = grid[r];
			var far = grid[r + 1];

			// A cell that has any of the other surface in it is drawn with the blend; the skirts never are, so what shows
			// of them where the ground dips is always this road's own surface.
			bool blendRow = blending && MathF.Max(frames[r].Weight, frames[r + 1].Weight) > 0.0f;

			for (int p = 0; p < near.Length - 1; p++)
			{
				bool skirtCell = skirted && (p == 0 || p == near.Length - 2);
				bool blend = blendRow && !skirtCell;
				var material = blend ? _Shape.BlendMaterial : _Material;

				if (!_Twisted)
				{
					// Split the way the plain surface's cells are.
					Triangle(_Mesh, material, blend, near[p], near[p + 1], far[p + 1]);
					Triangle(_Mesh, material, blend, near[p], far[p + 1], far[p]);
					continue;
				}

				var middle = new Vertex(
					(near[p].Position + near[p + 1].Position + far[p + 1].Position + far[p].Position) * 0.25f,
					(near[p].Uv + near[p + 1].Uv + far[p + 1].Uv + far[p].Uv) * 0.25f,
					(near[p].BlendUv + near[p + 1].BlendUv + far[p + 1].BlendUv + far[p].BlendUv) * 0.25f,
					(near[p].Weight + far[p].Weight) * 0.5f,
					default);

				middle = middle with { Handle = _Mesh.AddVertices(middle.Position)[0] };

				Triangle(_Mesh, material, blend, near[p], near[p + 1], middle);
				Triangle(_Mesh, material, blend, near[p + 1], far[p + 1], middle);
				Triangle(_Mesh, material, blend, far[p + 1], far[p], middle);
				Triangle(_Mesh, material, blend, far[p], near[p], middle);
			}
		}
	}



	/// <summary>
	/// One point of a shaped surface: where it is, its texture coordinates as the road's own material takes them and
	/// as a blend's first layer must be given them, how much of another surface is mixed in there, and its vertex.
	/// </summary>
	private readonly record struct Vertex(Vector3 Position, Vector2 Uv, Vector2 BlendUv, float Weight, HalfEdgeMesh.VertexHandle Handle);



	private static void Triangle(PolygonMesh _Mesh, Material _Material, bool _Blend, in Vertex _A, in Vertex _B, in Vertex _C)
	{
		if (!_Blend)
		{
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, _A.Handle, _B.Handle, _C.Handle, _A.Uv, _B.Uv, _C.Uv);
			return;
		}

		var face = MeshUtility.AddTexturedTriangle(_Mesh, _Material, _A.Handle, _B.Handle, _C.Handle, _A.BlendUv, _B.BlendUv, _C.BlendUv);

		// A blendable material's first layer is this road, its second the surface blending into it, mixed by the red.
		MeshUtility.SetFaceBlends(_Mesh, face, new[] { BlendColour(_A.Weight), BlendColour(_B.Weight), BlendColour(_C.Weight) });
	}



	/// <summary>
	/// The vertex blend that mixes a blendable material's second layer in by <paramref name="_Weight"/>.
	/// </summary>
	internal static Color32 BlendColour(float _Weight)
	{
		return new Color32((byte)Math.Clamp(MathF.Round(_Weight * 255.0f), 0.0f, 255.0f), 0, 0, 255);
	}



	/// <summary>
	/// Where a point a share of the way across a road samples a texture fitted to the road's width, past either edge
	/// (down a skirt) turned back on itself there: the texture carries on at its own size, the way it runs inside, its
	/// edge where the road's is.
	/// </summary>
	internal static float SkirtShare(float _Share)
	{
		return _Share < 0.0f ? -_Share : _Share > 1.0f ? 2.0f - _Share : _Share;
	}



	/// <summary>
	/// The rows a shaped surface is laid on, as (distance along the curve, the plain row at or before it, how far on
	/// towards the next): the plain rows, and between them a row at every step of a fade or a blend and every so often
	/// where a trail wanders, so the trails rise and swing smoothly, and the blend mixes smoothly, even where the plain
	/// rows are far apart.
	/// </summary>
	private static List<(float Distance, int Row, float T)> ShapedRows(List<float> _Rows, in Shape _Shape)
	{
		float step = 0.0f;

		foreach (var trail in _Shape.Trails)
		{
			if (trail.Chaos <= 0.0f)
				continue;

			float own = MathF.Max(MinWanderStep, WanderShare / trail.ChaosScale);

			step = step > 0.0f ? MathF.Min(step, own) : own;
		}

		var knots = new List<float>(_Shape.Fade.Knots());

		if (_Shape.BlendMaterial is not null)
			knots.AddRange(_Shape.Blend.Knots());

		var rows = new List<(float Distance, int Row, float T)>(_Rows.Count * 2);
		var cuts = new List<float>();

		for (int i = 0; i < _Rows.Count; i++)
		{
			rows.Add((_Rows[i], i, 0.0f));

			if (i == _Rows.Count - 1)
				break;

			float from = _Rows[i];
			float to = _Rows[i + 1];
			float gap = to - from;

			if (gap <= MinRowGap * 2.0f)
				continue;

			cuts.Clear();

			foreach (float knot in knots)
			{
				if (knot > from && knot < to)
					cuts.Add(knot);
			}

			if (step > 0.0f)
			{
				int count = (int)MathF.Ceiling(gap / step);

				for (int j = 1; j < count; j++)
					cuts.Add(from + gap * j / count);
			}

			cuts.Sort();

			float previous = from;

			foreach (float cut in cuts)
			{
				if (cut - previous < MinRowGap || to - cut < MinRowGap)
					continue;

				rows.Add((cut, i, (cut - from) / gap));
				previous = cut;
			}
		}

		return rows;
	}



	/// <summary>
	/// The height of one cell of road surface, as it is built, over a point in plan. A cell with no columns is split
	/// from its near left corner to its far right one, so which of its two triangles the point falls on is which side
	/// of that diagonal it lies; a cut-up cell is found the column the point lies in, then the triangle of that
	/// column's fan. The triangle's plane is used even for a point just off the cell (paint hanging over the road's
	/// edge), which keeps that paint on the surface's own plane.
	/// </summary>
	public static float CellHeight(Vector3 _NearLeft, Vector3 _NearRight, Vector3 _FarRight, Vector3 _FarLeft, int _Columns, Vector2 _Point)
	{
		if (_Columns <= 0)
		{
			Vector2 diagonal = Flat(_FarRight) - Flat(_NearLeft);
			float side = Cross(diagonal, _Point - Flat(_NearLeft));
			float nearRightSide = Cross(diagonal, Flat(_NearRight) - Flat(_NearLeft));

			return side * nearRightSide >= 0.0f
				? PlaneHeight(_NearLeft, _NearRight, _FarRight, _Point)
				: PlaneHeight(_NearLeft, _FarRight, _FarLeft, _Point);
		}

		// The first column line with the point on its left (the cell's left edge's side) closes the point's column;
		// past the last line it is the rightmost column, edge or no edge.
		int column = _Columns - 1;

		for (int k = 1; k < _Columns; k++)
		{
			Vector2 near = Flat(ColumnPoint(_NearLeft, _NearRight, k, _Columns));
			Vector2 line = Flat(ColumnPoint(_FarLeft, _FarRight, k, _Columns)) - near;

			if (Cross(line, _Point - near) * Cross(line, Flat(_NearLeft) - near) > 0.0f)
			{
				column = k - 1;
				break;
			}
		}

		Vector3 nearLeft = ColumnPoint(_NearLeft, _NearRight, column, _Columns);
		Vector3 nearRight = ColumnPoint(_NearLeft, _NearRight, column + 1, _Columns);
		Vector3 farRight = ColumnPoint(_FarLeft, _FarRight, column + 1, _Columns);
		Vector3 farLeft = ColumnPoint(_FarLeft, _FarRight, column, _Columns);
		Vector3 middle = (nearLeft + nearRight + farRight + farLeft) * 0.25f;

		// The fan's triangle the point is inside has no weight below zero; off the column, the one it is least
		// outside of.
		float best = float.MinValue;
		float height = middle.z;

		FanTriangle(nearLeft, nearRight, middle, _Point, ref best, ref height);
		FanTriangle(nearRight, farRight, middle, _Point, ref best, ref height);
		FanTriangle(farRight, farLeft, middle, _Point, ref best, ref height);
		FanTriangle(farLeft, nearLeft, middle, _Point, ref best, ref height);

		return height;
	}



	/// <summary>
	/// Where a row meets one of the lines that cut a cell into columns: the row's own ends for the outermost two, so
	/// the surface meets its borders exactly, evenly between them for the rest.
	/// </summary>
	private static Vector3 ColumnPoint(Vector3 _Left, Vector3 _Right, int _Column, int _Columns)
	{
		if (_Column <= 0)
			return _Left;

		if (_Column >= _Columns)
			return _Right;

		return Vector3.Lerp(_Left, _Right, (float)_Column / _Columns);
	}



	/// <summary>
	/// How far a cell's far left corner sits above or below the plane through its other three: nothing for a road that
	/// is level, or straight, there.
	/// </summary>
	private static float Twist(Vector3 _NearLeft, Vector3 _NearRight, Vector3 _FarRight, Vector3 _FarLeft)
	{
		return MathF.Abs(_FarLeft.z - PlaneHeight(_NearLeft, _NearRight, _FarRight, Flat(_FarLeft)));
	}



	/// <summary>
	/// Takes one triangle of a fan as the point's when the point lies further inside it (its least weight is larger)
	/// than inside any tried before.
	/// </summary>
	private static void FanTriangle(Vector3 _A, Vector3 _B, Vector3 _C, Vector2 _Point, ref float _Best, ref float _Height)
	{
		Vector2 a = Flat(_A);
		Vector2 b = Flat(_B);
		Vector2 c = Flat(_C);
		float area = Cross(b - a, c - a);

		if (MathF.Abs(area) < 0.0001f)
			return;

		float weightA = Cross(b - _Point, c - _Point) / area;
		float weightB = Cross(c - _Point, a - _Point) / area;
		float weightC = 1.0f - weightA - weightB;
		float least = MathF.Min(weightA, MathF.Min(weightB, weightC));

		if (least <= _Best)
			return;

		_Best = least;
		_Height = weightA * _A.z + weightB * _B.z + weightC * _C.z;
	}



	/// <summary>
	/// The height of the plane through three points over a point in plan.
	/// </summary>
	private static float PlaneHeight(Vector3 _A, Vector3 _B, Vector3 _C, Vector2 _Point)
	{
		Vector2 a = Flat(_A);
		Vector2 b = Flat(_B);
		Vector2 c = Flat(_C);
		float area = Cross(b - a, c - a);

		if (MathF.Abs(area) < 0.0001f)
			return _A.z;

		float weightA = Cross(b - _Point, c - _Point) / area;
		float weightB = Cross(c - _Point, a - _Point) / area;

		return weightA * _A.z + weightB * _B.z + (1.0f - weightA - weightB) * _C.z;
	}



	private static Vector2 Flat(Vector3 _Vector)
	{
		return new Vector2(_Vector.x, _Vector.y);
	}



	private static float Cross(Vector2 _A, Vector2 _B)
	{
		return _A.x * _B.y - _A.y * _B.x;
	}
}
