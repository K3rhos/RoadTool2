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
				if (!network.TryGetCurve(segment, out var curve))
					continue;

				float reach = segment.Width + 2.0f * (RoadTerrainConformer.BorderWidth(network.ProfileOf(segment)) + Falloff);
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
	private readonly struct Entry
	{
		public readonly float Distance;
		public readonly float Target;

		public Entry(float _Distance, float _Target)
		{
			Distance = _Distance;
			Target = _Target;
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

		foreach (var segment in _Network.Segments)
		{
			var table = _Network.GetCurveTable(segment);

			if (table is null)
				continue;

			var profile = _Network.ProfileOf(segment);
			var taper = _Network.GetBorderTaper(segment);
			float halfWidth = segment.Width * 0.5f;
			float border = BorderWidth(profile);
			float reach = halfWidth + border + _Falloff;

			Vector3 previous = networkTransform.PointToWorld(table.SamplePosition(0));

			for (int i = 1; i < table.SampleCount; i++)
			{
				Vector3 current = networkTransform.PointToWorld(table.SamplePosition(i));

				// The border along this piece, as the segment's taper leaves it at the piece's middle.
				float middle = (table.SampleDistance(i - 1) + table.SampleDistance(i)) * 0.5f;
				float relief = taper.ReliefAt(middle, table.Length, out _, out _, out _);
				float pieceBorder = border * taper.WidthScaleAt(middle, table.Length, profile.FlatBorderWidth);

				Rasterise(entries, _Terrain, terrainTransform, resolution, cell, previous, current, reach, halfWidth, pieceBorder, relief, profile, _Network.SurfaceOffset, _Falloff, _Sink);
				previous = current;
			}
		}

		if (entries.Count == 0)
			return 0;

		// The touched rectangle, for the undo snapshot and the collider update.
		int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;

		foreach (int index in entries.Keys)
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

		foreach (var (index, entry) in entries)
		{
			int x = index % resolution;
			int y = index / resolution;

			// The target is a world height; the map stores heights above the terrain's own origin.
			Vector3 worldCentre = terrainTransform.PointToWorld(new Vector3((x + 0.5f) * cell, (y + 0.5f) * cell, 0.0f));
			float local = terrainTransform.PointToLocal(worldCentre.WithZ(entry.Target)).z;

			storage.HeightMap[index] = (ushort)Math.Clamp(MathF.Round(local * heightScale), 0.0f, ushort.MaxValue);
		}

		ushort[] after = CopyRegion(storage.HeightMap, resolution, region);

		_Terrain.SyncGPUTexture();
		_Terrain.UpdateCollision(Terrain.SyncFlags.Height, region);

		SceneEditorSession.Active.UndoSystem.Insert("Conform Terrain To Roads",
			() => RestoreRegion(_Terrain, before, region),
			() => RestoreRegion(_Terrain, after, region));

		return entries.Count;
	}



	/// <summary>
	/// Visits every height sample within reach of one straight piece of road and records the nearest road's
	/// target height for it.
	/// </summary>
	private static void Rasterise(Dictionary<int, Entry> _Entries, Terrain _Terrain, in Transform _TerrainTransform, int _Resolution, float _Cell,
		Vector3 _From, Vector3 _To, float _Reach, float _HalfWidth, float _Border, float _Relief, RoadProfile _Profile, float _SurfaceOffset, float _Falloff, float _Sink)
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
		float originalScale = _Terrain.Storage.TerrainHeight / ushort.MaxValue;

		for (int y = minY; y <= maxY; y++)
		{
			for (int x = minX; x <= maxX; x++)
			{
				Vector3 world = _TerrainTransform.PointToWorld(new Vector3((x + 0.5f) * _Cell, (y + 0.5f) * _Cell, 0.0f));
				var point = new Vector2(world.x, world.y);

				// Nearest point on the piece, and the road height there.
				float t = lengthSquared > 0.0f ? Math.Clamp(Vector2.Dot(point - a, ab) / lengthSquared, 0.0f, 1.0f) : 0.0f;
				float distance = (a + ab * t).Distance(point);

				if (distance > _Reach)
					continue;

				int index = x + y * _Resolution;

				if (_Entries.TryGetValue(index, out var existing) && existing.Distance <= distance)
					continue;

				float surface = MathX.Lerp(_From.z, _To.z, t) + _SurfaceOffset;
				float original = _TerrainTransform.PointToWorld(new Vector3(0.0f, 0.0f, _Terrain.Storage.HeightMap[index] * originalScale)).z;
				float target = TargetHeight(distance, surface, original, _HalfWidth, _Border, _Relief, _Profile, _Falloff, _Sink);

				_Entries[index] = new Entry(distance, target);
			}
		}
	}



	/// <summary>
	/// The ground height at a distance from a road's centreline: kept under the surface across the road, ramping
	/// under the border to meet the border's outer edge, then blending into the untouched terrain.
	/// </summary>
	private static float TargetHeight(float _Distance, float _Surface, float _Original, float _HalfWidth, float _Border, float _Relief, RoadProfile _Profile, float _Falloff, float _Sink)
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

		if (_Falloff <= 0.0f)
			return _Original;

		float blend = Math.Clamp((_Distance - outer) / _Falloff, 0.0f, 1.0f);
		float smooth = blend * blend * (3.0f - 2.0f * blend);

		return MathX.Lerp(edge, _Original, smooth);
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
