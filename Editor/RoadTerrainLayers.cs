using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// The two maps of a terrain a road layer covers.
/// </summary>
internal enum RoadLayerMap
{
	/// <summary>
	/// The height samples, shaped by Conform Terrain To Roads.
	/// </summary>
	Heights,

	/// <summary>
	/// The control map texels, painted by Paint Terrain Along Roads.
	/// </summary>
	Paint
}



/// <summary>
/// Where one of the things the roads lay into a terrain can reach, in plan and in world space, and a signature that
/// changes whenever anything deciding what it does there does. Live Update compares them to find the patch of
/// terrain to redo.
/// </summary>
internal readonly record struct RoadTerrainFootprint(Vector2 Min, Vector2 Max, long Signature);



/// <summary>
/// Laying the roads into a terrain through its <see cref="RoadTerrainLayer"/>, and taking them back out.
///
/// Under every sample the roads hold, the layer keeps the ground and what the roads wrote. Each time the roads are
/// laid in, every sample they held first gets its ground back, and only then is written again, from that ground:
/// so a conform can be pressed any number of times without the result creeping outward (each one used to start
/// from what the last one left), a sample a road has moved away from goes back to the ground, and the whole layer
/// can be taken out again. A sample changed by hand since the roads wrote it keeps the change: a height sculpted
/// up or down moves its ground as much, a texel painted over keeps its new paint.
/// </summary>
internal static class RoadTerrainLayers
{
	/// <summary>
	/// The side of the squares a layer is packed in, in samples. A change to the roads only unpacks and repacks the
	/// squares it reaches, which is what keeps Live Update cheap on a big terrain.
	/// </summary>
	public const int TileSize = 64;

	/// <summary>
	/// Leads every packed tile, so data in a format changed later is told apart instead of misread.
	/// </summary>
	private const byte PackVersion = 2;

	/// <summary>
	/// The first format: the whole layer in one string. Still read, to move it into tiles.
	/// </summary>
	private const byte LegacyVersion = 1;

	/// <summary>
	/// How many unpacked tiles the ground queries keep at hand. A node dragged across the ground asks for the same
	/// few every frame.
	/// </summary>
	private const int RecentTiles = 16;

	/// <summary>
	/// How long, in milliseconds, bringing a terrain's collider up to date may take before Live Update stops doing it
	/// with every change and leaves it until the edits settle (see <see cref="Settle"/>).
	/// </summary>
	private const double SlowCollider = 40.0;

	private static readonly List<(string Data, Dictionary<int, (uint Ground, uint Written)> Samples)> s_Recent = [];
	private static readonly List<RoadTerrainLayerTile> s_NoTiles = [];

	/// <summary>
	/// The terrains whose collider took too long to bring up to date with every change. Until late October 2026 s&box
	/// rebuilt the whole of a terrain's collider for any change however small, most of a second each time on a 4096
	/// one; a terrain is let off again as soon as it is quick.
	/// </summary>
	private static readonly HashSet<Terrain> s_SlowColliders = [];



	/// <summary>
	/// The terrains in a scene the roads can be laid into.
	/// </summary>
	public static List<Terrain> TerrainsIn(Scene _Scene)
	{
		if (_Scene is null)
			return [];

		return _Scene.GetAllComponents<Terrain>().Where(terrain => terrain.IsValid() && terrain.Storage is not null).ToList();
	}



	/// <summary>
	/// The road layer the roads are laid into a terrain through, or null, with why, when it has none to use.
	/// </summary>
	public static RoadTerrainLayer LayerOf(Terrain _Terrain, out string _Problem)
	{
		var layer = _Terrain.GetComponent<RoadTerrainLayer>(true);

		if (layer is null || !layer.IsStarted)
		{
			_Problem = $"\"{_Terrain.GameObject.Name}\" has no road layer yet: press Start Road Layer first, while no road is shaped into the terrain.";
			return null;
		}

		if (layer.Resolution != _Terrain.Storage.Resolution)
		{
			_Problem = $"\"{_Terrain.GameObject.Name}\" was resampled since its road layer was started, so the layer no longer lines up with it: start the road layer again.";
			return null;
		}

		_Problem = null;

		return layer;
	}



	/// <summary>
	/// Where each terrain's road layer stands, a line per terrain, for the sidebar.
	/// </summary>
	public static string Describe(IReadOnlyList<Terrain> _Terrains)
	{
		if (_Terrains.Count == 0)
			return "No terrain in the scene.";

		var lines = new List<string>(_Terrains.Count);

		foreach (var terrain in _Terrains)
		{
			var layer = terrain.GetComponent<RoadTerrainLayer>(true);

			string state = layer is null || !layer.IsStarted ? "Not started."
				: layer.Resolution != terrain.Storage.Resolution ? "Out of line with the terrain, which was resampled since: start it again."
				: layer.HeightSamples + layer.PaintTexels == 0 ? "Started, holding nothing yet."
				: $"Holding {layer.HeightSamples:N0} shaped height samples and {layer.PaintTexels:N0} painted texels.";

			lines.Add(_Terrains.Count == 1 ? state : $"{terrain.GameObject.Name}: {state}");
		}

		return string.Join("\n", lines);
	}



	/// <summary>
	/// How many samples the road layers on these terrains hold, shaped and painted together; a layer its terrain was
	/// resampled away from holds nothing that can still be used.
	/// </summary>
	public static int HeldBy(IReadOnlyList<Terrain> _Terrains)
	{
		int held = 0;

		foreach (var terrain in _Terrains)
		{
			if (terrain.GetComponent<RoadTerrainLayer>(true) is { IsStarted: true } layer && layer.Resolution == terrain.Storage.Resolution)
				held += layer.HeightSamples + layer.PaintTexels;
		}

		return held;
	}



	/// <summary>
	/// Starts the road layer on each terrain: the terrain as it is now becomes the ground the roads are laid over.
	/// A layer started before forgets what it held, and whatever the roads did under it stays in the terrain as
	/// part of the new ground. One undo step.
	/// </summary>
	public static void Start(IReadOnlyList<Terrain> _Terrains)
	{
		var existing = _Terrains.Select(terrain => terrain.GetComponent<RoadTerrainLayer>(true)).Where(layer => layer.IsValid()).ToList();

		using (SceneEditorSession.Active.UndoScope("Start Road Layer").WithComponentCreations().WithComponentChanges(existing).Push())
		{
			foreach (var terrain in _Terrains)
			{
				var layer = terrain.GetComponent<RoadTerrainLayer>(true) ?? terrain.AddComponent<RoadTerrainLayer>();

				layer.Resolution = terrain.Storage.Resolution;
				layer.HeightTiles = [];
				layer.PaintTiles = [];
				layer.Heights = "";
				layer.Paint = "";
				layer.HeightSamples = 0;
				layer.PaintTexels = 0;
				layer.Generation++;
			}
		}
	}



	/// <summary>
	/// Takes the roads back out of each terrain: every sample a road layer holds gets its ground back. The layers
	/// stay started, holding nothing, so the roads can be laid in again from that ground. A terrain cannot follow
	/// roads taken out of it, so Live Update goes off with them (and comes back on with an undo). Returns how many
	/// samples changed.
	/// </summary>
	public static int Remove(IReadOnlyList<Terrain> _Terrains, RoadNetwork _Network, RoadTerrainUndo _Undo, out string _Problem)
	{
		_Problem = null;

		if (_Network.IsValid() && _Network.TerrainLiveUpdate)
		{
			_Network.TerrainLiveUpdate = false;

			_Undo.Add(() =>
			{
				if (_Network.IsValid())
					_Network.TerrainLiveUpdate = true;
			}, () =>
			{
				if (_Network.IsValid())
					_Network.TerrainLiveUpdate = false;
			});
		}

		int changed = 0;
		var none = new HashSet<int>();

		foreach (var terrain in _Terrains)
		{
			// A terrain whose layer was never started has nothing to give back.
			if (terrain.GetComponent<RoadTerrainLayer>(true) is not { IsStarted: true })
				continue;

			var layer = LayerOf(terrain, out string reason);

			if (layer is not null)
			{
				changed += Lay(terrain, layer, RoadLayerMap.Heights, null, none, null, _Undo, out _, out _, out string heights);
				changed += Lay(terrain, layer, RoadLayerMap.Paint, null, none, null, _Undo, out _, out _, out string paint);
				reason = heights ?? paint;
			}

			_Problem ??= reason;
		}

		return changed;
	}



	/// <summary>
	/// Lays the roads into one map of a terrain through its road layer, and returns how many of the map's samples
	/// changed. Every sample the layer held inside <paramref name="_Scope"/> (everywhere, without one) gets its
	/// ground back; every sample in <paramref name="_Reached"/>, which must lie inside the scope, is then set to
	/// what <paramref name="_Write"/> makes of its ground; and the layer is left holding exactly the samples that
	/// are off their ground. With nothing reached, the roads are taken out. The change is added to
	/// <paramref name="_Undo"/>; without one (Live Update) nothing is recorded, and only the changed rectangle,
	/// <paramref name="_Changed"/>, is sent to the terrain's texture and collider. <paramref name="_Held"/> is how
	/// many samples the layer holds after it.
	/// </summary>
	public static int Lay(Terrain _Terrain, RoadTerrainLayer _Layer, RoadLayerMap _Map, IReadOnlyList<RectInt> _Scope, HashSet<int> _Reached, Func<int, uint, uint> _Write, RoadTerrainUndo _Undo, out RectInt _Changed, out int _Held, out string _Problem)
	{
		var storage = _Terrain.Storage;
		int resolution = storage.Resolution;
		bool heights = _Map == RoadLayerMap.Heights;

		_Changed = default;
		_Held = heights ? _Layer.HeightSamples : _Layer.PaintTexels;
		_Problem = null;

		if (!TryMigrate(_Layer, _Map, resolution))
		{
			_Problem = Unreadable(_Terrain);
			return 0;
		}

		var tiles = TilesOf(_Layer, _Map);
		var byIndex = new Dictionary<int, RoadTerrainLayerTile>(tiles.Count);

		foreach (var tile in tiles)
		{
			if (tile is not null)
				byIndex[tile.Index] = tile;
		}

		// The reached samples, by the tile each falls in.
		var reachedByTile = new Dictionary<int, List<int>>();

		foreach (int index in _Reached)
		{
			int tile = TileOf(index, resolution);

			if (!reachedByTile.TryGetValue(tile, out var list))
			{
				list = new List<int>();
				reachedByTile[tile] = list;
			}

			list.Add(index);
		}

		// Every tile this pass looks at: those a reached sample falls in, and those the layer holds that the scope
		// reaches. All are read before anything changes, so a damaged one leaves the terrain alone.
		var touched = new HashSet<int>(reachedByTile.Keys);

		foreach (int tile in byIndex.Keys)
		{
			if (_Scope is null || Overlaps(TileRect(tile, resolution), _Scope))
				touched.Add(tile);
		}

		var held = new Dictionary<int, Dictionary<int, (uint Ground, uint Written)>>(touched.Count);

		foreach (int tile in touched)
		{
			if (!TryRead(byIndex.GetValueOrDefault(tile), tile, resolution, out var samples))
			{
				_Problem = Unreadable(_Terrain);
				return 0;
			}

			held[tile] = samples;
		}

		var indices = new List<int>();
		var before = new List<uint>();
		var after = new List<uint>();
		var replaced = new Dictionary<int, RoadTerrainLayerTile>();

		foreach (var (tile, samples) in held)
		{
			var next = new Dictionary<int, (uint Ground, uint Written)>(samples.Count);

			// What the layer held here: outside the scope it stays as it is; inside, a sample no road reaches any
			// more gets its ground back (those still reached are laid below).
			foreach (var (index, sample) in samples)
			{
				if (_Scope is not null && !Contains(_Scope, index, resolution))
					next[LocalIndex(index, resolution)] = sample;
				else if (!_Reached.Contains(index))
					Visit(index, true, sample, false, next);
			}

			if (reachedByTile.TryGetValue(tile, out var reached))
			{
				foreach (int index in reached)
				{
					bool isHeld = samples.TryGetValue(index, out var sample);

					Visit(index, isHeld, sample, true, next);
				}
			}

			var old = byIndex.GetValueOrDefault(tile);
			string data = next.Count > 0 ? Pack(next) : "";

			if ((old?.Data ?? "") != data)
				replaced[tile] = next.Count > 0 ? new RoadTerrainLayerTile { Index = tile, Count = next.Count, Data = data } : null;
		}

		// Pressed again with nothing changed since: the terrain already is what the roads make of the ground.
		if (indices.Count == 0 && replaced.Count == 0)
			return 0;

		// The layer's tiles after this pass, kept in order so the scene file only changes where the layer did. A list
		// is never changed once made, so the one before stays as it was for the undo.
		var nextTiles = tiles.Where(tile => tile is not null && !replaced.ContainsKey(tile.Index))
			.Concat(replaced.Values.Where(tile => tile is not null))
			.OrderBy(tile => tile.Index)
			.ToList();

		int heldBefore = _Held;
		int heldAfter = nextTiles.Sum(tile => tile.Count);
		int[] changed = indices.ToArray();
		uint[] was = before.ToArray();
		uint[] now = after.ToArray();
		var region = Bounds(changed, resolution);

		Apply(_Terrain, _Map, resolution, changed, now, region, nextTiles, heldAfter, _Undo is null);

		_Undo?.Add(() => Apply(_Terrain, _Map, resolution, changed, was, region, tiles, heldBefore, false),
			() => Apply(_Terrain, _Map, resolution, changed, now, region, nextTiles, heldAfter, false));

		_Changed = region;
		_Held = heldAfter;

		return changed.Length;

		void Visit(int _Index, bool _IsHeld, (uint Ground, uint Written) _Sample, bool _Wanted, Dictionary<int, (uint Ground, uint Written)> _Next)
		{
			uint current = heights ? storage.HeightMap[_Index] : storage.ControlMap[_Index];
			uint ground = _IsHeld ? GroundUnder(heights, current, _Sample) : current;
			uint written = _Wanted ? _Write(_Index, ground) : ground;

			// Only what the roads move off the ground is theirs to remember.
			if (written != ground)
				_Next[LocalIndex(_Index, resolution)] = (ground, written);

			if (written == current)
				return;

			indices.Add(_Index);
			before.Add(current);
			after.Add(written);
		}
	}



	/// <summary>
	/// The ground the roads are laid over at a point in plan (world space), and how far it lies from the terrain as
	/// the roads shaped it there, which is nothing away from them: the road layer's ground where it holds samples,
	/// the terrain as it is everywhere else. False off the terrain, or when it has no road layer to ask.
	/// </summary>
	public static bool TryGetGround(Terrain _Terrain, Vector2 _Point, out float _Height, out float _Offset)
	{
		_Height = 0.0f;
		_Offset = 0.0f;

		var layer = LayerOf(_Terrain, out _);
		var storage = _Terrain.Storage;
		int resolution = storage.Resolution;

		if (layer is null || storage.HeightMap is null || storage.HeightMap.Length != resolution * resolution || !TryMigrate(layer, RoadLayerMap.Heights, resolution))
			return false;

		float cell = storage.TerrainSize / resolution;
		var transform = _Terrain.WorldTransform;
		Vector3 local = transform.PointToLocal(new Vector3(_Point.x, _Point.y, transform.Position.z));

		// Between the four samples round the point: their centres sit half a cell in from their cell's corner,
		// where the terrain's collider puts them too.
		float gridX = local.x / cell - 0.5f;
		float gridY = local.y / cell - 0.5f;

		if (gridX < -0.5f || gridY < -0.5f || gridX > resolution - 0.5f || gridY > resolution - 0.5f)
			return false;

		int x0 = Math.Clamp((int)MathF.Floor(gridX), 0, resolution - 1);
		int y0 = Math.Clamp((int)MathF.Floor(gridY), 0, resolution - 1);
		int x1 = Math.Min(x0 + 1, resolution - 1);
		int y1 = Math.Min(y0 + 1, resolution - 1);
		float fx = Math.Clamp(gridX - x0, 0.0f, 1.0f);
		float fy = Math.Clamp(gridY - y0, 0.0f, 1.0f);

		var tiles = TilesOf(layer, RoadLayerMap.Heights);
		float ground = 0.0f;
		float current = 0.0f;

		Take(x0, y0, (1.0f - fx) * (1.0f - fy));
		Take(x1, y0, fx * (1.0f - fy));
		Take(x0, y1, (1.0f - fx) * fy);
		Take(x1, y1, fx * fy);

		float scale = storage.TerrainHeight / ushort.MaxValue;

		_Height = transform.PointToWorld(local.WithZ(ground * scale)).z;
		_Offset = _Height - transform.PointToWorld(local.WithZ(current * scale)).z;

		return true;

		void Take(int _X, int _Y, float _Weight)
		{
			int index = _X + _Y * resolution;
			uint value = storage.HeightMap[index];
			var samples = ReadRecent(FindTile(tiles, TileOf(index, resolution)));

			ground += _Weight * (samples is not null && samples.TryGetValue(LocalIndex(index, resolution), out var sample) ? GroundUnder(true, value, sample) : value);
			current += _Weight * value;
		}
	}



	/// <summary>
	/// Where a trace that hit a terrain meets the ground the roads are laid over instead. Under its roads the terrain
	/// is shaped to them (sunk below the surface, cut and filled round it), so a node dropped onto it would settle
	/// into the shape its own road gave the ground, and sink a little more each time it is moved. Away from the
	/// roads, and on anything other than a terrain with a road layer, the trace's own hit stands.
	/// </summary>
	public static Vector3 OntoGround(in SceneTraceResult _Trace, Ray _Ray)
	{
		var terrain = _Trace.GameObject?.Components.Get<Terrain>();

		if (!terrain.IsValid() || terrain.Storage is null)
			return _Trace.EndPosition;

		Vector3 point = _Trace.EndPosition;

		if (!TryGetGround(terrain, new Vector2(point.x, point.y), out float ground, out float offset) || MathF.Abs(offset) < 0.01f)
			return point;

		// The ground lies above or below the shaped terrain here, so the ray meets it a little short of the hit or
		// further along: a few steps, each to where the ray is at the ground's height under the last point, find it.
		for (int i = 0; i < 4 && MathF.Abs(_Ray.Forward.z) > 0.05f; i++)
		{
			float along = (ground - _Ray.Position.z) / _Ray.Forward.z;

			if (along <= 0.0f)
				break;

			Vector3 next = _Ray.Position + _Ray.Forward * along;

			if (!TryGetGround(terrain, new Vector2(next.x, next.y), out float there, out _))
				break;

			bool settled = MathF.Abs(next.x - point.x) + MathF.Abs(next.y - point.y) < 0.01f;

			point = next;
			ground = there;

			if (settled)
				break;
		}

		return point.WithZ(ground);
	}



	/// <summary>
	/// The rectangle of a terrain's grid under a rectangle in plan (world space), a sample wider all round; null when
	/// it misses the terrain.
	/// </summary>
	public static RectInt? ToGrid(Terrain _Terrain, Vector2 _Min, Vector2 _Max)
	{
		var storage = _Terrain.Storage;
		int resolution = storage.Resolution;
		float cell = storage.TerrainSize / resolution;
		var transform = _Terrain.WorldTransform;
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

		for (int corner = 0; corner < 4; corner++)
		{
			var world = new Vector3((corner & 1) == 0 ? _Min.x : _Max.x, (corner & 2) == 0 ? _Min.y : _Max.y, transform.Position.z);
			Vector3 local = transform.PointToLocal(world);

			minX = MathF.Min(minX, local.x);
			maxX = MathF.Max(maxX, local.x);
			minY = MathF.Min(minY, local.y);
			maxY = MathF.Max(maxY, local.y);
		}

		int left = (int)MathF.Floor(minX / cell) - 1;
		int top = (int)MathF.Floor(minY / cell) - 1;
		int right = (int)MathF.Ceiling(maxX / cell) + 1;
		int bottom = (int)MathF.Ceiling(maxY / cell) + 1;

		if (right < 0 || bottom < 0 || left >= resolution || top >= resolution)
			return null;

		left = Math.Max(left, 0);
		top = Math.Max(top, 0);
		right = Math.Min(right, resolution - 1);
		bottom = Math.Min(bottom, resolution - 1);

		return new RectInt(left, top, right - left + 1, bottom - top + 1);
	}



	/// <summary>
	/// The ground under a sample the layer holds. While the sample still carries what the roads wrote (or has been
	/// put back on its ground) it is the ground the layer remembers. Changed by hand since, the change is kept: a
	/// height sculpted up or down moves the ground as much, a texel painted over keeps its new paint.
	/// </summary>
	private static uint GroundUnder(bool _Heights, uint _Current, (uint Ground, uint Written) _Sample)
	{
		if (_Current == _Sample.Written || _Current == _Sample.Ground)
			return _Sample.Ground;

		if (!_Heights)
			return _Current;

		return (uint)Math.Clamp((long)_Sample.Ground + _Current - _Sample.Written, 0L, ushort.MaxValue);
	}



	/// <summary>
	/// Puts samples into one of a terrain's maps and the tiles into its road layer, then sends the changed rectangle to
	/// the terrain's texture and brings its collision up to date: when <paramref name="_Quick"/> (Live Update, many
	/// times a second while a road is dragged) only if that is quick on this terrain, else with everything that
	/// depends on the terrain told too (see <see cref="Settle"/>).
	/// </summary>
	private static void Apply(Terrain _Terrain, RoadLayerMap _Map, int _Resolution, int[] _Indices, uint[] _Values, RectInt _Region, List<RoadTerrainLayerTile> _Tiles, int _Held, bool _Quick)
	{
		// A terrain deleted or resampled since has nothing these samples line up with.
		if (!_Terrain.IsValid() || _Terrain.Storage is null || _Terrain.Storage.Resolution != _Resolution)
			return;

		var storage = _Terrain.Storage;
		bool heights = _Map == RoadLayerMap.Heights;

		for (int i = 0; i < _Indices.Length; i++)
		{
			if (heights)
				storage.HeightMap[_Indices[i]] = (ushort)_Values[i];
			else
				storage.ControlMap[_Indices[i]] = _Values[i];
		}

		if (_Terrain.GetComponent<RoadTerrainLayer>(true) is { } layer)
		{
			if (heights)
			{
				layer.HeightTiles = _Tiles;
				layer.HeightSamples = _Held;
			}
			else
			{
				layer.PaintTiles = _Tiles;
				layer.PaintTexels = _Held;
			}
		}

		if (_Indices.Length == 0)
			return;

		SendRegion(_Terrain, heights, _Region);

		if (_Quick)
			FollowRegion(_Terrain, heights, _Region);
		else
			Settle(_Terrain, heights ? Terrain.SyncFlags.Height : Terrain.SyncFlags.Control, _Region);
	}



	/// <summary>
	/// Brings a terrain's collider (and the lighting baked from its heights, which comes with it) up to date with one
	/// rectangle of a map as Live Update changes it, unless that has been too slow on this terrain: then it waits until
	/// the edits settle (see <see cref="Settle"/>), and only the texture follows each change. What else depends on the
	/// terrain (its clutter) is not told: Live Update does that once the edits settle.
	/// </summary>
	private static void FollowRegion(Terrain _Terrain, bool _Heights, RectInt _Region)
	{
		if (s_SlowColliders.Contains(_Terrain))
			return;

		var watch = System.Diagnostics.Stopwatch.StartNew();

		_Terrain.UpdateCollision(_Heights ? Terrain.SyncFlags.Height : Terrain.SyncFlags.Control, _Region);
		Timed(_Terrain, watch.Elapsed.TotalMilliseconds, _Region);
	}



	/// <summary>
	/// Brings a terrain's collider (and the lighting baked from its heights) up to date with a rectangle of its maps
	/// whose texture is already sent, and tells what else depends on the terrain (its clutter) when the scene has any:
	/// telling it sends the whole terrain to the GPU again, which a big one feels. How long the collider took decides
	/// whether Live Update keeps it up to date with every change or leaves it until the edits settle.
	/// </summary>
	public static void Settle(Terrain _Terrain, Terrain.SyncFlags _Flags, RectInt _Region)
	{
		s_SlowColliders.RemoveWhere(terrain => !terrain.IsValid());

		if (!_Terrain.IsValid() || _Terrain.Storage is null)
			return;

		if (_Terrain.Scene?.GetAllComponents<Sandbox.Clutter.ClutterComponent>().Any() == true)
		{
			_Terrain.ApplyStorageChanges(_Flags, _Region);
			return;
		}

		var watch = System.Diagnostics.Stopwatch.StartNew();

		_Terrain.UpdateCollision(_Flags, _Region);
		Timed(_Terrain, watch.Elapsed.TotalMilliseconds, _Region);
	}



	/// <summary>
	/// Whether bringing a terrain's collider up to date has been too slow for Live Update to do it with every change.
	/// </summary>
	public static bool HasSlowCollider(Terrain _Terrain)
	{
		return s_SlowColliders.Contains(_Terrain);
	}



	/// <summary>
	/// Notes how long bringing a terrain's collider up to date took: too long, and Live Update leaves it until the edits
	/// settle from then on (said once, so it is known why the collision lags behind the shape); quick, and it follows
	/// every change again.
	/// </summary>
	private static void Timed(Terrain _Terrain, double _Milliseconds, RectInt _Region)
	{
		if (_Milliseconds < SlowCollider)
		{
			s_SlowColliders.Remove(_Terrain);
			return;
		}

		if (s_SlowColliders.Add(_Terrain))
			Log.Info($"Road Tool 2: updating the collision of \"{_Terrain.GameObject.Name}\" took {_Milliseconds:0} ms for a {_Region.Width}x{_Region.Height} change, so Live Update now brings it up to date once the edits settle instead of with every change.");
	}



	/// <summary>
	/// Sends one rectangle of a terrain's map to its texture, without sending the rest.
	/// </summary>
	private static void SendRegion(Terrain _Terrain, bool _Heights, RectInt _Region)
	{
		var storage = _Terrain.Storage;
		int resolution = storage.Resolution;

		if (_Heights)
		{
			var data = new ushort[_Region.Width * _Region.Height];

			for (int y = 0; y < _Region.Height; y++)
				Array.Copy(storage.HeightMap, _Region.Left + (_Region.Top + y) * resolution, data, y * _Region.Width, _Region.Width);

			_Terrain.HeightMap?.Update<ushort>(data, _Region.Left, _Region.Top, _Region.Width, _Region.Height);
		}
		else
		{
			var data = new uint[_Region.Width * _Region.Height];

			for (int y = 0; y < _Region.Height; y++)
				Array.Copy(storage.ControlMap, _Region.Left + (_Region.Top + y) * resolution, data, y * _Region.Width, _Region.Width);

			_Terrain.ControlMap?.Update<uint>(data, _Region.Left, _Region.Top, _Region.Width, _Region.Height);
		}
	}



	/// <summary>
	/// The rectangle of the grid round a set of samples.
	/// </summary>
	private static RectInt Bounds(int[] _Indices, int _Resolution)
	{
		if (_Indices.Length == 0)
			return default;

		int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

		foreach (int index in _Indices)
		{
			int x = index % _Resolution;
			int y = index / _Resolution;

			left = Math.Min(left, x);
			right = Math.Max(right, x);
			top = Math.Min(top, y);
			bottom = Math.Max(bottom, y);
		}

		return new RectInt(left, top, right - left + 1, bottom - top + 1);
	}



	/// <summary>
	/// Whether two rectangles of the grid share a sample.
	/// </summary>
	public static bool Overlap(RectInt _A, RectInt _B)
	{
		return _A.Left < _B.Left + _B.Width && _B.Left < _A.Left + _A.Width && _A.Top < _B.Top + _B.Height && _B.Top < _A.Top + _A.Height;
	}



	/// <summary>
	/// Whether a rectangle of the grid shares a sample with any of some others.
	/// </summary>
	public static bool Overlaps(RectInt _Rect, IReadOnlyList<RectInt> _Scope)
	{
		foreach (var rect in _Scope)
		{
			if (Overlap(_Rect, rect))
				return true;
		}

		return false;
	}



	private static bool Contains(IReadOnlyList<RectInt> _Scope, int _Index, int _Resolution)
	{
		int x = _Index % _Resolution;
		int y = _Index / _Resolution;

		foreach (var rect in _Scope)
		{
			if (x >= rect.Left && x < rect.Left + rect.Width && y >= rect.Top && y < rect.Top + rect.Height)
				return true;
		}

		return false;
	}



	private static int TilesAcross(int _Resolution)
	{
		return (_Resolution + TileSize - 1) / TileSize;
	}



	/// <summary>
	/// The tile a sample falls in.
	/// </summary>
	private static int TileOf(int _Index, int _Resolution)
	{
		int x = _Index % _Resolution;
		int y = _Index / _Resolution;

		return x / TileSize + y / TileSize * TilesAcross(_Resolution);
	}



	/// <summary>
	/// A tile's rectangle of the grid; the last ones across and down are cut short by the grid's edge.
	/// </summary>
	private static RectInt TileRect(int _Tile, int _Resolution)
	{
		int across = TilesAcross(_Resolution);
		int left = _Tile % across * TileSize;
		int top = _Tile / across * TileSize;

		return new RectInt(left, top, Math.Min(TileSize, _Resolution - left), Math.Min(TileSize, _Resolution - top));
	}



	/// <summary>
	/// Where a sample sits within its tile.
	/// </summary>
	private static int LocalIndex(int _Index, int _Resolution)
	{
		return _Index % _Resolution % TileSize + _Index / _Resolution % TileSize * TileSize;
	}



	private static List<RoadTerrainLayerTile> TilesOf(RoadTerrainLayer _Layer, RoadLayerMap _Map)
	{
		return (_Map == RoadLayerMap.Heights ? _Layer.HeightTiles : _Layer.PaintTiles) ?? s_NoTiles;
	}



	private static RoadTerrainLayerTile FindTile(List<RoadTerrainLayerTile> _Tiles, int _Index)
	{
		foreach (var tile in _Tiles)
		{
			if (tile is not null && tile.Index == _Index)
				return tile;
		}

		return null;
	}



	private static string Unreadable(Terrain _Terrain)
	{
		return $"The road layer on \"{_Terrain.GameObject.Name}\" can't be read back, so it was left alone: start the road layer again.";
	}



	/// <summary>
	/// Moves one map of a layer packed by the first version (all of it in one string) into tiles. False when that
	/// data can't be read.
	/// </summary>
	private static bool TryMigrate(RoadTerrainLayer _Layer, RoadLayerMap _Map, int _Resolution)
	{
		bool heights = _Map == RoadLayerMap.Heights;
		string legacy = heights ? _Layer.Heights : _Layer.Paint;

		if (string.IsNullOrEmpty(legacy))
			return true;

		if (!TryUnpack(legacy, LegacyVersion, _Resolution * _Resolution, out var samples))
			return false;

		var byTile = new SortedDictionary<int, Dictionary<int, (uint Ground, uint Written)>>();

		foreach (var (index, sample) in samples)
		{
			int tile = TileOf(index, _Resolution);

			if (!byTile.TryGetValue(tile, out var tileSamples))
			{
				tileSamples = new Dictionary<int, (uint Ground, uint Written)>();
				byTile[tile] = tileSamples;
			}

			tileSamples[LocalIndex(index, _Resolution)] = sample;
		}

		var tiles = byTile.Select(pair => new RoadTerrainLayerTile { Index = pair.Key, Count = pair.Value.Count, Data = Pack(pair.Value) }).ToList();

		if (heights)
		{
			_Layer.HeightTiles = tiles;
			_Layer.Heights = "";
		}
		else
		{
			_Layer.PaintTiles = tiles;
			_Layer.Paint = "";
		}

		return true;
	}



	/// <summary>
	/// A tile's samples by their index on the whole grid; none for a tile the layer does not hold.
	/// </summary>
	private static bool TryRead(RoadTerrainLayerTile _Tile, int _Index, int _Resolution, out Dictionary<int, (uint Ground, uint Written)> _Samples)
	{
		_Samples = new Dictionary<int, (uint Ground, uint Written)>();

		if (_Tile is null || string.IsNullOrEmpty(_Tile.Data))
			return true;

		if (!TryUnpack(_Tile.Data, PackVersion, TileSize * TileSize, out var local))
			return false;

		var rect = TileRect(_Index, _Resolution);

		foreach (var (index, sample) in local)
		{
			int x = rect.Left + index % TileSize;
			int y = rect.Top + index / TileSize;

			// A tile at the grid's edge is cut short: nothing this layer wrote lies past it.
			if (x >= _Resolution || y >= _Resolution)
				return false;

			_Samples[x + y * _Resolution] = sample;
		}

		return true;
	}



	/// <summary>
	/// A tile's samples by their place in the tile, for the ground queries, kept for the next few asks. A packed tile
	/// never changes (a change makes a new one), so what was read from it stays true while it is around.
	/// </summary>
	private static Dictionary<int, (uint Ground, uint Written)> ReadRecent(RoadTerrainLayerTile _Tile)
	{
		if (_Tile is null || string.IsNullOrEmpty(_Tile.Data))
			return null;

		foreach (var (data, samples) in s_Recent)
		{
			if (ReferenceEquals(data, _Tile.Data))
				return samples;
		}

		if (!TryUnpack(_Tile.Data, PackVersion, TileSize * TileSize, out var read))
			return null;

		if (s_Recent.Count >= RecentTiles)
			s_Recent.RemoveAt(0);

		s_Recent.Add((_Tile.Data, read));

		return read;
	}



	/// <summary>
	/// Packs samples for the scene. In index order, each is written as the gap since the one before, its ground as
	/// the step from that one's ground, and how far the roads moved it off its ground as the step from how far they
	/// moved that one, all as variable-length numbers. Along a road those are mostly zero or close to it, which is
	/// what lets the deflate after squeeze them down.
	/// </summary>
	private static string Pack(Dictionary<int, (uint Ground, uint Written)> _Samples)
	{
		if (_Samples.Count == 0)
			return "";

		int[] indices = _Samples.Keys.ToArray();
		Array.Sort(indices);

		using var payload = new MemoryStream(indices.Length * 4 + 8);

		WriteNumber(payload, (ulong)indices.Length);

		int previousIndex = -1;
		long previousGround = 0;
		long previousOffset = 0;

		foreach (int index in indices)
		{
			var (ground, written) = _Samples[index];
			long offset = (long)written - ground;

			WriteNumber(payload, (ulong)(index - previousIndex - 1));
			WriteNumber(payload, ZigZag(ground - previousGround));
			WriteNumber(payload, ZigZag(offset - previousOffset));

			previousIndex = index;
			previousGround = ground;
			previousOffset = offset;
		}

		using var packed = new MemoryStream();

		packed.WriteByte(PackVersion);

		using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, true))
			payload.WriteTo(deflate);

		return Convert.ToBase64String(packed.GetBuffer(), 0, (int)packed.Length);
	}



	/// <summary>
	/// Reads packed samples back; false when they can't be, written in another format or damaged, or naming a
	/// sample at or past <paramref name="_Limit"/>.
	/// </summary>
	private static bool TryUnpack(string _Packed, byte _Version, int _Limit, out Dictionary<int, (uint Ground, uint Written)> _Samples)
	{
		_Samples = new Dictionary<int, (uint Ground, uint Written)>();

		if (string.IsNullOrEmpty(_Packed))
			return true;

		byte[] payload;

		try
		{
			byte[] packed = Convert.FromBase64String(_Packed);

			if (packed.Length == 0 || packed[0] != _Version)
				return false;

			using var deflate = new DeflateStream(new MemoryStream(packed, 1, packed.Length - 1), CompressionMode.Decompress);
			using var inflated = new MemoryStream();

			deflate.CopyTo(inflated);
			payload = inflated.ToArray();
		}
		catch (Exception exception) when (exception is FormatException or InvalidDataException)
		{
			return false;
		}

		int position = 0;

		if (!TryReadNumber(payload, ref position, out ulong count))
			return false;

		int previousIndex = -1;
		long previousGround = 0;
		long previousOffset = 0;

		for (ulong i = 0; i < count; i++)
		{
			if (!TryReadNumber(payload, ref position, out ulong gap) || !TryReadNumber(payload, ref position, out ulong groundStep) || !TryReadNumber(payload, ref position, out ulong offsetStep))
				return false;

			long index = previousIndex + 1L + (long)gap;
			long ground = previousGround + UnZigZag(groundStep);
			long offset = previousOffset + UnZigZag(offsetStep);
			long written = ground + offset;

			if (index < 0 || index >= _Limit || ground < 0 || ground > uint.MaxValue || written < 0 || written > uint.MaxValue)
				return false;

			_Samples[(int)index] = ((uint)ground, (uint)written);

			previousIndex = (int)index;
			previousGround = ground;
			previousOffset = offset;
		}

		return true;
	}



	private static void WriteNumber(Stream _Stream, ulong _Value)
	{
		while (_Value >= 0x80)
		{
			_Stream.WriteByte((byte)(_Value | 0x80));
			_Value >>= 7;
		}

		_Stream.WriteByte((byte)_Value);
	}



	private static bool TryReadNumber(byte[] _Bytes, ref int _Position, out ulong _Value)
	{
		_Value = 0;

		for (int shift = 0; shift < 64; shift += 7)
		{
			if (_Position >= _Bytes.Length)
				return false;

			byte part = _Bytes[_Position++];

			_Value |= (ulong)(part & 0x7F) << shift;

			if ((part & 0x80) == 0)
				return true;
		}

		return false;
	}



	/// <summary>
	/// Folds a signed step into an unsigned one, small either way round: 0, -1, 1, -2, 2... become 0, 1, 2, 3, 4...
	/// </summary>
	private static ulong ZigZag(long _Value)
	{
		return (ulong)((_Value << 1) ^ (_Value >> 63));
	}



	private static long UnZigZag(ulong _Value)
	{
		return (long)(_Value >> 1) ^ -(long)(_Value & 1);
	}
}



/// <summary>
/// What one press of a Terrain tool button changed, over every terrain in the scene: taken back and redone as a
/// single step.
/// </summary>
internal sealed class RoadTerrainUndo
{
	private readonly List<Action> m_Undo = [];
	private readonly List<Action> m_Redo = [];



	public void Add(Action _Undo, Action _Redo)
	{
		m_Undo.Add(_Undo);
		m_Redo.Add(_Redo);
	}



	/// <summary>
	/// Records the step, if anything changed. The scene is marked as changed with it: the road layers live in the
	/// scene, and must be saved along with the terrains or they no longer match them.
	/// </summary>
	public void Push(string _Title)
	{
		if (m_Undo.Count == 0)
			return;

		Action[] undo = m_Undo.ToArray();
		Action[] redo = m_Redo.ToArray();

		SceneEditorSession.Active.UndoSystem.Insert(_Title,
			() =>
			{
				for (int i = undo.Length - 1; i >= 0; i--)
					undo[i]();
			},
			() =>
			{
				foreach (var action in redo)
					action();
			});

		SceneEditorSession.Active.HasUnsavedChanges = true;
	}
}
