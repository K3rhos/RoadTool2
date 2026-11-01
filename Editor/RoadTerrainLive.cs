using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Live Update: keeps the terrain matching the roads while they are edited. A moment after the roads change, and
/// every so often while a drag goes on, the patch of terrain round whatever changed is shaped again (and painted
/// again, when a paint material is set) through each terrain's road layer, from the ground the layer remembers.
/// Nothing is recorded for undo: the terrain follows the roads, so taking a road edit back takes the terrain back
/// with it.
/// </summary>
internal static class RoadTerrainLive
{
	/// <summary>
	/// How long, at least, between two updates while the roads keep changing, in seconds.
	/// </summary>
	private const float Interval = 0.1f;

	/// <summary>
	/// How long the terrain has to stay as it is before the rest of what depends on it (its clutter) is told.
	/// </summary>
	private const float Settle = 0.5f;

	/// <summary>
	/// The same for a terrain whose collider is slow to bring up to date (see <see cref="RoadTerrainLayers.Settle"/>):
	/// it waits until the edits have stopped, rather than for the pause between two of them.
	/// </summary>
	private const float SlowSettle = 2.0f;

	/// <summary>
	/// What Live Update knows a terrain was last laid with.
	/// </summary>
	private sealed class Laid
	{
		public RoadTerrainLayer Layer;
		public int Generation;
		public long Placement;

		/// <summary>
		/// What the terrain was last shaped and painted with. Null until known: whatever the roads are at the next
		/// update is then taken as what it holds.
		/// </summary>
		public List<RoadTerrainFootprint> Shaped;
		public List<RoadTerrainFootprint> Painted;

		/// <summary>
		/// The whole terrain to be laid in again at the next update, not only round what changed.
		/// </summary>
		public bool Full;

		/// <summary>
		/// What changed in each map since the terrain's dependants were last told.
		/// </summary>
		public RectInt? Heights;
		public RectInt? Paint;
	}

	private static RoadNetwork s_Network;
	private static bool s_Live;
	private static int s_Revision = -1;
	private static long s_Settings;
	private static float s_LastUpdate;
	private static float s_LastChange;
	private static readonly Dictionary<Terrain, Laid> s_Laid = new();



	/// <summary>
	/// Starts over, taking whatever the terrains hold as matching the roads as they are: for the Terrain tool, once
	/// it has laid everything in itself.
	/// </summary>
	public static void Forget()
	{
		SettleAll();

		s_Network = null;
		s_Laid.Clear();
	}



	[EditorEvent.Frame]
	private static void Frame()
	{
		var scene = SceneEditorSession.Active?.Scene;
		var network = scene.IsValid() && scene.IsEditor ? scene.Get<RoadNetwork>() : null;

		if (network != s_Network)
		{
			// Another scene, or a network seen for the first time: its terrains are taken as matching it.
			SettleAll();
			s_Laid.Clear();

			s_Network = network;
			s_Live = network.IsValid() && network.TerrainLiveUpdate;
			s_Revision = -1;
		}

		if (!network.IsValid())
			return;

		if (!network.TerrainLiveUpdate)
		{
			if (s_Live)
			{
				SettleAll();
				s_Laid.Clear();
				s_Live = false;
			}

			return;
		}

		// Switched on by an undo or a redo (the Terrain tool lays everything in itself, then starts over): the roads
		// may have moved while it was off, so every terrain is laid in again.
		bool switchedOn = !s_Live;

		s_Live = true;

		var terrains = RoadTerrainLayers.TerrainsIn(scene);
		bool pending = switchedOn || network.GraphRevision != s_Revision || Settings(network) != s_Settings;

		foreach (var terrain in s_Laid.Keys.ToList())
		{
			if (!terrains.Contains(terrain))
				s_Laid.Remove(terrain);
		}

		foreach (var terrain in terrains)
		{
			var layer = RoadTerrainLayers.LayerOf(terrain, out _);

			if (layer is null)
			{
				s_Laid.Remove(terrain);
				continue;
			}

			long placement = Placement(terrain);

			if (s_Laid.TryGetValue(terrain, out var laid) && laid.Layer == layer && laid.Generation == layer.Generation && laid.Placement == placement)
			{
				laid.Full |= switchedOn;
				pending |= laid.Full || laid.Shaped is null;
				continue;
			}

			// New to Live Update, started again, or moved or resized under the roads. A layer holding nothing has the
			// roads laid into it, and so has a terrain the roads now lie over somewhere else; one holding something
			// is taken as matching them.
			bool moved = laid is not null && laid.Layer == layer && laid.Generation == layer.Generation;
			bool empty = layer.HeightSamples + layer.PaintTexels == 0;

			s_Laid[terrain] = new Laid
			{
				Layer = layer,
				Generation = layer.Generation,
				Placement = placement,
				Full = switchedOn || moved || empty,
				Heights = laid?.Heights,
				Paint = laid?.Paint
			};

			pending = true;
		}

		float now = RealTime.Now;

		if (pending && now - s_LastUpdate >= Interval)
		{
			s_LastUpdate = now;
			Update(network);
		}

		// Once the terrain has stayed as it is for a moment, the rest of what depends on it is told.
		if (now - s_LastChange >= SettleDelay())
			SettleAll();
	}



	/// <summary>
	/// How long the terrains have to stay as they are before they are settled: longer when one of them waiting has a
	/// slow collider.
	/// </summary>
	private static float SettleDelay()
	{
		foreach (var (terrain, laid) in s_Laid)
		{
			if (laid.Heights is not null && RoadTerrainLayers.HasSlowCollider(terrain))
				return SlowSettle;
		}

		return Settle;
	}



	/// <summary>
	/// Lays in again whatever changed since the last update, on every terrain Live Update keeps.
	/// </summary>
	private static void Update(RoadNetwork _Network)
	{
		s_Revision = _Network.GraphRevision;
		s_Settings = Settings(_Network);

		if (s_Laid.Count == 0)
			return;

		var shapers = RoadTerrainConformer.Collect(_Network);
		var shaped = shapers.Select(shaper => shaper.Footprint).ToList();
		var strokes = _Network.TerrainPaintMaterial is null ? null : RoadTerrainPainter.Collect(_Network);
		var painted = strokes?.Select(stroke => stroke.Footprint).ToList();
		bool changed = false;

		foreach (var (terrain, laid) in s_Laid)
		{
			if (laid.Full || laid.Shaped is not null)
			{
				var clip = laid.Full ? null : Dirty(terrain, laid.Shaped, shaped);

				if ((clip is null || clip.Count > 0) && RoadTerrainConformer.Conform(terrain, laid.Layer, _Network, shapers, clip, null, out RectInt region, out _, out _) > 0)
				{
					laid.Heights = Union(laid.Heights, region);
					changed = true;
				}
			}

			laid.Shaped = shaped;

			// Without a material to paint with, what was painted stays as it is; once one is set, everything not
			// known to be painted with it is.
			if (painted is null)
			{
				laid.Painted ??= new List<RoadTerrainFootprint>();
			}
			else
			{
				if (laid.Full || laid.Painted is not null)
				{
					var clip = laid.Full ? null : Dirty(terrain, laid.Painted, painted);

					if ((clip is null || clip.Count > 0) && RoadTerrainPainter.Paint(terrain, laid.Layer, _Network, strokes, clip, null, out RectInt region, out _, out _) > 0)
					{
						laid.Paint = Union(laid.Paint, region);
						changed = true;
					}
				}

				laid.Painted = painted;
			}

			laid.Full = false;
		}

		if (!changed)
			return;

		s_LastChange = RealTime.Now;

		// The road layers live in the scene, and the terrains are saved along with it.
		SceneEditorSession.Active.HasUnsavedChanges = true;
	}



	/// <summary>
	/// The rectangles of a terrain's grid to lay in again: under everything that is new or gone, which is also what
	/// a road that moved or changed is, so the ground is redone both where it was and where it is now. Whatever is
	/// still the same is matched off by its signature.
	/// </summary>
	private static List<RectInt> Dirty(Terrain _Terrain, List<RoadTerrainFootprint> _Before, List<RoadTerrainFootprint> _After)
	{
		var unmatched = new Dictionary<long, int>();

		foreach (var footprint in _Before)
			unmatched[footprint.Signature] = unmatched.GetValueOrDefault(footprint.Signature) + 1;

		var rects = new List<RectInt>();

		foreach (var footprint in _After)
		{
			if (unmatched.TryGetValue(footprint.Signature, out int count) && count > 0)
				unmatched[footprint.Signature] = count - 1;
			else
				Add(footprint);
		}

		foreach (var footprint in _Before)
		{
			if (!unmatched.TryGetValue(footprint.Signature, out int count) || count == 0)
				continue;

			unmatched[footprint.Signature] = count - 1;
			Add(footprint);
		}

		return Merge(rects);

		void Add(RoadTerrainFootprint _Footprint)
		{
			if (RoadTerrainLayers.ToGrid(_Terrain, _Footprint.Min, _Footprint.Max) is RectInt rect)
				rects.Add(rect);
		}
	}



	/// <summary>
	/// Joins rectangles that overlap into the one round both, until none do, so no sample is laid twice.
	/// </summary>
	private static List<RectInt> Merge(List<RectInt> _Rects)
	{
		for (int i = 0; i < _Rects.Count; i++)
		{
			for (int j = i + 1; j < _Rects.Count; j++)
			{
				if (!RoadTerrainLayers.Overlap(_Rects[i], _Rects[j]))
					continue;

				_Rects[i] = Union(_Rects[i], _Rects[j]);
				_Rects.RemoveAt(j);

				// The bigger rectangle may now reach ones already passed: look at all of them again.
				i = -1;
				break;
			}
		}

		return _Rects;
	}



	private static RectInt Union(RectInt _A, RectInt _B)
	{
		int left = Math.Min(_A.Left, _B.Left);
		int top = Math.Min(_A.Top, _B.Top);
		int right = Math.Max(_A.Left + _A.Width, _B.Left + _B.Width);
		int bottom = Math.Max(_A.Top + _A.Height, _B.Top + _B.Height);

		return new RectInt(left, top, right - left, bottom - top);
	}



	private static RectInt? Union(RectInt? _A, RectInt _B)
	{
		return _A is RectInt a ? Union(a, _B) : _B;
	}



	/// <summary>
	/// Brings the rest of each terrain up to date with what Live Update changed in it, once the edits settle: its
	/// collider, where that was too slow to follow every change, and what else depends on it (its clutter), which
	/// would be sent the whole terrain again each time (see <see cref="RoadTerrainLayers.Settle"/>).
	/// </summary>
	private static void SettleAll()
	{
		foreach (var (terrain, laid) in s_Laid)
		{
			if (laid.Heights is null && laid.Paint is null)
				continue;

			if (terrain.IsValid() && terrain.Storage is not null)
			{
				Terrain.SyncFlags flags = default;
				RectInt? region = null;

				if (laid.Heights is RectInt heights)
				{
					flags |= Terrain.SyncFlags.Height;
					region = heights;
				}

				if (laid.Paint is RectInt paint)
				{
					flags |= Terrain.SyncFlags.Control;
					region = Union(region, paint);
				}

				RoadTerrainLayers.Settle(terrain, flags, region.Value);
			}

			laid.Heights = null;
			laid.Paint = null;
		}
	}



	/// <summary>
	/// Everything outside the graph that what the roads do to the terrain depends on: the terrain settings, and where
	/// the network is.
	/// </summary>
	private static long Settings(RoadNetwork _Network)
	{
		var hash = StableHash.Create();
		var transform = _Network.WorldTransform;

		hash.Add(_Network.TerrainFalloff);
		hash.Add(_Network.TerrainSink);
		hash.Add(_Network.TerrainPaintMaterial?.ResourcePath);
		hash.Add(_Network.TerrainPaintMargin);
		hash.Add(_Network.TerrainPaintFade);
		hash.Add(_Network.SurfaceOffset);
		AddTransform(ref hash, transform);

		return hash.Value;
	}



	/// <summary>
	/// Where a terrain is and how big: moving or resizing it puts the roads over other samples.
	/// </summary>
	private static long Placement(Terrain _Terrain)
	{
		var hash = StableHash.Create();

		AddTransform(ref hash, _Terrain.WorldTransform);
		hash.Add(_Terrain.Storage.Resolution);
		hash.Add(_Terrain.Storage.TerrainSize);
		hash.Add(_Terrain.Storage.TerrainHeight);

		return hash.Value;
	}



	private static void AddTransform(ref StableHash _Hash, in Transform _Transform)
	{
		_Hash.Add(_Transform.Position);
		_Hash.Add(_Transform.Rotation.x);
		_Hash.Add(_Transform.Rotation.y);
		_Hash.Add(_Transform.Rotation.z);
		_Hash.Add(_Transform.Rotation.w);
		_Hash.Add(_Transform.Scale);
	}
}
