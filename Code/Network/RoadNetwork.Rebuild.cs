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
	private const int GeneratorVersion = 3;

	/// <summary>
	/// A stretch of road shorter than this is not worth a chunk (it happens when a junction swallows a whole chunk).
	/// </summary>
	private const float MinChunkLength = 1.0f;

	private readonly record struct ChunkPlan(int Index, int Count, float Start, float End, long Signature);

	private readonly record struct PendingGroup(long Signature, int Remaining);

	/// <summary>
	/// One piece to (re)build: a chunk of a segment, or, when <see cref="NodeId"/> is set, a junction.
	/// </summary>
	private readonly record struct RebuildJob(int SegmentId, int NodeId, long GroupSignature, ChunkPlan Plan, RoadCurveTable Table, float Width, RoadJunctionLayout Junction);

	private bool m_IsDirty;
	private readonly Queue<RebuildJob> m_Jobs = new();
	private readonly Dictionary<int, PendingGroup> m_PendingSegments = new();
	private readonly Dictionary<int, PendingGroup> m_PendingJunctions = new();

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



	private RoadChunk FindChunk(int _SegmentId, int _ChunkIndex)
	{
		return GetChunks().FirstOrDefault(chunk => !chunk.IsJunction && chunk.SegmentId == _SegmentId && chunk.ChunkIndex == _ChunkIndex);
	}



	private RoadChunk FindJunctionChunk(int _NodeId)
	{
		return GetChunks().FirstOrDefault(chunk => chunk.IsJunction && chunk.NodeId == _NodeId);
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
		var junctionChunks = new Dictionary<int, RoadChunk>();

		foreach (var chunk in GetChunks())
		{
			if (chunk.IsJunction)
			{
				// Two pieces for one junction can only be leftovers; keep one, the reconcile sorts out whether it is current.
				if (junctionChunks.TryGetValue(chunk.NodeId, out var duplicate))
					duplicate.GameObject.Destroy();

				junctionChunks[chunk.NodeId] = chunk;
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

			float trimStart = layouts.GetValueOrDefault(segment.StartNodeId)?.FindArm(segment.Id)?.Trim ?? 0.0f;
			float trimEnd = layouts.GetValueOrDefault(segment.EndNodeId)?.FindArm(segment.Id)?.Trim ?? 0.0f;
			var plans = PlanChunks(table, trimStart, trimEnd, ComputeSegmentSignature(segment, table.Curve));
			long groupSignature = CombineSignatures(plans);

			segmentChunks.TryGetValue(segment.Id, out var existing);

			if (IsSegmentUpToDate(existing, plans))
				continue;

			// Already queued with these exact inputs: let those jobs finish rather than doubling them.
			if (m_PendingSegments.TryGetValue(segment.Id, out var pending) && pending.Signature == groupSignature)
				continue;

			EnqueueSegment(segment, table, plans, groupSignature, existing);
		}

		foreach (var (nodeId, layout) in layouts)
		{
			long signature = ComputeJunctionSignature(layout);

			if (junctionChunks.TryGetValue(nodeId, out var existing) && (existing.Locked || existing.Signature == signature))
				continue;

			if (m_PendingJunctions.TryGetValue(nodeId, out var pending) && pending.Signature == signature)
				continue;

			m_Jobs.Enqueue(new RebuildJob(-1, nodeId, signature, default, null, 0.0f, layout));
			m_PendingJunctions[nodeId] = new PendingGroup(signature, 1);
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

		foreach (var (nodeId, chunk) in junctionChunks)
		{
			if (layouts.ContainsKey(nodeId))
				continue;

			chunk.GameObject.Destroy();
			m_PendingJunctions.Remove(nodeId);
		}
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
	/// Splits a segment into chunks at fixed distances from its start, clipped by the junction trims at each end.
	/// Anchoring the boundaries to the segment rather than to the trimmed length means a junction change at one
	/// end only touches the chunk next to it.
	/// </summary>
	private List<ChunkPlan> PlanChunks(RoadCurveTable _Table, float _TrimStart, float _TrimEnd, long _BaseSignature)
	{
		float length = _Table.Length;
		int count = Math.Max(1, (int)MathF.Ceiling(length / ChunkLength));
		float usableEnd = length - _TrimEnd;
		var plans = new List<ChunkPlan>(count);

		for (int i = 0; i < count; i++)
		{
			float start = MathF.Max(length * i / count, _TrimStart);
			float end = MathF.Min(length * (i + 1) / count, usableEnd);

			if (end - start < MinChunkLength)
				continue;

			var hash = StableHash.Create();
			hash.Add(_BaseSignature);
			hash.Add(i);
			hash.Add(count);
			hash.Add(start);
			hash.Add(end);

			plans.Add(new ChunkPlan(i, count, start, end, hash.Value));
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
			var chunk = _Existing.FirstOrDefault(candidate => candidate.ChunkIndex == plan.Index);

			if (chunk is null)
				return false;

			// A locked chunk is exempt: it is deliberately stale.
			if (!chunk.Locked && chunk.Signature != plan.Signature)
				return false;
		}

		return true;
	}



	private long ComputeSegmentSignature(RoadSegment _Segment, in RoadCurve _Curve)
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
		hash.Add(TextureRepeat);
		hash.Add(SurfaceOffset);
		hash.Add(RoadMaterial?.ResourcePath);

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



	private long ComputeJunctionSignature(RoadJunctionLayout _Layout)
	{
		var hash = StableHash.Create();

		hash.Add(GeneratorVersion);
		hash.Add(_Layout.Centre);
		hash.Add(_Layout.Polygon.Count);

		foreach (var vertex in _Layout.Polygon)
			hash.Add(vertex);

		hash.Add(TextureRepeat);
		hash.Add(RoadMaterial?.ResourcePath);

		return hash.Value;
	}



	private void EnqueueSegment(RoadSegment _Segment, RoadCurveTable _Table, List<ChunkPlan> _Plans, long _GroupSignature, List<RoadChunk> _Existing)
	{
		// Chunks with no plan (swallowed by a junction, or past a shorter road's end) have no job coming to replace them.
		if (_Existing is not null)
		{
			foreach (var chunk in _Existing)
			{
				if (!chunk.Locked && _Plans.All(plan => plan.Index != chunk.ChunkIndex))
					chunk.GameObject.Destroy();
			}
		}

		if (_Plans.Count == 0)
		{
			m_PendingSegments.Remove(_Segment.Id);
			return;
		}

		foreach (var plan in _Plans)
			m_Jobs.Enqueue(new RebuildJob(_Segment.Id, -1, _GroupSignature, plan, _Table, _Segment.Width, null));

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
				// Superseded by a newer layout of the same junction, or the junction is gone.
				if (!m_PendingJunctions.TryGetValue(job.NodeId, out var pending) || pending.Signature != job.GroupSignature)
					continue;

				BuildJunction(job);
				budget--;

				m_PendingJunctions.Remove(job.NodeId);
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
		var existing = FindChunk(_Job.SegmentId, plan.Index);

		if (existing is not null && (existing.Locked || existing.Signature == plan.Signature))
			return false;

		var mesh = RoadSurfaceBuilder.Build(_Job.Table, plan.Start, plan.End, _Job.Width, SampleSpacing, TextureRepeat, SurfaceOffset, SurfaceMaterial);

		CreateChunkObject($"Road {_Job.SegmentId} [{plan.Index + 1}/{plan.Count}]", _Job.SegmentId, -1, plan.Index, plan.Count, plan.Signature, mesh);

		// The new piece is in place before the old one goes, so the road never flickers.
		existing?.GameObject.Destroy();

		return true;
	}



	private void BuildJunction(in RebuildJob _Job)
	{
		var existing = FindJunctionChunk(_Job.NodeId);

		if (existing is not null && existing.Locked)
			return;

		var mesh = RoadJunctionBuilder.Build(_Job.Junction, TextureRepeat, SurfaceMaterial);

		CreateChunkObject($"Junction {_Job.NodeId}", -1, _Job.NodeId, 0, 1, _Job.GroupSignature, mesh);

		existing?.GameObject.Destroy();
	}



	private Material SurfaceMaterial => RoadMaterial ?? Material.Load("roadtool2/materials/asphalt.vmat");



	private void CreateChunkObject(string _Name, int _SegmentId, int _NodeId, int _Index, int _Count, long _Signature, PolygonMesh _Mesh)
	{
		var child = new GameObject(GameObject, true, _Name);
		child.Tags.Add(ChunkTag);

		var chunk = child.AddComponent<RoadChunk>();
		chunk.SegmentId = _SegmentId;
		chunk.NodeId = _NodeId;
		chunk.ChunkIndex = _Index;
		chunk.ChunkCount = _Count;
		chunk.Signature = _Signature;

		var meshComponent = child.AddComponent<MeshComponent>();
		meshComponent.Mesh = _Mesh;
		meshComponent.SmoothingAngle = 40.0f;
	}
}
