using System.Text.Json.Serialization;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// One trail worn into a road along its length: the rut a cart's wheels leave, a path trodden by people or animals.
/// It is a groove lower than the rest of the surface, with eased walls and a flat bottom, and with chaos it wanders
/// from side to side instead of running dead straight.
/// </summary>
public sealed class RoadTrail
{
	/// <summary>
	/// How far the middle of the trail sits from the road's centre line: to the right looking along the road when
	/// positive, to the left when negative.
	/// </summary>
	[Range(-500.0f, 500.0f)] public float Offset { get; set { field = value.Clamp(-100000.0f, 100000.0f); } } = 0.0f;

	/// <summary>
	/// How wide the trail is at the surface, from the top of one wall to the top of the other.
	/// </summary>
	[Range(5.0f, 300.0f)] public float Width { get; set { field = value.Clamp(1.0f, 100000.0f); } } = 30.0f;

	/// <summary>
	/// How far the trail's bottom lies below the rest of the road.
	/// </summary>
	[Range(0.0f, 50.0f)] public float Depth { get; set { field = value.Clamp(0.0f, 1000.0f); } } = 4.0f;

	/// <summary>
	/// How far the trail wanders either way of its offset, in units. 0 runs it dead straight along the road.
	/// </summary>
	[Range(0.0f, 200.0f)] public float Chaos { get; set { field = value.Clamp(0.0f, 10000.0f); } } = 12.0f;

	/// <summary>
	/// How often the trail wanders: smaller values give longer, gentler swings.
	/// </summary>
	[Range(0.0005f, 0.02f)] public float ChaosScale { get; set { field = value.Clamp(0.00001f, 10.0f); } } = 0.003f;

	/// <summary>
	/// Trails with the same seed wander together, as the two ruts of a cart do; give a trail a seed of its own to send
	/// it its own way.
	/// </summary>
	[Range(0, 100)] public int Seed { get; set { field = value.Clamp(0, 1000); } } = 0;

	/// <summary>
	/// Whether the trail cuts anything at all into the road.
	/// </summary>
	[Hide, JsonIgnore] public bool IsVisible => Width > 0.0f && Depth > 0.0f;



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(Offset);
		_Hash.Add(Width);
		_Hash.Add(Depth);
		_Hash.Add(Chaos);
		_Hash.Add(ChaosScale);
		_Hash.Add(Seed);
	}
}
