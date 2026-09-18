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
	private const int GeneratorVersion = 5;

	/// <summary>
	/// A stretch of road shorter than this is not worth a chunk (it happens when a junction swallows a whole chunk).
	/// </summary>
	private const float MinChunkLength = 1.0f;

	private readonly record struct ChunkPlan(int Index, int Count, float Start, float End, RoadChunkKind Kind, BorderCaps Caps, long Signature);

	private readonly record struct PendingGroup(long Signature, int Remaining);

	/// <summary>
	/// One piece to (re)build: a chunk of a segment, or, when <see cref="NodeId"/> is set, a piece of a junction.
	/// </summary>
	private readonly record struct RebuildJob(int SegmentId, int NodeId, RoadChunkKind Kind, long GroupSignature, ChunkPlan Plan, RoadCurveTable Table, float Width, RoadProfile Profile, RoadJunctionLayout Junction);

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
		var liveSegments = new HashSet<int>();

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
			var plans = PlanChunks(table, trimStart, trimEnd, profile, caps, ComputeSurfaceSignature(segment, table.Curve, profile));
			long groupSignature = CombineSignatures(plans);

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
			ReconcileJunctionPiece(nodeId, layout, RoadChunkKind.Surface, ComputeJunctionSurfaceSignature(layout), junctionChunks);

			bool anyBorder = layout.Arms.Any(arm => ProfileOf(arm.Segment).HasBorder);

			if (anyBorder)
				ReconcileJunctionPiece(nodeId, layout, RoadChunkKind.Border, ComputeJunctionBorderSignature(layout), junctionChunks);
			else if (junctionChunks.TryGetValue((nodeId, RoadChunkKind.Border), out var border))
				border.GameObject.Destroy();
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



	private void ReconcileJunctionPiece(int _NodeId, RoadJunctionLayout _Layout, RoadChunkKind _Kind, long _Signature, Dictionary<(int NodeId, RoadChunkKind Kind), RoadChunk> _Existing)
	{
		var key = (_NodeId, _Kind);

		if (_Existing.TryGetValue(key, out var existing) && (existing.Locked || existing.Signature == _Signature))
			return;

		if (m_PendingJunctions.TryGetValue(key, out var pending) && pending.Signature == _Signature)
			return;

		m_Jobs.Enqueue(new RebuildJob(-1, _NodeId, _Kind, _Signature, default, null, 0.0f, null, _Layout));
		m_PendingJunctions[key] = new PendingGroup(_Signature, 1);
	}



	/// <summary>
	/// Lays out every junction, then makes sure no segment is trimmed by its two ends into nothing (two junctions
	/// right next to each other share what little road there is), and only then builds the junction polygons.
	/// </summary>
	private Dictionary<int, RoadJunctionLayout> ComputeJunctionLayouts()
	{
		var layouts = new Dictionary<int, RoadJunctionLayout>();
		var liveNodes = new HashSet<int>();

		foreach (var node in Nodes)
		{
			liveNodes.Add(node.Id);

			long key = ComputeJunctionKey(node);
			RoadJunctionLayout layout;

			if (m_JunctionCache.TryGetValue(node.Id, out var cached) && cached.Key == key)
			{
				layout = cached.Layout;
			}
			else
			{
				layout = RoadJunctionLayout.Create(this, node, SurfaceOffset);
				m_JunctionCache[node.Id] = (key, layout);
			}

			if (layout is null)
				continue;

			layout.ResetTrims();
			layouts[node.Id] = layout;
		}

		foreach (int nodeId in m_JunctionCache.Keys.Where(id => !liveNodes.Contains(id)).ToList())
			m_JunctionCache.Remove(nodeId);

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
			bool armLeftContinued = layout.CornerPaths.Any(path => path.ArmA.Segment == _Segment);
			bool armRightContinued = layout.CornerPaths.Any(path => path.ArmB.Segment == _Segment);

			_Left = !(_AtStart ? armLeftContinued : armRightContinued);
			_Right = !(_AtStart ? armRightContinued : armLeftContinued);
			return _Left || _Right;
		}

		if (degree == 2)
		{
			var other = GetSegmentsAt(_NodeId).FirstOrDefault(candidate => candidate != _Segment);
			bool continuous = other is not null && ReferenceEquals(ProfileOf(other), ProfileOf(_Segment)) && other.Width == _Segment.Width;

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
	private List<ChunkPlan> PlanChunks(RoadCurveTable _Table, float _TrimStart, float _TrimEnd, RoadProfile _Profile, BorderCaps _Caps, long _SurfaceSignature)
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

		var plans = new List<ChunkPlan>(ranges.Count * 2);
		var borderHash = StableHash.Create();
		borderHash.Add(_SurfaceSignature);
		_Profile.AddBorderToHash(ref borderHash);
		long borderSignature = borderHash.Value;

		for (int r = 0; r < ranges.Count; r++)
		{
			var (index, start, end) = ranges[r];

			var hash = StableHash.Create();
			hash.Add(_SurfaceSignature);
			hash.Add(index);
			hash.Add(count);
			hash.Add(start);
			hash.Add(end);

			plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Surface, BorderCaps.None, hash.Value));

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

			plans.Add(new ChunkPlan(index, count, start, end, RoadChunkKind.Border, caps, hash.Value));
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



	private static bool IsSegmentUpToDate(List<RoadChunk> _Existing, List<ChunkPlan> _Plans)
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
			if (!chunk.Locked && chunk.Signature != plan.Signature)
				return false;
		}

		return true;
	}



	private long ComputeSurfaceSignature(RoadSegment _Segment, in RoadCurve _Curve, RoadProfile _Profile)
	{
		var hash = StableHash.Create();

		hash.Add(GeneratorVersion);
		hash.Add(_Curve.P0);
		hash.Add(_Curve.P1);
		hash.Add(_Curve.P2);
		hash.Add(_Curve.P3);
		hash.Add(_Segment.Width);
		hash.Add(SampleSpacing);
		hash.Add(ChunkLength);
		hash.Add(SurfaceOffset);
		_Profile.AddSurfaceToHash(ref hash);

		return hash.Value;
	}



	/// <summary>
	/// Everything a node's junction layout depends on: the node itself and the curve and width of each road at it.
	/// </summary>
	private long ComputeJunctionKey(RoadNode _Node)
	{
		var hash = StableHash.Create();

		hash.Add(GeneratorVersion);
		hash.Add(_Node.Position);
		hash.Add(_Node.CornerRadius);
		hash.Add(SurfaceOffset);

		foreach (var segment in GetSegmentsAt(_Node.Id).OrderBy(segment => segment.Id))
		{
			hash.Add(segment.Id);
			hash.Add(segment.Width);
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



	private long ComputeJunctionBorderSignature(RoadJunctionLayout _Layout)
	{
		var hash = StableHash.Create();

		AddPolygonToHash(_Layout, ref hash);

		foreach (var arm in _Layout.Arms)
		{
			var profile = ProfileOf(arm.Segment);

			hash.Add(arm.Segment.Id);
			hash.Add(arm.AtStart ? 1 : 0);
			profile.AddSurfaceToHash(ref hash);
			profile.AddBorderToHash(ref hash);
		}

		return hash.Value;
	}



	/// <summary>
	/// A junction's surface takes after its main road: the widest arm.
	/// </summary>
	private RoadProfile JunctionSurfaceProfile(RoadJunctionLayout _Layout)
	{
		var main = _Layout.Arms.OrderByDescending(arm => arm.Width).ThenBy(arm => arm.Segment.Id).First();

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
			m_Jobs.Enqueue(new RebuildJob(_Segment.Id, -1, plan.Kind, _GroupSignature, plan, _Table, _Segment.Width, _Profile, null));

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

		if (existing is not null && (existing.Locked || existing.Signature == plan.Signature))
			return false;

		PolygonMesh mesh;
		string name;

		if (plan.Kind == RoadChunkKind.Border)
		{
			mesh = RoadBorderBuilder.BuildSegment(_Job.Table, plan.Start, plan.End, _Job.Width, _Job.Profile, SampleSpacing, SurfaceOffset, plan.Caps);
			name = $"Road {_Job.SegmentId} border [{plan.Index + 1}/{plan.Count}]";
		}
		else
		{
			mesh = RoadSurfaceBuilder.Build(_Job.Table, plan.Start, plan.End, _Job.Width, SampleSpacing, _Job.Profile.RoadTextureRepeat, SurfaceOffset, _Job.Profile.ResolvedRoadMaterial);
			name = $"Road {_Job.SegmentId} [{plan.Index + 1}/{plan.Count}]";
		}

		CreateChunkObject(name, _Job.SegmentId, -1, plan.Kind, plan.Index, plan.Count, plan.Signature, mesh);

		// The new piece is in place before the old one goes, so the road never flickers.
		existing?.GameObject.Destroy();

		return true;
	}



	private void BuildJunctionPiece(in RebuildJob _Job)
	{
		var existing = FindJunctionChunk(_Job.NodeId, _Job.Kind);

		if (existing is not null && existing.Locked)
			return;

		PolygonMesh mesh;
		string name;

		if (_Job.Kind == RoadChunkKind.Border)
		{
			mesh = RoadBorderBuilder.BuildJunction(_Job.Junction, ProfileOf);
			name = $"Junction {_Job.NodeId} border";
		}
		else
		{
			var profile = JunctionSurfaceProfile(_Job.Junction);

			mesh = RoadJunctionBuilder.Build(_Job.Junction, profile.RoadTextureRepeat, profile.ResolvedRoadMaterial);
			name = $"Junction {_Job.NodeId}";
		}

		CreateChunkObject(name, -1, _Job.NodeId, _Job.Kind, 0, 1, _Job.GroupSignature, mesh);

		existing?.GameObject.Destroy();
	}



	private void CreateChunkObject(string _Name, int _SegmentId, int _NodeId, RoadChunkKind _Kind, int _Index, int _Count, long _Signature, PolygonMesh _Mesh)
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

		var meshComponent = child.AddComponent<MeshComponent>();
		meshComponent.Mesh = _Mesh;
		meshComponent.SmoothingAngle = 40.0f;
	}
}
