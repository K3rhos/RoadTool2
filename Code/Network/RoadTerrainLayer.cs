using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// What the roads have done to the terrain on this GameObject, kept apart from it the way a layer would be: for
/// every height sample the roads shaped and every control map texel they painted, the ground under it (with any
/// sculpting or painting done there since) and what the roads wrote over it. The Terrain tool's Conform and Paint
/// start from that ground every time, so they can be repeated, follow the roads when they move, and be taken back
/// out with Remove Road Layer, leaving the rest of the terrain as it is. Added by the tool's Start Road Layer.
/// </summary>
[Icon("layers")]
public sealed class RoadTerrainLayer : Component
{
	/// <summary>
	/// The terrain's resolution when the layer was started: its samples are numbered on that grid, so a terrain
	/// resized since can no longer take them. Zero while the layer has not been started.
	/// </summary>
	[Property, ReadOnly] public int Resolution { get; set; }

	/// <summary>
	/// How many height samples the roads hold away from the ground.
	/// </summary>
	[Property(Title = "Shaped Samples"), ReadOnly] public int HeightSamples { get; set; }

	/// <summary>
	/// How many control map texels the roads have painted.
	/// </summary>
	[Property(Title = "Painted Texels"), ReadOnly] public int PaintTexels { get; set; }

	/// <summary>
	/// The shaped height samples, packed by the Terrain tool.
	/// </summary>
	[Property, Hide] public string Heights { get; set; } = "";

	/// <summary>
	/// The painted texels, packed the same way.
	/// </summary>
	[Property, Hide] public string Paint { get; set; } = "";

	public bool IsStarted => Resolution > 0;
}
