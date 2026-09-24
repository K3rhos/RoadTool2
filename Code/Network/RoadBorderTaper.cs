using System;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// How one side's border tapers at either end of a segment: over the given length from the node its relief (kerb
/// height, verge drop) runs to the share of itself given for that end — nothing when the other border is of another
/// kind, the height the two meet at when it is of the same kind — and its width runs to the width the two meet at,
/// so the two borders meet flush at the node. A zero length is no taper. An end marked as a spill has a country road
/// on the other side, whose gravel is blended onto the border there. A gore end is the nose where this road and
/// another diverge from one edge: the border there grows from nothing as the square of the distance, the way the gap
/// between two roads parting from a common tangent opens up, so the two borders fill the gore between them without
/// crossing.
/// </summary>
/// <param name="StartRelief">
/// The share of the profile's own relief the border has at the start node: 0 flattens it into the road, 1 leaves
/// it as it is, and anything else brings it to a height it shares with the border on the other side (above 1 for
/// the lower of the two).
/// </param>
/// <param name="StartOffset">
/// How far from the start node the taper ends: 0 at a plain pass-through, the arm's trim at a junction, where the
/// corner starts. Everything nearer the node than that — the corner itself — is built in the shape the taper runs
/// to, so the two roads round a corner meet in one cross-section instead of turning into each other on the arc.
/// </param>
public readonly record struct RoadBorderTaper(float StartLength, float StartWidth, float StartRelief, bool StartSpill, float EndLength, float EndWidth, float EndRelief, bool EndSpill, bool StartGore = false, bool EndGore = false, float StartOffset = 0.0f, float EndOffset = 0.0f)
{
	/// <summary>
	/// The share of the profile's relief in force at a curve distance (1 away from any taper, that end's relief at
	/// its node, and beyond its offset), how far along a taper the point is (0 outside one), the width the border is
	/// that far towards, and whether that taper's neighbour spills onto it. The two ends' tapers, offsets included,
	/// are each held to half the segment, so no point is ever inside both.
	/// </summary>
	public float ReliefAt(float _Along, float _Length, out float _Towards, out float _TargetWidth, out bool _Spill)
	{
		float relief = 1.0f;

		_Towards = 0.0f;
		_TargetWidth = 0.0f;
		_Spill = false;

		if (StartLength > 0.0f && _Along < StartOffset + StartLength)
		{
			float progress = Math.Clamp((_Along - StartOffset) / StartLength, 0.0f, 1.0f);

			_Towards = Towards(progress, StartGore);
			_TargetWidth = StartWidth;
			_Spill = StartSpill;
			relief = MathX.Lerp(StartRelief, 1.0f, progress);
		}

		if (EndLength > 0.0f && _Length - _Along < EndOffset + EndLength)
		{
			float progress = Math.Clamp((_Length - _Along - EndOffset) / EndLength, 0.0f, 1.0f);
			float towards = Towards(progress, EndGore);

			if (towards > _Towards)
			{
				_Towards = towards;
				_TargetWidth = EndWidth;
				_Spill = EndSpill;
				relief = MathX.Lerp(EndRelief, 1.0f, progress);
			}
		}

		return relief;
	}



	/// <summary>
	/// How far towards the node's width the border is at a progress along the taper (0 at the node, 1 at its far
	/// end): straight along a plain taper, and along a gore the square, so the width is the square of the progress.
	/// </summary>
	private static float Towards(float _Progress, bool _Gore)
	{
		return _Gore ? 1.0f - _Progress * _Progress : 1.0f - _Progress;
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
		_Hash.Add(StartRelief);
		_Hash.Add(StartSpill ? 1 : 0);
		_Hash.Add(StartGore ? 1 : 0);
		_Hash.Add(StartOffset);
		_Hash.Add(EndLength);
		_Hash.Add(EndWidth);
		_Hash.Add(EndRelief);
		_Hash.Add(EndSpill ? 1 : 0);
		_Hash.Add(EndGore ? 1 : 0);
		_Hash.Add(EndOffset);
	}

	/// <summary>
	/// Whether the border tapers at the end a node is at.
	/// </summary>
	public bool TapersAt(bool _AtStart)
	{
		return _AtStart ? StartLength > 0.0f : EndLength > 0.0f;
	}
}

/// <summary>
/// The tapers of a segment's two borders, looking along the segment.
/// </summary>
public readonly record struct SegmentBorderTapers(RoadBorderTaper Left, RoadBorderTaper Right)
{
	public RoadBorderTaper Side(bool _Left)
	{
		return _Left ? Left : Right;
	}

	public void AddToHash(ref StableHash _Hash)
	{
		Left.AddToHash(ref _Hash);
		Right.AddToHash(ref _Hash);
	}
}
