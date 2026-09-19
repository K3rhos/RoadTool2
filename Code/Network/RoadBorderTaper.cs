using System;

namespace RedSnail.RoadTool2;

/// <summary>
/// How a segment's border tapers into a different road type at either end: over the given length from the node
/// its relief (kerb height, verge drop) sinks to nothing when the other border is of another kind, and its width
/// runs to the neighbour's flat width, so the two borders meet flush at the node. A zero length is no taper. An
/// end marked as a spill has a country road on the other side, whose gravel is blended onto the border there.
/// </summary>
public readonly record struct RoadBorderTaper(float StartLength, float StartWidth, bool StartFlatten, bool StartSpill, float EndLength, float EndWidth, bool EndFlatten, bool EndSpill)
{
	/// <summary>
	/// The fraction of the profile's relief in force at a curve distance (1 away from any taper, 0 at a node the
	/// border flattens into), how far along a taper the point is (0 outside one), the width the border is that far
	/// towards, and whether that taper's neighbour spills onto it.
	/// </summary>
	public float ReliefAt(float _Along, float _Length, out float _Towards, out float _TargetWidth, out bool _Spill)
	{
		float relief = 1.0f;

		_Towards = 0.0f;
		_TargetWidth = 0.0f;
		_Spill = false;

		if (StartLength > 0.0f && _Along < StartLength)
		{
			float progress = Math.Clamp(_Along / StartLength, 0.0f, 1.0f);

			_Towards = 1.0f - progress;
			_TargetWidth = StartWidth;
			_Spill = StartSpill;

			if (StartFlatten)
				relief = progress;
		}

		if (EndLength > 0.0f && _Length - _Along < EndLength)
		{
			float progress = Math.Clamp((_Length - _Along) / EndLength, 0.0f, 1.0f);

			if (1.0f - progress > _Towards)
			{
				_Towards = 1.0f - progress;
				_TargetWidth = EndWidth;
				_Spill = EndSpill;
			}

			if (EndFlatten)
				relief = MathF.Min(relief, progress);
		}

		return relief;
	}



	/// <summary>
	/// The factor the border's nominal width is scaled by at a curve distance, on its way to the neighbour's.
	/// </summary>
	public float WidthScaleAt(float _Along, float _Length, float _NominalWidth)
	{
		ReliefAt(_Along, _Length, out float towards, out float targetWidth, out _);

		if (towards <= 0.0f || _NominalWidth <= 0.001f)
			return 1.0f;

		return (_NominalWidth + (targetWidth - _NominalWidth) * towards) / _NominalWidth;
	}



	public void AddToHash(ref StableHash _Hash)
	{
		_Hash.Add(StartLength);
		_Hash.Add(StartWidth);
		_Hash.Add(StartFlatten ? 1 : 0);
		_Hash.Add(StartSpill ? 1 : 0);
		_Hash.Add(EndLength);
		_Hash.Add(EndWidth);
		_Hash.Add(EndFlatten ? 1 : 0);
		_Hash.Add(EndSpill ? 1 : 0);
	}
}
