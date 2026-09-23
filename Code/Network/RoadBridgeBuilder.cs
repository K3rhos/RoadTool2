using System;
using System.Collections.Generic;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Generates the bridge under a stretch of road: the deck (its two side faces and underside; the road and its borders
/// are the top), what stands along the deck's edges (a parapet wall, a railing, or a prefab repeated along it) and
/// what holds it up (pillars down to the ground, or a prefab). Each of those is one part with its own designs, so a
/// new kind of bridge is a new design of a part. Everything is built in the network's local space, rows at the same
/// distances as the road surface so the deck follows it exactly.
/// </summary>
internal static class RoadBridgeBuilder
{
	/// <summary>
	/// A prefab to stand as a child of the bridge piece, in network space.
	/// </summary>
	public readonly record struct Placement(GameObject Prefab, Vector3 Position, Rotation Rotation);

	/// <summary>
	/// A bridge segment's whole built range (curve distances), whether each end is closed off (nothing continues
	/// the deck there) and whether it gets the railing post at that end. At a node where another bridge segment
	/// carries on, one of the two segments provides the node's post so there is never a pair of them.
	/// </summary>
	public readonly record struct BridgeSpan(float RangeStart, float RangeEnd, bool CapStart, bool PostStart, bool CapEnd, bool PostEnd)
	{
		public void AddToHash(ref StableHash _Hash)
		{
			_Hash.Add(RangeStart);
			_Hash.Add(RangeEnd);
			_Hash.Add(CapStart ? 1 : 0);
			_Hash.Add(PostStart ? 1 : 0);
			_Hash.Add(CapEnd ? 1 : 0);
			_Hash.Add(PostEnd ? 1 : 0);
		}
	}

	/// <summary>
	/// One face of a swept cross-section, between two of its nodes, with its U range across.
	/// </summary>
	private readonly record struct StripFace(int NodeA, int NodeB, Material Material, float Repeat, float U0, float U1);

	/// <summary>
	/// A cross-section to sweep along one side of the road: nodes as (distance outward from the road edge, height
	/// above the surface), node 0 always the road edge itself, which is the reference the fold removal works from
	/// and is never built.
	/// </summary>
	private sealed class StripShape
	{
		public readonly List<Vector2> Nodes = new() { Vector2.Zero };
		public readonly List<StripFace> Faces = new();

		public int Add(Vector2 _Node)
		{
			Nodes.Add(_Node);
			return Nodes.Count - 1;
		}

		/// <summary>
		/// A face between the two last nodes added, textured across from <paramref name="_UFrom"/> (the node the
		/// material's U starts at), skipped when the two nodes coincide.
		/// </summary>
		public void Face(Material _Material, float _Repeat, int _UFrom)
		{
			int b = Nodes.Count - 1;
			int a = b - 1;

			if (Nodes[a].Distance(Nodes[b]) < 0.001f)
				return;

			Faces.Add(new StripFace(a, b, _Material, _Repeat, Across(_UFrom, a) / _Repeat, Across(_UFrom, b) / _Repeat));
		}

		private float Across(int _From, int _To)
		{
			float distance = 0.0f;

			for (int k = _From; k < _To; k++)
				distance += Nodes[k].Distance(Nodes[k + 1]);

			return distance;
		}
	}

	/// <summary>
	/// Two corners of a face closer than this are one point: the face is a triangle.
	/// </summary>
	private const float JoinDistance = 1.0f;



	/// <summary>
	/// Builds the bridge under one stretch of a segment, from <paramref name="_StartDistance"/> to
	/// <paramref name="_EndDistance"/> of the segment's whole built range in <paramref name="_Span"/>, which is what
	/// the posts and supports are spaced evenly along. Prefab parts are not meshed; they come back as placements
	/// for the caller to stand under the piece. <paramref name="_GroundAt"/> finds the ground under a point (network
	/// space), for the supports to land on; null when there is none.
	/// </summary>
	public static PolygonMesh Build(RoadCurveTable _Table, float _StartDistance, float _EndDistance, in BridgeSpan _Span, in RoadWidth _Width, RoadProfile _Profile, BridgeStyle _Style, float _Spacing, float _Lift, Func<Vector3, float?> _GroundAt, List<Placement> _Placements)
	{
		var mesh = new PolygonMesh();

		// Only the outermost pieces close the ends and hold the end posts.
		bool first = _StartDistance <= _Span.RangeStart + 0.01f;
		bool last = _EndDistance >= _Span.RangeEnd - 0.01f;
		var span = new Span(_Span.RangeStart, _Span.RangeEnd, _StartDistance, _EndDistance, first && _Span.PostStart, last && _Span.PostEnd);
		bool capStart = first && _Span.CapStart;
		bool capEnd = last && _Span.CapEnd;

		// The same rows the road on the deck is built on, so the deck follows it exactly.
		var rows = RoadRows.Build(_Table, _StartDistance, _EndDistance, _Spacing, _Width, _Profile.Simplification);

		var left = new List<RoadBorderBuilder.PathPoint>(rows.Count);
		var right = new List<RoadBorderBuilder.PathPoint>(rows.Count);

		foreach (float distance in rows)
		{
			Vector3 rightDirection = RoadCurve.RightOf(_Table.TangentAt(distance));

			RoadSurfaceBuilder.CrossSection(_Table, distance, _Width, _Lift, out Vector3 leftPos, out Vector3 rightPos);

			left.Add(new RoadBorderBuilder.PathPoint(leftPos, -rightDirection, distance));
			right.Add(new RoadBorderBuilder.PathPoint(rightPos, rightDirection, distance));
		}

		var shape = DeckShape(_Profile, _Style);
		var leftHandles = SweepStrip(mesh, left, shape);
		var rightHandles = SweepStrip(mesh, right, shape);

		AddUnderside(mesh, left, right, leftHandles, rightHandles, shape.Nodes.Count - 1, _Style);

		if (capStart)
			AddCap(mesh, left[0], right[0], _Profile, _Style, _Table.TangentAt(_StartDistance) * -1.0f);

		if (capEnd)
			AddCap(mesh, left[^1], right[^1], _Profile, _Style, _Table.TangentAt(_EndDistance));

		AddSides(mesh, _Table, span, _Width, _Lift, left, right, _Profile, _Style, _Placements);
		AddSupports(mesh, _Table, span, _Width, _Lift, _Profile, _Style, _GroundAt, _Placements);

		return mesh;
	}



	/// <summary>
	/// Where things that repeat along a bridge go: spread evenly over the segment's whole built range (from cap
	/// to cap, or to the node where the next bridge segment carries on), in as many equal bays as come nearest
	/// to the asked spacing, of which this piece builds the ones within its own stretch.
	/// </summary>
	private readonly struct Span
	{
		public readonly float RangeStart;
		public readonly float RangeEnd;
		public readonly float Start;
		public readonly float End;
		public readonly bool PostStart;
		public readonly bool PostEnd;

		public Span(float _RangeStart, float _RangeEnd, float _Start, float _End, bool _PostStart, bool _PostEnd)
		{
			RangeStart = _RangeStart;
			RangeEnd = MathF.Max(_RangeStart, _RangeEnd);
			Start = _Start;
			End = _End;
			PostStart = _PostStart;
			PostEnd = _PostEnd;
		}

		private int Bays(float _Spacing)
		{
			return Math.Max(1, (int)MathF.Round((RangeEnd - RangeStart) / MathF.Max(1.0f, _Spacing)));
		}

		/// <summary>
		/// The bay boundaries within this piece, the two ends of the range only when this piece holds their post.
		/// </summary>
		public IEnumerable<float> Edges(float _Spacing)
		{
			int bays = Bays(_Spacing);
			float pitch = (RangeEnd - RangeStart) / bays;

			for (int k = 0; k < bays; k++)
			{
				if (k == 0 && !PostStart)
					continue;

				float distance = RangeStart + k * pitch;

				if (distance >= Start && distance < End)
					yield return distance;
			}

			if (PostEnd)
				yield return RangeEnd;
		}

		/// <summary>
		/// The middle of every bay within this piece.
		/// </summary>
		public IEnumerable<float> Centres(float _Spacing)
		{
			int bays = Bays(_Spacing);
			float pitch = (RangeEnd - RangeStart) / bays;

			for (int k = 0; k < bays; k++)
			{
				float distance = RangeStart + (k + 0.5f) * pitch;

				if (distance >= Start && distance < End)
					yield return distance;
			}
		}
	}



	/// <summary>
	/// The height of the road's border at its outer edge, where a wall or railing stands: the pavement top of a
	/// sidewalk, the road itself otherwise (a country verge dips below the road and into the deck).
	/// </summary>
	private static float SideBase(RoadProfile _Profile)
	{
		return _Profile.Border == RoadBorderStyle.Sidewalk ? _Profile.CurbHeight : 0.0f;
	}



	/// <summary>
	/// How far the deck reaches beyond the road's edge: the border, the strip the side stands on, the overhang.
	/// </summary>
	public static float DeckExtent(RoadProfile _Profile, BridgeStyle _Style)
	{
		return _Profile.FlatBorderWidth + _Style.SideStripWidth + _Style.DeckOverhang;
	}



	/// <summary>
	/// The deck's cross-section on one side, outward from the road edge: over the border to where the side stands
	/// (a wall is part of it: up its inner face, over its top, down its outer face to deck level), along the bare
	/// overhang, then down the deck's side face. The underside is closed separately between the two sides.
	/// </summary>
	private static StripShape DeckShape(RoadProfile _Profile, BridgeStyle _Style)
	{
		var shape = new StripShape();
		float border = _Profile.FlatBorderWidth;
		float strip = _Style.SideStripWidth;
		float edge = border + strip + _Style.DeckOverhang;
		var deck = _Style.ResolvedDeckMaterial;

		if (_Style.Side == BridgeSideStyle.Wall)
		{
			float bottom = SideBase(_Profile);
			float top = bottom + _Style.SideHeight;
			var wall = _Style.ResolvedSideMaterial;

			int first = shape.Add(new Vector2(border, bottom));
			shape.Add(new Vector2(border, top));
			shape.Face(wall, _Style.SideTextureRepeat, first);
			shape.Add(new Vector2(border + strip, top));
			shape.Face(wall, _Style.SideTextureRepeat, first);
			shape.Add(new Vector2(border + strip, 0.0f));
			shape.Face(wall, _Style.SideTextureRepeat, first);

			int ledge = shape.Nodes.Count - 1;
			shape.Add(new Vector2(edge, 0.0f));
			shape.Face(deck, _Style.DeckTextureRepeat, ledge);
			shape.Add(new Vector2(edge, -_Style.DeckDepth));
			shape.Face(deck, _Style.DeckTextureRepeat, ledge);
		}
		else
		{
			int first = shape.Add(new Vector2(border, 0.0f));
			shape.Add(new Vector2(edge, 0.0f));
			shape.Face(deck, _Style.DeckTextureRepeat, first);
			shape.Add(new Vector2(edge, -_Style.DeckDepth));
			shape.Face(deck, _Style.DeckTextureRepeat, first);
		}

		return shape;
	}



	/// <summary>
	/// Sweeps a cross-section along a side's path and returns the vertex of every node on every row (shared where a
	/// node stalled on the previous row to take a fold out, so the faces round it close as triangles).
	/// </summary>
	private static HalfEdgeMesh.VertexHandle[][] SweepStrip(PolygonMesh _Mesh, List<RoadBorderBuilder.PathPoint> _Path, StripShape _Shape)
	{
		int nodeCount = _Shape.Nodes.Count;
		var positions = new Vector3[_Path.Count][];

		for (int i = 0; i < _Path.Count; i++)
		{
			positions[i] = new Vector3[nodeCount];

			for (int k = 0; k < nodeCount; k++)
				positions[i][k] = _Path[i].Position + _Path[i].Outward * _Shape.Nodes[k].x + Vector3.Up * _Shape.Nodes[k].y;
		}

		var handles = SharedHandles(_Mesh, _Path, positions, nodeCount);

		// Which side of its path the strip lies on, summed over every step: the chord from the first point to the
		// last says nothing on a path that turns back on itself (a loop ramp's deck), and a strip wound against its
		// neighbours is a hole.
		float winding = 0.0f;

		for (int i = 0; i < _Path.Count - 1; i++)
			winding += Vector3.Dot(Vector3.Cross(_Path[i + 1].Position - _Path[i].Position, _Path[i].Outward), Vector3.Up);

		bool flip = winding > 0.0f;

		for (int i = 0; i < _Path.Count - 1; i++)
		{
			float v0 = _Path[i].Along;
			float v1 = _Path[i + 1].Along;

			foreach (var face in _Shape.Faces)
			{
				int a = face.NodeA;
				int b = face.NodeB;

				AddStripFace(_Mesh, face.Material, flip,
					handles[i][a], handles[i][b], handles[i + 1][b], handles[i + 1][a],
					positions[i][a], positions[i][b], positions[i + 1][b], positions[i + 1][a],
					new Vector2(face.U0, v0 / face.Repeat), new Vector2(face.U1, v0 / face.Repeat),
					new Vector2(face.U1, v1 / face.Repeat), new Vector2(face.U0, v1 / face.Repeat));
			}
		}

		return handles;
	}



	/// <summary>
	/// One vertex per row and node, except that a node which folded back on the inside of a bend shares the
	/// vertex of the row it stalled on.
	/// </summary>
	private static HalfEdgeMesh.VertexHandle[][] SharedHandles(PolygonMesh _Mesh, List<RoadBorderBuilder.PathPoint> _Path, Vector3[][] _Positions, int _NodeCount)
	{
		var stalls = RoadBorderBuilder.RemoveFolds(_Path, _Positions, _NodeCount);
		var handles = new HalfEdgeMesh.VertexHandle[_Path.Count][];

		for (int i = 0; i < _Path.Count; i++)
		{
			handles[i] = new HalfEdgeMesh.VertexHandle[_NodeCount];

			for (int k = 1; k < _NodeCount; k++)
			{
				if (stalls[i][k] < 0)
					handles[i][k] = _Mesh.AddVertices(_Positions[i][k])[0];
			}
		}

		// Backward stalls point at later rows, hence the second pass.
		for (int i = 0; i < _Path.Count; i++)
		{
			for (int k = 1; k < _NodeCount; k++)
			{
				int source = i;

				while (stalls[source][k] >= 0)
					source = stalls[source][k];

				handles[i][k] = handles[source][k];
			}
		}

		return handles;
	}



	/// <summary>
	/// The deck's underside, closed between the two sides' bottom edges from their own vertices, so it is one
	/// surface with them. Facing down.
	/// </summary>
	private static void AddUnderside(PolygonMesh _Mesh, List<RoadBorderBuilder.PathPoint> _Left, List<RoadBorderBuilder.PathPoint> _Right, HalfEdgeMesh.VertexHandle[][] _LeftHandles, HalfEdgeMesh.VertexHandle[][] _RightHandles, int _BottomNode, BridgeStyle _Style)
	{
		var material = _Style.ResolvedDeckMaterial;
		float repeat = _Style.DeckTextureRepeat;

		for (int i = 0; i < _Left.Count - 1; i++)
		{
			Vector3 l0 = _Mesh.GetVertexPosition(_LeftHandles[i][_BottomNode]);
			Vector3 l1 = _Mesh.GetVertexPosition(_LeftHandles[i + 1][_BottomNode]);
			Vector3 r0 = _Mesh.GetVertexPosition(_RightHandles[i][_BottomNode]);
			Vector3 r1 = _Mesh.GetVertexPosition(_RightHandles[i + 1][_BottomNode]);

			float uLeft = -l0.Distance(r0) * 0.5f / repeat;
			float uRight = -uLeft;
			float v0 = _Left[i].Along / repeat;
			float v1 = _Left[i + 1].Along / repeat;

			// The road surface's order faces up; reversed, the underside faces down.
			AddStripFace(_Mesh, material, false,
				_LeftHandles[i][_BottomNode], _LeftHandles[i + 1][_BottomNode], _RightHandles[i + 1][_BottomNode], _RightHandles[i][_BottomNode],
				l0, l1, r1, r0,
				new Vector2(uLeft, v0), new Vector2(uLeft, v1), new Vector2(uRight, v1), new Vector2(uRight, v0));
		}
	}



	/// <summary>
	/// Closes the deck's cross-section at an open end: the slab across the whole width, plus each wall's own
	/// section, as separate flat faces on fresh vertices (a concave outline in one face is not worth the risk).
	/// </summary>
	private static void AddCap(PolygonMesh _Mesh, in RoadBorderBuilder.PathPoint _Left, in RoadBorderBuilder.PathPoint _Right, RoadProfile _Profile, BridgeStyle _Style, Vector3 _Facing)
	{
		float extent = DeckExtent(_Profile, _Style);
		var deck = _Style.ResolvedDeckMaterial;

		Vector3 leftEdge = _Left.Position + _Left.Outward * extent;
		Vector3 rightEdge = _Right.Position + _Right.Outward * extent;
		Vector3 down = Vector3.Down * _Style.DeckDepth;

		AddFlatQuad(_Mesh, deck, _Style.DeckTextureRepeat, _Facing, leftEdge + down, rightEdge + down, rightEdge, leftEdge);

		if (_Style.Side != BridgeSideStyle.Wall)
			return;

		float border = _Profile.FlatBorderWidth;
		float top = SideBase(_Profile) + _Style.SideHeight;
		var wall = _Style.ResolvedSideMaterial;

		foreach (var point in new[] { _Left, _Right })
		{
			Vector3 inner = point.Position + point.Outward * border;
			Vector3 outer = inner + point.Outward * _Style.SideWidth;

			AddFlatQuad(_Mesh, wall, _Style.SideTextureRepeat, _Facing, inner, outer, outer + Vector3.Up * top, inner + Vector3.Up * top);
		}
	}



	/// <summary>
	/// The railing's posts and rails, or the prefabs, along both edges of the deck. Posts stand at the bay
	/// boundaries, the ends of the bridge included, so the rails (open bars) run into one at each end; prefabs
	/// fill the bays.
	/// </summary>
	private static void AddSides(PolygonMesh _Mesh, RoadCurveTable _Table, in Span _Span, in RoadWidth _Width, float _Lift, List<RoadBorderBuilder.PathPoint> _Left, List<RoadBorderBuilder.PathPoint> _Right, RoadProfile _Profile, BridgeStyle _Style, List<Placement> _Placements)
	{
		float border = _Profile.FlatBorderWidth;
		float centre = border + _Style.SideWidth * 0.5f;

		switch (_Style.Side)
		{
			case BridgeSideStyle.Railing:
			{
				float height = SideBase(_Profile) + _Style.SideHeight;
				var material = _Style.ResolvedSideMaterial;
				float repeat = _Style.SideTextureRepeat;

				foreach (float distance in _Span.Edges(_Style.PostSpacing))
				{
					Frame(_Table, distance, _Width, _Lift, out Vector3 leftEdge, out Vector3 rightEdge, out Vector3 right, out Vector3 along);

					AddPost(_Mesh, material, repeat, leftEdge - right * centre, right, along, _Style.SideWidth, height);
					AddPost(_Mesh, material, repeat, rightEdge + right * centre, right, along, _Style.SideWidth, height);
				}

				AddRails(_Mesh, _Left, centre, height, material, repeat, _Style);
				AddRails(_Mesh, _Right, centre, height, material, repeat, _Style);
				break;
			}

			case BridgeSideStyle.Prefab:
			{
				if (!_Style.SidePrefab.IsValid())
					break;

				foreach (float distance in _Span.Centres(_Style.SideSpacing))
				{
					Frame(_Table, distance, _Width, _Lift, out Vector3 leftEdge, out Vector3 rightEdge, out Vector3 right, out Vector3 along);

					// Forward along the road, right pointing away from it: on the left side that is backwards.
					_Placements.Add(new Placement(_Style.SidePrefab, leftEdge - right * centre, Rotation.LookAt(-along, Vector3.Up)));
					_Placements.Add(new Placement(_Style.SidePrefab, rightEdge + right * centre, Rotation.LookAt(along, Vector3.Up)));
				}

				break;
			}
		}
	}



	/// <summary>
	/// The railing's horizontal rails along one side: square bars swept along the road, evenly spaced down from
	/// the top of the railing.
	/// </summary>
	private static void AddRails(PolygonMesh _Mesh, List<RoadBorderBuilder.PathPoint> _Path, float _Centre, float _Height, Material _Material, float _Repeat, BridgeStyle _Style)
	{
		// Thinner than the posts they run through, so their faces never lie on the posts'.
		float size = MathF.Min(_Style.RailSize, _Style.SideWidth * 0.9f);
		float pitch = _Style.SideHeight / _Style.RailCount;

		for (int r = 0; r < _Style.RailCount; r++)
		{
			float middle = _Height - size * 0.5f - r * pitch;

			if (middle - size * 0.5f < 0.0f)
				break;

			// Round the bar clockwise in the (outward, up) plane, which is what gives every face an outward normal.
			var shape = new StripShape();
			int first = shape.Add(new Vector2(_Centre - size * 0.5f, middle + size * 0.5f));
			shape.Add(new Vector2(_Centre + size * 0.5f, middle + size * 0.5f));
			shape.Face(_Material, _Repeat, first);
			shape.Add(new Vector2(_Centre + size * 0.5f, middle - size * 0.5f));
			shape.Face(_Material, _Repeat, first);
			shape.Add(new Vector2(_Centre - size * 0.5f, middle - size * 0.5f));
			shape.Face(_Material, _Repeat, first);
			shape.Add(new Vector2(_Centre - size * 0.5f, middle + size * 0.5f));
			shape.Face(_Material, _Repeat, first);

			SweepStrip(_Mesh, _Path, shape);
		}
	}



	/// <summary>
	/// The pillars (or support prefabs) under the deck, one in the middle of each bay along the segment, each
	/// dropped onto the ground under it.
	/// </summary>
	private static void AddSupports(PolygonMesh _Mesh, RoadCurveTable _Table, in Span _Span, in RoadWidth _Width, float _Lift, RoadProfile _Profile, BridgeStyle _Style, Func<Vector3, float?> _GroundAt, List<Placement> _Placements)
	{
		if (_Style.Supports == BridgeSupportStyle.None)
			return;

		if (_Style.Supports == BridgeSupportStyle.Prefab && !_Style.SupportPrefab.IsValid())
			return;

		float extent = DeckExtent(_Profile, _Style);
		float underside = -_Style.DeckDepth;

		foreach (float distance in _Span.Centres(_Style.SupportSpacing))
		{
			Frame(_Table, distance, _Width, _Lift, out Vector3 leftEdge, out Vector3 rightEdge, out Vector3 right, out Vector3 along);
			Vector3 centre = (leftEdge + rightEdge) * 0.5f + Vector3.Up * underside;

			if (_Style.Supports == BridgeSupportStyle.Prefab)
			{
				_Placements.Add(new Placement(_Style.SupportPrefab, Landing(centre, _Style, _GroundAt, 0.0f), Rotation.LookAt(along, Vector3.Up)));
				continue;
			}

			var material = _Style.ResolvedSupportMaterial;

			if (_Style.PillarLayout == BridgePillarLayout.Single)
			{
				AddPillar(_Mesh, material, centre, right, along, _Style, _GroundAt);
				continue;
			}

			// A pair stands under the deck's edges, inset; too narrow a deck for a pair gets one in the middle.
			float inset = extent - _Style.PillarInset - _Style.PillarSize * 0.5f;
			float deckWidth = _Width.WidthAt(distance) + 2.0f * extent;

			if (deckWidth - 2.0f * (extent - inset) <= _Style.PillarSize * 2.0f)
			{
				AddPillar(_Mesh, material, centre, right, along, _Style, _GroundAt);
				continue;
			}

			Vector3 down = Vector3.Up * underside;

			AddPillar(_Mesh, material, leftEdge - right * inset + down, right, along, _Style, _GroundAt);
			AddPillar(_Mesh, material, rightEdge + right * inset + down, right, along, _Style, _GroundAt);
		}
	}



	/// <summary>
	/// Where a support standing at a point under the deck ends: sunk into the ground found below it, or the
	/// fallback height down when there is none.
	/// </summary>
	private static Vector3 Landing(Vector3 _Top, BridgeStyle _Style, Func<Vector3, float?> _GroundAt, float _Sink)
	{
		float? ground = _GroundAt?.Invoke(_Top);
		float z = ground.HasValue ? ground.Value - _Sink : _Top.z - _Style.SupportFallbackHeight;

		return _Top.WithZ(MathF.Min(z, _Top.z - 1.0f));
	}



	private static void AddPillar(PolygonMesh _Mesh, Material _Material, Vector3 _Top, Vector3 _Right, Vector3 _Along, BridgeStyle _Style, Func<Vector3, float?> _GroundAt)
	{
		Vector3 bottom = Landing(_Top, _Style, _GroundAt, _Style.SupportSink);
		float half = _Style.PillarSize * 0.5f;
		Vector3[] ring;

		if (_Style.PillarShape == BridgePillarShape.Round)
		{
			int segments = Math.Max(3, _Style.PillarSegments);
			ring = new Vector3[segments];

			for (int s = 0; s < segments; s++)
			{
				float angle = MathF.Tau * s / segments;
				ring[s] = bottom + _Right * (MathF.Cos(angle) * half) + _Along * (MathF.Sin(angle) * half);
			}
		}
		else
		{
			ring = new[]
			{
				bottom - _Right * half - _Along * half,
				bottom + _Right * half - _Along * half,
				bottom + _Right * half + _Along * half,
				bottom - _Right * half + _Along * half
			};
		}

		// Its top is inside the deck and its bottom under the ground: sides only.
		AddPrism(_Mesh, _Material, _Style.DeckTextureRepeat, ring, _Top.z - bottom.z, false, false);
	}



	/// <summary>
	/// A railing post: a square column standing on the deck at deck level.
	/// </summary>
	private static void AddPost(PolygonMesh _Mesh, Material _Material, float _Repeat, Vector3 _Base, Vector3 _Right, Vector3 _Along, float _Size, float _Height)
	{
		float half = _Size * 0.5f;

		var ring = new[]
		{
			_Base - _Right * half - _Along * half,
			_Base + _Right * half - _Along * half,
			_Base + _Right * half + _Along * half,
			_Base - _Right * half + _Along * half
		};

		AddPrism(_Mesh, _Material, _Repeat, ring, _Height, true, false);
	}



	/// <summary>
	/// A vertical prism on a base ring (counterclockwise seen from above). The sides share their vertices round
	/// the ring, so a round pillar's facets smooth into each other while a square one's corners stay hard; the
	/// ends are fans on vertices of their own.
	/// </summary>
	private static void AddPrism(PolygonMesh _Mesh, Material _Material, float _Repeat, Vector3[] _Ring, float _Height, bool _Top, bool _Bottom)
	{
		if (_Height <= 0.01f)
			return;

		Vector3 up = Vector3.Up * _Height;
		int count = _Ring.Length;
		var lower = _Mesh.AddVertices(_Ring);
		var upper = new HalfEdgeMesh.VertexHandle[count];

		for (int s = 0; s < count; s++)
			upper[s] = _Mesh.AddVertices(_Ring[s] + up)[0];

		Vector3 centre = Vector3.Zero;

		foreach (var point in _Ring)
			centre += point;

		centre /= count;

		float around = 0.0f;
		float v = _Height / _Repeat;

		for (int s = 0; s < count; s++)
		{
			int n = (s + 1) % count;
			float next = around + _Ring[s].Distance(_Ring[n]);
			var uvA = new Vector2(around / _Repeat, 0.0f);
			var uvB = new Vector2(next / _Repeat, 0.0f);
			var uvC = new Vector2(next / _Repeat, v);
			var uvD = new Vector2(around / _Repeat, v);

			// Wound so the face looks away from the prism's axis, whichever way round the ring was given.
			Vector3 outwards = (_Ring[s] + _Ring[n]) * 0.5f - centre;
			Vector3 normal = Vector3.Cross(_Ring[n] - _Ring[s], up);

			if (Vector3.Dot(normal, outwards) >= 0.0f)
				MeshUtility.AddTexturedQuad(_Mesh, _Material, lower[s], lower[n], upper[n], upper[s], uvA, uvB, uvC, uvD);
			else
				MeshUtility.AddTexturedQuad(_Mesh, _Material, lower[s], upper[s], upper[n], lower[n], uvA, uvD, uvC, uvB);

			around = next;
		}

		if (_Top)
			AddFlatPolygon(_Mesh, _Material, _Repeat, Vector3.Up, _Ring, up);

		if (_Bottom)
			AddFlatPolygon(_Mesh, _Material, _Repeat, Vector3.Down, _Ring, Vector3.Zero);
	}



	/// <summary>
	/// A ring's flat end as a fan of triangles from its centre, on fresh vertices.
	/// </summary>
	private static void AddFlatPolygon(PolygonMesh _Mesh, Material _Material, float _Repeat, Vector3 _Facing, Vector3[] _Ring, Vector3 _Offset)
	{
		Vector3 centre = Vector3.Zero;

		foreach (var point in _Ring)
			centre += point;

		centre = centre / _Ring.Length + _Offset;

		for (int s = 0; s < _Ring.Length; s++)
		{
			Vector3 a = _Ring[s] + _Offset;
			Vector3 b = _Ring[(s + 1) % _Ring.Length] + _Offset;
			Vector3 normal = Vector3.Cross(a - centre, b - centre);
			var handles = _Mesh.AddVertices(centre, a, b);

			if (Vector3.Dot(normal, _Facing) >= 0.0f)
				MeshUtility.AddTexturedTriangle(_Mesh, _Material, handles[0], handles[1], handles[2], PlanarUv(centre, _Repeat), PlanarUv(a, _Repeat), PlanarUv(b, _Repeat));
			else
				MeshUtility.AddTexturedTriangle(_Mesh, _Material, handles[0], handles[2], handles[1], PlanarUv(centre, _Repeat), PlanarUv(b, _Repeat), PlanarUv(a, _Repeat));
		}
	}



	/// <summary>
	/// A quad on fresh vertices, wound to face <paramref name="_Facing"/>, with U running from the first corner to
	/// the second and V up the third and fourth.
	/// </summary>
	private static void AddFlatQuad(PolygonMesh _Mesh, Material _Material, float _Repeat, Vector3 _Facing, Vector3 _A, Vector3 _B, Vector3 _C, Vector3 _D, float _U0 = 0.0f, float _U1 = float.NaN, float _V1 = float.NaN)
	{
		if (float.IsNaN(_U1))
			_U1 = _U0 + _A.Distance(_B) / _Repeat;

		if (float.IsNaN(_V1))
			_V1 = _B.Distance(_C) / _Repeat;

		Vector3 normal = Vector3.Cross(_B - _A, _D - _A);
		var handles = _Mesh.AddVertices(_A, _B, _C, _D);
		var uvA = new Vector2(_U0, 0.0f);
		var uvB = new Vector2(_U1, 0.0f);
		var uvC = new Vector2(_U1, _V1);
		var uvD = new Vector2(_U0, _V1);

		if (Vector3.Dot(normal, _Facing) >= 0.0f)
			MeshUtility.AddTexturedQuad(_Mesh, _Material, handles[0], handles[1], handles[2], handles[3], uvA, uvB, uvC, uvD);
		else
			MeshUtility.AddTexturedQuad(_Mesh, _Material, handles[0], handles[3], handles[2], handles[1], uvA, uvD, uvC, uvB);
	}



	/// <summary>
	/// A face of a swept strip between two rows: a quad, or a triangle where two of its corners sit on one point
	/// (a node that stalled to take a fold out), as two triangles in the run's winding.
	/// </summary>
	private static void AddStripFace(PolygonMesh _Mesh, Material _Material, bool _Flip,
		HalfEdgeMesh.VertexHandle _HA, HalfEdgeMesh.VertexHandle _HB, HalfEdgeMesh.VertexHandle _HC, HalfEdgeMesh.VertexHandle _HD,
		Vector3 _PA, Vector3 _PB, Vector3 _PC, Vector3 _PD,
		Vector2 _UvA, Vector2 _UvB, Vector2 _UvC, Vector2 _UvD)
	{
		bool outerJoined = _PB.Distance(_PC) < JoinDistance;
		bool innerJoined = _PA.Distance(_PD) < JoinDistance;

		if (outerJoined && innerJoined)
			return;

		if (outerJoined)
			AddTriangle(_Mesh, _Material, _Flip, _HA, _HB, _HD, _UvA, _UvB, _UvD);
		else if (innerJoined)
			AddTriangle(_Mesh, _Material, _Flip, _HA, _HB, _HC, _UvA, _UvB, _UvC);
		else
		{
			AddTriangle(_Mesh, _Material, _Flip, _HA, _HB, _HC, _UvA, _UvB, _UvC);
			AddTriangle(_Mesh, _Material, _Flip, _HA, _HC, _HD, _UvA, _UvC, _UvD);
		}
	}



	private static void AddTriangle(PolygonMesh _Mesh, Material _Material, bool _Flip,
		HalfEdgeMesh.VertexHandle _HA, HalfEdgeMesh.VertexHandle _HB, HalfEdgeMesh.VertexHandle _HC,
		Vector2 _UvA, Vector2 _UvB, Vector2 _UvC)
	{
		if (!_Flip)
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, _HA, _HB, _HC, _UvA, _UvB, _UvC);
		else
			MeshUtility.AddTexturedTriangle(_Mesh, _Material, _HA, _HC, _HB, _UvA, _UvC, _UvB);
	}



	/// <summary>
	/// The road's frame at a distance: its edge points, and its right and along directions (horizontal).
	/// </summary>
	private static void Frame(RoadCurveTable _Table, float _Distance, in RoadWidth _Width, float _Lift, out Vector3 _Left, out Vector3 _Right, out Vector3 _RightDirection, out Vector3 _Along)
	{
		_RightDirection = RoadCurve.RightOf(_Table.TangentAt(_Distance));
		_Along = Vector3.Cross(Vector3.Up, _RightDirection).Normal;

		RoadSurfaceBuilder.CrossSection(_Table, _Distance, _Width, _Lift, out _Left, out _Right);
	}



	private static Vector2 PlanarUv(Vector3 _Position, float _Repeat)
	{
		return new Vector2(_Position.x, _Position.y) / _Repeat;
	}
}
