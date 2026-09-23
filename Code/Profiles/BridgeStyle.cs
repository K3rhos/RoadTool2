using System.Text.Json.Serialization;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// What stands along the two edges of a bridge deck.
/// </summary>
public enum BridgeSideStyle
{
	/// <summary>
	/// Nothing above the deck: its side face is all there is.
	/// </summary>
	None,

	/// <summary>
	/// A solid parapet wall.
	/// </summary>
	Wall,

	/// <summary>
	/// Posts at intervals with horizontal rails running between them.
	/// </summary>
	Railing,

	/// <summary>
	/// A prefab of your own repeated along the edge (a fence panel, a lamp, a cable anchor...).
	/// </summary>
	Prefab
}

/// <summary>
/// What holds a bridge deck up.
/// </summary>
public enum BridgeSupportStyle
{
	None,

	/// <summary>
	/// Pillars at intervals, from the underside of the deck down into the ground.
	/// </summary>
	Pillars,

	/// <summary>
	/// A prefab of your own at intervals under the deck (a pier, a tower...).
	/// </summary>
	Prefab
}

public enum BridgePillarShape
{
	Square,
	Round
}

/// <summary>
/// One pillar under the middle of the deck, or one under each edge.
/// </summary>
public enum BridgePillarLayout
{
	Single,
	Pair
}

/// <summary>
/// How a stretch of road becomes a bridge: the deck under it, what stands along the deck's edges, and what holds it
/// up. Each part is picked from a set of built-in designs or handed to a prefab of your own, so new kinds of bridge
/// (cable-stayed, trusses...) are new designs of a part rather than a new kind of road. Roads reference a style, so
/// every bridge built from it changes together when the asset is edited.
/// </summary>
[AssetType(Name = "Bridge Style", Extension = "bridg", Category = "Road Tool 2")]
public sealed class BridgeStyle : GameResource
{
	private const string DefaultMaterial = "roadtool2/materials/concrete.vmat";

	/// <summary>
	/// Material of the deck's sides and underside (and of the walls and pillars when they have none of their own).
	/// </summary>
	[Group("Deck")] public Material DeckMaterial { get; set; }
	[Group("Deck"), Range(10.0f, 10000.0f)] public float DeckTextureRepeat { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 500.0f;

	/// <summary>
	/// The deck's thickness below the road surface.
	/// </summary>
	[Group("Deck"), Range(5.0f, 500.0f)] public float DeckDepth { get; set { field = value.Clamp(1.0f, 10000.0f); } } = 60.0f;

	/// <summary>
	/// A bare ledge of deck outside the side (wall, railing) on each edge. 0 ends the deck at the side's outer face.
	/// </summary>
	[Group("Deck"), Range(0.0f, 300.0f)] public float DeckOverhang { get; set { field = value.Clamp(0.0f, 10000.0f); } } = 0.0f;

	[Group("Side")] public BridgeSideStyle Side { get; set; } = BridgeSideStyle.Wall;

	/// <summary>
	/// Material of the wall or the railing. Empty uses the deck's.
	/// </summary>
	[Group("Side"), ShowIf(nameof(HasBuiltSide), true)] public Material SideMaterial { get; set; }
	[Group("Side"), ShowIf(nameof(HasBuiltSide), true), Range(10.0f, 10000.0f)] public float SideTextureRepeat { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 100.0f;

	/// <summary>
	/// The wall's thickness, or the railing's post size; also the width of the strip a prefab side stands on.
	/// </summary>
	[Group("Side"), HideIf(nameof(Side), BridgeSideStyle.None), Range(1.0f, 200.0f)] public float SideWidth { get; set { field = value.Clamp(0.1f, 10000.0f); } } = 20.0f;

	/// <summary>
	/// Height of the wall or railing above the road's border (the pavement top when there is one).
	/// </summary>
	[Group("Side"), ShowIf(nameof(HasBuiltSide), true), Range(5.0f, 300.0f)] public float SideHeight { get; set { field = value.Clamp(0.1f, 10000.0f); } } = 45.0f;

	[Group("Railing"), ShowIf(nameof(Side), BridgeSideStyle.Railing), Range(50.0f, 1000.0f)] public float PostSpacing { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 200.0f;
	[Group("Railing"), ShowIf(nameof(Side), BridgeSideStyle.Railing), Range(1, 6)] public int RailCount { get; set { field = value.Clamp(1, 32); } } = 2;
	[Group("Railing"), ShowIf(nameof(Side), BridgeSideStyle.Railing), Range(1.0f, 50.0f)] public float RailSize { get; set { field = value.Clamp(0.1f, 1000.0f); } } = 6.0f;

	/// <summary>
	/// Placed on the deck's edge strip with its forward along the road and its right pointing away from the road.
	/// </summary>
	[Group("Side"), ShowIf(nameof(Side), BridgeSideStyle.Prefab)] public GameObject SidePrefab { get; set; }
	[Group("Side"), ShowIf(nameof(Side), BridgeSideStyle.Prefab), Range(10.0f, 2000.0f)] public float SideSpacing { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 200.0f;

	[Group("Supports")] public BridgeSupportStyle Supports { get; set; } = BridgeSupportStyle.Pillars;
	[Group("Supports"), HideIf(nameof(Supports), BridgeSupportStyle.None), Range(200.0f, 10000.0f)] public float SupportSpacing { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 1500.0f;

	/// <summary>
	/// The pillars' material. Empty uses the deck's.
	/// </summary>
	[Group("Supports"), ShowIf(nameof(Supports), BridgeSupportStyle.Pillars)] public Material SupportMaterial { get; set; }
	[Group("Supports"), ShowIf(nameof(Supports), BridgeSupportStyle.Pillars)] public BridgePillarShape PillarShape { get; set; } = BridgePillarShape.Square;
	[Group("Supports"), ShowIf(nameof(Supports), BridgeSupportStyle.Pillars)] public BridgePillarLayout PillarLayout { get; set; } = BridgePillarLayout.Single;
	[Group("Supports"), ShowIf(nameof(Supports), BridgeSupportStyle.Pillars), Range(10.0f, 1000.0f)] public float PillarSize { get; set { field = value.Clamp(1.0f, 10000.0f); } } = 100.0f;

	/// <summary>
	/// For a pair of pillars: how far in from the deck's edges they stand.
	/// </summary>
	[Group("Supports"), ShowIf(nameof(Supports), BridgeSupportStyle.Pillars), Range(0.0f, 500.0f)] public float PillarInset { get; set { field = value.Clamp(0.0f, 10000.0f); } } = 20.0f;
	[Group("Supports"), ShowIf(nameof(Supports), BridgeSupportStyle.Pillars), Range(3, 32)] public int PillarSegments { get; set { field = value.Clamp(3, 64); } } = 12;

	/// <summary>
	/// How far a pillar carries on below the ground it lands on, so it never floats off a slope.
	/// </summary>
	[Group("Supports"), HideIf(nameof(Supports), BridgeSupportStyle.None), Range(0.0f, 500.0f)] public float SupportSink { get; set { field = value.Clamp(0.0f, 10000.0f); } } = 50.0f;

	/// <summary>
	/// How far below the deck a support reaches when there is no ground under it to land on.
	/// </summary>
	[Group("Supports"), HideIf(nameof(Supports), BridgeSupportStyle.None), Range(100.0f, 10000.0f)] public float SupportFallbackHeight { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 1000.0f;

	/// <summary>
	/// Placed on the ground under the middle of the deck (or the fallback height below it), with its forward along
	/// the road. It is up to the prefab to reach the deck.
	/// </summary>
	[Group("Supports"), ShowIf(nameof(Supports), BridgeSupportStyle.Prefab)] public GameObject SupportPrefab { get; set; }

	[Hide, JsonIgnore] public bool HasBuiltSide => Side is BridgeSideStyle.Wall or BridgeSideStyle.Railing;

	[Hide, JsonIgnore] public Material ResolvedDeckMaterial => DeckMaterial ?? Material.Load(DefaultMaterial);

	[Hide, JsonIgnore] public Material ResolvedSideMaterial => SideMaterial ?? ResolvedDeckMaterial;

	[Hide, JsonIgnore] public Material ResolvedSupportMaterial => SupportMaterial ?? ResolvedDeckMaterial;

	/// <summary>
	/// How wide the strip along each deck edge is that the side occupies (nothing for a bare deck).
	/// </summary>
	[Hide, JsonIgnore] public float SideStripWidth => Side == BridgeSideStyle.None ? 0.0f : SideWidth;



	/// <summary>
	/// Folds everything the bridge geometry is built from into a hash, so pieces built with an older version of
	/// the style are noticed.
	/// </summary>
	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(DeckMaterial?.ResourcePath);
		_Hash.Add(DeckTextureRepeat);
		_Hash.Add(DeckDepth);
		_Hash.Add(DeckOverhang);
		_Hash.Add((int)Side);
		_Hash.Add(SideMaterial?.ResourcePath);
		_Hash.Add(SideTextureRepeat);
		_Hash.Add(SideWidth);
		_Hash.Add(SideHeight);
		_Hash.Add(PostSpacing);
		_Hash.Add(RailCount);
		_Hash.Add(RailSize);
		_Hash.Add(RoadPropSet.PrefabKey(SidePrefab));
		_Hash.Add(SideSpacing);
		_Hash.Add((int)Supports);
		_Hash.Add(SupportSpacing);
		_Hash.Add(SupportMaterial?.ResourcePath);
		_Hash.Add((int)PillarShape);
		_Hash.Add((int)PillarLayout);
		_Hash.Add(PillarSize);
		_Hash.Add(PillarInset);
		_Hash.Add(PillarSegments);
		_Hash.Add(SupportSink);
		_Hash.Add(SupportFallbackHeight);
		_Hash.Add(RoadPropSet.PrefabKey(SupportPrefab));
	}



	/// <summary>
	/// The asset was saved: every network with a bridge in this style has stale pieces now.
	/// </summary>
	protected override void PostReload()
	{
		base.PostReload();

		var scene = Application.Editor.Scene;

		if (!scene.IsValid())
			return;

		foreach (var network in scene.GetAllComponents<RoadNetwork>())
			network.MarkDirty();
	}



	protected override Bitmap CreateAssetTypeIcon(int _Width, int _Height)
	{
		return CreateSimpleAssetTypeIcon("architecture", _Width, _Height, "#7fb2ff", "black");
	}
}
