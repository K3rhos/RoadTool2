using System.Text.Json.Serialization;
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
/// What a pedestrian crossing is painted as.
/// </summary>
public enum RoadCrossingStyle
{
	/// <summary>
	/// A ladder of stripes running the way traffic does, laid on the road outside the junction.
	/// </summary>
	Stripes,

	/// <summary>
	/// Two lines across the road, marking the walk over the junction's mouth: the outer one on the corner of the
	/// pavement, the inner one a crossing's depth further in.
	/// </summary>
	Lines
}

/// <summary>
/// How one kind of painted line looks. Which lines a road gets follows from its lanes (a lane line between lanes
/// running the same way, a centre line where the directions change, edge lines along the sides); the profile
/// only says what each kind looks like.
/// </summary>
public sealed class RoadLineStyle
{
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Paint material. Empty uses the tool's white (or yellow, for the centre line).
	/// </summary>
	public Material Material { get; set; }

	[Range(1.0f, 50.0f)] public float Width { get; set { field = value.Clamp(0.1f, 1000.0f); } } = 8.0f;

	/// <summary>
	/// Painted length of each dash. 0 paints a solid line.
	/// </summary>
	[Range(0.0f, 2000.0f)] public float DashLength { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 0.0f;

	[Range(0.0f, 2000.0f)] public float GapLength { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 0.0f;

	/// <summary>
	/// Two lines this far apart (centre to centre) instead of one. 0 paints a single line.
	/// </summary>
	[Range(0.0f, 100.0f)] public float DoubleSpacing { get; set { field = value.Clamp(0.0f, 1000.0f); } } = 0.0f;

	[Hide, JsonIgnore] public bool IsDashed => DashLength > 0.0f && GapLength > 0.0f;

	[Hide, JsonIgnore] public bool IsDouble => DoubleSpacing > 0.0f;



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(Enabled ? 1 : 0);
		_Hash.Add(Material?.ResourcePath);
		_Hash.Add(Width);
		_Hash.Add(DashLength);
		_Hash.Add(GapLength);
		_Hash.Add(DoubleSpacing);
	}
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
	public static RoadProfile Fallback => s_Fallback ??= ResourceLibrary.Get<RoadProfile>("roadtool2/resources/profiles/asphalt.road") ?? new RoadProfile();

	[Group("Road")] public Material RoadMaterial { get; set; }
	[Group("Road"), Range(10.0f, 10000.0f)] public float RoadTextureRepeat { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 500.0f;

	/// <summary>
	/// Thins the generated geometry out where this road type hardly bends: a straight street is a couple of long
	/// quads instead of one every sample. Worth leaving off for a road whose shape matters at every row (a country
	/// verge wanders with noise, and rows are what it wanders at).
	/// </summary>
	[Group("Optimization")] public bool Simplify { get; set; } = true;

	/// <summary>
	/// A row where the road turns less than this may be dropped.
	/// </summary>
	[Group("Optimization"), ShowIf(nameof(Simplify), true), Range(0.1f, 10.0f)] public float SimplifyAngle { get; set { field = value.Clamp(0.0f, 180.0f); } } = 1.0f;

	/// <summary>
	/// How far the thinned road may stray from its true curve. Small keeps a gentle bend smooth; 0 drops rows on
	/// the turn angle alone, which lets a long shallow curve flatten.
	/// </summary>
	[Group("Optimization"), ShowIf(nameof(Simplify), true), Range(0.0f, 50.0f)] public float SimplifyDeviation { get; set { field = value.Clamp(0.0f, 10000.0f); } } = 2.0f;

	/// <summary>
	/// Rounds off a road of this type where it dead ends: the surface carries on past the last node as a half disc
	/// and the border wraps round it, so the road reads as a turning head instead of being sliced through. Nothing
	/// happens at an end another road carries on from.
	/// </summary>
	[Group("Road"), Title("Rounded Dead Ends")] public bool RoundedEnds { get; set; } = true;

	[Group("Border")] public RoadBorderStyle Border { get; set; } = RoadBorderStyle.None;

	/// <summary>
	/// Pavement material for a sidewalk; verge material (grass, dirt, gravel) for a country side border.
	/// </summary>
	[Group("Border"), HideIf(nameof(Border), RoadBorderStyle.None)] public Material BorderMaterial { get; set; }
	[Group("Border"), HideIf(nameof(Border), RoadBorderStyle.None), Range(10.0f, 10000.0f)] public float BorderTextureRepeat { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 200.0f;

	/// <summary>
	/// Sidewalk: pavement width. Country side: width of the road-material shoulder before the drop.
	/// </summary>
	[Group("Border"), HideIf(nameof(Border), RoadBorderStyle.None), Range(0.0f, 500.0f)] public float BorderWidth { get; set { field = value.Clamp(0.0f, 5000.0f); } } = 150.0f;

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
	[Group("Country Side"), ShowIf(nameof(Border), RoadBorderStyle.CountrySide), Range(0.0f, 0.2f)] public float ChaosScale { get; set { field = value.Clamp(0.00001f, 10.0f); } } = 0.02f;

	/// <summary>
	/// Where this road type meets another at a pass-through node, its border tapers into the other's over this
	/// length: a kerb sinks to the road, a verge flattens out, the widths meet, so the change of road type is a
	/// slope rather than a step.
	/// </summary>
	[Group("Border"), HideIf(nameof(Border), RoadBorderStyle.None), Range(0.0f, 2000.0f)] public float TransitionLength { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 300.0f;

	/// <summary>
	/// The material a sidewalk's pavement takes over the transition into a country road, so the verge's gravel
	/// can be seen creeping onto it: a "blendable" material whose first layer is the pavement and whose second is
	/// the gravel, mixed per vertex (solid gravel at the node, pavement again by the end of the transition, with a
	/// ragged edge between). Empty keeps the pavement plain up to the node.
	/// </summary>
	[Group("Sidewalk"), ShowIf(nameof(Border), RoadBorderStyle.Sidewalk)] public Material BlendMaterial { get; set; }

	/// <summary>
	/// Between two lanes running the same way.
	/// </summary>
	[Group("Markings")] public RoadLineStyle LaneLine { get; set; } = new() { DashLength = 200.0f, GapLength = 300.0f };

	/// <summary>
	/// Between the two directions of a two-way road.
	/// </summary>
	[Group("Markings")] public RoadLineStyle CentreLine { get; set; } = new();

	/// <summary>
	/// Along both sides of the road.
	/// </summary>
	[Group("Markings")] public RoadLineStyle EdgeLine { get; set; } = new() { Enabled = false, Width = 6.0f };

	/// <summary>
	/// How far in from the road's edge the edge line sits (to its outer side). A wide margin makes a cycle lane
	/// or a hard shoulder.
	/// </summary>
	[Group("Markings"), Range(-100.0f, 100.0f)] public float EdgeLineMargin { get; set { field = value.Clamp(-1000.0f, 1000.0f); } } = -10.0f;

	/// <summary>
	/// Pedestrian crossings across every arm of a junction, just outside the junction: a row of painted stripes
	/// laid on the road, as many as its width has room for. Roads can still turn theirs off end by end.
	/// </summary>
	[Group("Crossings")] public bool Crossings { get; set; } = false;

	/// <summary>
	/// What a crossing looks like: a ladder of stripes laid outside the junction, or the two lines across the
	/// junction's mouth used in much of the US.
	/// </summary>
	[Group("Crossings"), ShowIf(nameof(Crossings), true), Title("Style")] public RoadCrossingStyle CrossingStyle { get; set; } = RoadCrossingStyle.Stripes;

	/// <summary>
	/// The paint a crossing's stripes are made of. Empty uses the tool's own.
	/// </summary>
	[Group("Crossings"), ShowIf(nameof(Crossings), true)] public Material CrossingMaterial { get; set; }

	/// <summary>
	/// How far along the road the crossing reaches: the length of each of its stripes.
	/// </summary>
	[Group("Crossings"), ShowIf(nameof(Crossings), true), Range(20.0f, 500.0f)] public float CrossingDepth { get; set { field = value.Clamp(1.0f, 10000.0f); } } = 120.0f;

	/// <summary>
	/// How wide one stripe of a crossing is, across the road; in the two-line style, how thick each line is.
	/// </summary>
	[Group("Crossings"), ShowIf(nameof(Crossings), true), Range(5.0f, 200.0f)] public float CrossingStripeWidth { get; set { field = value.Clamp(1.0f, 10000.0f); } } = 40.0f;

	/// <summary>
	/// The gap left between one stripe and the next. How many stripes a crossing gets follows from this and the
	/// width of the road it is painted on.
	/// </summary>
	[Group("Crossings"), ShowIf(nameof(Crossings), true), Range(5.0f, 200.0f)] public float CrossingStripeGap { get; set { field = value.Clamp(1.0f, 10000.0f); } } = 40.0f;

	/// <summary>
	/// Gap between the crossing and the road's edges on each side.
	/// </summary>
	[Group("Crossings"), ShowIf(nameof(Crossings), true), Range(0.0f, 100.0f)] public float CrossingInset { get; set { field = value.Clamp(0.0f, 10000.0f); } } = 10.0f;

	/// <summary>
	/// Gap the painted lines keep from a crossing.
	/// </summary>
	[Group("Crossings"), ShowIf(nameof(Crossings), true), Range(0.0f, 300.0f)] public float CrossingClearance { get; set { field = value.Clamp(0.0f, 10000.0f); } } = 40.0f;

	[Hide, JsonIgnore] public bool HasBorder => Border != RoadBorderStyle.None;

	/// <summary>
	/// How this road type's rows are thinned out, as the row sampler wants it. A verge that wanders with noise caps
	/// how far apart the rows may be spread, because the sampler decides what it can drop by measuring the ROAD'S
	/// EDGES and knows nothing about a border that moves between them: left to itself it drops every row on a
	/// straight road and leaves the verge a run of long flat facets stepping past each other. One feature of that
	/// noise is 1 / <see cref="ChaosScale"/> across, and nothing that fine survives rows spread wider than it — so
	/// a coarser <see cref="ChaosScale"/> (or no <see cref="Chaos"/> at all) is what buys a country road its
	/// thinning back.
	/// </summary>
	[Hide, JsonIgnore] public RoadSimplify Simplification => new(Simplify, SimplifyAngle, SimplifyDeviation, BorderWanders ? 1.0f / ChaosScale : 0.0f);

	/// <summary>
	/// Whether this road type's border has shape of its own between the rows, rather than following the road.
	/// </summary>
	[Hide, JsonIgnore] public bool BorderWanders => Border == RoadBorderStyle.CountrySide && Chaos > 0.0f && ChaosScale > 0.0f;

	[Hide, JsonIgnore] public Material ResolvedCrossingMaterial => CrossingMaterial ?? Material.Load("roadtool2/materials/crossingstrip.vmat");



	/// <summary>
	/// Folds the values the crossings are built from into a hash.
	/// </summary>
	public void AddCrossingsToHash(ref StableHash _Hash)
	{
		_Hash.Add(Crossings ? 1 : 0);
		_Hash.Add((int)CrossingStyle);
		_Hash.Add(CrossingMaterial?.ResourcePath);
		_Hash.Add(CrossingDepth);
		_Hash.Add(CrossingStripeWidth);
		_Hash.Add(CrossingStripeGap);
		_Hash.Add(CrossingInset);
		_Hash.Add(CrossingClearance);
	}

	/// <summary>
	/// The material lists use "road" for surfaces that extend the road and "border" for the rest.
	/// </summary>
	[Hide, JsonIgnore] public Material ResolvedRoadMaterial => RoadMaterial ?? Material.Load("roadtool2/materials/asphalt.vmat");

	[Hide, JsonIgnore] public Material ResolvedBorderMaterial => BorderMaterial ?? Material.Load("roadtool2/materials/sidewalk.vmat");

	[Hide, JsonIgnore] public Material ResolvedLaneLineMaterial => LaneLine?.Material ?? Material.Load("roadtool2/materials/linewhite.vmat");

	[Hide, JsonIgnore] public Material ResolvedCentreLineMaterial => CentreLine?.Material ?? Material.Load("roadtool2/materials/lineyellow.vmat");

	[Hide, JsonIgnore] public Material ResolvedEdgeLineMaterial => EdgeLine?.Material ?? Material.Load("roadtool2/materials/linewhite.vmat");



	/// <summary>
	/// Folds the values the painted lines are built from into a hash.
	/// </summary>
	public void AddMarkingsToHash(ref StableHash _Hash)
	{
		foreach (var style in new[] { LaneLine, CentreLine, EdgeLine })
		{
			_Hash.Add(style is null ? 0 : 1);
			style?.AddToHash(ref _Hash);
		}

		_Hash.Add(EdgeLineMargin);
	}



	/// <summary>
	/// Folds the values the road surface is built from into a hash, so pieces built with an older version of the
	/// profile are noticed.
	/// </summary>
	public void AddSurfaceToHash(ref StableHash _Hash)
	{
		_Hash.Add(RoadMaterial?.ResourcePath);
		_Hash.Add(RoadTextureRepeat);

		// Where the rows sit is what the surface, the borders and the bridge are all built on, and every one of
		// their signatures folds this one in.
		Simplification.AddToHash(ref _Hash);
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
		_Hash.Add(TransitionLength);
		_Hash.Add(BlendMaterial?.ResourcePath);
	}



	/// <summary>
	/// The width of this road type's border when flattened to the road: what a neighbouring road type's border
	/// tapers to where the two meet.
	/// </summary>
	[Hide, JsonIgnore] public float FlatBorderWidth => Border switch
	{
		RoadBorderStyle.Sidewalk => BorderWidth,
		RoadBorderStyle.CountrySide => BorderWidth + VergeWidth,
		_ => 0.0f
	};



	/// <summary>
	/// The asset was saved: every network using it has stale pieces now.
	/// </summary>
	protected override void PostReload()
	{
		base.PostReload();

		RoadNetwork.Editor_MarkDirty();
	}



	protected override Bitmap CreateAssetTypeIcon(int _Width, int _Height)
	{
		return CreateSimpleAssetTypeIcon("add_road", _Width, _Height, "#ffb347", "black");
	}
}
