using System;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// How the surface a road blends from (see <see cref="RoadProfile.RoadBlendMaterial"/>) is laid where it meets the
/// road, in the road's own terms: so the blend's second layer can pick that surface's texture up exactly as it was
/// where it stopped, and the two meet without a seam. Laid along a road (a junction carries each arm's on into
/// itself along the arm's curve, a parking lot its mouth, another road its own), <see cref="Phase"/> plus the
/// distance along the road's curve (the other way round when <see cref="Reversed"/>) is the distance along that
/// surface's own; or <see cref="Planar"/>, flat from above. <see cref="Scale"/> and <see cref="Offset"/> are its
/// material's texture transform, and <see cref="Shift"/> the whole number of repeats the texture is taken round by,
/// so the coordinates the blend moves between start out as close as they can be.
/// </summary>
public readonly record struct RoadBlendSource(float Repeat, bool FitWidth, bool Planar, float Phase, bool Reversed, Vector2 Scale, Vector2 Offset, Vector2 Shift)
{
	/// <summary>
	/// A surface laid along the road, as a road of <paramref name="_Surface"/>'s type lays its own.
	/// </summary>
	public static RoadBlendSource Along(RoadProfile _Surface, float _Phase, bool _Reversed)
	{
		var (scale, offset) = RoadProfile.TextureTransform(_Surface.ResolvedRoadMaterial);

		return new RoadBlendSource(MathF.Max(1.0f, _Surface.RoadTextureRepeat), _Surface.RoadTextureFitsWidth, false, _Phase, _Reversed, scale, offset, Vector2.Zero);
	}

	/// <summary>
	/// A surface laid flat from above at <paramref name="_Surface"/>'s texture size.
	/// </summary>
	public static RoadBlendSource Flat(RoadProfile _Surface)
	{
		var (scale, offset) = RoadProfile.TextureTransform(_Surface.ResolvedRoadMaterial);

		return new RoadBlendSource(MathF.Max(1.0f, _Surface.RoadTextureRepeat), false, true, 0.0f, false, scale, offset, Vector2.Zero);
	}



	/// <summary>
	/// Where the surface's material samples its texture at a point of the road: <paramref name="_Distance"/> along the
	/// road's curve, <paramref name="_Across"/> out to its right from the centreline (a texture fitted to the width is
	/// held at the road's edges past them), where the road is <paramref name="_Left"/> and <paramref name="_Right"/>
	/// wide each side, at <paramref name="_Position"/> in network space.
	/// </summary>
	public Vector2 SampleAt(float _Distance, float _Across, float _Left, float _Right, Vector3 _Position)
	{
		Vector2 uv;

		if (Planar)
		{
			uv = new Vector2(_Position.x, _Position.y) / Repeat;
		}
		else
		{
			// Laid the other way round, the surface's right is the road's left.
			float sign = Reversed ? -1.0f : 1.0f;
			float across = _Across * sign;
			float left = Reversed ? _Right : _Left;
			float width = _Left + _Right;
			float u = !FitWidth ? across / Repeat : width > 0.001f ? Math.Clamp((across + left) / width, 0.0f, 1.0f) : 0.5f;

			uv = new Vector2(u, (Phase + sign * _Distance) / Repeat);
		}

		return new Vector2(uv.x * Scale.x + Offset.x + Shift.x, uv.y * Scale.y + Offset.y + Shift.y);
	}



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(Repeat);
		_Hash.Add(FitWidth ? 1 : 0);
		_Hash.Add(Planar ? 1 : 0);
		_Hash.Add(Phase);
		_Hash.Add(Reversed ? 1 : 0);
		_Hash.Add(Scale.x);
		_Hash.Add(Scale.y);
		_Hash.Add(Offset.x);
		_Hash.Add(Offset.y);
		_Hash.Add(Shift.x);
		_Hash.Add(Shift.y);
	}
}
