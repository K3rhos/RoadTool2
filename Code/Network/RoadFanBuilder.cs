using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// How a surface fanned out from a centre (a junction, a parking lot) is textured over one owner's stretch of its
/// outline.
/// </summary>
internal interface IFanMapping
{
	/// <summary>
	/// The texture coordinates at a point.
	/// </summary>
	Vector2 Uv(Vector3 _Point);

	/// <summary>
	/// The texture coordinates at a point of the outline held to its kerb: where whatever runs along the edge of the
	/// road in the texture sits.
	/// </summary>
	Vector2 KerbUv(Vector3 _Point);

	/// <summary>
	/// What the fan's centre takes on this owner's triangles.
	/// </summary>
	Vector2 CentreUv(Vector3 _Centre);

	/// <summary>
	/// Where a point sits in the mapping's own terms, in world units: what a straight run along a spoke is measured
	/// against to tell whether the spoke needs cutting into pieces.
	/// </summary>
	Vector2 Measure(Vector3 _Point);

	/// <summary>
	/// The lane the texture is laid across where a point is: U at its left edge and at its right edge, and how wide it
	/// is there in world units.
	/// </summary>
	void Lane(Vector3 _Point, out float _LeftU, out float _RightU, out float _Width);
}

/// <summary>
/// The surface of a road an owner's stretch of a fan gives way to across it (a road blending a junction's surface into
/// its own, see <see cref="RoadMouthBlend"/>): the fan's own surface where the blend starts, the road's where it ends,
/// and the blend material between.
/// </summary>
internal interface IFanBlend
{
	/// <summary>
	/// The material the blend is drawn with, and the road's own, where the blend has given way to it.
	/// </summary>
	Material Material { get; }
	Material Own { get; }

	/// <summary>
	/// How far into the blend its rows are laid, from its start to its end.
	/// </summary>
	IReadOnlyList<float> Steps { get; }

	/// <summary>
	/// How far into the blend a point is, in world units: nought where it starts, less short of it.
	/// </summary>
	float Into(Vector3 _Point);

	/// <summary>
	/// How much of the fan's own surface there is a distance into the blend.
	/// </summary>
	float WeightAt(float _Into);

	/// <summary>
	/// The road's own texture coordinates at a point, as its own material takes them.
	/// </summary>
	Vector2 OwnUv(Vector3 _Point);

	/// <summary>
	/// The blend material's coordinates where the road has <paramref name="_Own"/>, the fan
	/// <paramref name="_Uv"/>, and there is <paramref name="_Weight"/> of the fan's surface.
	/// </summary>
	Vector2 BlendUv(Vector2 _Own, Vector2 _Uv, float _Weight);
}

/// <summary>
/// Fans a surface out from its centre to its outline, each edge of the outline textured by the mapping of the owner
/// it belongs to. The owners meet on the spokes between their stretches, each side of a spoke taking its own owner's
/// coordinates, the way the arms of a junction meet on lines from the node to the middle of its corners. Spokes
/// along which a mapping bends (a road curving in, an edge's texture turning back on itself) are cut into pieces so
/// the texture follows it, and a spoke that is long for the road it reaches has a rim along the outline (see
/// <see cref="SpokeUvs"/>). The pieces lie on the spokes, so the fan's triangles keep their planes and the surface is
/// the same however finely it is cut. An owner whose road blends the fan's surface into its own across its stretch
/// (see <see cref="IFanBlend"/>) has the spokes either side of it cut at each step of the blend too, so it is laid in
/// rows across the fan.
/// </summary>
internal static class RoadFanBuilder
{
	/// <summary>
	/// How many points along a spoke how far into a blend it reaches is read at, to find where it crosses the blend's
	/// steps: it runs all but straight between them.
	/// </summary>
	private const int BlendSamples = 16;

	/// <summary>
	/// How close to all or none of a surface a blend's points can be and the triangle still be drawn in that surface
	/// alone: nearer than the vertex blend can tell apart.
	/// </summary>
	private const float WeightEpsilon = 1.0f / 512.0f;

	/// <summary>
	/// How far (in world units) a straight run of a mapping along a spoke may drift from the mapping itself before
	/// the spoke is cut into pieces.
	/// </summary>
	private const float SpokeTolerance = 2.0f;

	/// <summary>
	/// The most pieces a spoke is cut into.
	/// </summary>
	private const int MaxSpokePieces = 8;

	/// <summary>
	/// A spoke longer than this many times the width of the road it reaches has a rim.
	/// </summary>
	private const float RimReach = 2.0f;

	/// <summary>
	/// How deep a rim is, as a share of the width of the road it runs along: room for the texture's edge band.
	/// </summary>
	private const float RimDepth = 0.15f;

	/// <summary>
	/// How far across its road a kerb runs (the sine of the angle between them) where a rim starts turning the
	/// texture back into the lane, and where it has turned it back in full.
	/// </summary>
	private const float FoldFrom = 0.15f;
	private const float FoldTo = 0.5f;

	/// <summary>
	/// How far past its lane's edge (in U) a kerb has to lie for a rim to turn the texture back in full.
	/// </summary>
	private const float FoldBeyond = 0.05f;



	/// <summary>
	/// Adds the fan to a mesh. <paramref name="_Owners"/> names, for each outline point, the owner of the edge leaving
	/// it; <paramref name="_Kerbs"/> whether that owner's outline points are held to their kerb. Vertices come from
	/// <paramref name="_Vertex"/> when given (to share them with the rest of a mesh, or set their heights), straight
	/// from the mesh otherwise. An open fan (<paramref name="_Closed"/> false) stops at its last point instead of
	/// closing back to its first: a fan from a point on the edge of something else, along which its two end spokes lie.
	/// <paramref name="_Blends"/> gives, by owner, the road that owner's stretch gives way to, if any.
	/// </summary>
	public static void Build(PolygonMesh _Mesh, Material _Material, Vector3 _Centre, IReadOnlyList<Vector3> _Points, IReadOnlyList<int> _Owners,
		IReadOnlyList<IFanMapping> _Mappings, IReadOnlyList<bool> _Kerbs, Func<Vector3, HalfEdgeMesh.VertexHandle> _Vertex = null, bool _Closed = true,
		IReadOnlyList<IFanBlend> _Blends = null)
	{
		int count = _Points.Count;

		if (count < (_Closed ? 3 : 2))
			return;

		_Vertex ??= position => _Mesh.AddVertices(position)[0];

		// The spokes: where along each its points are (nought at the centre, one at the outline), how much of it is
		// rim, and their vertices.
		var centreHandle = _Vertex(_Centre);
		var fractions = new float[count][];
		var rims = new float[count];
		var spokes = new HalfEdgeMesh.VertexHandle[count][];

		for (int k = 0; k < count; k++)
		{
			// An open fan's end spokes are shared with whatever it is fanned from the edge of, which knows nothing of
			// any points along them: they stay whole, so the two meet point for point.
			if (!_Closed && (k == 0 || k == count - 1))
			{
				fractions[k] = new[] { 0.0f, 1.0f };
			}
			else
			{
				int entering = _Owners[(k + count - 1) % count];
				int leaving = _Owners[k];

				fractions[k] = SpokeFractions(_Mappings[entering], _Mappings[leaving], _Centre, _Points[k], out rims[k]);
				fractions[k] = BlendCuts(fractions[k], rims[k], BlendOf(_Blends, entering), BlendOf(_Blends, leaving), _Centre, _Points[k]);
			}

			var spoke = new HalfEdgeMesh.VertexHandle[fractions[k].Length];

			spoke[0] = centreHandle;

			for (int j = 1; j < spoke.Length; j++)
				spoke[j] = _Vertex(j == spoke.Length - 1 ? _Points[k] : Vector3.Lerp(_Centre, _Points[k], fractions[k][j]));

			spokes[k] = spoke;
		}

		for (int k = 0; k < (_Closed ? count : count - 1); k++)
		{
			int next = (k + 1) % count;
			var mapping = _Mappings[_Owners[k]];
			bool kerbs = _Kerbs[_Owners[k]];
			var uvs = SpokeUvs(mapping, kerbs, _Centre, _Points, k, _Closed, fractions[k], rims[k]);
			var nextUvs = SpokeUvs(mapping, kerbs, _Centre, _Points, next, _Closed, fractions[next], rims[next]);

			if (BlendOf(_Blends, _Owners[k]) is { } blend)
				ZipBlended(_Mesh, _Material, blend, _Centre, _Points[k], spokes[k], fractions[k], uvs, _Points[next], spokes[next], fractions[next], nextUvs);
			else
				Zip(_Mesh, _Material, spokes[k], fractions[k], uvs, spokes[next], fractions[next], nextUvs);
		}
	}



	/// <summary>
	/// The road an owner's stretch gives way to, if any.
	/// </summary>
	private static IFanBlend BlendOf(IReadOnlyList<IFanBlend> _Blends, int _Owner)
	{
		return _Blends is not null && _Owner >= 0 && _Owner < _Blends.Count ? _Blends[_Owner] : null;
	}



	/// <summary>
	/// A spoke's points (see <see cref="SpokeFractions"/>) with more where it crosses each step of a blend either side of
	/// it, inside its rim, so the blend is laid in rows across the fan as it is along the road. A crossing within a unit of
	/// a point the spoke has already is left out.
	/// </summary>
	private static float[] BlendCuts(float[] _Fractions, float _Rim, IFanBlend _Entering, IFanBlend _Leaving, Vector3 _Centre, Vector3 _End)
	{
		if (_Entering is null && _Leaving is null)
			return _Fractions;

		float reach = 1.0f - _Rim;
		int innerLast = _Rim > 0.0f ? _Fractions.Length - 2 : _Fractions.Length - 1;
		float gap = 1.0f / MathF.Max(1.0f, Flat(_End - _Centre).Length);
		var points = new List<float>();
		var cuts = new List<float>();

		for (int j = 0; j <= innerLast; j++)
			points.Add(_Fractions[j]);

		Crossings(_Entering, _Centre, _End, reach, cuts);

		if (_Leaving != _Entering)
			Crossings(_Leaving, _Centre, _End, reach, cuts);

		foreach (float cut in cuts)
		{
			if (cut > 0.0f && cut < reach && points.TrueForAll(point => MathF.Abs(point - cut) >= gap))
				points.Add(cut);
		}

		points.Sort();

		if (_Rim > 0.0f)
			points.Add(_Fractions[^1]);

		return points.ToArray();
	}



	/// <summary>
	/// Adds where a spoke crosses each step of a blend, as shares of its length out to <paramref name="_Reach"/>.
	/// </summary>
	private static void Crossings(IFanBlend _Blend, Vector3 _Centre, Vector3 _End, float _Reach, List<float> _Cuts)
	{
		if (_Blend is null)
			return;

		var into = new float[BlendSamples + 1];

		for (int i = 0; i <= BlendSamples; i++)
			into[i] = _Blend.Into(Vector3.Lerp(_Centre, _End, _Reach * i / BlendSamples));

		foreach (float step in _Blend.Steps)
		{
			for (int i = 0; i < BlendSamples; i++)
			{
				float from = into[i] - step;
				float to = into[i + 1] - step;

				if (from * to > 0.0f || from == to)
					continue;

				_Cuts.Add(_Reach * (i + from / (from - to)) / BlendSamples);
			}
		}
	}



	/// <summary>
	/// Where along a spoke its points are, as shares of its length: evenly out to its rim (to the outline itself, for
	/// a spoke without one) in as many pieces as either owner it borders needs, then the outline. A spoke longer than
	/// <see cref="RimReach"/> times the wider of the two owners' roads has a rim <see cref="RimDepth"/> of that width
	/// deep; <paramref name="_Rim"/> is the share of the spoke it takes, nought for none.
	/// </summary>
	private static float[] SpokeFractions(IFanMapping _Entering, IFanMapping _Leaving, Vector3 _Centre, Vector3 _End, out float _Rim)
	{
		_Entering.Lane(_End, out _, out _, out float enteringWidth);
		_Leaving.Lane(_End, out _, out _, out float leavingWidth);

		float width = MathF.Max(enteringWidth, leavingWidth);
		float length = Flat(_End - _Centre).Length;

		_Rim = length > RimReach * width && length > 0.001f ? RimDepth * width / length : 0.0f;

		Vector3 inner = Vector3.Lerp(_Centre, _End, 1.0f - _Rim);
		int pieces = Math.Max(SpokePieces(_Entering, _Centre, inner), SpokePieces(_Leaving, _Centre, inner));
		var fractions = new float[pieces + (_Rim > 0.0f ? 2 : 1)];

		for (int j = 0; j <= pieces; j++)
			fractions[j] = (1.0f - _Rim) * j / pieces;

		fractions[^1] = 1.0f;

		return fractions;
	}



	/// <summary>
	/// How many pieces the spoke from the centre to an outline point needs for a straight run of a mapping along each
	/// to stay within <see cref="SpokeTolerance"/> of the mapping itself.
	/// </summary>
	private static int SpokePieces(IFanMapping _Mapping, Vector3 _Centre, Vector3 _End)
	{
		Vector2 from = _Mapping.Measure(_Centre);
		Vector2 to = _Mapping.Measure(_End);
		float drift = 0.0f;

		for (int i = 1; i < 4; i++)
		{
			float t = i * 0.25f;

			drift = MathF.Max(drift, (_Mapping.Measure(Vector3.Lerp(_Centre, _End, t)) - Vector2.Lerp(from, to, t)).Length);
		}

		// A straight run's drift from a curve falls with the square of its length.
		return Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(drift / SpokeTolerance)), 1, MaxSpokePieces);
	}



	/// <summary>
	/// The mapping at each point of a spoke: the owner's own, with whatever the centre and the outline point are held
	/// to worked in evenly from nothing at the other end, so the texture never folds back on itself on the way.
	/// Worked in over the whole of a long spoke, the outline point's hold would draw its kerb's band out along the
	/// spoke, so a long spoke has a rim: its point there takes the value that leaves the spoke inside it just as it
	/// would be without one, unless the kerb runs across its road and lies past its lane's edge (the nose between two
	/// roads that split apart, seen from a node far back). There it is set as far into the lane as the texture is past
	/// it, turned back in, so the band runs round the nose within the rim while the texture inside still changes from
	/// one spoke to the next rather than being smeared along them.
	/// </summary>
	private static Vector2[] SpokeUvs(IFanMapping _Mapping, bool _Kerbs, Vector3 _Centre, IReadOnlyList<Vector3> _Points, int _Index, bool _Closed,
		float[] _Fractions, float _Rim)
	{
		Vector3 end = _Points[_Index];
		var uvs = new Vector2[_Fractions.Length];
		Vector2 centre = _Mapping.Uv(_Centre);
		Vector2 centreHeld = _Mapping.CentreUv(_Centre);
		Vector2 outer = _Mapping.Uv(end);
		Vector2 outerHeld = _Kerbs ? _Mapping.KerbUv(end) : outer;

		// The stretch inside the rim, out to the rim's point (the outline itself, without a rim).
		float reach = 1.0f - _Rim;
		int innerLast = _Rim > 0.0f ? _Fractions.Length - 2 : _Fractions.Length - 1;
		Vector2 inner = outer;
		Vector2 innerHeld = outerHeld;

		if (_Rim > 0.0f)
		{
			inner = _Mapping.Uv(Vector3.Lerp(_Centre, end, reach));
			innerHeld = inner + (centreHeld - centre) * _Rim + (outerHeld - outer) * reach;

			if (_Kerbs)
			{
				int count = _Points.Count;
				Vector3 before = _Points[_Closed ? (_Index + count - 1) % count : Math.Max(_Index - 1, 0)];
				Vector3 after = _Points[_Closed ? (_Index + 1) % count : Math.Min(_Index + 1, count - 1)];
				float field = inner.x + (centreHeld.x - centre.x) * _Rim;

				innerHeld.x = Folded(_Mapping, _Centre, before, end, after, _Rim, innerHeld.x, field, outerHeld.x, outer.x);
			}
		}

		for (int j = 0; j <= innerLast; j++)
		{
			float t = _Fractions[j] / reach;
			Vector2 uv = j == 0 ? centre : j == innerLast ? inner : _Mapping.Uv(Vector3.Lerp(_Centre, end, _Fractions[j]));

			uvs[j] = uv + (centreHeld - centre) * (1.0f - t) + (innerHeld - inner) * t;
		}

		if (_Rim > 0.0f)
			uvs[^1] = outerHeld;

		return uvs;
	}



	/// <summary>
	/// U at a rim's point: <paramref name="_Plain"/> (the spoke's own value there), turned as far towards the
	/// texture's own U there (<paramref name="_Field"/>, with the centre's hold worked in), reflected back into the
	/// lane about the rim's own depth into it, as the kerb (U <paramref name="_Outline"/> on the mapping, held to
	/// <paramref name="_Edge"/>) runs across its road and lies past its lane's edge. Reflected rather than clamped, the
	/// texture keeps changing from one rim point to the next; about the rim's depth rather than the lane's edge, the
	/// edge band never shows where the texture crosses the edge.
	/// </summary>
	private static float Folded(IFanMapping _Mapping, Vector3 _Centre, Vector3 _Before, Vector3 _End, Vector3 _After, float _Rim, float _Plain, float _Field,
		float _Edge, float _Outline)
	{
		Vector2 chord = Flat(_After - _Before);
		Vector2 spoke = Flat(_End - _Centre);
		float chordLength = chord.Length;
		float spokeLength = spoke.Length;

		if (chordLength < 0.001f || spokeLength < 0.001f)
			return _Plain;

		float along = MathF.Min(1.0f, MathF.Abs(_Mapping.Measure(_After).y - _Mapping.Measure(_Before).y) / chordLength);
		float across = MathF.Sqrt(1.0f - along * along);

		_Mapping.Lane(_End, out float leftU, out float rightU, out float width);

		float middle = (leftU + rightU) * 0.5f;
		float inward = middle >= _Edge ? 1.0f : -1.0f;
		float beyond = MathF.Max(0.0f, (_Edge - _Outline) * inward);
		float weight = SmoothStep(FoldFrom, FoldTo, across) * SmoothStep(0.0f, FoldBeyond, beyond);

		if (weight <= 0.0f)
			return _Plain;

		// How deep into the lane the rim's point is, square to the kerb, and how deep the texture there is.
		float facing = MathF.Abs(Cross(spoke, chord)) / (spokeLength * chordLength);
		float lateral = width > 0.001f ? MathF.Abs(rightU - leftU) / width : 0.0f;
		float rimDepth = _Rim * spokeLength * facing * lateral;
		float fieldDepth = (_Field - _Edge) * inward;
		float depth = MathF.Min(MathF.Abs(middle - _Edge), rimDepth + MathF.Abs(fieldDepth - rimDepth));

		return _Plain + (_Edge + inward * depth - _Plain) * weight;
	}



	/// <summary>
	/// Fills one triangle of the fan, between two spokes that start at the centre, with triangles from one to the other
	/// in the order their points reach out (<paramref name="_AtA"/> and <paramref name="_AtB"/>, as shares of each
	/// spoke), wound the way the fan triangle is.
	/// </summary>
	private static void Zip(PolygonMesh _Mesh, Material _Material, HalfEdgeMesh.VertexHandle[] _A, float[] _AtA, Vector2[] _UvA, HalfEdgeMesh.VertexHandle[] _B,
		float[] _AtB, Vector2[] _UvB)
	{
		var handles = Join(_A, _B);
		var uvs = Join(_UvA, _UvB);

		foreach (var (p, q, r) in Strip(_AtA, _AtB))
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, handles[p], handles[q], handles[r], uvs[p], uvs[q], uvs[r]);
	}



	/// <summary>
	/// Fills one triangle of the fan as <see cref="Zip"/> does, over a stretch whose road gives way to the fan's surface
	/// across it: out along the spokes in the order their points reach into the blend, so each of its steps is a row
	/// across the fan. A triangle that is all the fan's surface is drawn in it as the rest of the fan is, one that is all
	/// the road's in the road's own material, and anything between in the blend material, its points painted with how
	/// much of the fan's surface there is at each.
	/// </summary>
	private static void ZipBlended(PolygonMesh _Mesh, Material _Material, IFanBlend _Blend, Vector3 _Centre,
		Vector3 _EndA, HalfEdgeMesh.VertexHandle[] _A, float[] _AtA, Vector2[] _UvA,
		Vector3 _EndB, HalfEdgeMesh.VertexHandle[] _B, float[] _AtB, Vector2[] _UvB)
	{
		var handles = Join(_A, _B);
		var uvs = Join(_UvA, _UvB);
		var positions = Join(SpokePoints(_Centre, _EndA, _AtA), SpokePoints(_Centre, _EndB, _AtB));
		int count = positions.Length;
		var into = new float[count];
		var weights = new float[count];
		var own = new Vector2[count];
		var mixed = new Vector2[count];

		for (int j = 0; j < count; j++)
		{
			into[j] = _Blend.Into(positions[j]);
			weights[j] = _Blend.WeightAt(into[j]);
			own[j] = _Blend.OwnUv(positions[j]);
			mixed[j] = _Blend.BlendUv(own[j], uvs[j], weights[j]);
		}

		foreach (var (p, q, r) in Strip(into[.._A.Length], into[_A.Length..]))
		{
			float least = MathF.Min(weights[p], MathF.Min(weights[q], weights[r]));
			float most = MathF.Max(weights[p], MathF.Max(weights[q], weights[r]));

			if (least >= 1.0f - WeightEpsilon)
			{
				MeshUtility.AddTexturedTriangle(_Mesh, _Material, handles[p], handles[q], handles[r], uvs[p], uvs[q], uvs[r]);
				continue;
			}

			if (most <= WeightEpsilon)
			{
				MeshUtility.AddTexturedTriangle(_Mesh, _Blend.Own, handles[p], handles[q], handles[r], own[p], own[q], own[r]);
				continue;
			}

			var face = MeshUtility.AddTexturedTriangle(_Mesh, _Blend.Material, handles[p], handles[q], handles[r], mixed[p], mixed[q], mixed[r]);

			MeshUtility.SetFaceBlends(_Mesh, face, new[] { RoadSurfaceBuilder.BlendColour(weights[p]), RoadSurfaceBuilder.BlendColour(weights[q]), RoadSurfaceBuilder.BlendColour(weights[r]) });
		}
	}



	/// <summary>
	/// The triangles filling one triangle of the fan between two spokes that start at the centre, from one to the other
	/// in the order their points reach out by <paramref name="_A"/> and <paramref name="_B"/>, wound the way the fan
	/// triangle is: each as three points, A's numbered from nought and B's on from the end of A's.
	/// </summary>
	private static IEnumerable<(int, int, int)> Strip(float[] _A, float[] _B)
	{
		int lastA = _A.Length - 1;
		int lastB = _B.Length - 1;
		int offset = _A.Length;
		int a = 1;
		int b = 1;

		yield return (0, 1, offset + 1);

		while (a < lastA || b < lastB)
		{
			// Step out along whichever spoke is further behind.
			if (b == lastB || (a < lastA && _A[a + 1] <= _B[b + 1]))
			{
				yield return (a, a + 1, offset + b);
				a++;
			}
			else
			{
				yield return (a, offset + b + 1, offset + b);
				b++;
			}
		}
	}



	/// <summary>
	/// Where a spoke's points are: the centre, the outline point, and the shares of the way between.
	/// </summary>
	private static Vector3[] SpokePoints(Vector3 _Centre, Vector3 _End, float[] _Fractions)
	{
		var points = new Vector3[_Fractions.Length];

		for (int j = 0; j < points.Length; j++)
			points[j] = j == 0 ? _Centre : j == points.Length - 1 ? _End : Vector3.Lerp(_Centre, _End, _Fractions[j]);

		return points;
	}



	private static T[] Join<T>(T[] _A, T[] _B)
	{
		var joined = new T[_A.Length + _B.Length];

		_A.CopyTo(joined, 0);
		_B.CopyTo(joined, _A.Length);

		return joined;
	}



	private static float SmoothStep(float _From, float _To, float _Value)
	{
		float t = Math.Clamp((_Value - _From) / (_To - _From), 0.0f, 1.0f);

		return t * t * (3.0f - 2.0f * t);
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
