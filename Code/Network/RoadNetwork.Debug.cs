using System;
using System.Linq;
using System.Text;
using Sandbox;

namespace RedSnail.RoadTool2;

public sealed partial class RoadNetwork
{
	/// <summary>
	/// The whole border debug record as text: the graph and generation settings, then what every border piece was
	/// built from. Only pieces built while <see cref="DebugBorders"/> was on carry a record.
	/// </summary>
	public string BuildDebugRecord()
	{
		var text = new StringBuilder();

		text.AppendLine($"ROADTOOL2 BORDER DEBUG generator={GeneratorVersion} revision={DebugRevision} time={DateTime.Now:HH:mm:ss}");
		text.AppendLine($"NETWORK name=\"{GameObject.Name}\" surfaceOffset={SurfaceOffset} spacing={SampleSpacing} chunkLength={ChunkLength} defaultProfile={DefaultProfile?.ResourceName ?? "(none)"}");

		foreach (var node in Nodes)
			text.AppendLine($"NODE id={node.Id} position={BorderDebug.Format(node.Position)} cornerRadius={node.CornerRadius} autoTangents={(node.AutoTangents ? 1 : 0)} degree={GetDegree(node.Id)}");

		foreach (var segment in Segments)
		{
			text.AppendLine($"SEGMENT id={segment.Id} start={segment.StartNodeId} end={segment.EndNodeId} startTangent={BorderDebug.Format(segment.StartTangent)} endTangent={BorderDebug.Format(segment.EndTangent)} width={segment.Width} profile={ProfileOf(segment).ResourceName}");

			if (TryGetCurve(segment, out var curve))
				text.AppendLine($"CURVE segment={segment.Id} p0={BorderDebug.Format(curve.P0)} p1={BorderDebug.Format(curve.P1)} p2={BorderDebug.Format(curve.P2)} p3={BorderDebug.Format(curve.P3)} length={GetCurveTable(segment)?.Length ?? 0.0f:0.###}");
		}

		foreach (var chunk in GetChunks().OrderBy(chunk => chunk.IsJunction ? 1 : 0).ThenBy(chunk => chunk.IsJunction ? chunk.NodeId : chunk.SegmentId).ThenBy(chunk => chunk.ChunkIndex))
		{
			if (chunk.Kind != RoadChunkKind.Border)
				continue;

			text.AppendLine();
			text.AppendLine($"PIECE name=\"{chunk.GameObject.Name}\" segment={chunk.SegmentId} node={chunk.NodeId} index={chunk.ChunkIndex} count={chunk.ChunkCount} record={(chunk.Debug is not null ? 1 : 0)}");

			if (chunk.Debug is not null)
				text.Append(chunk.Debug.Text);
		}

		return text.ToString();
	}
}
