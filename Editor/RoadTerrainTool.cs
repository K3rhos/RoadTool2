using System;
using System.Collections.Generic;
using System.Linq;
using Editor;
using Sandbox;

namespace RedSnail.RoadTool2.Editor;

/// <summary>
/// Terrain and roads, both ways: shape the terrain to the roads (the ground is cut and filled to meet the road
/// surface and its borders, blending back into the landscape over a falloff), or drop the road nodes onto the
/// ground. Both are explicit and undoable; nothing happens on its own while roads are being laid out.
/// </summary>
[Title("Terrain")]
[Icon("landscape")]
[Group("1")]
[Order(3)]
public class RoadTerrainTool : EditorTool
{
	/// <summary>
	/// How far beyond a road's border the terrain takes to blend back into the landscape.
	/// </summary>
	[Property, Title("Falloff"), Range(0.0f, 2000.0f)] public float Falloff { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 300.0f;

	/// <summary>
	/// How far below the road surface the ground is kept, so it never shows through the asphalt.
	/// </summary>
	[Property, Title("Sink"), Range(0.0f, 50.0f)] public float Sink { get; set { field = value.Clamp(0.0f, 1000.0f); } } = 5.0f;

	/// <summary>
	/// The terrain material painted along the roads. Give a country road's verge the same texture and the two
	/// merge, instead of the verge ending on a line against the landscape.
	/// </summary>
	[Property, Title("Paint Material")] public TerrainMaterial PaintMaterial { get; set; }

	/// <summary>
	/// How far past a road's border the material is painted at full strength.
	/// </summary>
	[Property, Title("Margin"), Range(0.0f, 1000.0f)] public float PaintMargin { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 100.0f;

	/// <summary>
	/// How far it fades out beyond that, back into whatever the ground was painted with.
	/// </summary>
	[Property, Title("Fade"), Range(0.0f, 2000.0f)] public float PaintFade { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 200.0f;

	public RoadNetwork Network => Root?.Network;

	private RoadNetworkTool Root => Manager?.CurrentTool as RoadNetworkTool;

	/// <summary>
	/// A short report of the last operation, for the sidebar.
	/// </summary>
	public string LastResult { get; private set; } = "";

	public int ResultVersion { get; private set; }



	public override Widget CreateToolSidebar()
	{
		return new RoadTerrainToolWidget(this);
	}



	public override void OnUpdate()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		// Show how far the terrain will be touched: the outer edge of the falloff round every road.
		using (Gizmo.Scope("roadtool2_terrain"))
		{
			Gizmo.Draw.IgnoreDepth = true;
			Gizmo.Draw.LineThickness = 1.0f;
			Gizmo.Draw.Color = Color.Green.WithAlpha(0.35f);

			foreach (var segment in network.Segments)
			{
				if (segment.Bridge is not null || !segment.ConformTerrain || !network.TryGetCurve(segment, out var curve))
					continue;

				float reach = network.WidthOf(segment).MaxHalf * 2.0f + 2.0f * (RoadTerrainConformer.BorderWidth(network.ProfileOf(segment)) + Falloff);
				RoadGizmos.DrawRoadOutline(curve, reach, network.WorldTransform);
			}
		}
	}



	public void ConformTerrain()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		var terrains = Scene.GetAllComponents<Terrain>().Where(terrain => terrain.IsValid() && terrain.Storage is not null).ToList();

		if (terrains.Count == 0)
		{
			Report("No terrain in the scene.");
			return;
		}

		int texels = 0;

		foreach (var terrain in terrains)
			texels += RoadTerrainConformer.Conform(terrain, network, Falloff, Sink);

		Report(texels > 0 ? $"Terrain shaped under the roads ({texels:N0} height samples)." : "No road lies over a terrain.");
	}



	public void PaintTerrain()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		if (PaintMaterial is null)
		{
			Report("Pick a terrain material to paint with first.");
			return;
		}

		var terrains = Scene.GetAllComponents<Terrain>().Where(terrain => terrain.IsValid() && terrain.Storage is not null).ToList();

		if (terrains.Count == 0)
		{
			Report("No terrain in the scene.");
			return;
		}

		int texels = 0;
		string problem = null;

		foreach (var terrain in terrains)
		{
			texels += RoadTerrainPainter.Paint(terrain, network, PaintMaterial, PaintMargin, PaintFade, out string reason);
			problem ??= reason;
		}

		Report(texels > 0 ? $"Terrain painted along the roads ({texels:N0} texels)." : problem ?? "No road lies over a terrain.");
	}



	/// <summary>
	/// Drops every node onto whatever is under it (roads skipped), keeping the roads smooth between nodes.
	/// </summary>
	public void SnapNodesToGround()
	{
		var network = Network;

		if (!network.IsValid())
			return;

		int moved = 0;

		using (SceneEditorSession.Active.UndoScope("Snap Nodes To Ground").WithComponentChanges(network).Push())
		{
			foreach (var node in network.Nodes.ToList())
			{
				// A node with only bridges at it is up in the air on purpose; a bridge's end still lands.
				var roads = network.GetSegmentsAt(node.Id).ToList();

				if (roads.Count > 0 && roads.All(segment => segment.Bridge is not null))
					continue;

				Vector3 world = network.WorldTransform.PointToWorld(node.Position);

				if (!TryGroundHeight(world, out float height) || MathF.Abs(height - world.z) < 0.01f)
					continue;

				network.MoveNode(node.Id, network.WorldTransform.PointToLocal(world.WithZ(height)));
				moved++;
			}
		}

		Report(moved > 0 ? $"{moved} node{(moved == 1 ? "" : "s")} dropped onto the ground." : "Every node already sits on the ground.");
	}



	private bool TryGroundHeight(Vector3 _World, out float _Height)
	{
		const float reach = 20000.0f;

		var ray = new Ray(_World.WithZ(_World.z + reach * 0.5f), Vector3.Down);
		var physics = Scene.Trace.Ray(ray, reach).WithoutTags(RoadNetwork.ChunkTag).Run();

		if (physics.Hit)
		{
			_Height = physics.EndPosition.z;
			return true;
		}

		var meshes = Scene.Trace.Ray(ray, reach).UseRenderMeshes(true, EditorPreferences.BackfaceSelection).WithoutTags("hidden", RoadNetwork.ChunkTag).UsePhysicsWorld(false).Run();

		_Height = meshes.Hit ? meshes.EndPosition.z : 0.0f;

		return meshes.Hit;
	}



	private void Report(string _Message)
	{
		LastResult = _Message;
		ResultVersion++;
		Log.Info($"Road Tool 2: {_Message}");
	}
}



/// <summary>
/// Shapes a terrain's heightmap to a road network. Every height sample within reach of a road takes the height
/// of the nearest road there: below the surface under the road and its border, the border's own outer height at
/// its edge, and a smooth blend back to the untouched terrain across the falloff. Where roads meet, the nearest
/// one wins, which is what the junction geometry does too.
/// </summary>
internal static class RoadTerrainConformer
{
	/// <summary>
	/// What the roads within reach of one height sample ask of it: the height the nearest road wants the ground
	/// at (under the road, or at its border's edge), and how much of the untouched ground is left once every
	/// road's pull on the sample is taken together. A sample between two roads is pulled by both, so the ground
	/// between them stays level with them instead of mounding up as soon as it leaves either edge.
	/// </summary>
	private readonly struct Entry
	{
		public readonly float Distance;
		public readonly float Anchor;
		public readonly float Remaining;

		public Entry(float _Distance, float _Anchor, float _Remaining)
		{
			Distance = _Distance;
			Anchor = _Anchor;
			Remaining = _Remaining;
		}
	}

	/// <summary>
	/// How far out from the road edge the terrain is shaped to the road before the falloff starts: the pavement
	/// of a sidewalk, the shoulder of a country road (its verge is meant to run under the ground, not sit on it).
	/// </summary>
	public static float BorderWidth(RoadProfile _Profile)
	{
		return _Profile.Border switch
		{
			RoadBorderStyle.Sidewalk => _Profile.BorderWidth,
			RoadBorderStyle.CountrySide => _Profile.BorderWidth,
			_ => 0.0f
		};
	}



	/// <summary>
	/// Shapes one terrain, records an undo step, and returns how many height samples changed.
	/// </summary>
	public static int Conform(Terrain _Terrain, RoadNetwork _Network, float _Falloff, float _Sink)
	{
		var storage = _Terrain.Storage;
		int resolution = storage.Resolution;

		if (resolution <= 0 || storage.HeightMap is null || storage.HeightMap.Length != resolution * resolution || storage.TerrainHeight <= 0.0f)
			return 0;

		float cell = storage.TerrainSize / resolution;
		var terrainTransform = _Terrain.WorldTransform;
		var networkTransform = _Network.WorldTransform;
		var entries = new Dictionary<int, Entry>();
		var caps = new Dictionary<int, float>();
		var road = new Dictionary<int, Entry>();

		foreach (var segment in _Network.Segments)
		{
			var table = _Network.GetCurveTable(segment);

			// A road set to leave the ground alone (one on a dam, a quay, a model of its own) shapes nothing.
			if (table is null || !segment.ConformTerrain)
				continue;

			var profile = _Network.ProfileOf(segment);
			var taper = _Network.GetBorderTaper(segment);
			var width = new RoadWidth(segment, table.Length);
			float border = BorderWidth(profile);

			// A bridge spans the terrain: the ground under it is not shaped to it, only kept under its deck, which
			// is what stops it showing through the deck's ends where the bridge meets the ground.
			bool bridge = segment.Bridge is not null;
			float deck = bridge ? profile.FlatBorderWidth + segment.Bridge.SideStripWidth + segment.Bridge.DeckOverhang : 0.0f;
			float reach = bridge ? width.MaxHalf + deck : width.MaxHalf + border + _Falloff;

			Vector3 previous = networkTransform.PointToWorld(table.SamplePosition(0));

			road.Clear();

			for (int i = 1; i < table.SampleCount; i++)
			{
				Vector3 current = networkTransform.PointToWorld(table.SamplePosition(i));

				// Each side of the piece as it is at the piece's middle: how far out its edge sits, and its border
				// as the segment's taper leaves it there.
				float middle = (table.SampleDistance(i - 1) + table.SampleDistance(i)) * 0.5f;

				if (bridge)
				{
					RasteriseDeck(caps, _Terrain, terrainTransform, resolution, cell, previous, current, width.LeftAt(middle) + deck, width.RightAt(middle) + deck, _Network.SurfaceOffset, _Sink);
					previous = current;
					continue;
				}

				var leftSide = new Side(width.LeftAt(middle),
					border * taper.Left.WidthScaleAt(middle, table.Length, profile.FlatBorderWidth),
					taper.Left.ReliefAt(middle, table.Length, out _, out _, out _));

				var rightSide = new Side(width.RightAt(middle),
					border * taper.Right.WidthScaleAt(middle, table.Length, profile.FlatBorderWidth),
					taper.Right.ReliefAt(middle, table.Length, out _, out _, out _));

				Rasterise(road, caps, terrainTransform, resolution, cell, previous, current, reach, leftSide, rightSide, profile, _Network.SurfaceOffset, _Falloff, _Sink);
				previous = current;
			}

			// One road pulls on a sample once, from its nearest piece; roads pull together.
			foreach (var (index, pull) in road)
			{
				if (entries.TryGetValue(index, out var existing))
					entries[index] = new Entry(MathF.Min(existing.Distance, pull.Distance), existing.Distance <= pull.Distance ? existing.Anchor : pull.Anchor, existing.Remaining * pull.Remaining);
				else
					entries[index] = pull;
			}
		}

		// The junction and parking lot surfaces and the pavements round them: the ground is held under them and
		// brought up to meet their kerbs, the same way a road holds it.
		foreach (var (centre, polygon, pavementReach, pavementTop) in _Network.GetJunctionPolygons(segment => segment.ConformTerrain))
		{
			road.Clear();
			RasteriseArea(road, caps, _Terrain, terrainTransform, networkTransform, resolution, cell, centre, polygon, pavementReach, pavementTop, _Falloff, _Sink);

			foreach (var (index, pull) in road)
			{
				if (entries.TryGetValue(index, out var existing))
					entries[index] = new Entry(MathF.Min(existing.Distance, pull.Distance), existing.Distance <= pull.Distance ? existing.Anchor : pull.Anchor, existing.Remaining * pull.Remaining);
				else
					entries[index] = pull;
			}
		}

		if (entries.Count == 0 && caps.Count == 0)
			return 0;

		// The touched rectangle, for the undo snapshot and the collider update.
		int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

		foreach (int index in entries.Keys.Concat(caps.Keys))
		{
			int x = index % resolution;
			int y = index / resolution;

			left = Math.Min(left, x);
			right = Math.Max(right, x);
			top = Math.Min(top, y);
			bottom = Math.Max(bottom, y);
		}

		var region = new RectInt(left, top, right - left + 1, bottom - top + 1);
		ushort[] before = CopyRegion(storage.HeightMap, resolution, region);
		float heightScale = ushort.MaxValue / storage.TerrainHeight;
		float originalScale = storage.TerrainHeight / ushort.MaxValue;
		int changed = 0;

		foreach (int index in entries.Keys.Concat(caps.Keys).Distinct())
		{
			int x = index % resolution;
			int y = index / resolution;

			// What the roads round this sample pull it to, capped by every road surface lying over it: a sample
			// under one road and in another's falloff is still under a road.
			Vector3 worldCentre = terrainTransform.PointToWorld(new Vector3((x + 0.5f) * cell, (y + 0.5f) * cell, 0.0f));
			float original = terrainTransform.PointToWorld(new Vector3(0.0f, 0.0f, storage.HeightMap[index] * originalScale)).z;
			float target = entries.TryGetValue(index, out var entry) ? MathX.Lerp(entry.Anchor, original, entry.Remaining) : original;

			if (caps.TryGetValue(index, out float cap))
				target = MathF.Min(target, cap);

			// The target is a world height; the map stores heights above the terrain's own origin.
			float local = terrainTransform.PointToLocal(worldCentre.WithZ(target)).z;

			storage.HeightMap[index] = (ushort)Math.Clamp(MathF.Round(local * heightScale), 0.0f, ushort.MaxValue);
			changed++;
		}

		ushort[] after = CopyRegion(storage.HeightMap, resolution, region);

		_Terrain.SyncGPUTexture();
		_Terrain.UpdateCollision(Terrain.SyncFlags.Height, region);

		SceneEditorSession.Active.UndoSystem.Insert("Conform Terrain To Roads",
			() => RestoreRegion(_Terrain, before, region),
			() => RestoreRegion(_Terrain, after, region));

		return changed;
	}



	/// <summary>
	/// One side of a piece of road: how far out from the centreline its edge sits, the width of its border there,
	/// and how much of the border's relief is in force.
	/// </summary>
	private readonly record struct Side(float Edge, float Border, float Relief);

	/// <summary>
	/// Visits every height sample within reach of one straight piece of road and records, for the road it is a
	/// piece of, what its nearest piece asks of the sample; and, for every sample under the road's surface, the
	/// height it must at least stay under.
	/// </summary>
	private static void Rasterise(Dictionary<int, Entry> _Road, Dictionary<int, float> _Caps, in Transform _TerrainTransform, int _Resolution, float _Cell,
		Vector3 _From, Vector3 _To, float _Reach, in Side _Left, in Side _Right, RoadProfile _Profile, float _SurfaceOffset, float _Falloff, float _Sink)
	{
		// The piece's footprint in terrain samples.
		Vector3 fromLocal = _TerrainTransform.PointToLocal(_From);
		Vector3 toLocal = _TerrainTransform.PointToLocal(_To);
		float localReach = _Reach / _TerrainTransform.Scale.x;

		int minX = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.x, toLocal.x) - localReach) / _Cell), 0, _Resolution - 1);
		int maxX = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.x, toLocal.x) + localReach) / _Cell), 0, _Resolution - 1);
		int minY = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.y, toLocal.y) - localReach) / _Cell), 0, _Resolution - 1);
		int maxY = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.y, toLocal.y) + localReach) / _Cell), 0, _Resolution - 1);

		if (minX > maxX || minY > maxY)
			return;

		var a = new Vector2(_From.x, _From.y);
		var b = new Vector2(_To.x, _To.y);
		Vector2 ab = b - a;
		float lengthSquared = ab.LengthSquared;

		for (int y = minY; y <= maxY; y++)
		{
			for (int x = minX; x <= maxX; x++)
			{
				Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
				var point = new Vector2(world.x, world.y);

				// Nearest point on the piece, the road height there, and which side of the road the sample is on
				// (its edge may sit further out on one side than the other).
				float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0.0f, 1.0f) : 0.0f;
				Vector2 nearest = a + ab * t;
				float distance = nearest.Distance(point);

				if (distance > _Reach)
					continue;

				bool left = ab.x * (point.y - a.y) - ab.y * (point.x - a.x) >= 0.0f;
				var side = left ? _Left : _Right;
				int index = x + y * _Resolution;
				float surface = MathX.Lerp(_From.z, _To.z, t) + _SurfaceOffset;
				float anchor = AnchorHeight(distance, surface, side.Edge, side.Border, side.Relief, _Profile, _Sink);

				// Under the road, and under a pavement, the ground can be no higher than this whatever another
				// road nearby asks for.
				bool covered = distance <= side.Edge + (_Profile.Border == RoadBorderStyle.Sidewalk ? side.Border : 0.0f);

				if (covered && (!_Caps.TryGetValue(index, out float existingCap) || anchor < existingCap))
					_Caps[index] = anchor;

				if (_Road.TryGetValue(index, out var existing) && existing.Distance <= distance)
					continue;

				// This road's pull on the sample: all of it out to the border's edge, fading over the falloff. The
				// roads' pulls are taken together afterwards, so between two roads the ground is held level.
				float beyond = MathF.Max(0.0f, distance - side.Edge - side.Border);
				float release = _Falloff > 0.0f ? Math.Clamp(beyond / _Falloff, 0.0f, 1.0f) : (beyond > 0.0f ? 1.0f : 0.0f);
				release = release * release * (3.0f - 2.0f * release);

				_Road[index] = new Entry(distance, anchor, release);
			}
		}
	}



	/// <summary>
	/// Keeps every height sample under a bridge's deck below the road on it: the ground is not shaped to a bridge,
	/// but it must not come up through its deck where the bridge runs into the ground at its ends.
	/// </summary>
	private static void RasteriseDeck(Dictionary<int, float> _Caps, Terrain _Terrain, in Transform _TerrainTransform, int _Resolution, float _Cell,
		Vector3 _From, Vector3 _To, float _LeftReach, float _RightReach, float _SurfaceOffset, float _Sink)
	{
		float reach = MathF.Max(_LeftReach, _RightReach);
		Vector3 fromLocal = _TerrainTransform.PointToLocal(_From);
		Vector3 toLocal = _TerrainTransform.PointToLocal(_To);
		float localReach = reach / _TerrainTransform.Scale.x;

		int minX = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.x, toLocal.x) - localReach) / _Cell), 0, _Resolution - 1);
		int maxX = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.x, toLocal.x) + localReach) / _Cell), 0, _Resolution - 1);
		int minY = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.y, toLocal.y) - localReach) / _Cell), 0, _Resolution - 1);
		int maxY = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.y, toLocal.y) + localReach) / _Cell), 0, _Resolution - 1);

		if (minX > maxX || minY > maxY)
			return;

		var a = new Vector2(_From.x, _From.y);
		var b = new Vector2(_To.x, _To.y);
		Vector2 ab = b - a;
		float lengthSquared = ab.LengthSquared;

		for (int y = minY; y <= maxY; y++)
		{
			for (int x = minX; x <= maxX; x++)
			{
				Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
				var point = new Vector2(world.x, world.y);

				float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0.0f, 1.0f) : 0.0f;
				float distance = (a + ab * t).Distance(point);
				bool left = ab.x * (point.y - a.y) - ab.y * (point.x - a.x) >= 0.0f;

				if (distance > (left ? _LeftReach : _RightReach))
					continue;

				int index = x + y * _Resolution;
				float cap = MathX.Lerp(_From.z, _To.z, t) + _SurfaceOffset - _Sink;

				if (!_Caps.TryGetValue(index, out float existingCap) || cap < existingCap)
					_Caps[index] = cap;
			}
		}
	}



	/// <summary>
	/// Keeps every height sample under a junction's surface (a fan from its centre to its outline) below it, and
	/// every sample under the pavement round the outline just under the pavement's top.
	/// </summary>
	private static void RasteriseArea(Dictionary<int, Entry> _Area, Dictionary<int, float> _Caps, Terrain _Terrain, in Transform _TerrainTransform, in Transform _NetworkTransform, int _Resolution, float _Cell,
		Vector3 _Centre, IReadOnlyList<Vector3> _Polygon, float _PavementReach, float _PavementTop, float _Falloff, float _Sink)
	{
		var centre = _NetworkTransform.PointToWorld(_Centre);
		var vertices = new Vector3[_Polygon.Count];
		float localReach = (_PavementReach + _Falloff) / _TerrainTransform.Scale.x;
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

		for (int i = 0; i < _Polygon.Count; i++)
		{
			vertices[i] = _NetworkTransform.PointToWorld(_Polygon[i]);
			Vector3 local = _TerrainTransform.PointToLocal(vertices[i]);

			minX = MathF.Min(minX, local.x - localReach);
			maxX = MathF.Max(maxX, local.x + localReach);
			minY = MathF.Min(minY, local.y - localReach);
			maxY = MathF.Max(maxY, local.y + localReach);
		}

		int fromX = Math.Clamp((int)MathF.Floor(minX / _Cell), 0, _Resolution - 1);
		int toX = Math.Clamp((int)MathF.Ceiling(maxX / _Cell), 0, _Resolution - 1);
		int fromY = Math.Clamp((int)MathF.Floor(minY / _Cell), 0, _Resolution - 1);
		int toY = Math.Clamp((int)MathF.Ceiling(maxY / _Cell), 0, _Resolution - 1);

		for (int y = fromY; y <= toY; y++)
		{
			for (int x = fromX; x <= toX; x++)
			{
				Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
				var point = new Vector2(world.x, world.y);
				float distance;
				float edge;

				if (FanHeight(centre, vertices, point) is { } height)
				{
					// Under the surface itself.
					distance = 0.0f;
					edge = height;
				}
				else
				{
					distance = OutlineDistance(vertices, point, out edge);

					if (distance > _PavementReach + _Falloff)
						continue;
				}

				// Under the surface the ground is kept below it; under the pavement it comes up to meet its outer
				// edge; beyond that it is held there and let go over the falloff.
				float anchor = _PavementReach > 0.0f
					? MathX.Lerp(edge - _Sink, edge + _PavementTop - 0.5f, Math.Clamp(distance / _PavementReach, 0.0f, 1.0f))
					: (distance <= 0.0f ? edge - _Sink : edge - 1.0f);

				int index = x + y * _Resolution;

				if (distance <= _PavementReach && (!_Caps.TryGetValue(index, out float existingCap) || anchor < existingCap))
					_Caps[index] = anchor;

				float beyond = MathF.Max(0.0f, distance - _PavementReach);
				float release = _Falloff > 0.0f ? Math.Clamp(beyond / _Falloff, 0.0f, 1.0f) : (beyond > 0.0f ? 1.0f : 0.0f);
				release = release * release * (3.0f - 2.0f * release);

				if (_Area.TryGetValue(index, out var existing) && existing.Distance <= distance)
					continue;

				_Area[index] = new Entry(distance, anchor, release);
			}
		}
	}



	/// <summary>
	/// How far a point in plan is from an outline, and the outline's height at the nearest point.
	/// </summary>
	internal static float OutlineDistance(Vector3[] _Vertices, Vector2 _Point, out float _Height)
	{
		float best = float.MaxValue;

		_Height = 0.0f;

		for (int i = 0; i < _Vertices.Length; i++)
		{
			Vector3 v0 = _Vertices[i];
			Vector3 v1 = _Vertices[(i + 1) % _Vertices.Length];
			var p0 = new Vector2(v0.x, v0.y);
			Vector2 edge = new Vector2(v1.x, v1.y) - p0;
			float lengthSquared = edge.LengthSquared;
			float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(_Point - p0, edge) / lengthSquared, 0.0f, 1.0f) : 0.0f;
			float distance = (p0 + edge * t).Distance(_Point);

			if (distance >= best)
				continue;

			best = distance;
			_Height = MathX.Lerp(v0.z, v1.z, t);
		}

		return best;
	}



	/// <summary>
	/// The height of the fan of triangles from a centre to an outline at a point in plan, or null outside it.
	/// </summary>
	internal static float? FanHeight(Vector3 _Centre, Vector3[] _Vertices, Vector2 _Point)
	{
		var c = new Vector2(_Centre.x, _Centre.y);

		for (int i = 0; i < _Vertices.Length; i++)
		{
			Vector3 v0 = _Vertices[i];
			Vector3 v1 = _Vertices[(i + 1) % _Vertices.Length];
			var p0 = new Vector2(v0.x, v0.y);
			var p1 = new Vector2(v1.x, v1.y);

			float area = Cross(p0 - c, p1 - c);

			if (MathF.Abs(area) < 0.0001f)
				continue;

			// Barycentric weights; a little slack so samples on the seams between triangles are not missed.
			float w0 = Cross(p1 - _Point, c - _Point) / area;
			float w1 = Cross(c - _Point, p0 - _Point) / area;
			float w2 = 1.0f - w0 - w1;

			if (w0 < -0.001f || w1 < -0.001f || w2 < -0.001f)
				continue;

			return w0 * v0.z + w1 * v1.z + w2 * _Centre.z;
		}

		return null;
	}



	private static float Cross(Vector2 _A, Vector2 _B)
	{
		return _A.x * _B.y - _A.y * _B.x;
	}



	/// <summary>
	/// The height a road holds the ground at, at a distance from its centreline: under the surface across the
	/// road, ramping under the border to meet the border's outer edge, and the edge's height from there on (how
	/// far that hold reaches before the untouched terrain takes over is the falloff, applied by the caller).
	/// </summary>
	private static float AnchorHeight(float _Distance, float _Surface, float _HalfWidth, float _Border, float _Relief, RoadProfile _Profile, float _Sink)
	{
		float outer = _HalfWidth + _Border;
		float edge;

		// The border's relief is what it is here: sinking to nothing where the road type changes.
		switch (_Profile.Border)
		{
			case RoadBorderStyle.Sidewalk:
				// Flush with the pavement (a hair under, so its edge stays a clean line).
				edge = _Surface + _Profile.CurbHeight * _Relief - 0.5f;
				break;

			case RoadBorderStyle.CountrySide:
				// The ground meets the foot of the shoulder's drop; the verge slopes on down below it, so the
				// terrain runs through the verge and the two merge.
				edge = _Surface - _Profile.Drop * _Relief;
				break;

			default:
				// Bare edge: the ground meets the slab just under its lip.
				edge = _Surface - 1.0f;
				break;
		}

		if (_Distance <= _HalfWidth)
			return _Surface - _Sink;

		if (_Distance <= outer)
		{
			float across = (_Distance - _HalfWidth) / MathF.Max(_Border, 0.001f);

			switch (_Profile.Border)
			{
				case RoadBorderStyle.Sidewalk:
					// Hidden under the pavement, rising to meet its outer edge.
					return MathX.Lerp(_Surface - _Sink, edge, across);

				case RoadBorderStyle.CountrySide:
					// Hidden under the shoulder, coming up to the foot of the drop at its edge.
					return MathX.Lerp(_Surface - _Sink, edge, across);

				default:
					return edge;
			}
		}

		return edge;
	}



	private static ushort[] CopyRegion(ushort[] _Data, int _Stride, RectInt _Region)
	{
		var region = new ushort[_Region.Width * _Region.Height];

		for (int y = 0; y < _Region.Height; y++)
		{
			for (int x = 0; x < _Region.Width; x++)
				region[x + y * _Region.Width] = _Data[_Region.Left + x + (_Region.Top + y) * _Stride];
		}

		return region;
	}



	private static void RestoreRegion(Terrain _Terrain, ushort[] _Region, RectInt _Rect)
	{
		if (!_Terrain.IsValid() || _Terrain.Storage is null)
			return;

		int stride = _Terrain.Storage.Resolution;

		for (int y = 0; y < _Rect.Height; y++)
		{
			for (int x = 0; x < _Rect.Width; x++)
				_Terrain.Storage.HeightMap[_Rect.Left + x + (_Rect.Top + y) * stride] = _Region[x + y * _Rect.Width];
		}

		_Terrain.SyncGPUTexture();
		_Terrain.UpdateCollision(Terrain.SyncFlags.Height, _Rect);
	}
}

/// <summary>
/// Paints a terrain material along the roads. Every texel under a road and its border, and out by a margin from
/// there, takes the chosen material as its overlay layer — at full strength inside that band, fading to nothing
/// over the fade beyond it. Give a country road's verge the same texture and the two merge into each other; a
/// street gets a worn band round it.
/// </summary>
internal static class RoadTerrainPainter
{
	/// <summary>
	/// A control map texel names its two materials in five bits each, so a terrain can hold this many.
	/// </summary>
	private const int MaxMaterials = 32;

	/// <summary>
	/// Paints one terrain, records an undo step, and returns how many texels changed. <paramref name="_Problem"/>
	/// is set when nothing could be painted for a reason the user needs telling about.
	/// </summary>
	public static int Paint(Terrain _Terrain, RoadNetwork _Network, TerrainMaterial _Material, float _Margin, float _Fade, out string _Problem)
	{
		_Problem = null;

		var storage = _Terrain.Storage;
		int resolution = storage.Resolution;

		if (resolution <= 0 || storage.ControlMap is null || storage.ControlMap.Length != resolution * resolution)
			return 0;

		int id = storage.Materials.IndexOf(_Material);

		if (id < 0)
		{
			if (storage.Materials.Count >= MaxMaterials)
			{
				_Problem = $"\"{_Terrain.GameObject.Name}\" already carries {MaxMaterials} terrain materials, which is all its control map can name.";
				return 0;
			}

			// The control map names materials by their place in the terrain's own list, so one the terrain has
			// never held joins the list first.
			storage.Materials.Add(_Material);
			_Terrain.UpdateMaterialsBuffer();

			id = storage.Materials.Count - 1;
		}

		float cell = storage.TerrainSize / resolution;
		var terrainTransform = _Terrain.WorldTransform;
		var networkTransform = _Network.WorldTransform;
		var coverage = new Dictionary<int, float>();

		foreach (var segment in _Network.Segments)
		{
			var table = _Network.GetCurveTable(segment);

			// A bridge carries its road over the ground rather than lying on it: what is under it is landscape.
			if (table is null || segment.Bridge is not null)
				continue;

			var profile = _Network.ProfileOf(segment);
			var taper = _Network.GetBorderTaper(segment);
			var width = new RoadWidth(segment, table.Length);
			float border = profile.FlatBorderWidth;

			Vector3 previous = networkTransform.PointToWorld(table.SamplePosition(0));

			for (int i = 1; i < table.SampleCount; i++)
			{
				Vector3 current = networkTransform.PointToWorld(table.SamplePosition(i));

				// The band as the piece's middle has it: the road's edge on that side, its border as the segment's
				// taper leaves it there, and the margin.
				float middle = (table.SampleDistance(i - 1) + table.SampleDistance(i)) * 0.5f;
				float left = width.LeftAt(middle) + border * taper.Left.WidthScaleAt(middle, table.Length, border) + _Margin;
				float right = width.RightAt(middle) + border * taper.Right.WidthScaleAt(middle, table.Length, border) + _Margin;

				RasterisePiece(coverage, terrainTransform, resolution, cell, previous, current, left, right, _Fade);

				previous = current;
			}
		}

		// A junction or a parking lot is a surface of its own: painted inside its outline and out from it.
		foreach (var (centre, polygon, pavementReach, _) in _Network.GetJunctionPolygons())
			RasteriseArea(coverage, terrainTransform, networkTransform, resolution, cell, centre, polygon, pavementReach + _Margin, _Fade);

		if (coverage.Count == 0)
			return 0;

		// The touched rectangle, for the undo snapshot and the collider update.
		int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;

		foreach (int index in coverage.Keys)
		{
			int x = index % resolution;
			int y = index / resolution;

			minX = Math.Min(minX, x);
			maxX = Math.Max(maxX, x);
			minY = Math.Min(minY, y);
			maxY = Math.Max(maxY, y);
		}

		var region = new RectInt(minX, minY, maxX - minX + 1, maxY - minY + 1);
		uint[] before = CopyRegion(storage.ControlMap, resolution, region);
		int changed = 0;

		foreach (var (index, strength) in coverage)
		{
			// The material goes on as the OVERLAY, so whatever the ground was painted with stays underneath it as
			// the base and the band's edge is a blend rather than a cut.
			var texel = new CompactTerrainMaterial(storage.ControlMap[index])
			{
				OverlayTextureId = (byte)id,
				BlendFactor = (byte)Math.Clamp(MathF.Round(strength * 255.0f), 0.0f, 255.0f)
			};

			if (storage.ControlMap[index] == texel.Packed)
				continue;

			storage.ControlMap[index] = texel.Packed;
			changed++;
		}

		if (changed == 0)
			return 0;

		uint[] after = CopyRegion(storage.ControlMap, resolution, region);

		_Terrain.SyncGPUTexture();
		_Terrain.UpdateCollision(Terrain.SyncFlags.Control, region);

		SceneEditorSession.Active.UndoSystem.Insert("Paint Terrain Along Roads",
			() => RestoreRegion(_Terrain, before, region),
			() => RestoreRegion(_Terrain, after, region));

		return changed;
	}



	/// <summary>
	/// Visits every control map texel within reach of one straight piece of road and records how much of the
	/// material it takes. A texel near two roads takes the most either of them gives it.
	/// </summary>
	private static void RasterisePiece(Dictionary<int, float> _Coverage, in Transform _TerrainTransform, int _Resolution, float _Cell,
		Vector3 _From, Vector3 _To, float _Left, float _Right, float _Fade)
	{
		Vector3 fromLocal = _TerrainTransform.PointToLocal(_From);
		Vector3 toLocal = _TerrainTransform.PointToLocal(_To);
		float localReach = (MathF.Max(_Left, _Right) + _Fade) / _TerrainTransform.Scale.x;

		int minX = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.x, toLocal.x) - localReach) / _Cell), 0, _Resolution - 1);
		int maxX = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.x, toLocal.x) + localReach) / _Cell), 0, _Resolution - 1);
		int minY = Math.Clamp((int)MathF.Floor((MathF.Min(fromLocal.y, toLocal.y) - localReach) / _Cell), 0, _Resolution - 1);
		int maxY = Math.Clamp((int)MathF.Ceiling((MathF.Max(fromLocal.y, toLocal.y) + localReach) / _Cell), 0, _Resolution - 1);

		if (minX > maxX || minY > maxY)
			return;

		var a = new Vector2(_From.x, _From.y);
		var b = new Vector2(_To.x, _To.y);
		Vector2 ab = b - a;
		float lengthSquared = ab.LengthSquared;

		for (int y = minY; y <= maxY; y++)
		{
			for (int x = minX; x <= maxX; x++)
			{
				Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
				var point = new Vector2(world.x, world.y);

				float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0.0f, 1.0f) : 0.0f;
				float distance = (a + ab * t).Distance(point);

				// Which side the texel is on: a road's edge may sit further out on one side than the other.
				bool left = ab.x * (point.y - a.y) - ab.y * (point.x - a.x) >= 0.0f;

				Take(_Coverage, x + y * _Resolution, Strength(distance, left ? _Left : _Right, _Fade));
			}
		}
	}



	/// <summary>
	/// The same for the surface of a junction or a parking lot: everything inside its outline, and out from it.
	/// </summary>
	private static void RasteriseArea(Dictionary<int, float> _Coverage, in Transform _TerrainTransform, in Transform _NetworkTransform, int _Resolution, float _Cell,
		Vector3 _Centre, IReadOnlyList<Vector3> _Polygon, float _Band, float _Fade)
	{
		if (_Polygon.Count < 3)
			return;

		var centre = _NetworkTransform.PointToWorld(_Centre);
		var vertices = new Vector3[_Polygon.Count];
		float localReach = (_Band + _Fade) / _TerrainTransform.Scale.x;
		float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;

		for (int i = 0; i < _Polygon.Count; i++)
		{
			vertices[i] = _NetworkTransform.PointToWorld(_Polygon[i]);

			Vector3 local = _TerrainTransform.PointToLocal(vertices[i]);

			minX = MathF.Min(minX, local.x - localReach);
			maxX = MathF.Max(maxX, local.x + localReach);
			minY = MathF.Min(minY, local.y - localReach);
			maxY = MathF.Max(maxY, local.y + localReach);
		}

		int fromX = Math.Clamp((int)MathF.Floor(minX / _Cell), 0, _Resolution - 1);
		int toX = Math.Clamp((int)MathF.Ceiling(maxX / _Cell), 0, _Resolution - 1);
		int fromY = Math.Clamp((int)MathF.Floor(minY / _Cell), 0, _Resolution - 1);
		int toY = Math.Clamp((int)MathF.Ceiling(maxY / _Cell), 0, _Resolution - 1);

		for (int y = fromY; y <= toY; y++)
		{
			for (int x = fromX; x <= toX; x++)
			{
				Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
				var point = new Vector2(world.x, world.y);

				float distance = RoadTerrainConformer.FanHeight(centre, vertices, point) is not null
					? 0.0f
					: RoadTerrainConformer.OutlineDistance(vertices, point, out _);

				Take(_Coverage, x + y * _Resolution, Strength(distance, _Band, _Fade));
			}
		}
	}



	private static void Take(Dictionary<int, float> _Coverage, int _Index, float _Strength)
	{
		if (_Strength <= 0.0f)
			return;

		if (!_Coverage.TryGetValue(_Index, out float existing) || _Strength > existing)
			_Coverage[_Index] = _Strength;
	}



	/// <summary>
	/// How much of the material a texel takes at a distance from a road: all of it out to the band's edge, then
	/// smoothly to nothing over the fade.
	/// </summary>
	private static float Strength(float _Distance, float _Band, float _Fade)
	{
		if (_Distance <= _Band)
			return 1.0f;

		if (_Fade <= 0.0f)
			return 0.0f;

		float beyond = Math.Clamp((_Distance - _Band) / _Fade, 0.0f, 1.0f);

		return 1.0f - beyond * beyond * (3.0f - 2.0f * beyond);
	}



	private static uint[] CopyRegion(uint[] _Data, int _Stride, RectInt _Region)
	{
		var region = new uint[_Region.Width * _Region.Height];

		for (int y = 0; y < _Region.Height; y++)
		{
			for (int x = 0; x < _Region.Width; x++)
				region[x + y * _Region.Width] = _Data[_Region.Left + x + (_Region.Top + y) * _Stride];
		}

		return region;
	}



	private static void RestoreRegion(Terrain _Terrain, uint[] _Region, RectInt _Rect)
	{
		if (!_Terrain.IsValid() || _Terrain.Storage is null)
			return;

		int stride = _Terrain.Storage.Resolution;

		for (int y = 0; y < _Rect.Height; y++)
		{
			for (int x = 0; x < _Rect.Width; x++)
				_Terrain.Storage.ControlMap[_Rect.Left + x + (_Rect.Top + y) * stride] = _Region[x + y * _Rect.Width];
		}

		_Terrain.SyncGPUTexture();
		_Terrain.UpdateCollision(Terrain.SyncFlags.Control, _Rect);
	}
}



file class RoadTerrainToolWidget : ToolSidebarWidget
{
	private readonly RoadTerrainTool m_Tool;
	private Label m_Result;
	private int m_ShownVersion = -1;



	public RoadTerrainToolWidget(RoadTerrainTool _Tool)
	{
		m_Tool = _Tool;

		AddTitle("Terrain", "landscape");

		var serialized = _Tool.GetSerialized();
		var settings = AddGroup("Shaping");

		SidebarRows.Add(settings, serialized.GetProperty(nameof(RoadTerrainTool.Falloff)));
		SidebarRows.Add(settings, serialized.GetProperty(nameof(RoadTerrainTool.Sink)));
		settings.Add(new Label("The ground is cut and filled to meet every road and its border, then blends back over the falloff. Roads themselves do not move.") { WordWrap = true });
		settings.Add(new Button("Conform Terrain To Roads", "landscape") { Clicked = m_Tool.ConformTerrain });

		var painting = AddGroup("Painting");

		SidebarRows.Add(painting, serialized.GetProperty(nameof(RoadTerrainTool.PaintMaterial)));
		SidebarRows.Add(painting, serialized.GetProperty(nameof(RoadTerrainTool.PaintMargin)));
		SidebarRows.Add(painting, serialized.GetProperty(nameof(RoadTerrainTool.PaintFade)));
		painting.Add(new Label("Paints the material onto the terrain under every road and its border, out by the margin and fading from there. Give a country verge the same texture and the two merge.") { WordWrap = true });
		painting.Add(new Button("Paint Terrain Along Roads", "brush") { Clicked = m_Tool.PaintTerrain });

		var nodes = AddGroup("Nodes");

		nodes.Add(new Label("Drops every node onto the ground under it; the roads stay smooth between nodes.") { WordWrap = true });
		nodes.Add(new Button("Snap Nodes To Ground", "vertical_align_bottom") { Clicked = m_Tool.SnapNodesToGround });

		var result = AddGroup("Result");

		m_Result = result.Add(new Label("") { WordWrap = true });

		Layout.AddStretchCell();
	}



	[EditorEvent.Frame]
	public void Frame()
	{
		if (m_Tool.ResultVersion == m_ShownVersion)
			return;

		m_ShownVersion = m_Tool.ResultVersion;
		m_Result.Text = m_Tool.LastResult;
	}
}
