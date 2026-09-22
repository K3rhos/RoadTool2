using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace RedSnail.RoadTool2;

public sealed partial class RoadNetwork
{
	/// <summary>
	/// Bump whenever the generated geometry changes (UV layout, sampling, profile...), so pieces built by an older
	/// generator are replaced on the next reconcile instead of lingering until their road is edited.
	/// </summary>
	private const int GeneratorVersion = 29;

	/// <summary>
	/// A stretch of road shorter than this is not worth a chunk (it happens when a junction swallows a whole chunk).
	/// </summary>
	private const float MinChunkLength = 1.0f;

	/// <summary>
	/// Where a segment's painted lines may run: between <see cref="Start"/> and <see cref="End"/>, skipping the
	/// gap between <see cref="GapStart"/> and <see cref="GapEnd"/> when there is one (a crossing in the middle of
	/// a short road between two junctions).
	/// </summary>
	private readonly record struct LineRange(float Start, float End, float GapStart, float GapEnd)
	{
		public bool HasGap => GapEnd > GapStart;

		public void AddToHash(ref StableHash _Hash)
		{
			_Hash.Add(Start);
			_Hash.Add(End);
			_Hash.Add(GapStart);
			_Hash.Add(GapEnd);
		}
	}

	/// <summary>
	/// A segment's pedestrian crossings: at either end, or one shared crossing halfway along a short road between
	/// two junctions (recorded on the start end). Centres are curve distances from the segment's start.
	/// </summary>
	private readonly record struct SegmentCrossings(bool AtStart, float StartCentre, bool AtEnd, float EndCentre);

	/// <summary>
	/// One piece of a segment to build. <see cref="Lines"/> bounds the painted lines; <see cref="Taper"/> is the
	/// border's run-out into a different road type at either end; <see cref="Bridge"/> is the whole built range
	/// and the ends of a bridge piece; the other kinds ignore them.
	/// </summary>
	private readonly record struct ChunkPlan(int Index, int Count, float Start, float End, RoadChunkKind Kind, BorderCaps Caps, RoadBorderBuilder.SegmentBorderBlends Blends, long Signature, LineRange Lines = default, SegmentBorderTapers Taper = default, RoadBridgeBuilder.BridgeSpan Bridge = default, RoadPropBuilder.PropSpan Props = default, RoadMarkingBuilder.DashPhase Dash = default);

	private readonly record struct PendingGroup(long Signature, int Remaining);

	/// <summary>
	/// One piece to (re)build: a chunk of a segment, or, when <see cref="NodeId"/> is set, a piece of a junction.
	/// <see cref="ArmBlends"/> and <see cref="ArmTapers"/> carry, for a junction's border piece, what each arm's
	/// chunks were built with; <see cref="ArmCrossings"/>, for its crossings piece, the crossing centre of each
	/// arm that gets one. All keyed by segment.
	/// </summary>
	private readonly record struct RebuildJob(int SegmentId, int NodeId, RoadChunkKind Kind, long GroupSignature, ChunkPlan Plan, RoadCurveTable Table, RoadWidth Width, RoadProfile Profile, RoadJunctionLayout Junction, Dictionary<int, RoadBorderBuilder.SegmentBorderBlends> ArmBlends, Dictionary<int, SegmentBorderTapers> ArmTapers, Dictionary<int, float> ArmCrossings);

	private bool m_IsDirty;
	private readonly Queue<RebuildJob> m_Jobs = new();
	private readonly Dictionary<int, PendingGroup> m_PendingSegments = new();
	private readonly Dictionary<(int NodeId, RoadChunkKind Kind), PendingGroup> m_PendingJunctions = new();

	/// <summary>
	/// Junction layouts keyed by node, each with a hash of what it was computed from. Finding corners on curved
	/// edges is the one expensive step of a reconcile, and while a node is dragged only its own junction and its
	/// neighbours' change.
	/// </summary>
	private readonly Dictionary<int, (long Key, RoadJunctionLayout Layout)> m_JunctionCache = new();

	/// <summary>
	/// True while pieces are still queued for (re)generation.
	/// </summary>
	public bool IsRebuilding => m_Jobs.Count > 0;



	/// <summary>
	/// Asks for a reconcile pass on the next editor frame: the graph is compared against the pieces that exist and
	/// only what differs gets queued for regeneration.
	/// </summary>
	public void MarkDirty()
	{
		m_IsDirty = true;
	}



	protected override void OnEnabled()
	{
		MarkDirty();
	}



	/// <summary>
	/// Undo/redo restore the graph by deserializing into this component; the list setters catch most of it, this
	/// catches the rest (and inspector edits).
	/// </summary>
	protected override void OnValidate()
	{
		MarkDirty();
	}



	protected override void OnUpdate()
	{
		// Only the editor scene generates; in play mode the pieces are whatever was saved.
		if (!Scene.IsEditor)
			return;

		if (m_IsDirty)
		{
			m_IsDirty = false;
			Reconcile();
		}

		ProcessJobs();
	}



	public IEnumerable<RoadChunk> GetChunks()
	{
		foreach (var child in GameObject.Children)
		{
			if (!child.IsValid() || child.IsDestroyed)
				continue;

			var chunk = child.Components.Get<RoadChunk>();

			if (chunk is not null)
				yield return chunk;
		}
	}



	private RoadChunk FindChunk(int _SegmentId, int _ChunkIndex, RoadChunkKind _Kind)
	{
		return GetChunks().FirstOrDefault(chunk => !chunk.IsJunction && chunk.SegmentId == _SegmentId && chunk.ChunkIndex == _ChunkIndex && chunk.Kind == _Kind);
	}



	private RoadChunk FindJunctionChunk(int _NodeId, RoadChunkKind _Kind)
	{
		return GetChunks().FirstOrDefault(chunk => chunk.IsJunction && chunk.NodeId == _NodeId && chunk.Kind == _Kind);
	}



	/// <summary>
	/// Compares the graph with the pieces under this GameObject and queues work for everything that is missing,
	/// stale or built from different settings. Stateless on purpose: it copes with undo, reloads, hand deleted
	/// pieces and hot reloads alike, because the pieces themselves carry what they were built from.
	/// </summary>
	private void Reconcile()
	{
		PruneCurveTables();

		var segmentChunks = new Dictionary<int, List<RoadChunk>>();
		var junctionChunks = new Dictionary<(int NodeId, RoadChunkKind Kind), RoadChunk>();

		foreach (var chunk in GetChunks())
		{
			if (chunk.IsJunction)
			{
				// Two pieces for one junction can only be leftovers; keep one, the reconcile sorts out whether it is current.
				if (junctionChunks.TryGetValue((chunk.NodeId, chunk.Kind), out var duplicate))
					duplicate.GameObject.Destroy();

				junctionChunks[(chunk.NodeId, chunk.Kind)] = chunk;
				continue;
			}

			if (!segmentChunks.TryGetValue(chunk.SegmentId, out var list))
			{
				list = new List<RoadChunk>();
				segmentChunks[chunk.SegmentId] = list;
			}

			list.Add(chunk);
		}

		var layouts = ComputeJunctionLayouts();
		var dashPhases = ComputeDashPhases();
		var liveSegments = new HashSet<int>();
		var segmentBlends = new Dictionary<int, RoadBorderBuilder.SegmentBorderBlends>();
		var segmentTapers = new Dictionary<int, SegmentBorderTapers>();
		var segmentCrossings = new Dictionary<int, SegmentCrossings>();

		foreach (var segment in Segments)
		{
			var table = GetCurveTable(segment);

			if (table is null)
				continue;

			liveSegments.Add(segment.Id);

			var profile = ProfileOf(segment);
			float trimStart = layouts.GetValueOrDefault(segment.StartNodeId)?.FindArm(segment.Id)?.Trim ?? 0.0f;
			float trimEnd = layouts.GetValueOrDefault(segment.EndNodeId)?.FindArm(segment.Id)?.Trim ?? 0.0f;
			var caps = ComputeBorderCaps(segment, layouts);
			var blends = ComputeBorderBlends(segment, table, layouts);
			var taper = ComputeBorderTaper(segment, table);
			var crossings = ComputeCrossings(segment, table, layouts, trimStart, trimEnd);
			var lines = ComputeLineRange(table, profile, trimStart, trimEnd, crossings);
			var dash = dashPhases.GetValueOrDefault(segment.Id);

			var plans = PlanChunks(segment, table, trimStart, trimEnd, profile, caps, blends, taper, ComputeSurfaceSignature(segment, table.Curve, profile), lines, dash);
			long groupSignature = CombineSignatures(plans);

			segmentBlends[segment.Id] = blends;
			segmentTapers[segment.Id] = taper;
			segmentCrossings[segment.Id] = crossings;
			segmentChunks.TryGetValue(segment.Id, out var existing);

			if (IsSegmentUpToDate(existing, plans))
				continue;

			// Already queued with these exact inputs: let those jobs finish rather than doubling them.
			if (m_PendingSegments.TryGetValue(segment.Id, out var pending) && pending.Signature == groupSignature)
				continue;

			EnqueueSegment(segment, table, profile, plans, groupSignature, existing);
		}

		foreach (var (nodeId, layout) in layouts)
		{
			ReconcileJunctionPiece(nodeId, layout, RoadChunkKind.Surface, ComputeJunctionSurfaceSignature(layout), junctionChunks, null);

			bool anyBorder = layout.Arms.Any(arm => ProfileOf(arm.Segment).HasBorder);

			if (anyBorder)
			{
				// The junction's border runs continue the arms' chunks, so they take the same lifts and tapers those
				// were built with.
				var armBlends = new Dictionary<int, RoadBorderBuilder.SegmentBorderBlends>();
				var armTapers = new Dictionary<int, SegmentBorderTapers>();

				foreach (var arm in layout.Arms)
				{
					armBlends[arm.Segment.Id] = segmentBlends.GetValueOrDefault(arm.Segment.Id);
					armTapers[arm.Segment.Id] = segmentTapers.GetValueOrDefault(arm.Segment.Id);
				}

				ReconcileJunctionPiece(nodeId, layout, RoadChunkKind.Border, ComputeJunctionBorderSignature(layout, armBlends, armTapers), junctionChunks, armBlends, armTapers);
			}
			else if (junctionChunks.TryGetValue((nodeId, RoadChunkKind.Border), out var border))
			{
				border.GameObject.Destroy();
			}

			// The junction's crossings: one decal per arm that gets one at this end.
			var armCrossings = new Dictionary<int, float>();

			foreach (var arm in layout.Arms)
			{
				if (!segmentCrossings.TryGetValue(arm.Segment.Id, out var crossing))
					continue;

				if (arm.AtStart && crossing.AtStart)
					armCrossings[arm.Segment.Id] = crossing.StartCentre;
				else if (!arm.AtStart && crossing.AtEnd)
					armCrossings[arm.Segment.Id] = crossing.EndCentre;
			}

			if (armCrossings.Count > 0)
				ReconcileJunctionPiece(nodeId, layout, RoadChunkKind.Marking, ComputeJunctionMarkingSignature(layout, armCrossings), junctionChunks, null, null, armCrossings);
			else if (junctionChunks.TryGetValue((nodeId, RoadChunkKind.Marking), out var marking))
				marking.GameObject.Destroy();
		}

		// Whatever no longer belongs to anything in the graph goes, locked or not.
		foreach (var (segmentId, chunks) in segmentChunks)
		{
			if (liveSegments.Contains(segmentId))
				continue;

			foreach (var chunk in chunks)
				chunk.GameObject.Destroy();

			m_PendingSegments.Remove(segmentId);
		}

		foreach (var (key, chunk) in junctionChunks)
		{
			if (layouts.ContainsKey(key.NodeId))
				continue;

			chunk.GameObject.Destroy();
			m_PendingJunctions.Remove(key);
		}
	}



	private void ReconcileJunctionPiece(int _NodeId, RoadJunctionLayout _Layout, RoadChunkKind _Kind, long _Signature, Dictionary<(int NodeId, RoadChunkKind Kind), RoadChunk> _Existing, Dictionary<int, RoadBorderBuilder.SegmentBorderBlends> _ArmBlends, Dictionary<int, SegmentBorderTapers> _ArmTapers = null, Dictionary<int, float> _ArmCrossings = null)
	{
		var key = (_NodeId, _Kind);

		if (_Existing.TryGetValue(key, out var existing) && (existing.Locked || (existing.Signature == _Signature && !LacksDebugRecord(existing))))
			return;

		if (m_PendingJunctions.TryGetValue(key, out var pending) && pending.Signature == _Signature)
			return;

		m_Jobs.Enqueue(new RebuildJob(-1, _NodeId, _Kind, _Signature, default, null, RoadWidth.Uniform(0.0f), null, _Layout, _ArmBlends, _ArmTapers, _ArmCrossings));
		m_PendingJunctions[key] = new PendingGroup(_Signature, 1);
	}



	/// <summary>
	/// Where every segment sits in its run: the stretch of road carried on through pass-through nodes until a
	/// junction, a road end or a loop. Each run is walked once, from the lowest-numbered segment in it backwards
	/// to its head and then forwards to its tail, in that segment's direction, so every segment of the run agrees
	/// on where the run's distance starts and the dashes carry across the nodes.
	/// </summary>
	private Dictionary<int, RoadMarkingBuilder.DashPhase> ComputeDashPhases()
	{
		var phases = new Dictionary<int, RoadMarkingBuilder.DashPhase>();

		foreach (var seed in Segments.OrderBy(segment => segment.Id))
		{
			if (phases.ContainsKey(seed.Id))
				continue;

			var seen = new HashSet<int> { seed.Id };
			var run = new List<(RoadSegment Segment, bool Forward)>();

			// Back to the head: a road carrying on through the node runs with the run when it ends at that node.
			var segment = seed;
			int nodeId = seed.StartNodeId;

			while (RunContinuation(segment, nodeId) is { } previous && seen.Add(previous.Id))
			{
				bool forward = previous.EndNodeId == nodeId;

				run.Add((previous, forward));
				segment = previous;
				nodeId = forward ? previous.StartNodeId : previous.EndNodeId;
			}

			run.Reverse();
			run.Add((seed, true));

			// On to the tail.
			segment = seed;
			nodeId = seed.EndNodeId;

			while (RunContinuation(segment, nodeId) is { } next && seen.Add(next.Id))
			{
				bool forward = next.StartNodeId == nodeId;

				run.Add((next, forward));
				segment = next;
				nodeId = forward ? next.EndNodeId : next.StartNodeId;
			}

			float offset = 0.0f;

			foreach (var (member, forward) in run)
			{
				phases[member.Id] = new RoadMarkingBuilder.DashPhase(offset, !forward);
				offset += GetCurveTable(member)?.Length ?? 0.0f;
			}
		}

		return phases;
	}



	/// <summary>
	/// The road a segment carries straight on into at a pass-through node, or null at anything else.
	/// </summary>
	private RoadSegment RunContinuation(RoadSegment _Segment, int _NodeId)
	{
		if (GetDegree(_NodeId) != 2)
			return null;

		return GetSegmentsAt(_NodeId).FirstOrDefault(candidate => candidate.Id != _Segment.Id);
	}



	/// <summary>
	/// How a segment's border runs out at each end where the road type changes at a pass-through node: over the
	/// profile's transition length (at most half the road, so the two ends never overlap), towards the flat width
	/// of the road type on the other side. Junctions of three roads or more and road ends do not taper.
	/// </summary>
	public SegmentBorderTapers GetBorderTaper(RoadSegment _Segment)
	{
		var table = GetCurveTable(_Segment);

		return table is null ? default : ComputeBorderTaper(_Segment, table);
	}



	private SegmentBorderTapers ComputeBorderTaper(RoadSegment _Segment, RoadCurveTable _Table)
	{
		var profile = ProfileOf(_Segment);

		if (!profile.HasBorder)
			return default;

		float limit = _Table.Length * 0.5f;
		RoadBorderTaper both = default;

		if (profile.TransitionLength > 0.0f)
		{
			TaperAt(_Segment, _Segment.StartNodeId, profile, limit, out float startLength, out float startWidth, out bool startFlatten, out bool startSpill);
			TaperAt(_Segment, _Segment.EndNodeId, profile, limit, out float endLength, out float endWidth, out bool endFlatten, out bool endSpill);

			both = new RoadBorderTaper(startLength, startWidth, startFlatten, startSpill, endLength, endWidth, endFlatten, endSpill);
		}

		// At a nose, the border on the side facing the road this one parts from grows out of nothing instead.
		var left = both;
		var right = both;

		if (GoreLength(_Segment, _Table, _Segment.StartNodeId, true, limit) is { } startLeft)
			left = left with { StartLength = startLeft, StartWidth = 0.0f, StartFlatten = true, StartSpill = false, StartGore = true };

		if (GoreLength(_Segment, _Table, _Segment.StartNodeId, false, limit) is { } startRight)
			right = right with { StartLength = startRight, StartWidth = 0.0f, StartFlatten = true, StartSpill = false, StartGore = true };

		if (GoreLength(_Segment, _Table, _Segment.EndNodeId, true, limit) is { } endLeft)
			left = left with { EndLength = endLeft, EndWidth = 0.0f, EndFlatten = true, EndSpill = false, EndGore = true };

		if (GoreLength(_Segment, _Table, _Segment.EndNodeId, false, limit) is { } endRight)
			right = right with { EndLength = endRight, EndWidth = 0.0f, EndFlatten = true, EndSpill = false, EndGore = true };

		return new SegmentBorderTapers(left, right);
	}



	/// <summary>
	/// How far from a node the gap between this segment's edge on one side (its own left or right) and the edge of
	/// another road parting from the same point has opened enough for both roads' borders to fit side by side; null
	/// when no road parts from that edge there. Sampled along both roads at equal distances from the node, which
	/// is how two roads leaving a common tangent separate.
	/// </summary>
	private float? GoreLength(RoadSegment _Segment, RoadCurveTable _Table, int _NodeId, bool _Left, float _Limit)
	{
		const float step = 25.0f;

		if (GetDegree(_NodeId) < 3)
			return null;

		bool atStart = _Segment.StartNodeId == _NodeId;
		float own = ProfileOf(_Segment).FlatBorderWidth;
		var width = new RoadWidth(_Segment, _Table.Length);

		// This segment's edge point at a distance from the node, on the side asked for.
		Vector3 OwnEdge(float _FromNode)
		{
			float distance = atStart ? _FromNode : _Table.Length - _FromNode;

			RoadSurfaceBuilder.CrossSection(_Table, distance, width, 0.0f, out Vector3 leftEdge, out Vector3 rightEdge);

			return _Left ? leftEdge : rightEdge;
		}

		Vector3 start = OwnEdge(0.0f);

		foreach (var other in GetSegmentsAt(_NodeId))
		{
			if (other.Id == _Segment.Id)
				continue;

			var otherTable = GetCurveTable(other);

			if (otherTable is null)
				continue;

			bool otherAtStart = other.StartNodeId == _NodeId;
			var otherWidth = new RoadWidth(other, otherTable.Length);

			Vector3 OtherEdge(float _FromNode, bool _OtherLeft)
			{
				float distance = otherAtStart ? _FromNode : otherTable.Length - _FromNode;

				RoadSurfaceBuilder.CrossSection(otherTable, distance, otherWidth, 0.0f, out Vector3 leftEdge, out Vector3 rightEdge);

				return _OtherLeft ? leftEdge : rightEdge;
			}

			// The other road's edge that starts on ours, if either does.
			bool otherLeft;

			if (OtherEdge(0.0f, true).Distance(start) < 1.0f)
				otherLeft = true;
			else if (OtherEdge(0.0f, false).Distance(start) < 1.0f)
				otherLeft = false;
			else
				continue;

			// Parting means leaving the node the same way; a road turning off at an angle is a corner, not a gore.
			Vector3 ownWay = OwnEdge(step) - start;
			Vector3 otherWay = OtherEdge(step, otherLeft) - start;

			if (ownWay.IsNearZeroLength || otherWay.IsNearZeroLength || Vector3.Dot(ownWay.Normal, otherWay.Normal) < 0.7f)
				continue;

			float needed = own + ProfileOf(other).FlatBorderWidth;
			float reach = MathF.Min(_Limit, MathF.Min(_Table.Length, otherTable.Length));

			for (float fromNode = step; fromNode <= reach; fromNode += step)
			{
				if (OwnEdge(fromNode).Distance(OtherEdge(fromNode, otherLeft)) >= needed)
					return MathF.Max(step, fromNode);
			}

			return MathF.Max(step, reach);
		}

		return null;
	}



	/// <summary>
	/// A different kind of border on the other side flattens this one out on the way to the node; the same kind
	/// in another width just runs the width across. A country road on the other side spills its gravel over.
	/// </summary>
	private void TaperAt(RoadSegment _Segment, int _NodeId, RoadProfile _Profile, float _Limit, out float _Length, out float _Width, out bool _Flatten, out bool _Spill)
	{
		_Length = 0.0f;
		_Width = 0.0f;
		_Flatten = false;
		_Spill = false;

		if (GetDegree(_NodeId) != 2)
			return;

		var other = GetSegmentsAt(_NodeId).FirstOrDefault(candidate => candidate.Id != _Segment.Id);

		if (other is null)
			return;

		var otherProfile = ProfileOf(other);
		bool sameKind = otherProfile.Border == _Profile.Border;

		if (sameKind && MathF.Abs(otherProfile.FlatBorderWidth - _Profile.FlatBorderWidth) < 0.01f)
			return;

		_Length = MathF.Min(_Profile.TransitionLength, _Limit);
		_Width = otherProfile.FlatBorderWidth;
		_Flatten = !sameKind;
		_Spill = !sameKind && otherProfile.Border == RoadBorderStyle.CountrySide;
	}



	/// <summary>
	/// Lays out every junction. A road too short for the fillets at both its ends gets those fillets shrunk until
	/// their tangent points fit on it (a trim past the tangent point would send the corner geometry back over the
	/// road); only corners that cannot fit even sharp are clamped. Then the junction polygons are built.
	/// </summary>
	private Dictionary<int, RoadJunctionLayout> ComputeJunctionLayouts()
	{
		var radii = Nodes.ToDictionary(node => node.Id, node => MathF.Max(0.0f, node.CornerRadius));
		var layouts = BuildJunctionLayouts(radii);
		var shrink = new Dictionary<int, float>();

		foreach (var segment in Segments)
		{
			var table = GetCurveTable(segment);

			if (table is null)
				continue;

			var startArm = layouts.GetValueOrDefault(segment.StartNodeId)?.FindArm(segment.Id);
			var endArm = layouts.GetValueOrDefault(segment.EndNodeId)?.FindArm(segment.Id);
			float sharp = (startArm?.SharpTrim ?? 0.0f) + (endArm?.SharpTrim ?? 0.0f);
			float fillet = (startArm is null ? 0.0f : startArm.RawTrim - startArm.SharpTrim) + (endArm is null ? 0.0f : endArm.RawTrim - endArm.SharpTrim);
			float available = MathF.Max(0.0f, table.Length - MinChunkLength);

			if (fillet <= 0.0f || sharp + fillet <= available)
				continue;

			// The fillet's share of a trim is proportional to the radius, so this factor makes it fit exactly.
			float factor = Math.Clamp((available - sharp) / fillet, 0.0f, 1.0f);

			foreach (int nodeId in new[] { segment.StartNodeId, segment.EndNodeId })
			{
				if (layouts.ContainsKey(nodeId))
					shrink[nodeId] = MathF.Min(shrink.GetValueOrDefault(nodeId, 1.0f), factor);
			}
		}

		if (shrink.Count > 0)
		{
			foreach (var (nodeId, factor) in shrink)
				radii[nodeId] *= factor;

			layouts = BuildJunctionLayouts(radii);
		}

		// Last resort, for corners that do not fit even when sharp: share what road there is.
		foreach (var segment in Segments)
		{
			var table = GetCurveTable(segment);

			if (table is null)
				continue;

			var startArm = layouts.GetValueOrDefault(segment.StartNodeId)?.FindArm(segment.Id);
			var endArm = layouts.GetValueOrDefault(segment.EndNodeId)?.FindArm(segment.Id);
			float total = (startArm?.Trim ?? 0.0f) + (endArm?.Trim ?? 0.0f);
			float available = MathF.Max(0.0f, table.Length - MinChunkLength);

			if (total <= available || total <= 0.0f)
				continue;

			float scale = available / total;

			if (startArm is not null)
				startArm.Trim *= scale;

			if (endArm is not null)
				endArm.Trim *= scale;
		}

		foreach (var layout in layouts.Values)
			layout.BuildPolygon();

		return layouts;
	}



	/// <summary>
	/// The corner fold on one side of a segment at one of its nodes, and whether the segment is that fold's first
	/// arm (its left edge) or second (its right edge). Looking out of the node, a segment starting there has its
	/// left on the arm's left; one ending there has it on the arm's right.
	/// </summary>
	private static RoadJunctionLayout.CornerFold FindFold(RoadSegment _Segment, int _NodeId, bool _AtStart, bool _Left, Dictionary<int, RoadJunctionLayout> _Layouts, out bool _SideA)
	{
		_SideA = _AtStart == _Left;

		if (!_Layouts.TryGetValue(_NodeId, out var layout))
			return null;

		var path = _SideA
			? layout.CornerPaths.FirstOrDefault(candidate => candidate.ArmA.Segment.Id == _Segment.Id)
			: layout.CornerPaths.FirstOrDefault(candidate => candidate.ArmB.Segment.Id == _Segment.Id);

		return path?.Fold;
	}



	/// <summary>
	/// The junction layout of every node for the given fillet radii, from the cache where the inputs match.
	/// </summary>
	private Dictionary<int, RoadJunctionLayout> BuildJunctionLayouts(Dictionary<int, float> _Radii)
	{
		var layouts = new Dictionary<int, RoadJunctionLayout>();
		var liveNodes = new HashSet<int>();

		foreach (var node in Nodes)
		{
			liveNodes.Add(node.Id);

			float radius = _Radii[node.Id];
			long key = ComputeJunctionKey(node, radius);
			RoadJunctionLayout layout;

			// A cached layout is only as good as its references: undo and reloads swap the graph's objects.
			if (m_JunctionCache.TryGetValue(node.Id, out var cached) && cached.Key == key && (cached.Layout is null || cached.Layout.Refresh(this)))
			{
				layout = cached.Layout;
			}
			else
			{
				layout = RoadJunctionLayout.Create(this, node, SurfaceOffset, radius);
				m_JunctionCache[node.Id] = (key, layout);
			}

			if (layout is null)
				continue;

			layout.ResetTrims();
			layouts[node.Id] = layout;
		}

		foreach (int nodeId in m_JunctionCache.Keys.Where(id => !liveNodes.Contains(id)).ToList())
			m_JunctionCache.Remove(nodeId);

		return layouts;
	}



	/// <summary>
	/// Whether an end of a road asks for a pedestrian crossing: the road type has them, the road has not turned
	/// this end's off, and the end is a real junction (three roads or more; a bend has a layout too, but nobody
	/// crosses at a bend) that trims the road.
	/// </summary>
	private bool WantsCrossing(RoadSegment _Segment, int _NodeId, Dictionary<int, RoadJunctionLayout> _Layouts, float _Trim)
	{
		if (!ProfileOf(_Segment).Crossings || !_Segment.CrossingAt(_NodeId))
			return false;

		return _Layouts.TryGetValue(_NodeId, out var layout) && layout.Arms.Count >= 3 && _Trim > 0.01f;
	}



	/// <summary>
	/// Places a road's crossings on the stretch left between its junctions. Two crossings need room for both and
	/// a gap between; a shorter road between two junctions gets one crossing shared by both, halfway along; a road
	/// shorter than a crossing gets none.
	/// </summary>
	private SegmentCrossings ComputeCrossings(RoadSegment _Segment, RoadCurveTable _Table, Dictionary<int, RoadJunctionLayout> _Layouts, float _TrimStart, float _TrimEnd)
	{
		var profile = ProfileOf(_Segment);
		float depth = profile.CrossingDepth;
		float clear = _Table.Length - _TrimStart - _TrimEnd;
		bool wantStart = WantsCrossing(_Segment, _Segment.StartNodeId, _Layouts, _TrimStart);
		bool wantEnd = WantsCrossing(_Segment, _Segment.EndNodeId, _Layouts, _TrimEnd);

		if (clear < depth || (!wantStart && !wantEnd))
			return default;

		float startCentre = _TrimStart + depth * 0.5f;
		float endCentre = _Table.Length - _TrimEnd - depth * 0.5f;

		if (wantStart && wantEnd)
		{
			if (clear >= 2.0f * depth + profile.CrossingClearance)
				return new SegmentCrossings(true, startCentre, true, endCentre);

			return new SegmentCrossings(true, _TrimStart + clear * 0.5f, false, 0.0f);
		}

		return wantStart
			? new SegmentCrossings(true, startCentre, false, 0.0f)
			: new SegmentCrossings(false, 0.0f, true, endCentre);
	}



	/// <summary>
	/// Where the painted lines run: the stretch between the junctions, kept clear of the crossings.
	/// </summary>
	private static LineRange ComputeLineRange(RoadCurveTable _Table, RoadProfile _Profile, float _TrimStart, float _TrimEnd, in SegmentCrossings _Crossings)
	{
		float reach = _Profile.CrossingDepth * 0.5f + _Profile.CrossingClearance;
		float start = _TrimStart;
		float end = _Table.Length - _TrimEnd;
		float gapStart = 0.0f;
		float gapEnd = 0.0f;

		if (_Crossings.AtEnd)
			end = MathF.Min(end, _Crossings.EndCentre - reach);

		if (_Crossings.AtStart)
		{
			float before = _Crossings.StartCentre - reach;
			float after = _Crossings.StartCentre + reach;

			// A crossing at the very start just pushes the lines back; one further along cuts a gap in them.
			if (before <= start + 1.0f)
			{
				start = MathF.Max(start, after);
			}
			else
			{
				gapStart = before;
				gapEnd = after;
			}
		}

		return new LineRange(start, end, gapStart, gapEnd);
	}



	/// <summary>
	/// Which ends of a segment's borders nothing continues: a road end, a side of a junction with no corner on it,
	/// or a pass-through node where the road changes type or width.
	/// </summary>
	private BorderCaps ComputeBorderCaps(RoadSegment _Segment, Dictionary<int, RoadJunctionLayout> _Layouts)
	{
		var caps = BorderCaps.None;

		if (IsBorderOpen(_Segment, _Segment.StartNodeId, true, _Layouts, out bool startLeft, out bool startRight))
		{
			caps |= startLeft ? BorderCaps.StartLeft : BorderCaps.None;
			caps |= startRight ? BorderCaps.StartRight : BorderCaps.None;
		}

		if (IsBorderOpen(_Segment, _Segment.EndNodeId, false, _Layouts, out bool endLeft, out bool endRight))
		{
			caps |= endLeft ? BorderCaps.EndLeft : BorderCaps.None;
			caps |= endRight ? BorderCaps.EndRight : BorderCaps.None;
		}

		return caps;
	}



	/// <summary>
	/// A bridge segment's built range and ends. An end is closed off unless it is a pass-through node onto
	/// another bridge of the same style, width and border, where the two decks meet flush; there, only one of the
	/// two segments provides the node's railing post: the one leaving the node, or the lower id when both leave
	/// or both arrive.
	/// </summary>
	private RoadBridgeBuilder.BridgeSpan ComputeBridgeSpan(RoadSegment _Segment, float _RangeStart, float _RangeEnd)
	{
		var startNeighbour = DeckContinuation(_Segment, _Segment.StartNodeId);
		var endNeighbour = DeckContinuation(_Segment, _Segment.EndNodeId);

		bool postStart = startNeighbour is null || startNeighbour.EndNodeId == _Segment.StartNodeId || _Segment.Id < startNeighbour.Id;
		bool postEnd = endNeighbour is null || (endNeighbour.EndNodeId == _Segment.EndNodeId && _Segment.Id < endNeighbour.Id);

		return new RoadBridgeBuilder.BridgeSpan(_RangeStart, _RangeEnd, startNeighbour is null, postStart, endNeighbour is null, postEnd);
	}



	/// <summary>
	/// The bridge segment the deck carries straight on to at a node, or null where the deck ends.
	/// </summary>
	private RoadSegment DeckContinuation(RoadSegment _Segment, int _NodeId)
	{
		if (GetDegree(_NodeId) != 2)
			return null;

		var other = GetSegmentsAt(_NodeId).FirstOrDefault(candidate => candidate.Id != _Segment.Id);

		if (other?.Bridge is null || other.Bridge.ResourcePath != _Segment.Bridge.ResourcePath || !SameEdgesAt(_Segment, other, _NodeId))
			return null;

		bool sameExtent = MathF.Abs(RoadBridgeBuilder.DeckExtent(ProfileOf(other), other.Bridge) - RoadBridgeBuilder.DeckExtent(ProfileOf(_Segment), _Segment.Bridge)) < 0.01f;

		return sameExtent ? other : null;
	}



	/// <summary>
	/// The lift lines of a segment's two borders: at each end, the corner's height difference on that side (or
	/// nothing), run linearly along the road between the two.
	/// </summary>
	private RoadBorderBuilder.SegmentBorderBlends ComputeBorderBlends(RoadSegment _Segment, RoadCurveTable _Table, Dictionary<int, RoadJunctionLayout> _Layouts)
	{
		return new RoadBorderBuilder.SegmentBorderBlends(SideBlendFor(_Segment, _Table, true, _Layouts), SideBlendFor(_Segment, _Table, false, _Layouts));
	}



	private RoadBorderBuilder.SideBlend SideBlendFor(RoadSegment _Segment, RoadCurveTable _Table, bool _Left, Dictionary<int, RoadJunctionLayout> _Layouts)
	{
		bool hasStart = CornerLift(_Segment, _Segment.StartNodeId, true, _Left, _Layouts, out float startDistance, out float startDelta);
		bool hasEnd = CornerLift(_Segment, _Segment.EndNodeId, false, _Left, _Layouts, out float endDistance, out float endDelta);

		if (!hasStart && !hasEnd)
			return default;

		// An end with no corner on this side holds the border at road height from the segment's end.
		if (!hasStart)
		{
			startDistance = 0.0f;
			startDelta = 0.0f;
		}

		if (!hasEnd)
		{
			endDistance = _Table.Length;
			endDelta = 0.0f;
		}

		return new RoadBorderBuilder.SideBlend(true, startDistance, startDelta, endDistance, endDelta);
	}



	/// <summary>
	/// Where along the segment the corner's tangent point on this side is, and how much higher the corner sits
	/// than the road edge there; false when there is no inside corner on that side at that end.
	/// </summary>
	private static bool CornerLift(RoadSegment _Segment, int _NodeId, bool _AtStart, bool _Left, Dictionary<int, RoadJunctionLayout> _Layouts, out float _Distance, out float _Delta)
	{
		_Distance = 0.0f;
		_Delta = 0.0f;

		var fold = FindFold(_Segment, _NodeId, _AtStart, _Left, _Layouts, out bool sideA);

		if (fold is null || !_Layouts.TryGetValue(_NodeId, out var layout))
			return false;

		var arm = layout.FindArm(_Segment.Id);

		if (arm is null)
			return false;

		_Distance = arm.CurveDistance(sideA ? fold.TangentParamA : fold.TangentParamB);
		_Delta = fold.ApexHeight - (sideA ? fold.TangentHeightA : fold.TangentHeightB);

		return true;
	}



	private bool IsBorderOpen(RoadSegment _Segment, int _NodeId, bool _AtStart, Dictionary<int, RoadJunctionLayout> _Layouts, out bool _Left, out bool _Right)
	{
		int degree = GetDegree(_NodeId);

		if (degree <= 1)
		{
			_Left = true;
			_Right = true;
			return true;
		}

		if (_Layouts.TryGetValue(_NodeId, out var layout))
		{
			// A corner path starting on this arm continues its left border (looking out of the node); one ending on
			// it continues its right. Looking out of the node along a segment that ends there swaps the sides.
			bool armLeftContinued = layout.CornerPaths.Any(path => path.ArmA.Segment.Id == _Segment.Id);
			bool armRightContinued = layout.CornerPaths.Any(path => path.ArmB.Segment.Id == _Segment.Id);

			_Left = !(_AtStart ? armLeftContinued : armRightContinued);
			_Right = !(_AtStart ? armRightContinued : armLeftContinued);
			return _Left || _Right;
		}

		if (degree == 2)
		{
			var other = GetSegmentsAt(_NodeId).FirstOrDefault(candidate => candidate.Id != _Segment.Id);
			bool continuous = other is not null && RoadBorderBuilder.SameProfile(ProfileOf(other), ProfileOf(_Segment)) && SameEdgesAt(_Segment, other, _NodeId);

			_Left = !continuous;
			_Right = !continuous;
			return !continuous;
		}

		_Left = true;
		_Right = true;
		return true;
	}



	/// <summary>
	/// Splits a segment into chunks at fixed distances from its start, clipped by the junction trims at each end,
	/// with a border chunk over each stretch when the road type has borders. Anchoring the boundaries to the
	/// segment rather than to the trimmed length means a junction change at one end only touches the chunk next
	/// to it.
	/// </summary>
	private List<ChunkPlan> PlanChunks(RoadSegment _Segment, RoadCurveTable _Table, float _TrimStart, float _TrimEnd, RoadProfile _Profile, BorderCaps _Caps, in RoadBorderBuilder.SegmentBorderBlends _Blends, in SegmentBorderTapers _Taper, long _SurfaceSignature, in LineRange _Lines, in RoadMarkingBuilder.DashPhase _Dash)
	{
		float length = _Table.Length;
		int count = Math.Max(1, (int)MathF.Ceiling(length / ChunkLength));
		float usableEnd = length - _TrimEnd;
		var ranges = new List<(int Index, float Start, float End)>(count);

		for (int i = 0; i < count; i++)
		{
			float start = MathF.Max(length * i / count, _TrimStart);
			float end = MathF.Min(length * (i + 1) / count, usableEnd);

			if (end - start >= MinChunkLength)
				ranges.Add((i, start, end));
		}

		var plans = new List<ChunkPlan>(ranges.Count * 4);
		var borderHash = StableHash.Create();
		borderHash.Add(_SurfaceSignature);
		_Profile.AddBorderToHash(ref borderHash);
		_Blends.AddToHash(ref borderHash);
		_Taper.AddToHash(ref borderHash);
		borderHash.Add(DebugBorders ? 1 : 0);
		long borderSignature = borderHash.Value;

		// The deck follows the road and stands off its border, so it is stale when either changes; its posts and
		// supports are spread over the whole built range, so every piece is stale when an end moves.
		var bridge = _Segment.Bridge;
		var bridgeSpan = bridge is null ? default : ComputeBridgeSpan(_Segment, _TrimStart, usableEnd);
		long bridgeSignature = 0;

		if (bridge is not null)
		{
			var bridgeHash = StableHash.Create();
			bridgeHash.Add(_SurfaceSignature);
			bridgeHash.Add(bridge.ResourcePath);
			_Profile.AddBorderToHash(ref bridgeHash);
			bridge.AddToHash(ref bridgeHash);
			bridgeSpan.AddToHash(ref bridgeHash);
			bridgeSignature = bridgeHash.Value;
		}

		// Props are spread over the whole built range too, and stand on the border, so they follow its shape.
		var props = _Segment.Props;
		var propSpan = props is null ? default : new RoadPropBuilder.PropSpan(_TrimStart, usableEnd, GetDegree(_Segment.StartNodeId) >= 3, GetDegree(_Segment.EndNodeId) >= 3);
		long propSignature = 0;

		if (props is not null && !props.IsEmpty)
		{
			var propHash = StableHash.Create();
			propHash.Add(_SurfaceSignature);
			propHash.Add(props.ResourcePath);
			propHash.Add(bridge is null ? 0 : 1);
			_Profile.AddBorderToHash(ref propHash);
			props.AddToHash(ref propHash);
			propSpan.AddToHash(ref propHash);
			propSignature = propHash.Value;
		}

		bool markings = RoadMarkingBuilder.HasMarkings(_Segment, _Profile);
		var markingHash = StableHash.Create();
		markingHash.Add(_SurfaceSignature);
		markingHash.Add(_Segment.LaneCount);
		markingHash.Add(_Segment.ForwardLanes);
		_Lines.AddToHash(ref markingHash);
		_Dash.AddToHash(ref markingHash);
		_Profile.AddMarkingsToHash(ref markingHash);
		long markingSignature = markingHash.Value;

		for (int r = 0; r < ranges.Count; r++)
		{
			var (index, start, end) = ranges[r];

			var hash = StableHash.Create();
			hash.Add(_SurfaceSignature);
			hash.Add(index);
			hash.Add(count);
			hash.Add(start);
			hash.Add(end);

			plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Surface, BorderCaps.None, default, hash.Value));

			if (markings)
			{
				hash = StableHash.Create();
				hash.Add(markingSignature);
				hash.Add(index);
				hash.Add(count);
				hash.Add(start);
				hash.Add(end);

				plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Marking, BorderCaps.None, default, hash.Value, _Lines, Dash: _Dash));
			}

			if (bridge is not null)
			{
				// The piece works out for itself whether it is an outermost one, from its stretch against the span.
				hash = StableHash.Create();
				hash.Add(bridgeSignature);
				hash.Add(index);
				hash.Add(count);
				hash.Add(start);
				hash.Add(end);

				plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Bridge, BorderCaps.None, default, hash.Value, Bridge: bridgeSpan));
			}

			if (propSignature != 0)
			{
				hash = StableHash.Create();
				hash.Add(propSignature);
				hash.Add(index);
				hash.Add(count);
				hash.Add(start);
				hash.Add(end);

				plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Props, BorderCaps.None, default, hash.Value, Props: propSpan));
			}

			if (!_Profile.HasBorder)
				continue;

			// Only the outermost stretches have open ends to close.
			var caps = BorderCaps.None;

			if (r == 0)
				caps |= _Caps & (BorderCaps.StartLeft | BorderCaps.StartRight);

			if (r == ranges.Count - 1)
				caps |= _Caps & (BorderCaps.EndLeft | BorderCaps.EndRight);

			hash = StableHash.Create();
			hash.Add(borderSignature);
			hash.Add(index);
			hash.Add(count);
			hash.Add(start);
			hash.Add(end);
			hash.Add((int)caps);

			plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Border, caps, _Blends, hash.Value, default, _Taper));
		}

		return plans;
	}



	private static long CombineSignatures(List<ChunkPlan> _Plans)
	{
		var hash = StableHash.Create();
		hash.Add(_Plans.Count);

		foreach (var plan in _Plans)
			hash.Add(plan.Signature);

		return hash.Value;
	}



	private bool IsSegmentUpToDate(List<RoadChunk> _Existing, List<ChunkPlan> _Plans)
	{
		int have = _Existing?.Count ?? 0;

		if (have != _Plans.Count)
			return false;

		foreach (var plan in _Plans)
		{
			var chunk = _Existing.FirstOrDefault(candidate => candidate.ChunkIndex == plan.Index && candidate.Kind == plan.Kind);

			if (chunk is null)
				return false;

			// A locked chunk is exempt: it is deliberately stale.
			if (!chunk.Locked && (chunk.Signature != plan.Signature || LacksDebugRecord(chunk)))
				return false;
		}

		return true;
	}



	/// <summary>
	/// While the border debug overlay is on, a border piece without its record (built before the flag went on, or
	/// before a hot reload, which drops the records) is rebuilt to get one.
	/// </summary>
	private bool LacksDebugRecord(RoadChunk _Chunk)
	{
		return DebugBorders && _Chunk.Kind == RoadChunkKind.Border && (_Chunk.Debug is null || _Chunk.Debug.Text.Length == 0);
	}



	/// <summary>
	/// Throws away every unlocked piece's signature so the next reconcile rebuilds all of them, junction layouts
	/// included. Toggling the GameObject does not do this: pieces whose inputs have not changed are kept as they
	/// are, so after a generator change this is the way to see it everywhere.
	/// </summary>
	[Button("Regenerate All", "refresh")]
	public void RegenerateAll()
	{
		foreach (var chunk in GetChunks())
		{
			if (!chunk.Locked)
				chunk.Signature = 0;
		}

		m_JunctionCache.Clear();
		MarkDirty();
	}



	private long ComputeSurfaceSignature(RoadSegment _Segment, in RoadCurve _Curve, RoadProfile _Profile)
	{
		var hash = StableHash.Create();

		hash.Add(GeneratorVersion);
		hash.Add(_Curve.P0);
		hash.Add(_Curve.P1);
		hash.Add(_Curve.P2);
		hash.Add(_Curve.P3);
		new RoadWidth(_Segment, 1.0f).AddToHash(ref hash);
		hash.Add(SampleSpacing);
		hash.Add(ChunkLength);
		hash.Add(SurfaceOffset);
		_Profile.AddSurfaceToHash(ref hash);

		return hash.Value;
	}



	/// <summary>
	/// Everything a node's junction layout depends on: the node itself and the curve and width of each road at it.
	/// </summary>
	private long ComputeJunctionKey(RoadNode _Node, float _Radius)
	{
		var hash = StableHash.Create();

		hash.Add(GeneratorVersion);
		hash.Add(_Node.Position);
		hash.Add(_Radius);
		hash.Add(SurfaceOffset);

		foreach (var segment in GetSegmentsAt(_Node.Id).OrderBy(segment => segment.Id))
		{
			hash.Add(segment.Id);
			new RoadWidth(segment, 1.0f).AddToHash(ref hash);
			hash.Add(segment.Ramp ? 1 : 0);
			hash.Add(segment.StartNodeId == _Node.Id ? 1 : 0);

			if (!TryGetCurve(segment, out var curve))
				continue;

			hash.Add(curve.P0);
			hash.Add(curve.P1);
			hash.Add(curve.P2);
			hash.Add(curve.P3);
		}

		return hash.Value;
	}



	private static void AddPolygonToHash(RoadJunctionLayout _Layout, ref StableHash _Hash)
	{
		_Hash.Add(GeneratorVersion);
		_Hash.Add(_Layout.Centre);
		_Hash.Add(_Layout.Polygon.Count);

		foreach (var vertex in _Layout.Polygon)
			_Hash.Add(vertex);
	}



	private long ComputeJunctionSurfaceSignature(RoadJunctionLayout _Layout)
	{
		var hash = StableHash.Create();

		AddPolygonToHash(_Layout, ref hash);
		JunctionSurfaceProfile(_Layout).AddSurfaceToHash(ref hash);

		return hash.Value;
	}



	private long ComputeJunctionBorderSignature(RoadJunctionLayout _Layout, Dictionary<int, RoadBorderBuilder.SegmentBorderBlends> _ArmBlends, Dictionary<int, SegmentBorderTapers> _ArmTapers)
	{
		var hash = StableHash.Create();

		AddPolygonToHash(_Layout, ref hash);
		hash.Add(DebugBorders ? 1 : 0);

		foreach (var arm in _Layout.Arms)
		{
			var profile = ProfileOf(arm.Segment);

			hash.Add(arm.Segment.Id);
			hash.Add(arm.AtStart ? 1 : 0);
			hash.Add(arm.Length);
			profile.AddSurfaceToHash(ref hash);
			profile.AddBorderToHash(ref hash);
			_ArmBlends.GetValueOrDefault(arm.Segment.Id).AddToHash(ref hash);
			_ArmTapers.GetValueOrDefault(arm.Segment.Id).AddToHash(ref hash);
		}

		return hash.Value;
	}



	/// <summary>
	/// Everything the junction's crossings depend on: where each arm's trim is, its width and road type, and
	/// whether it wants one.
	/// </summary>
	private long ComputeJunctionMarkingSignature(RoadJunctionLayout _Layout, Dictionary<int, float> _ArmCrossings)
	{
		var hash = StableHash.Create();

		hash.Add(GeneratorVersion);
		hash.Add(_Layout.Node.Id);
		hash.Add(SurfaceOffset);

		foreach (var arm in _Layout.Arms)
		{
			hash.Add(arm.Segment.Id);

			if (!_ArmCrossings.TryGetValue(arm.Segment.Id, out float centre))
			{
				hash.Add(-1);
				continue;
			}

			hash.Add(centre);
			hash.Add(arm.AtStart ? 1 : 0);
			arm.Widths.AddToHash(ref hash);
			hash.Add(arm.Table.Curve.P0);
			hash.Add(arm.Table.Curve.P1);
			hash.Add(arm.Table.Curve.P2);
			hash.Add(arm.Table.Curve.P3);
			ProfileOf(arm.Segment).AddCrossingsToHash(ref hash);
		}

		return hash.Value;
	}



	/// <summary>
	/// The junction's crossings piece: a marker object holding one decal per arm that gets a crossing at this
	/// end, projected down onto the road, spanning it between its edges.
	/// </summary>
	private void CreateJunctionCrossings(RoadJunctionLayout _Layout, long _Signature, Dictionary<int, float> _ArmCrossings)
	{
		var piece = new GameObject(GameObject, true, $"Junction {_Layout.Node.Id} crossings");
		piece.Tags.Add(ChunkTag);

		var chunk = piece.AddComponent<RoadChunk>();
		chunk.SegmentId = -1;
		chunk.NodeId = _Layout.Node.Id;
		chunk.Kind = RoadChunkKind.Marking;
		chunk.ChunkIndex = 0;
		chunk.ChunkCount = 1;
		chunk.Signature = _Signature;

		var transform = WorldTransform;

		foreach (var arm in _Layout.Arms)
		{
			if (_ArmCrossings is null || !_ArmCrossings.TryGetValue(arm.Segment.Id, out float curveDistance))
				continue;

			var profile = ProfileOf(arm.Segment);
			var definition = profile.ResolvedCrossingDecal;

			if (definition is null || definition.Width <= 0.0f || definition.Height <= 0.0f)
				continue;

			// Centred between the road's edges at the crossing's place along it, projected straight down.
			RoadSurfaceBuilder.CrossSection(arm.Table, curveDistance, arm.Widths, SurfaceOffset, out Vector3 leftEdge, out Vector3 rightEdge);

			Vector3 position = transform.PointToWorld((leftEdge + rightEdge) * 0.5f);
			Vector3 along = transform.NormalToWorld(arm.Table.TangentAt(curveDistance) * (arm.AtStart ? 1.0f : -1.0f));
			float span = MathF.Max(1.0f, arm.Widths.WidthAt(curveDistance) - 2.0f * profile.CrossingInset);

			var holder = new GameObject(piece, true, $"Crossing {arm.Segment.Id}");
			holder.Tags.Add(ChunkTag);
			holder.WorldPosition = position;
			holder.WorldRotation = Rotation.LookAt(Vector3.Down, along);

			var decal = holder.AddComponent<Decal>();
			decal.Decals = new List<DecalDefinition> { definition };
			decal.Size = new Vector2(span / definition.Width, profile.CrossingDepth / definition.Height);
			decal.Scale = new ParticleFloat(1.0f, 1.0f);
			decal.Rotation = new ParticleFloat(0.0f, 0.0f);
			decal.Depth = 16.0f;
			decal.AttenuationAngle = 1.0f;
		}
	}



	/// <summary>
	/// A junction's surface takes after its main road: the widest arm.
	/// </summary>
	private RoadProfile JunctionSurfaceProfile(RoadJunctionLayout _Layout)
	{
		var main = _Layout.Arms.OrderByDescending(arm => arm.Widths.Base).ThenBy(arm => arm.Segment.Id).First();

		return ProfileOf(main.Segment);
	}



	private void EnqueueSegment(RoadSegment _Segment, RoadCurveTable _Table, RoadProfile _Profile, List<ChunkPlan> _Plans, long _GroupSignature, List<RoadChunk> _Existing)
	{
		// Chunks with no plan (swallowed by a junction, past a shorter road's end, or a border the road no longer
		// has) have no job coming to replace them.
		if (_Existing is not null)
		{
			foreach (var chunk in _Existing)
			{
				if (!chunk.Locked && _Plans.All(plan => plan.Index != chunk.ChunkIndex || plan.Kind != chunk.Kind))
					chunk.GameObject.Destroy();
			}
		}

		if (_Plans.Count == 0)
		{
			m_PendingSegments.Remove(_Segment.Id);
			return;
		}

		foreach (var plan in _Plans)
			m_Jobs.Enqueue(new RebuildJob(_Segment.Id, -1, plan.Kind, _GroupSignature, plan, _Table, new RoadWidth(_Segment, _Table.Length), _Profile, null, null, null, null));

		m_PendingSegments[_Segment.Id] = new PendingGroup(_GroupSignature, _Plans.Count);
	}



	private void ProcessJobs()
	{
		int budget = Math.Max(1, RebuildBudget);

		while (budget > 0 && m_Jobs.Count > 0)
		{
			var job = m_Jobs.Dequeue();

			if (job.NodeId >= 0)
			{
				var key = (job.NodeId, job.Kind);

				// Superseded by a newer layout of the same junction, or the junction is gone.
				if (!m_PendingJunctions.TryGetValue(key, out var pending) || pending.Signature != job.GroupSignature)
					continue;

				BuildJunctionPiece(job);
				budget--;

				m_PendingJunctions.Remove(key);
				continue;
			}

			// Superseded by a newer rebuild of the same segment, or the segment is gone.
			if (!m_PendingSegments.TryGetValue(job.SegmentId, out var pendingSegment) || pendingSegment.Signature != job.GroupSignature)
				continue;

			if (BuildChunk(job))
				budget--;

			if (pendingSegment.Remaining <= 1)
				m_PendingSegments.Remove(job.SegmentId);
			else
				m_PendingSegments[job.SegmentId] = pendingSegment with { Remaining = pendingSegment.Remaining - 1 };
		}
	}



	/// <summary>
	/// Returns false when there was nothing to do: the chunk in place was already built from these inputs (a
	/// junction change at the far end of the road requeues the whole road, but only the chunk next to it differs).
	/// </summary>
	private bool BuildChunk(in RebuildJob _Job)
	{
		var plan = _Job.Plan;
		var existing = FindChunk(_Job.SegmentId, plan.Index, plan.Kind);

		if (existing is not null && (existing.Locked || (existing.Signature == plan.Signature && !LacksDebugRecord(existing))))
			return false;

		PolygonMesh mesh;
		string name;
		BorderDebug debug = null;
		var placements = new List<(GameObject Prefab, Transform Transform)>();

		if (plan.Kind == RoadChunkKind.Border)
		{
			debug = DebugBorders ? new BorderDebug() : null;
			mesh = RoadBorderBuilder.BuildSegment(_Job.Table, plan.Start, plan.End, _Job.Width, _Job.Profile, SampleSpacing, SurfaceOffset, plan.Caps, plan.Blends, plan.Taper, debug);
			name = $"Road {_Job.SegmentId} border [{plan.Index + 1}/{plan.Count}]";
		}
		else if (plan.Kind == RoadChunkKind.Bridge)
		{
			var segment = FindSegment(_Job.SegmentId);

			if (segment?.Bridge is null)
				return false;

			var parts = new List<RoadBridgeBuilder.Placement>();
			mesh = RoadBridgeBuilder.Build(_Job.Table, plan.Start, plan.End, plan.Bridge, _Job.Width, _Job.Profile, segment.Bridge, SampleSpacing, SurfaceOffset, GroundBelow, parts);
			name = $"Road {_Job.SegmentId} bridge [{plan.Index + 1}/{plan.Count}]";

			foreach (var part in parts)
				placements.Add((part.Prefab, new Transform(part.Position, part.Rotation)));
		}
		else if (plan.Kind == RoadChunkKind.Props)
		{
			var segment = FindSegment(_Job.SegmentId);

			if (segment?.Props is null)
				return false;

			// Nothing to mesh: the piece is only the parent of its prefabs.
			var props = new List<RoadPropBuilder.Placement>();
			var context = new RoadPropBuilder.Context(_Job.Table, plan.Start, plan.End, plan.Props, _Job.SegmentId, _Job.Width, _Job.Profile, segment.Bridge is not null, SurfaceOffset, GroundAround);

			RoadPropBuilder.Build(context, segment.Props, props);
			mesh = null;
			name = $"Road {_Job.SegmentId} props [{plan.Index + 1}/{plan.Count}]";

			foreach (var prop in props)
				placements.Add((prop.Prefab, new Transform(prop.Position, prop.Rotation, prop.Scale)));
		}
		else if (plan.Kind == RoadChunkKind.Marking)
		{
			var segment = FindSegment(_Job.SegmentId);

			if (segment is null)
				return false;

			// Lines only where the chunk overlaps the stretches left clear by the crossings.
			mesh = new PolygonMesh();

			if (plan.Lines.HasGap)
			{
				RoadMarkingBuilder.Build(mesh, _Job.Table, MathF.Max(plan.Start, plan.Lines.Start), MathF.Min(plan.End, plan.Lines.GapStart), segment, _Job.Width, _Job.Profile, SurfaceOffset, plan.Dash);
				RoadMarkingBuilder.Build(mesh, _Job.Table, MathF.Max(plan.Start, plan.Lines.GapEnd), MathF.Min(plan.End, plan.Lines.End), segment, _Job.Width, _Job.Profile, SurfaceOffset, plan.Dash);
			}
			else
			{
				RoadMarkingBuilder.Build(mesh, _Job.Table, MathF.Max(plan.Start, plan.Lines.Start), MathF.Min(plan.End, plan.Lines.End), segment, _Job.Width, _Job.Profile, SurfaceOffset, plan.Dash);
			}

			name = $"Road {_Job.SegmentId} markings [{plan.Index + 1}/{plan.Count}]";
		}
		else
		{
			mesh = RoadSurfaceBuilder.Build(_Job.Table, plan.Start, plan.End, _Job.Width, SampleSpacing, _Job.Profile.RoadTextureRepeat, SurfaceOffset, _Job.Profile.ResolvedRoadMaterial);
			name = $"Road {_Job.SegmentId} [{plan.Index + 1}/{plan.Count}]";
		}

		var piece = CreateChunkObject(name, _Job.SegmentId, -1, plan.Kind, plan.Index, plan.Count, plan.Signature, mesh, debug);

		foreach (var (prefab, transform) in placements)
			prefab.Clone(transform, piece);

		// The new piece is in place before the old one goes, so the road never flickers.
		existing?.GameObject.Destroy();

		return true;
	}



	/// <summary>
	/// The height of whatever lies under a point of the network (its own pieces excepted), in network space, for
	/// bridge supports to land on: the search starts just below the point, so the deck itself is never it. Null
	/// over nothing.
	/// </summary>
	private float? GroundBelow(Vector3 _Local)
	{
		return TraceGround(_Local, -1.0f);
	}



	/// <summary>
	/// The height of the ground at a point of the network, for props to stand on: the search starts well above
	/// the point, so ground rising beside a road is found as well as ground falling away from it.
	/// </summary>
	private float? GroundAround(Vector3 _Local)
	{
		return TraceGround(_Local, 3000.0f);
	}



	private float? TraceGround(Vector3 _Local, float _StartAbove)
	{
		const float reach = 50000.0f;

		var transform = WorldTransform;
		Vector3 from = transform.PointToWorld(_Local) + Vector3.Up * _StartAbove;
		var trace = Scene.Trace.Ray(from, from + Vector3.Down * reach).WithoutTags(ChunkTag).Run();

		if (!trace.Hit)
			return null;

		return transform.PointToLocal(trace.EndPosition).z;
	}



	private void BuildJunctionPiece(in RebuildJob _Job)
	{
		var existing = FindJunctionChunk(_Job.NodeId, _Job.Kind);

		if (existing is not null && existing.Locked)
			return;

		PolygonMesh mesh;
		string name;
		BorderDebug debug = null;

		if (_Job.Kind == RoadChunkKind.Marking)
		{
			CreateJunctionCrossings(_Job.Junction, _Job.GroupSignature, _Job.ArmCrossings);
			existing?.GameObject.Destroy();
			return;
		}

		if (_Job.Kind == RoadChunkKind.Border)
		{
			var armBlends = _Job.ArmBlends;
			var armTapers = _Job.ArmTapers;

			debug = DebugBorders ? new BorderDebug() : null;
			mesh = RoadBorderBuilder.BuildJunction(_Job.Junction, ProfileOf,
				segment => armBlends is not null ? armBlends.GetValueOrDefault(segment.Id) : default,
				segment => armTapers is not null ? armTapers.GetValueOrDefault(segment.Id) : default,
				debug);
			name = $"Junction {_Job.NodeId} border";
		}
		else
		{
			var profile = JunctionSurfaceProfile(_Job.Junction);

			mesh = RoadJunctionBuilder.Build(_Job.Junction, profile.RoadTextureRepeat, profile.ResolvedRoadMaterial);
			name = $"Junction {_Job.NodeId}";
		}

		CreateChunkObject(name, -1, _Job.NodeId, _Job.Kind, 0, 1, _Job.GroupSignature, mesh, debug);

		existing?.GameObject.Destroy();
	}



	private GameObject CreateChunkObject(string _Name, int _SegmentId, int _NodeId, RoadChunkKind _Kind, int _Index, int _Count, long _Signature, PolygonMesh _Mesh, BorderDebug _Debug)
	{
		var child = new GameObject(GameObject, true, _Name);
		child.Tags.Add(ChunkTag);

		var chunk = child.AddComponent<RoadChunk>();
		chunk.SegmentId = _SegmentId;
		chunk.NodeId = _NodeId;
		chunk.Kind = _Kind;
		chunk.ChunkIndex = _Index;
		chunk.ChunkCount = _Count;
		chunk.Signature = _Signature;
		chunk.Debug = _Debug;

		if (_Debug is not null)
			DebugRevision++;

		// A piece of props has no geometry of its own; it is the parent of its prefabs.
		if (_Mesh is null)
			return child;

		var meshComponent = child.AddComponent<MeshComponent>();
		meshComponent.Mesh = _Mesh;
		meshComponent.SmoothingAngle = 40.0f;

		// Paint lies flat on the road: nothing to collide with, and its shadow would be a sliver hidden under it,
		// at the cost of a shadow pass.
		if (_Kind == RoadChunkKind.Marking)
		{
			meshComponent.Collision = MeshComponent.CollisionType.None;
			meshComponent.RenderType = ModelRenderer.ShadowRenderType.Off;
		}

		return child;
	}
}
