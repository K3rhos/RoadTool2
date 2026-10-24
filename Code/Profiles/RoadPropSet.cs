using System.Collections.Generic;
using System.Text.Json.Serialization;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// Which side of a road something stands on, looking along the road (start to end) for props along it, and
/// looking into the junction for props at one.
/// </summary>
public enum RoadSide
{
	Both,
	Left,
	Right
}

/// <summary>
/// The line a prop's lateral offset is measured from.
/// </summary>
public enum PropAnchor
{
	/// <summary>
	/// The edge of the road surface.
	/// </summary>
	RoadEdge,

	/// <summary>
	/// The outer edge of the road's border (the back of the pavement, the far side of a verge); the road edge
	/// itself for a road with no border.
	/// </summary>
	BorderEdge
}

/// <summary>
/// One kind of prop repeated along a road: a prefab at even intervals, on one or both sides, at a set distance
/// from the road or its border. Two rows with the same spacing and different phases alternate (lamp posts with a
/// tree halfway between each pair). Its offset and its turn, pitch and roll can each be a range rather than one
/// value, so a line of poles or barriers stands a little out of true, each one its own way, and the same way every
/// time the road is rebuilt.
/// </summary>
public sealed class RoadPropRow
{
	/// <summary>
	/// Placed with its forward along the road and its right pointing away from the road.
	/// </summary>
	public GameObject Prefab { get; set; }

	public RoadSide Side { get; set; } = RoadSide.Both;

	/// <summary>
	/// Distance between two of them (joined end to end, the length of one piece). The road is divided into as many
	/// equal bays as come nearest to it.
	/// </summary>
	[Range(50.0f, 5000.0f)] public float Spacing { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 1000.0f;

	/// <summary>
	/// Lays them end to end (guard rails, fences, walls, kerb blocks): each one a straight piece reaching from where the
	/// last one ends to where the next begins, measured along the line they stand on rather than along the middle of
	/// the road, so a bend leaves no gaps on its outside and runs none into each other on its inside. Spacing is then
	/// the length of one piece: the line takes the whole number of them that comes nearest, each stretched or squeezed
	/// along its length by the little that needs. Phase, Scatter and an Offset range have no say, as they would pull
	/// pieces out of line; with Follow Road Normal on, each piece also tilts from one end to the other, so the chain
	/// runs smoothly up and down slopes.
	/// </summary>
	[Title("Join End To End")] public bool Chain { get; set; } = false;

	/// <summary>
	/// Where in each bay the prop stands, as a fraction of the spacing: 0 at the bay's start, 0.5 halfway along.
	/// </summary>
	[Range(0.0f, 1.0f)] public float Phase { get; set { field = value.Clamp(0.0f, 0.999f); } } = 0.5f;

	public PropAnchor Anchor { get; set; } = PropAnchor.BorderEdge;

	/// <summary>
	/// How far out from the anchor line it stands; negative brings it back towards the road. A range stands each one
	/// somewhere between the two (poles at 85 to 125 out rather than all at 100).
	/// </summary>
	public RangedFloat Offset { get; set; } = -30.0f;

	/// <summary>
	/// Raised (or sunk) this much from where it lands: on the border within it, on the ground beyond it.
	/// </summary>
	[Range(-100.0f, 100.0f)] public float HeightOffset { get; set { field = value.Clamp(-100000.0f, 100000.0f); } } = 0.0f;

	/// <summary>
	/// Turned this much from facing along the road, in degrees. A range turns each one somewhere between the two.
	/// </summary>
	public RangedFloat Yaw { get; set; } = 0.0f;

	/// <summary>
	/// Tipped this much forward or back once turned, in degrees, about its own right; a range tips each one its own
	/// way.
	/// </summary>
	public RangedFloat Pitch { get; set; } = 0.0f;

	/// <summary>
	/// Leant this much to one side or the other once turned, in degrees, about its own forward; a range leans each one
	/// its own way.
	/// </summary>
	public RangedFloat Roll { get; set; } = 0.0f;

	/// <summary>
	/// Stands square to the road's surface rather than upright, so it climbs and falls with the road (a barrier along
	/// a slope); the yaw, pitch and roll then turn it from there. Off, it stands straight up whatever the road does
	/// (lamp posts, poles, trees).
	/// </summary>
	[Title("Follow Road Normal")] public bool FollowRoad { get; set; } = false;

	/// <summary>
	/// The share of places that actually get one; the rest are left empty, at random but always the same way.
	/// </summary>
	[Group("Variation"), Range(0.0f, 1.0f)] public float Chance { get; set { field = value.Clamp(0.0f, 1.0f); } } = 1.0f;

	/// <summary>
	/// Each one turned any way round on top of its yaw (trees, rocks).
	/// </summary>
	[Group("Variation"), Title("Any Way Round")] public bool RandomYaw { get; set; } = false;

	/// <summary>
	/// Each one shifted up to this far from its place, along and across.
	/// </summary>
	[Group("Variation"), Range(0.0f, 500.0f)] public float Scatter { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 0.0f;

	[Group("Variation"), Range(0.1f, 5.0f)] public float MinScale { get; set { field = value.Clamp(0.01f, 100.0f); } } = 1.0f;
	[Group("Variation"), Range(0.1f, 5.0f)] public float MaxScale { get; set { field = value.Clamp(0.01f, 100.0f); } } = 1.0f;



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(RoadPropSet.PrefabKey(Prefab));
		_Hash.Add((int)Side);
		_Hash.Add(Spacing);
		_Hash.Add(Chain ? 1 : 0);
		_Hash.Add(Phase);
		_Hash.Add((int)Anchor);
		AddToHash(ref _Hash, Offset);
		_Hash.Add(HeightOffset);
		AddToHash(ref _Hash, Yaw);
		AddToHash(ref _Hash, Pitch);
		AddToHash(ref _Hash, Roll);
		_Hash.Add(FollowRoad ? 1 : 0);
		_Hash.Add(Chance);
		_Hash.Add(RandomYaw ? 1 : 0);
		_Hash.Add(Scatter);
		_Hash.Add(MinScale);
		_Hash.Add(MaxScale);
	}



	/// <summary>
	/// A range by its two ends; a fixed value's are the same.
	/// </summary>
	private static void AddToHash(ref StableHash _Hash, RangedFloat _Range)
	{
		_Hash.Add(_Range.Min);
		_Hash.Add(_Range.Max);
	}
}

/// <summary>
/// One kind of prop standing near a junction, a set distance before it along each road that meets it: a fire
/// hydrant, a post box, a sign.
/// </summary>
public sealed class RoadJunctionProp
{
	/// <summary>
	/// Placed with its forward towards the junction and its right pointing away from the road.
	/// </summary>
	public GameObject Prefab { get; set; }

	/// <summary>
	/// Looking into the junction. With right-hand traffic the right side is the one the traffic arrives on.
	/// </summary>
	public RoadSide Side { get; set; } = RoadSide.Right;

	/// <summary>
	/// How far back from the junction's edge it stands.
	/// </summary>
	[Range(0.0f, 2000.0f)] public float Distance { get; set { field = value.Clamp(0.0f, 100000.0f); } } = 150.0f;

	public PropAnchor Anchor { get; set; } = PropAnchor.BorderEdge;

	[Range(-500.0f, 500.0f)] public float Offset { get; set { field = value.Clamp(-100000.0f, 100000.0f); } } = -30.0f;

	[Range(-100.0f, 100.0f)] public float HeightOffset { get; set { field = value.Clamp(-100000.0f, 100000.0f); } } = 0.0f;

	[Range(-180.0f, 180.0f)] public float Yaw { get; set; } = 0.0f;

	/// <summary>
	/// Stands square to the road's surface rather than upright, so it leans with the road's grade.
	/// </summary>
	[Title("Follow Road Normal")] public bool FollowRoad { get; set; } = false;

	[Range(0.0f, 1.0f)] public float Chance { get; set { field = value.Clamp(0.0f, 1.0f); } } = 1.0f;



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(RoadPropSet.PrefabKey(Prefab));
		_Hash.Add((int)Side);
		_Hash.Add(Distance);
		_Hash.Add((int)Anchor);
		_Hash.Add(Offset);
		_Hash.Add(HeightOffset);
		_Hash.Add(Yaw);
		_Hash.Add(FollowRoad ? 1 : 0);
		_Hash.Add(Chance);
	}
}

/// <summary>
/// What stands along a road and near its junctions: rows of prefabs repeated along it (lamp posts, trees, bollards,
/// guard rails) and prefabs placed a set distance before each junction (hydrants, post boxes, signs). Roads reference
/// a set, so a street's furniture changes together when the asset is edited.
/// </summary>
[AssetType(Name = "Road Props", Extension = "props", Category = "Road Tool 2")]
public sealed class RoadPropSet : GameResource
{
	[Group("Along The Road")] public List<RoadPropRow> Rows { get; set; } = new();

	[Group("At Junctions")] public List<RoadJunctionProp> JunctionRows { get; set; } = new();

	/// <summary>
	/// Changes which places the chance, scatter and random turns pick, everywhere this set is used.
	/// </summary>
	[Group("Variation")] public int Seed { get; set; } = 0;

	[Hide, JsonIgnore] public bool IsEmpty => (Rows is null || Rows.Count == 0) && (JunctionRows is null || JunctionRows.Count == 0);



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(Seed);
		_Hash.Add(Rows?.Count ?? 0);

		if (Rows is not null)
		{
			foreach (var row in Rows)
				row.AddToHash(ref _Hash);
		}

		_Hash.Add(JunctionRows?.Count ?? 0);

		if (JunctionRows is not null)
		{
			foreach (var row in JunctionRows)
				row.AddToHash(ref _Hash);
		}
	}



	/// <summary>
	/// A prefab property holds the prefab's scene; its asset path is what tells one prefab from another.
	/// </summary>
	public static string PrefabKey(GameObject _Prefab)
	{
		if (!_Prefab.IsValid())
			return null;

		if (_Prefab is PrefabScene scene)
			return scene.Source?.ResourcePath ?? scene.Name;

		return _Prefab.PrefabInstanceSource ?? _Prefab.Name;
	}



	/// <summary>
	/// The asset was saved: every network with roads using this set has stale pieces now.
	/// </summary>
	protected override void PostReload()
	{
		base.PostReload();

		RoadNetwork.Editor_MarkDirty();
	}



	protected override Bitmap CreateAssetTypeIcon(int _Width, int _Height)
	{
		return CreateSimpleAssetTypeIcon("light", _Width, _Height, "#ffd866", "black");
	}
}
