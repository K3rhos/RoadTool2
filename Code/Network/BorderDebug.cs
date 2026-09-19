using System.Collections.Generic;
using System.Text;
using Sandbox;

namespace RedSnail.RoadTool2;

/// <summary>
/// What a border piece was built from, kept on the piece while the network's debug overlay is on: one line per row
/// from the kerb to the outer edge, coloured by the run it belongs to, plus a plain-text record of every number
/// that went into the rows, corners and patches (the editor writes those out to a file). Diagnostic only.
/// </summary>
public sealed class BorderDebug
{
	public readonly record struct DebugLine(Vector3 From, Vector3 To, Color Color);

	public readonly record struct DebugMarker(Vector3 Position, Color Color);

	public static readonly Color ArmColor = Color.Green;
	public static readonly Color FirstHalfColor = Color.Red;
	public static readonly Color SecondHalfColor = Color.Cyan;
	public static readonly Color PatchColor = Color.Yellow;
	public static readonly Color CapColor = Color.Magenta;

	public List<DebugLine> Lines { get; } = new();

	public List<DebugMarker> Markers { get; } = new();

	/// <summary>
	/// The record, one entry per line.
	/// </summary>
	public StringBuilder Text { get; } = new();



	public void Line(Vector3 _From, Vector3 _To, Color _Color)
	{
		Lines.Add(new DebugLine(_From, _To, _Color));
	}



	public void Marker(Vector3 _Position, Color _Color)
	{
		Markers.Add(new DebugMarker(_Position, _Color));
	}



	public void Note(string _Text)
	{
		Text.AppendLine(_Text);
	}



	public static string Format(Vector3 _Vector)
	{
		return $"({_Vector.x:0.###} {_Vector.y:0.###} {_Vector.z:0.###})";
	}



	public static string Format(Vector2 _Vector)
	{
		return $"({_Vector.x:0.###} {_Vector.y:0.###})";
	}
}
