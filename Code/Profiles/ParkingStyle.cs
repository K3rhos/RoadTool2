using System.Text.Json.Serialization;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// How a parking lot is laid out inside: the bays round its edges and, on a lot with room for them, the rows down
/// its middle. Lots reference a style, so every car park built from it changes together when the asset is edited.
/// </summary>
[AssetType(Name = "Parking Style", Extension = "park", Category = "Road Tool 2")]
public sealed class ParkingStyle : GameResource
{
	/// <summary>
	/// How wide one bay is, measured along the edge it stands on.
	/// </summary>
	[Group("Bays"), Range(80.0f, 400.0f)] public float BayWidth { get; set { field = value.Clamp(10.0f, 10000.0f); } } = 150.0f;

	/// <summary>
	/// How far a bay reaches in from the edge.
	/// </summary>
	[Group("Bays"), Range(100.0f, 800.0f)] public float BayDepth { get; set { field = value.Clamp(10.0f, 10000.0f); } } = 260.0f;

	/// <summary>
	/// Bays turned this far from square to the edge they stand on, so cars drive into them at an angle. The bays
	/// on the far side of an aisle lean the other way, as they do in a real car park.
	/// </summary>
	[Group("Bays"), Range(0.0f, 60.0f)] public float BayAngle { get; set { field = value.Clamp(0.0f, 80.0f); } } = 0.0f;

	/// <summary>
	/// Bays round the inside of the lot's edges.
	/// </summary>
	[Group("Bays")] public bool EdgeBays { get; set; } = true;

	/// <summary>
	/// Back to back rows of bays down the middle of the lot, as many as fit with an aisle between each of them.
	/// </summary>
	[Group("Rows")] public bool CentreRows { get; set; } = true;

	/// <summary>
	/// How much room is left for driving between one row of bays and the next.
	/// </summary>
	[Group("Rows"), Range(300.0f, 1500.0f)] public float AisleWidth { get; set { field = value.Clamp(50.0f, 100000.0f); } } = 600.0f;

	/// <summary>
	/// How the bay lines are painted. Empty paints them like a road's lane lines.
	/// </summary>
	[Group("Markings")] public RoadLineStyle Line { get; set; } = new() { Width = 8.0f };

	/// <summary>
	/// A line along the back of each row, closing its bays off.
	/// </summary>
	[Group("Markings")] public bool BackLine { get; set; } = false;

	[Hide, JsonIgnore] public Material ResolvedLineMaterial => Line?.Material ?? Material.Load("roadtool2/materials/linewhite.vmat");

	/// <summary>
	/// How far apart two bay lines sit along the edge, which is wider than a bay once the bays lean.
	/// </summary>
	[Hide, JsonIgnore] public float Pitch => BayWidth / float.Cos(BayAngle.DegreeToRadian());



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(BayWidth);
		_Hash.Add(BayDepth);
		_Hash.Add(BayAngle);
		_Hash.Add(EdgeBays ? 1 : 0);
		_Hash.Add(CentreRows ? 1 : 0);
		_Hash.Add(AisleWidth);
		_Hash.Add(BackLine ? 1 : 0);
		_Hash.Add(Line is null ? 0 : 1);
		Line?.AddToHash(ref _Hash);
	}



	/// <summary>
	/// The asset was saved: every network with a lot in this style has stale pieces now.
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
		return CreateSimpleAssetTypeIcon("local_parking", _Width, _Height, "#8fd3ff", "black");
	}
}
