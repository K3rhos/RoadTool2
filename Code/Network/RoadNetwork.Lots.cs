using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace RedSnail.RoadTool2;

public sealed partial class RoadNetwork
{
	/// <summary>
	/// The parking lots and yards of the network. Like the nodes and segments, a whole new list lands here on undo
	/// and reload, so the setter is where "the graph changed" is noticed.
	/// </summary>
	[Property, Hide]
	public List<RoadLot> Lots
	{
		get;
		set
		{
			field = value ?? new List<RoadLot>();
			MarkDirty();
		}
	} = [];



	public RoadLot FindLot(int _Id)
	{
		return Lots.FirstOrDefault(lot => lot.Id == _Id);
	}



	/// <summary>
	/// The lot a node opens into, or null when the node is not on a lot's edge.
	/// </summary>
	public RoadLot LotAtOpening(int _NodeId)
	{
		return Lots.FirstOrDefault(lot => lot.Openings.Contains(_NodeId));
	}



	/// <summary>
	/// The lot type a lot is surfaced as: its own, else the network's default, else plain asphalt.
	/// </summary>
	public RoadProfile ProfileOf(RoadLot _Lot)
	{
		return _Lot?.Profile ?? DefaultProfile ?? RoadProfile.Fallback;
	}



	/// <summary>
	/// Adds a lot reaching away from a node: the node ends up on the middle of the lot's near edge, and the road
	/// at it opens into the lot. Returns null when the node cannot carry one (it is not a road end).
	/// </summary>
	public RoadLot AddLot(int _NodeId, float _Width, float _Depth, RoadProfile _Profile = null, ParkingStyle _Parking = null)
	{
		var node = FindNode(_NodeId);

		if (node is null || GetDegree(_NodeId) > 1 || LotAtOpening(_NodeId) is not null)
			return null;

		var lot = new RoadLot
		{
			Id = NextId++,
			Width = _Width,
			Depth = _Depth,
			Profile = _Profile,
			Parking = _Parking
		};

		// Facing back down the road the node came from, so the lot lies beyond it.
		var segment = GetSegmentsAt(_NodeId).FirstOrDefault();
		Vector3 away = segment is null ? Vector3.Forward : -LeavingDirection(segment, _NodeId);

		lot.Yaw = away.EulerAngles.yaw;
		lot.Centre = node.Position + away.Normal * (lot.Depth * 0.5f);
		lot.Openings.Add(_NodeId);

		Lots.Add(lot);
		SnapOpenings(lot);
		MarkDirty();

		return lot;
	}



	/// <summary>
	/// Adds a node on a lot's edge for a road to open into, at the point of the edge nearest to where it is asked
	/// for. Returns the node, or null when the lot is gone.
	/// </summary>
	public RoadNode AddOpening(int _LotId, Vector3 _NearPosition)
	{
		var lot = FindLot(_LotId);

		if (lot is null)
			return null;

		int edge = lot.NearestEdge(_NearPosition, out float along);
		var node = new RoadNode { Id = NextId++, Position = lot.EdgePoint(edge, along), AutoTangents = false };

		Nodes.Add(node);
		lot.Openings.Add(node.Id);
		SnapOpenings(lot);
		MarkDirty();

		return node;
	}



	/// <summary>
	/// Makes a node that is already there another way into a lot: it moves onto the lot's edge, and the road at
	/// it turns to meet the lot square on. Nothing happens if it already opens into one.
	/// </summary>
	public bool JoinOpening(int _LotId, int _NodeId)
	{
		var lot = FindLot(_LotId);
		var node = FindNode(_NodeId);

		if (lot is null || node is null || LotAtOpening(_NodeId) is not null)
			return false;

		lot.Openings.Add(_NodeId);
		SnapOpenings(lot);
		MarkDirty();

		return true;
	}



	/// <summary>
	/// Puts every one of a lot's opening nodes back on its edge (a lot that has been moved or resized takes its
	/// entrances with it), far enough from the corners for the opening and its flares to fit, and drops the ones
	/// whose nodes have gone.
	/// </summary>
	public void SnapOpenings(RoadLot _Lot)
	{
		if (_Lot is null)
			return;

		for (int i = _Lot.Openings.Count - 1; i >= 0; i--)
		{
			var node = FindNode(_Lot.Openings[i]);

			if (node is null)
			{
				_Lot.Openings.RemoveAt(i);
				continue;
			}

			int edge = _Lot.NearestEdge(node.Position, out float along);

			_Lot.EdgeFrame(edge, out _, out _, out _, out float half);

			float reach = OpeningReach(_Lot, node.Id);
			float limit = MathF.Max(0.0f, half - _Lot.Radius - reach);
			Vector3 point = _Lot.EdgePoint(edge, Math.Clamp(along, -limit, limit));

			if (node.Position.Distance(point) < 0.01f)
				continue;

			node.Position = point;
			RefreshTangentsAround(node.Id);
		}
	}



	/// <summary>
	/// How much room an opening takes either side of its middle: half the road, and the flare that turns the kerb
	/// into it.
	/// </summary>
	private float OpeningReach(RoadLot _Lot, int _NodeId)
	{
		return OpeningHalfWidth(_NodeId) + LotFlare(_Lot);
	}



	/// <summary>
	/// Half the width of the road that opens into a lot at a node.
	/// </summary>
	private float OpeningHalfWidth(int _NodeId)
	{
		var segment = GetSegmentsAt(_NodeId).FirstOrDefault();

		return segment is null ? 0.0f : (EdgeAtNode(segment, _NodeId, true) + EdgeAtNode(segment, _NodeId, false)) * 0.5f;
	}



	/// <summary>
	/// The flare a lot turns its kerb into a road with, as its own size allows it.
	/// </summary>
	private static float LotFlare(RoadLot _Lot)
	{
		return MathF.Max(0.0f, MathF.Min(_Lot.EntranceRadius, MathF.Min(_Lot.HalfWidth, _Lot.HalfDepth) * 0.5f));
	}



	/// <summary>
	/// How far a road is held back from a lot's edge: the lot's surface fills the corners between the two, so the
	/// road starts at the far side of them.
	/// </summary>
	public float LotTrimAt(int _NodeId)
	{
		var lot = LotAtOpening(_NodeId);

		if (lot is null)
			return 0.0f;

		// The same length the lot's outline was built to, so the two meet on the road's own row.
		var segment = GetSegmentsAt(_NodeId).FirstOrDefault();
		var table = segment is null ? null : GetCurveTable(segment);
		float flare = LotFlare(lot);

		return table is null ? flare : MathF.Min(flare, table.Length * 0.5f);
	}



	public void SnapAllOpenings()
	{
		foreach (var lot in Lots)
			SnapOpenings(lot);
	}



	/// <summary>
	/// Removes a lot. Its opening nodes stay where they are, with their roads; they are simply no longer openings.
	/// </summary>
	public void RemoveLot(int _LotId)
	{
		var lot = FindLot(_LotId);

		if (lot is null)
			return;

		Lots.Remove(lot);
		MarkDirty();
	}



	/// <summary>
	/// The openings of a lot as its builders want them: which edge each one meets, where along it, how wide the
	/// road is and how far the kerb flares into it.
	/// </summary>
	public List<RoadLotOpening> OpeningsOf(RoadLot _Lot)
	{
		var openings = new List<RoadLotOpening>();

		foreach (int nodeId in _Lot.Openings)
		{
			var node = FindNode(nodeId);

			if (node is null)
				continue;

			int edge = _Lot.NearestEdge(node.Position, out float along);

			_Lot.EdgeFrame(edge, out _, out Vector3 direction, out Vector3 outward, out _);

			var opening = new RoadLotOpening
			{
				NodeId = nodeId,
				Edge = edge,
				Along = along,
				HalfWidth = OpeningHalfWidth(nodeId),
				Flare = LotFlare(_Lot),
				Mouth = _Lot.EdgePoint(edge, along),
				Outward = outward,
				Away = outward
			};

			// Where the road the lot hands over to actually starts: its own row at the far side of the flares.
			var segment = GetSegmentsAt(nodeId).FirstOrDefault();
			var table = segment is null ? null : GetCurveTable(segment);

			if (table is not null)
			{
				bool atStart = segment.StartNodeId == nodeId;
				float trim = MathF.Min(opening.Flare, table.Length * 0.5f);
				float distance = atStart ? trim : table.Length - trim;
				var width = new RoadWidth(segment, table.Length);

				RoadSurfaceBuilder.CrossSection(table, distance, width, 0.0f, out Vector3 left, out Vector3 right);

				// Ordered the way the lot's edge runs, so the outline meets them in the order it walks.
				bool leftFirst = Vector3.Dot(left - opening.Mouth, direction) < Vector3.Dot(right - opening.Mouth, direction);

				opening.MouthFrom = leftFirst ? left : right;
				opening.MouthTo = leftFirst ? right : left;
				opening.Away = (table.TangentAt(distance) * (atStart ? 1.0f : -1.0f)).Normal;
			}
			else
			{
				opening.MouthFrom = opening.Mouth - direction * opening.HalfWidth + outward * opening.Flare;
				opening.MouthTo = opening.Mouth + direction * opening.HalfWidth + outward * opening.Flare;
			}

			openings.Add(opening);
		}

		return openings;
	}



	/// <summary>
	/// The way out of the lot a node opens into, at that node: what a road leaving there has to run along, so the
	/// road meets the lot's edge square and the two surfaces butt together.
	/// </summary>
	public bool TryLotOutward(int _NodeId, out Vector3 _Outward)
	{
		_Outward = Vector3.Zero;

		if (Lots.Count == 0)
			return false;

		var lot = LotAtOpening(_NodeId);
		var node = FindNode(_NodeId);

		if (lot is null || node is null)
			return false;

		lot.EdgeFrame(lot.NearestEdge(node.Position, out _), out _, out _, out _Outward, out _);

		return !_Outward.IsNearZeroLength;
	}



	private readonly Dictionary<(int LotId, RoadChunkKind Kind), PendingGroup> m_PendingLots = new();



	/// <summary>
	/// Queues whatever of the lots is missing or stale, and clears out the pieces of lots that are gone.
	/// </summary>
	private void ReconcileLots(Dictionary<(int LotId, RoadChunkKind Kind), RoadChunk> _Existing)
	{
		var live = new HashSet<int>();

		foreach (var lot in Lots)
		{
			live.Add(lot.Id);

			var profile = ProfileOf(lot);
			var openings = OpeningsOf(lot);
			var outline = RoadLotOutline.Build(lot, openings);

			ReconcileLotPiece(lot, RoadChunkKind.Surface, LotSignature(lot, profile, openings, RoadChunkKind.Surface), _Existing);

			if (profile.HasBorder)
				ReconcileLotPiece(lot, RoadChunkKind.Border, LotSignature(lot, profile, openings, RoadChunkKind.Border), _Existing);
			else if (_Existing.TryGetValue((lot.Id, RoadChunkKind.Border), out var border))
				border.GameObject.Destroy();

			if (lot.Parking is not null)
				ReconcileLotPiece(lot, RoadChunkKind.Marking, LotSignature(lot, profile, openings, RoadChunkKind.Marking), _Existing);
			else if (_Existing.TryGetValue((lot.Id, RoadChunkKind.Marking), out var marking))
				marking.GameObject.Destroy();
		}

		foreach (var (key, chunk) in _Existing)
		{
			if (live.Contains(key.LotId))
				continue;

			chunk.GameObject.Destroy();
			m_PendingLots.Remove(key);
		}
	}



	private void ReconcileLotPiece(RoadLot _Lot, RoadChunkKind _Kind, long _Signature, Dictionary<(int LotId, RoadChunkKind Kind), RoadChunk> _Existing)
	{
		var key = (_Lot.Id, _Kind);

		if (_Existing.TryGetValue(key, out var existing) && (existing.Locked || (existing.Signature == _Signature && !LacksDebugRecord(existing))))
			return;

		if (m_PendingLots.TryGetValue(key, out var pending) && pending.Signature == _Signature)
			return;

		m_Jobs.Enqueue(new RebuildJob(-1, -1, _Kind, _Signature, default, null, RoadWidth.Uniform(0.0f), null, null, null, null, null, _Lot));
		m_PendingLots[key] = new PendingGroup(_Signature, 1);
	}



	private long LotSignature(RoadLot _Lot, RoadProfile _Profile, List<RoadLotOpening> _Openings, RoadChunkKind _Kind)
	{
		var hash = StableHash.Create();

		hash.Add(GeneratorVersion);
		hash.Add((int)_Kind);
		hash.Add(SurfaceOffset);
		_Lot.AddToHash(ref hash);

		switch (_Kind)
		{
			case RoadChunkKind.Border:
				_Profile.AddBorderToHash(ref hash);
				hash.Add(DebugBorders ? 1 : 0);
				break;

			case RoadChunkKind.Marking:
				hash.Add(_Lot.Parking?.ResourcePath);
				_Lot.Parking?.AddToHash(ref hash);
				break;

			default:
				_Profile.AddSurfaceToHash(ref hash);
				break;
		}

		// The openings cut the kerb and keep the bays clear, so where they are is part of every piece.
		hash.Add(_Openings.Count);

		foreach (var opening in _Openings)
			opening.AddToHash(ref hash);

		return hash.Value;
	}



	private RoadChunk FindLotChunk(int _LotId, RoadChunkKind _Kind)
	{
		return GetChunks().FirstOrDefault(chunk => chunk.IsLot && chunk.LotId == _LotId && chunk.Kind == _Kind);
	}



	/// <summary>
	/// Builds one piece of a lot: its surface, its kerb or its bays.
	/// </summary>
	private void BuildLotPiece(in RebuildJob _Job)
	{
		var lot = _Job.Lot;
		var existing = FindLotChunk(lot.Id, _Job.Kind);

		if (existing is not null && existing.Locked)
			return;

		var profile = ProfileOf(lot);
		var openings = OpeningsOf(lot);
		var outline = RoadLotOutline.Build(lot, openings);
		PolygonMesh mesh;
		string name;
		BorderDebug debug = null;

		switch (_Job.Kind)
		{
			case RoadChunkKind.Border:
				debug = DebugBorders ? new BorderDebug() : null;
				mesh = RoadLotBuilder.BuildBorder(lot, outline, profile, openings, SurfaceOffset, debug);
				name = $"Lot {lot.Id} border";
				break;

			case RoadChunkKind.Marking:
				mesh = RoadLotBuilder.BuildMarkings(lot, lot.Parking, openings, SurfaceOffset);
				name = $"Lot {lot.Id} markings";
				break;

			default:
				mesh = RoadLotBuilder.BuildSurface(lot, outline, profile, SurfaceOffset);
				name = $"Lot {lot.Id}";
				break;
		}

		CreateChunkObject(name, -1, -1, _Job.Kind, 0, 1, _Job.GroupSignature, mesh, debug, lot.Id);

		existing?.GameObject.Destroy();
	}
}
