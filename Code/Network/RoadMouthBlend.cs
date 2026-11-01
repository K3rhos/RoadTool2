using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// A road blending a junction's surface into its own (see <see cref="RoadProfile.RoadBlendMaterial"/>), carried back
/// across its mouth: the blend starts on the line across the arm from the middle of the corner on one side of it to the
/// middle of the corner on the other (see <see cref="RoadJunctionBuilder"/>), where the road's share of the outline
/// starts and its skirt and banks with it, so the junction's surface is already giving way to the road's where
/// the road's edges begin. On the road's centreline that is where the road's own blend starts too; either side of it the
/// blend is drawn out or closed up from that line to the row, where it is as far on all the way across as the road's own
/// is there, and the two carry on into each other. Before the line the junction's surface is laid as it is everywhere
/// else in the junction.
/// </summary>
internal sealed class RoadMouthBlend : IFanBlend
{
	/// <summary>
	/// The least the line the blend starts on stays short of the row, so the stretch between is never closed up to nothing.
	/// </summary>
	private const float MinDepth = 1.0f;

	private readonly RoadArmMapping m_Own;
	private readonly RoadSurfaceBlend m_Blend;
	private readonly RoadBlendSource m_Source;
	private readonly bool m_AtStart;
	private readonly float m_TableLength;
	private readonly float m_Row;
	private readonly float m_Centre;
	private readonly float m_Slope;
	private readonly bool m_FitWidth;
	private readonly Vector2 m_OwnScale;
	private readonly Vector2 m_OwnOffset;
	private readonly Vector2 m_Second;
	private readonly float[] m_Steps;

	public Material Material { get; }

	public Material Own { get; }

	public IReadOnlyList<float> Steps => m_Steps;



	/// <summary>
	/// <paramref name="_Blend"/> is the road's own blend, <paramref name="_Source"/> the junction's surface as it has it at
	/// this end, <paramref name="_Centre"/> how far from the node its blend starts on its centreline, and
	/// <paramref name="_Slope"/> how much further from the node the line it starts on lies for each unit out to the road's
	/// right.
	/// </summary>
	public RoadMouthBlend(RoadJunctionLayout.Arm _Arm, RoadProfile _Profile, in RoadSurfaceBlend _Blend, RoadBlendSource _Source, float _Centre, float _Slope)
	{
		m_Own = new RoadArmMapping(_Arm.Table, _Arm.AtStart, _Arm.Trim, _Arm.Widths, _Profile.RoadTextureRepeat, _Profile.RoadTextureFitsWidth, true);
		m_Blend = _Blend;
		m_Source = _Source;
		m_AtStart = _Arm.AtStart;
		m_TableLength = _Arm.Length;
		m_Row = _Arm.Trim;
		m_Centre = Math.Clamp(_Centre, 0.0f, _Arm.Trim);
		m_Slope = _Slope;
		m_FitWidth = _Profile.RoadTextureFitsWidth;
		(m_OwnScale, m_OwnOffset) = RoadProfile.TextureTransform(_Profile.ResolvedRoadMaterial);
		m_Second = RoadProfile.SecondLayerScale(_Profile.RoadBlendMaterial);
		m_Steps = _Blend.Steps().ToArray();

		Material = _Profile.RoadBlendMaterial;
		Own = _Profile.ResolvedRoadMaterial;
	}



	/// <summary>
	/// From the line the blend starts on to the row in even steps, so the blend's start runs along that line and it
	/// reaches the row exactly as far on as the road's own blend starts out; past the row, the road's own distance.
	/// </summary>
	public float Into(Vector3 _Point)
	{
		Vector2 measure = m_Own.Measure(_Point);
		float fromNode = m_AtStart ? measure.y : m_TableLength - measure.y;

		if (fromNode >= m_Row)
			return fromNode - m_Centre;

		float start = MathF.Min(m_Centre + m_Slope * measure.x, m_Row - MinDepth);

		return (fromNode - start) * (m_Row - m_Centre) / (m_Row - start);
	}

	public float WeightAt(float _Into)
	{
		return m_Blend.WeightInto(_Into);
	}

	/// <summary>
	/// The road's own mapping carried on into the junction, as its skirt has it there: a texture fitted to the width
	/// turned back on itself past the road's edges.
	/// </summary>
	public Vector2 OwnUv(Vector3 _Point)
	{
		Vector2 uv = m_Own.Uv(_Point);

		return m_FitWidth ? new Vector2(RoadSurfaceBuilder.SkirtShare(uv.x), uv.y) : uv;
	}

	/// <summary>
	/// As the road's own blend has them (see <see cref="RoadSurfaceBuilder"/>): the road's coordinates with its material's
	/// texture transform applied, moved over to the junction's as its material takes them (taken round by the same
	/// whole repeats as the road's blend, so the two meet) as there is more of the junction's surface.
	/// </summary>
	public Vector2 BlendUv(Vector2 _Own, Vector2 _Uv, float _Weight)
	{
		var baked = new Vector2(_Own.x * m_OwnScale.x + m_OwnOffset.x, _Own.y * m_OwnScale.y + m_OwnOffset.y);

		if (_Weight <= 0.0f)
			return baked;

		var source = new Vector2(_Uv.x * m_Source.Scale.x + m_Source.Offset.x + m_Source.Shift.x, _Uv.y * m_Source.Scale.y + m_Source.Offset.y + m_Source.Shift.y);

		return Vector2.Lerp(baked, new Vector2(source.x / m_Second.x, source.y / m_Second.y), _Weight);
	}
}
