using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// What runs along the sides of a road.
/// </summary>
public enum RoadBorderStyle
{
	/// <summary>
	/// Bare road edge.
	/// </summary>
	None,

	/// <summary>
	/// Raised kerb and pavement (street).
	/// </summary>
	Sidewalk,

	/// <summary>
	/// A shoulder of road material that drops onto an uneven verge falling away into the terrain.
	/// </summary>
	CountrySide
}

/// <summary>
/// A road type: its surface material and what its sides look like. Roads reference a profile, so every street in a
/// map changes together when the asset is edited.
/// </summary>
[AssetType(Name = "Road Profile", Extension = "road", Category = "Road Tool 2")]
public sealed class RoadProfile : GameResource
{
	private static RoadProfile s_Fallback;

	/// <summary>
	/// What a road gets when no profile is assigned anywhere: the shipped asphalt profile, or failing that plain
	/// asphalt with bare edges.
	/// </summary>
	public static RoadProfile Fallback => s_Fallback ??= ResourceLibrary.Get<RoadProfile>("roadtool2/profiles/asphalt.road") ?? new RoadProfile();

	[Group("Road")] public Material RoadMaterial { get; set; }
	[Group("Road"), Range(10.0f, 10000.0f)] public float RoadTextureRepeat { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 500.0f;

	[Group("Border")] public RoadBorderStyle Border { get; set; } = RoadBorderStyle.None;

	/// <summary>
	/// Pavement material for a sidewalk; verge material (grass, dirt, gravel) for a country side border.
	/// </summary>
	[Group("Border"), HideIf(nameof(Border), RoadBorderStyle.None)] public Material BorderMaterial { get; set; }
	[Group("Border"), HideIf(nameof(Border), RoadBorderStyle.None), Range(10.0f, 10000.0f)] public float BorderTextureRepeat { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 200.0f;

	/// <summary>
	/// Sidewalk: pavement width. Country side: width of the road-material shoulder before the drop.
	/// </summary>
	[Group("Border"), HideIf(nameof(Border), RoadBorderStyle.None), Range(10.0f, 500.0f)] public float BorderWidth { get; set { field = value.Clamp(1.0f, 5000.0f); } } = 150.0f;

	[Group("Sidewalk"), ShowIf(nameof(Border), RoadBorderStyle.Sidewalk), Range(0.1f, 100.0f)] public float CurbHeight { get; set { field = value.Clamp(0.1f, 1000.0f); } } = 5.0f;

	/// <summary>
	/// Pushes the top of the kerb outward, turning the vertical kerb face into a slope. 0 is a vertical kerb.
	/// </summary>
	[Group("Sidewalk"), ShowIf(nameof(Border), RoadBorderStyle.Sidewalk), Range(0.0f, 100.0f)] public float CurbBevel { get; set { field = value.Clamp(0.0f, 1000.0f); } } = 0.0f;

	/// <summary>
	/// How far the verge steps down from the shoulder: the exposed thickness of the road.
	/// </summary>
	[Group("Country Side"), ShowIf(nameof(Border), RoadBorderStyle.CountrySide), Range(0.0f, 100.0f)] public float Drop { get; set { field = value.Clamp(0.0f, 1000.0f); } } = 8.0f;
	[Group("Country Side"), ShowIf(nameof(Border), RoadBorderStyle.CountrySide), Range(10.0f, 1000.0f)] public float VergeWidth { get; set { field = value.Clamp(1.0f, 10000.0f); } } = 200.0f;
	[Group("Country Side"), ShowIf(nameof(Border), RoadBorderStyle.CountrySide), Range(0.0f, 500.0f)] public float VergeDepth { get; set { field = value.Clamp(0.0f, 10000.0f); } } = 60.0f;
	[Group("Country Side"), ShowIf(nameof(Border), RoadBorderStyle.CountrySide), Range(1, 12)] public int SlopeSegments { get; set { field = value.Clamp(1, 32); } } = 3;

	/// <summary>
	/// How far the shoulder and verge edges wander, and how much the slope rolls, in units.
	/// </summary>
	[Group("Country Side"), ShowIf(nameof(Border), RoadBorderStyle.CountrySide), Range(0.0f, 200.0f)] public float Chaos { get; set { field = value.Clamp(0.0f, 10000.0f); } } = 30.0f;

	/// <summary>
	/// Frequency of the wander: smaller values give longer, gentler waves.
	/// </summary>
	[Group("Country Side"), ShowIf(nameof(Border), RoadBorderStyle.CountrySide), Range(0.001f, 0.2f)] public float ChaosScale { get; set { field = value.Clamp(0.0001f, 10.0f); } } = 0.02f;

	public bool HasBorder => Border != RoadBorderStyle.None;

	/// <summary>
	/// The material lists use "road" for surfaces that extend the road and "border" for the rest.
	/// </summary>
	public Material ResolvedRoadMaterial => RoadMaterial ?? Material.Load("roadtool2/materials/asphalt.vmat");

	public Material ResolvedBorderMaterial => BorderMaterial ?? Material.Load("roadtool2/materials/sidewalk.vmat");



	/// <summary>
	/// Folds the values the road surface is built from into a hash, so pieces built with an older version of the
	/// profile are noticed.
	/// </summary>
	public void AddSurfaceToHash(ref StableHash _Hash)
	{
		_Hash.Add(RoadMaterial?.ResourcePath);
		_Hash.Add(RoadTextureRepeat);
	}



	/// <summary>
	/// Folds the values the borders are built from into a hash.
	/// </summary>
	public void AddBorderToHash(ref StableHash _Hash)
	{
		_Hash.Add((int)Border);
		_Hash.Add(BorderMaterial?.ResourcePath);
		_Hash.Add(BorderTextureRepeat);
		_Hash.Add(BorderWidth);
		_Hash.Add(CurbHeight);
		_Hash.Add(CurbBevel);
		_Hash.Add(Drop);
		_Hash.Add(VergeWidth);
		_Hash.Add(VergeDepth);
		_Hash.Add(SlopeSegments);
		_Hash.Add(Chaos);
		_Hash.Add(ChaosScale);
	}



	/// <summary>
	/// The asset was saved: every network using it has stale pieces now.
	/// </summary>
	protected override void PostReload()
	{
		base.PostReload();

		var scene = Game.ActiveScene;

		if (scene is null)
			return;

		foreach (var network in scene.GetAllComponents<RoadNetwork>())
			network.MarkDirty();
	}



	protected override Bitmap CreateAssetTypeIcon(int _Width, int _Height)
	{
		return CreateSimpleAssetTypeIcon("add_road", _Width, _Height, "#ffb347", "black");
	}
}
